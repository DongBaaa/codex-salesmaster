[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$WindowsSigningConfigPath,
    [string]$PackageRoot,
    [string[]]$Paths = @(),
    [switch]$RequireSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Get-DeploymentRoot {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidate = Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Set-ApiBaseUrl.ps1') } |
        Select-Object -First 1 -ExpandProperty FullName

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'Deployment root not found under project root.'
    }

    return $candidate
}

function Resolve-WindowsSigningConfigPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$ConfigPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        if (-not (Test-Path -LiteralPath $ConfigPath)) {
            throw "Windows signing config not found: $ConfigPath"
        }

        return (Resolve-Path -LiteralPath $ConfigPath).Path
    }

    $defaultPath = Join-Path $ProjectRoot 'tools\release\windows-signing.local.json'
    if (Test-Path -LiteralPath $defaultPath) {
        return (Resolve-Path -LiteralPath $defaultPath).Path
    }

    return ''
}

function Get-NormalizedText {
    param([object]$Value)

    if ($null -eq $Value) {
        return ''
    }

    return ([string]$Value).Trim()
}

function Get-NormalizedStringArray {
    param([object]$Value)

    $items = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @($Value)) {
        if ($null -eq $entry) {
            continue
        }

        $text = ([string]$entry).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $items.Add($text) | Out-Null
    }

    return $items.ToArray()
}

function Normalize-Thumbprint {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    return (($Value -replace '\s+', '')).ToUpperInvariant()
}

function Test-CodeSigningCertificate {
    param([Parameter(Mandatory = $true)]$Certificate)

    if (-not $Certificate.HasPrivateKey) {
        return $false
    }

    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $ekuExtensions = @($Certificate.Extensions | Where-Object {
            $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
        })
    if ($ekuExtensions.Count -eq 0) {
        return $true
    }

    foreach ($extension in $ekuExtensions) {
        foreach ($oid in $extension.EnhancedKeyUsages) {
            if ([string]::Equals([string]$oid.Value, $codeSigningOid, [System.StringComparison]::Ordinal)) {
                return $true
            }
        }
    }

    return $false
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

function Get-DefaultArtifactTargets {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $deploymentRoot = Get-DeploymentRoot -ProjectRoot $ProjectRoot
    $adminOutputRoot = Join-Path $deploymentRoot '관리자용'
    $packageRoot = Join-Path $adminOutputRoot '거래플랜-PC-설치패키지'

    return [pscustomobject]@{
        PackageRoot = $packageRoot
        Paths       = @(
            (Join-Path $adminOutputRoot '거래플랜-PC-설치패키지.msi'),
            (Join-Path $deploymentRoot '거래플랜-PC-설치패키지.exe')
        )
    }
}

function Resolve-SigningConfig {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    try {
        return (Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    catch {
        throw "Windows signing config could not be parsed: $ConfigPath"
    }
}

function Find-CertificateInStore {
    param(
        [Parameter(Mandatory = $true)][string]$StoreLocation,
        [Parameter(Mandatory = $true)][string]$StoreName,
        [string]$Thumbprint,
        [string]$SubjectContains
    )

    $thumbprint = Normalize-Thumbprint -Value $Thumbprint
    $subjectContains = Get-NormalizedText -Value $SubjectContains
    if ([string]::IsNullOrWhiteSpace($thumbprint) -and [string]::IsNullOrWhiteSpace($subjectContains)) {
        return $null
    }

    $storeLocationEnum = [System.Enum]::Parse([System.Security.Cryptography.X509Certificates.StoreLocation], $StoreLocation, $true)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, $storeLocationEnum)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    try {
        $now = Get-Date
        $matches = @(
            $store.Certificates |
                Where-Object {
                    (Test-CodeSigningCertificate -Certificate $_) -and
                    $_.NotAfter -gt $now -and
                    ($thumbprint.Length -eq 0 -or (Normalize-Thumbprint -Value $_.Thumbprint) -eq $thumbprint) -and
                    ($subjectContains.Length -eq 0 -or $_.Subject.IndexOf($subjectContains, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
                } |
                Sort-Object NotAfter -Descending
        )

        if ($matches.Count -gt 0) {
            return [pscustomobject]@{
                Kind          = 'Store'
                Thumbprint    = Normalize-Thumbprint -Value $matches[0].Thumbprint
                Subject       = [string]$matches[0].Subject
                StoreLocation = [string]$storeLocationEnum
                StoreName     = $StoreName
            }
        }
    }
    finally {
        $store.Close()
    }

    return $null
}

function Try-ResolveStoreCertificate {
    param([Parameter(Mandatory = $true)]$Config)

    $storeLocation = Get-NormalizedText -Value $Config.certificateStoreLocation
    if ([string]::IsNullOrWhiteSpace($storeLocation)) {
        $storeLocation = 'CurrentUser'
    }

    $storeName = Get-NormalizedText -Value $Config.certificateStoreName
    if ([string]::IsNullOrWhiteSpace($storeName)) {
        $storeName = 'My'
    }

    $thumbprint = Get-NormalizedText -Value $Config.certificateThumbprint
    $subjectContains = Get-NormalizedText -Value $Config.certificateSubjectContains
    if ([string]::IsNullOrWhiteSpace($thumbprint) -and [string]::IsNullOrWhiteSpace($subjectContains)) {
        return [pscustomobject]@{ Material = $null; Message = '' }
    }

    try {
        $material = Find-CertificateInStore -StoreLocation $storeLocation -StoreName $storeName -Thumbprint $thumbprint -SubjectContains $subjectContains
    }
    catch {
        return [pscustomobject]@{ Material = $null; Message = ('certificate_store_error={0}' -f $_.Exception.Message) }
    }

    if ($null -ne $material) {
        return [pscustomobject]@{ Material = $material; Message = '' }
    }

    return [pscustomobject]@{ Material = $null; Message = ('certificate_store_not_found store={0}/{1}' -f $storeLocation, $storeName) }
}

function Try-ResolvePfxCertificate {
    param([Parameter(Mandatory = $true)]$Config)

    $pathEnvName = Get-NormalizedText -Value $Config.certificatePathEnvironmentVariable
    if ([string]::IsNullOrWhiteSpace($pathEnvName)) {
        return [pscustomobject]@{ Material = $null; Message = '' }
    }

    $pfxPath = [Environment]::GetEnvironmentVariable($pathEnvName)
    if ([string]::IsNullOrWhiteSpace($pfxPath)) {
        return [pscustomobject]@{ Material = $null; Message = ('certificate_path_env_missing {0}' -f $pathEnvName) }
    }

    $pfxPath = Resolve-FullPath -Path $pfxPath
    if (-not (Test-Path -LiteralPath $pfxPath)) {
        return [pscustomobject]@{ Material = $null; Message = ('certificate_path_missing {0}' -f $pfxPath) }
    }

    $passwordEnvName = Get-NormalizedText -Value $Config.certificatePasswordEnvironmentVariable
    $password = ''
    if (-not [string]::IsNullOrWhiteSpace($passwordEnvName)) {
        $passwordValue = [Environment]::GetEnvironmentVariable($passwordEnvName)
        if ($null -eq $passwordValue) {
            return [pscustomobject]@{ Material = $null; Message = ('certificate_password_env_missing {0}' -f $passwordEnvName) }
        }

        $password = $passwordValue
    }

    try {
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
            $pfxPath,
            $password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        if (-not (Test-CodeSigningCertificate -Certificate $certificate)) {
            throw 'PFX certificate does not contain a usable private code-signing key.'
        }
        if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
            throw 'PFX code-signing certificate is not currently valid.'
        }
        $material = [pscustomobject]@{
            Kind       = 'Pfx'
            Path       = $pfxPath
            Password   = $password
            Thumbprint = Normalize-Thumbprint -Value $certificate.Thumbprint
            Subject    = [string]$certificate.Subject
        }
        $certificate.Reset()
        return [pscustomobject]@{ Material = $material; Message = '' }
    }
    catch {
        return [pscustomobject]@{ Material = $null; Message = ('certificate_pfx_load_failed {0}' -f $_.Exception.Message) }
    }
}

function Resolve-SigningMaterial {
    param([Parameter(Mandatory = $true)]$Config)

    $messages = New-Object System.Collections.Generic.List[string]

    $storeAttempt = Try-ResolveStoreCertificate -Config $Config
    if ($null -ne $storeAttempt.Material) {
        return [pscustomobject]@{ Material = $storeAttempt.Material; Message = '' }
    }
    if (-not [string]::IsNullOrWhiteSpace($storeAttempt.Message)) {
        $messages.Add($storeAttempt.Message) | Out-Null
    }

    $pfxAttempt = Try-ResolvePfxCertificate -Config $Config
    if ($null -ne $pfxAttempt.Material) {
        return [pscustomobject]@{ Material = $pfxAttempt.Material; Message = '' }
    }
    if (-not [string]::IsNullOrWhiteSpace($pfxAttempt.Message)) {
        $messages.Add($pfxAttempt.Message) | Out-Null
    }

    if ($messages.Count -eq 0) {
        $messages.Add('certificate configuration is empty') | Out-Null
    }

    return [pscustomobject]@{
        Material = $null
        Message  = ('No usable Windows signing certificate was resolved. {0}' -f ($messages -join ' | '))
    }
}

function Resolve-SignToolPath {
    param([Parameter(Mandatory = $true)]$Config)

    $candidates = New-Object System.Collections.Generic.List[string]

    $configuredPath = Get-NormalizedText -Value $Config.signToolPath
    if (-not [string]::IsNullOrWhiteSpace($configuredPath)) {
        $candidates.Add($configuredPath) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($env:SIGNTOOL_EXE)) {
        $candidates.Add($env:SIGNTOOL_EXE) | Out-Null
    }

    $kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $kitDirectories = Get-ChildItem -LiteralPath $kitsRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending
        foreach ($kitDirectory in $kitDirectories) {
            $candidates.Add((Join-Path $kitDirectory.FullName 'x64\signtool.exe')) | Out-Null
            $candidates.Add((Join-Path $kitDirectory.FullName 'x86\signtool.exe')) | Out-Null
        }
    }

    $candidates.Add('C:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool.exe') | Out-Null
    $candidates.Add('signtool.exe') | Out-Null

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $resolvedCommand = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $resolvedCommand) {
            return $resolvedCommand.Source
        }

        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return ''
}

function Invoke-SignToolForTarget {
    param(
        [Parameter(Mandatory = $true)][string]$SignToolPath,
        [Parameter(Mandatory = $true)]$SigningMaterial,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$FileDigestAlgorithm,
        [Parameter(Mandatory = $true)][string]$TimestampDigestAlgorithm,
        [Parameter(Mandatory = $true)][string]$TimestampUrl
    )

    $arguments = New-Object System.Collections.Generic.List[string]
    $arguments.Add('sign') | Out-Null
    $arguments.Add('/fd') | Out-Null
    $arguments.Add($FileDigestAlgorithm) | Out-Null
    $arguments.Add('/td') | Out-Null
    $arguments.Add($TimestampDigestAlgorithm) | Out-Null
    $arguments.Add('/tr') | Out-Null
    $arguments.Add($TimestampUrl) | Out-Null

    if ($SigningMaterial.Kind -eq 'Store') {
        if ($SigningMaterial.StoreLocation.Equals('LocalMachine', [System.StringComparison]::OrdinalIgnoreCase)) {
            $arguments.Add('/sm') | Out-Null
        }

        $arguments.Add('/s') | Out-Null
        $arguments.Add($SigningMaterial.StoreName) | Out-Null
        $arguments.Add('/sha1') | Out-Null
        $arguments.Add($SigningMaterial.Thumbprint) | Out-Null
    }
    else {
        $arguments.Add('/f') | Out-Null
        $arguments.Add($SigningMaterial.Path) | Out-Null
        $arguments.Add('/p') | Out-Null
        $arguments.Add($SigningMaterial.Password) | Out-Null
    }

    $arguments.Add($TargetPath) | Out-Null

    & $SignToolPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw ("signtool failed ({0}) for {1}" -f $LASTEXITCODE, $TargetPath)
    }
}

function Invoke-SignedArtifactVerification {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string[]]$TargetPaths,
        [Parameter(Mandatory = $true)]$SigningMaterial,
        [string[]]$ExpectedTimestampSubjectContains
    )

    $verifyScript = Join-Path $ProjectRoot 'tools\release\Test-GeoraePlanWindowsSigning.ps1'
    if (-not (Test-Path -LiteralPath $verifyScript)) {
        throw "Windows Authenticode verification script not found: $verifyScript"
    }

    $arguments = New-Object System.Collections.Generic.List[string]
    $arguments.Add('-NoProfile') | Out-Null
    $arguments.Add('-ExecutionPolicy') | Out-Null
    $arguments.Add('Bypass') | Out-Null
    $arguments.Add('-File') | Out-Null
    $arguments.Add($verifyScript) | Out-Null
    $arguments.Add('-RequireSigned') | Out-Null
    $arguments.Add('-RequireTimestamp') | Out-Null
    $arguments.Add('-Paths') | Out-Null
    foreach ($targetPath in $TargetPaths) {
        $arguments.Add($targetPath) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($SigningMaterial.Thumbprint)) {
        $arguments.Add('-ExpectedSignerThumbprint') | Out-Null
        $arguments.Add($SigningMaterial.Thumbprint) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($SigningMaterial.Subject)) {
        $arguments.Add('-ExpectedSignerSubjectContains') | Out-Null
        $arguments.Add($SigningMaterial.Subject) | Out-Null
    }

    if ($ExpectedTimestampSubjectContains.Count -gt 0) {
        $arguments.Add('-ExpectedTimestampSubjectContains') | Out-Null
        foreach ($fragment in $ExpectedTimestampSubjectContains) {
            $arguments.Add($fragment) | Out-Null
        }
    }

    & powershell @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Signed artifact verification failed after signtool completed.'
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
        $ProjectRoot = Resolve-ProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
    }
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

    $defaultTargets = $null
    if ([string]::IsNullOrWhiteSpace($PackageRoot) -and $Paths.Count -eq 0) {
        $defaultTargets = Get-DefaultArtifactTargets -ProjectRoot $ProjectRoot
        $PackageRoot = $defaultTargets.PackageRoot
        $Paths = $defaultTargets.Paths
    }

    $resolvedConfigPath = Resolve-WindowsSigningConfigPath -ProjectRoot $ProjectRoot -ConfigPath $WindowsSigningConfigPath
    if ([string]::IsNullOrWhiteSpace($resolvedConfigPath)) {
        if ($RequireSigning) {
            throw 'Windows signing config was not found. Copy tools\release\windows-signing.example.json to tools\release\windows-signing.local.json or pass -WindowsSigningConfigPath.'
        }

        Write-Host 'windows_authenticode_signing=SKIPPED_NO_CONFIG'
        exit 0
    }

    $config = Resolve-SigningConfig -ConfigPath $resolvedConfigPath
    $signingMaterialResult = Resolve-SigningMaterial -Config $config
    if ($null -eq $signingMaterialResult.Material) {
        if ($RequireSigning) {
            throw $signingMaterialResult.Message
        }

        Write-Warning $signingMaterialResult.Message
        Write-Host 'windows_authenticode_signing=SKIPPED_NO_CERTIFICATE'
        exit 0
    }

    $signToolPath = Resolve-SignToolPath -Config $config
    if ([string]::IsNullOrWhiteSpace($signToolPath)) {
        if ($RequireSigning) {
            throw 'signtool.exe was not found. Install Windows SDK / Signing Tools or set signToolPath/SIGNTOOL_EXE.'
        }

        Write-Warning 'signtool.exe was not found. Skipping optional Windows Authenticode signing.'
        Write-Host 'windows_authenticode_signing=SKIPPED_NO_SIGNTOOL'
        exit 0
    }

    $targetPaths = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
        foreach ($relativePath in @('App\거래플랜.Desktop.App.exe', 'App\거래플랜.exe', 'App\Updater\거래플랜.Updater.exe')) {
            $candidate = Resolve-FullPath -Path (Join-Path $PackageRoot $relativePath)
            if ($seen.Add($candidate)) {
                $targetPaths.Add($candidate) | Out-Null
            }
        }
    }

    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $candidate = Resolve-FullPath -Path $path
        if ($seen.Add($candidate)) {
            $targetPaths.Add($candidate) | Out-Null
        }
    }

    if ($targetPaths.Count -eq 0) {
        if ($RequireSigning) {
            throw 'Windows signing targets were not supplied.'
        }

        Write-Host 'windows_authenticode_signing=SKIPPED_NO_TARGETS'
        exit 0
    }

    $missingTargets = @($targetPaths | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missingTargets.Count -gt 0) {
        throw ("Windows signing target not found: {0}" -f ($missingTargets -join ', '))
    }

    $fileDigestAlgorithm = Get-NormalizedText -Value $config.fileDigestAlgorithm
    if ([string]::IsNullOrWhiteSpace($fileDigestAlgorithm)) {
        $fileDigestAlgorithm = 'SHA256'
    }
    else {
        $fileDigestAlgorithm = $fileDigestAlgorithm.ToUpperInvariant()
    }
    if ($fileDigestAlgorithm -ne 'SHA256') {
        throw "Only SHA256 is allowed for Windows file digest signing. configured=$fileDigestAlgorithm"
    }

    $timestampDigestAlgorithm = Get-NormalizedText -Value $config.timestampDigestAlgorithm
    if ([string]::IsNullOrWhiteSpace($timestampDigestAlgorithm)) {
        $timestampDigestAlgorithm = 'SHA256'
    }
    else {
        $timestampDigestAlgorithm = $timestampDigestAlgorithm.ToUpperInvariant()
    }
    if ($timestampDigestAlgorithm -ne 'SHA256') {
        throw "Only SHA256 is allowed for RFC3161 timestamp digest signing. configured=$timestampDigestAlgorithm"
    }

    $timestampUrl = Get-NormalizedText -Value $config.timestampRfc3161Url
    if ([string]::IsNullOrWhiteSpace($timestampUrl)) {
        $timestampUrl = 'https://timestamp.digicert.com'
    }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($timestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        -not [string]::Equals($timestampUri.Scheme, 'https', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "RFC3161 timestamp URL must be an absolute HTTPS URL. configured=$timestampUrl"
    }

    $expectedTimestampSubjectContains = @(Get-NormalizedStringArray -Value $config.timestampSubjectContains)
    foreach ($targetPath in $targetPaths) {
        Write-Host ("windows_authenticode_signing_target={0}" -f $targetPath)
        Invoke-SignToolForTarget -SignToolPath $signToolPath -SigningMaterial $signingMaterialResult.Material -TargetPath $targetPath -FileDigestAlgorithm $fileDigestAlgorithm -TimestampDigestAlgorithm $timestampDigestAlgorithm -TimestampUrl $timestampUrl
    }

    Invoke-SignedArtifactVerification -ProjectRoot $ProjectRoot -TargetPaths $targetPaths.ToArray() -SigningMaterial $signingMaterialResult.Material -ExpectedTimestampSubjectContains $expectedTimestampSubjectContains
    Write-Host ("windows_authenticode_signing=PASS signed_files={0}" -f $targetPaths.Count)
    exit 0
}
catch {
    Write-Host 'windows_authenticode_signing=FAIL'
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
