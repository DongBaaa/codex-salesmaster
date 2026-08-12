[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [Parameter(Mandatory = $true)]
    [string]$LocalApkPath,
    [string]$BaseUrl = "",
    [string]$Channel = "stable",
    [string]$PackageName = "kr.georaeplan.mobile",
    [string]$ApkSignerPath = "",
    [string]$ApkAnalyzerPath = "",
    [string]$JavaSdkDirectory = "",
    [switch]$AcceptCertificateChange
)

$ErrorActionPreference = 'Stop'

function Resolve-DefaultProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Resolve-AppBaseUrl {
    param(
        [string]$ExplicitBaseUrl
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitBaseUrl)) {
        return $ExplicitBaseUrl.TrimEnd('/')
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GEORAEPLAN_LIVE_BASE_URL)) {
        return $env:GEORAEPLAN_LIVE_BASE_URL.TrimEnd('/')
    }

    return 'https://trade.2884.kr'
}
function Resolve-ValidatedAndroidPackageUri {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$PackageUrl,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
        throw 'manifest android packageUrl is empty.'
    }

    $baseUri = $null
    if (-not [Uri]::TryCreate(
            $BaseUrl.TrimEnd('/') + '/',
            [UriKind]::Absolute,
            [ref]$baseUri)) {
        throw 'Android signing continuity base URL is invalid.'
    }

    $packageUri = $null
    if (-not [Uri]::TryCreate(
            $PackageUrl,
            [UriKind]::Absolute,
            [ref]$packageUri)) {
        if (-not [Uri]::TryCreate(
                $baseUri,
                $PackageUrl,
                [ref]$packageUri)) {
            throw 'manifest android packageUrl is invalid.'
        }
    }

    $expectedPath =
        '/updates/download/android/' +
        [Uri]::EscapeDataString($FileName)
    $sameOrigin =
        [string]::Equals(
            $packageUri.Scheme,
            $baseUri.Scheme,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            $packageUri.Host,
            $baseUri.Host,
            [StringComparison]::OrdinalIgnoreCase) -and
        $packageUri.Port -eq $baseUri.Port
    if (-not $sameOrigin -or
        -not [string]::Equals(
            $packageUri.AbsolutePath,
            $expectedPath,
            [StringComparison]::Ordinal) -or
        -not [string]::IsNullOrEmpty($packageUri.Query) -or
        -not [string]::IsNullOrEmpty($packageUri.Fragment)) {
        throw 'manifest android packageUrl must use the expected same-origin Android download route for fileName.'
    }

    return $packageUri
}

function Assert-AndroidManifestFileName {
    param([Parameter(Mandatory = $true)][string]$FileName)

    if ([string]::IsNullOrWhiteSpace($FileName) -or
        [IO.Path]::IsPathRooted($FileName) -or
        -not [string]::Equals(
            [IO.Path]::GetFileName($FileName),
            $FileName,
            [StringComparison]::Ordinal) -or
        -not $FileName.EndsWith(
            '.apk',
            [StringComparison]::OrdinalIgnoreCase) -or
        $FileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw 'manifest android fileName must be one safe APK leaf name.'
    }
}

function Get-DownloadEffectiveUri {
    param(
        [object]$Response,
        [Parameter(Mandatory = $true)][Uri]$RequestedUri
    )

    if ($null -ne $Response -and
        $null -ne $Response.BaseResponse) {
        if ($null -ne $Response.BaseResponse.ResponseUri) {
            return [Uri]$Response.BaseResponse.ResponseUri
        }
        if ($null -ne $Response.BaseResponse.RequestMessage -and
            $null -ne $Response.BaseResponse.RequestMessage.RequestUri) {
            return [Uri]$Response.BaseResponse.RequestMessage.RequestUri
        }
    }

    return $RequestedUri
}

function Assert-DownloadedAndroidApkHash {
    param(
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    $actualSha256 = (Get-FileHash `
            -LiteralPath $ApkPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals(
            $actualSha256,
            $ExpectedSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published Android APK SHA-256 does not match manifest android sha256.'
    }

    return $actualSha256
}

function Assert-AndroidPackageIdentity {
    param(
        [Parameter(Mandatory = $true)][object]$Metadata,
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    if (-not [string]::Equals(
            [string]$Metadata.ApplicationId,
            $PackageName,
            [StringComparison]::Ordinal)) {
        throw "$SourceName Android APK applicationId does not match PackageName."
    }
}

function Resolve-ApkSignerPath {
    param(
        [string]$ProjectRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath) -and (Test-Path -LiteralPath $RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $sdkCandidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $sdkCandidates.Add($candidate) | Out-Null
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $sdkCandidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk')) | Out-Null
        $sdkCandidates.Add((Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk')) | Out-Null
    }
    $sdkCandidates.Add((Join-Path $ProjectRoot '.android-sdk')) | Out-Null
    $sdkCandidates.Add((Join-Path $ProjectRoot '.tooling\android-sdk')) | Out-Null

    foreach ($sdkCandidate in $sdkCandidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $sdkCandidate -PathType Container)) {
            continue
        }

        $buildToolsRoot = Join-Path $sdkCandidate 'build-tools'
        if (-not (Test-Path -LiteralPath $buildToolsRoot)) {
            continue
        }

        $apkSigner = Get-ChildItem -LiteralPath $buildToolsRoot -Recurse -File -Filter 'apksigner.bat' -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $apkSigner) {
            return $apkSigner.FullName
        }
    }

    return ''
}

function Resolve-ApkAnalyzerPath {
    param(
        [string]$ProjectRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "apkanalyzer not found at requested path: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $sdkCandidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $sdkCandidates.Add($candidate) | Out-Null
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $sdkCandidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk')) | Out-Null
        $sdkCandidates.Add((Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk')) | Out-Null
    }
    $sdkCandidates.Add((Join-Path $ProjectRoot '.android-sdk')) | Out-Null
    $sdkCandidates.Add((Join-Path $ProjectRoot '.tooling\android-sdk')) | Out-Null

    foreach ($sdkRoot in $sdkCandidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($sdkRoot) -or -not (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
            continue
        }

        foreach ($candidate in @(
            (Join-Path $sdkRoot 'cmdline-tools\latest\bin\apkanalyzer.bat'),
            (Join-Path $sdkRoot 'cmdline-tools\latest\bin\apkanalyzer'),
            (Join-Path $sdkRoot 'tools\bin\apkanalyzer.bat'),
            (Join-Path $sdkRoot 'tools\bin\apkanalyzer')
        )) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }

        $commandLineToolsRoot = Join-Path $sdkRoot 'cmdline-tools'
        if (-not (Test-Path -LiteralPath $commandLineToolsRoot -PathType Container)) {
            continue
        }

        $analyzer = Get-ChildItem -LiteralPath $commandLineToolsRoot -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in @('apkanalyzer.bat', 'apkanalyzer') } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $analyzer) {
            return $analyzer.FullName
        }
    }

    return ''
}

function ConvertTo-PositiveAndroidVersionCode {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    $normalized = $Value.Trim()
    [long]$versionCode = 0
    if ($normalized -notmatch '^\d+$' -or
        -not [long]::TryParse($normalized, [ref]$versionCode) -or
        $versionCode -le 0) {
        throw "$SourceName APK versionCode must be a positive integer."
    }

    return $versionCode
}

function Get-ApkManifestMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$ApkAnalyzerPath,
        [Parameter(Mandatory = $true)][string]$JavaHome,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    $previousJavaHome = $env:JAVA_HOME
    $previousPath = $env:PATH
    try {
        $env:JAVA_HOME = $JavaHome
        $env:PATH = (Join-Path $JavaHome 'bin') + [System.IO.Path]::PathSeparator + $env:PATH

        $applicationIdOutput = & $ApkAnalyzerPath manifest application-id $ApkPath 2>&1
        $applicationIdExitCode = $LASTEXITCODE
        if ($applicationIdExitCode -ne 0) {
            throw "apkanalyzer application-id failed for $SourceName APK(exit=$applicationIdExitCode)."
        }
        $applicationId = (($applicationIdOutput | Out-String).Trim())
        if ($applicationId -notmatch '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$') {
            throw "$SourceName APK applicationId is not one valid package identifier."
        }

        $versionCodeOutput = & $ApkAnalyzerPath manifest version-code $ApkPath 2>&1
        $versionCodeExitCode = $LASTEXITCODE
        if ($versionCodeExitCode -ne 0) {
            throw "apkanalyzer version-code failed for $SourceName APK(exit=$versionCodeExitCode)."
        }

        return [pscustomobject]@{
            ApplicationId = $applicationId
            VersionCode = ConvertTo-PositiveAndroidVersionCode `
                -Value (($versionCodeOutput | Out-String).Trim()) `
                -SourceName $SourceName
        }
    }
    finally {
        $env:JAVA_HOME = $previousJavaHome
        $env:PATH = $previousPath
    }
}

function Resolve-JavaHomeForApkSigner {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $requestedJava = Join-Path $RequestedPath 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $requestedJava -PathType Leaf)) {
            $requestedJava = Join-Path $RequestedPath 'bin\java'
        }
        if (-not (Test-Path -LiteralPath $requestedJava -PathType Leaf)) {
            throw "Java SDK not found at requested path: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $candidates = [System.Collections.Generic.List[string]]::new()

    foreach ($candidate in @($env:JAVA_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $candidates.Add($candidate) | Out-Null
        }
    }

    foreach ($directCandidate in @(
        (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'),
        (Join-Path ${env:ProgramFiles(x86)} 'Android\Android Studio\jbr'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\jbr')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($directCandidate)) {
            $candidates.Add($directCandidate) | Out-Null
        }
    }

    foreach ($commandName in @('java', 'javac', 'keytool')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $command.Source))) | Out-Null
        }
    }

    foreach ($pattern in @(
        (Join-Path $env:USERPROFILE '.antigravity\extensions\*\jre\*\bin\java.exe'),
        'C:\Program Files\Microsoft\jdk*\bin\java.exe',
        'C:\Program Files\Java\*\bin\java.exe'
    )) {
        $match = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $match) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $match.FullName))) | Out-Null
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $javaExecutable = Join-Path $candidate 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $javaExecutable -PathType Leaf)) {
            $javaExecutable = Join-Path $candidate 'bin\java'
        }
        if (Test-Path -LiteralPath $javaExecutable -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return ''
}

function Get-ApkSigningCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$ApkSignerPath,
        [Parameter(Mandatory = $true)][string]$JavaHome
    )

    if (-not (Test-Path -LiteralPath $ApkPath)) {
        throw "APK not found: $ApkPath"
    }

    $previousJavaHome = $env:JAVA_HOME
    $previousPath = $env:PATH
    try {
        $env:JAVA_HOME = $JavaHome
        $env:PATH = (Join-Path $JavaHome 'bin') + ';' + $env:PATH
        $apkSignerOutput = & $ApkSignerPath verify --print-certs $ApkPath 2>&1
        $apkSignerExitCode = $LASTEXITCODE
        $apkSignerText = ($apkSignerOutput | Out-String -Width 4096)
        if ($apkSignerExitCode -ne 0) {
            throw "apksigner verify failed(exit=$apkSignerExitCode): $apkSignerText"
        }

        $dnMatch = [regex]::Match($apkSignerText, 'Signer\s+#1\s+certificate\s+DN:\s*(?<value>.+)')
        $shaMatch = [regex]::Match($apkSignerText, 'Signer\s+#1\s+certificate\s+SHA-256\s+digest:\s*(?<value>[0-9a-fA-F]+)')
        if (-not $shaMatch.Success) {
            throw "apksigner output did not include Signer #1 certificate SHA-256 digest: $apkSignerText"
        }

        $certificateDn = if ($dnMatch.Success) { $dnMatch.Groups['value'].Value.Trim() } else { '' }
        $certificateSha256 = $shaMatch.Groups['value'].Value.Trim().ToLowerInvariant()
        $isDebugSigning =
            $certificateDn.IndexOf('CN=Android Debug', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $certificateDn.IndexOf('O=Android', [System.StringComparison]::OrdinalIgnoreCase) -ge 0

        return [pscustomobject]@{
            CertificateDn = $certificateDn
            CertificateSha256 = $certificateSha256
            IsDebugSigning = $isDebugSigning
        }
    }
    finally {
        $env:JAVA_HOME = $previousJavaHome
        $env:PATH = $previousPath
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

$LocalApkPath = if ([System.IO.Path]::IsPathRooted($LocalApkPath)) {
    $LocalApkPath
}
else {
    Join-Path $ProjectRoot $LocalApkPath
}
if (-not (Test-Path -LiteralPath $LocalApkPath)) {
    throw "Local APK not found for signing continuity check: $LocalApkPath"
}
$LocalApkPath = (Resolve-Path -LiteralPath $LocalApkPath).Path

$resolvedBaseUrl = Resolve-AppBaseUrl -ExplicitBaseUrl $BaseUrl
$manifestUrl = "$resolvedBaseUrl/updates/manifest?channel=$Channel"
$apkSigner = Resolve-ApkSignerPath -ProjectRoot $ProjectRoot -RequestedPath $ApkSignerPath
if ([string]::IsNullOrWhiteSpace($apkSigner)) {
    throw 'apksigner not found. Install Android SDK build-tools or pass -ApkSignerPath.'
}
$apkAnalyzer = Resolve-ApkAnalyzerPath -ProjectRoot $ProjectRoot -RequestedPath $ApkAnalyzerPath
if ([string]::IsNullOrWhiteSpace($apkAnalyzer)) {
    throw 'apkanalyzer not found. Install Android SDK command-line tools or pass -ApkAnalyzerPath.'
}
$javaHome = Resolve-JavaHomeForApkSigner -RequestedPath $JavaSdkDirectory
if ([string]::IsNullOrWhiteSpace($javaHome)) {
    throw 'JAVA_HOME/java.exe not found for apksigner. Install JDK 17+ or pass -JavaSdkDirectory.'
}

$probeDirectory = Join-Path $ProjectRoot 'temp\android-signing-continuity'
New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
$probeRunDirectory = Join-Path $probeDirectory ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRunDirectory -Force | Out-Null
$remoteApkPath = ''

try {
    $manifest = Invoke-RestMethod -Uri $manifestUrl -Method Get -UseBasicParsing -TimeoutSec 30
    $androidFileName = [string]$manifest.android.fileName
    Assert-AndroidManifestFileName -FileName $androidFileName
    $androidPackageUrl = [string]$manifest.android.packageUrl
    $manifestSha256 = ([string]$manifest.android.sha256).Trim().ToLowerInvariant()
    if ($manifestSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'manifest android sha256 must be exactly 64 hexadecimal characters.'
    }

    $remotePackageUri = Resolve-ValidatedAndroidPackageUri `
        -BaseUrl $resolvedBaseUrl `
        -PackageUrl $androidPackageUrl `
        -FileName $androidFileName
    $remoteApkPath = Join-Path $probeRunDirectory $androidFileName
    $downloadResponse = Invoke-WebRequest `
        -Uri $remotePackageUri `
        -OutFile $remoteApkPath `
        -UseBasicParsing `
        -MaximumRedirection 0 `
        -TimeoutSec 180
    $effectivePackageUri = Get-DownloadEffectiveUri `
        -Response $downloadResponse `
        -RequestedUri $remotePackageUri
    $null = Resolve-ValidatedAndroidPackageUri `
        -BaseUrl $resolvedBaseUrl `
        -PackageUrl $effectivePackageUri.AbsoluteUri `
        -FileName $androidFileName

    $downloadedSha256 = Assert-DownloadedAndroidApkHash `
        -ApkPath $remoteApkPath `
        -ExpectedSha256 $manifestSha256

    $localMetadata = Get-ApkManifestMetadata `
        -ApkPath $LocalApkPath `
        -ApkAnalyzerPath $apkAnalyzer `
        -JavaHome $javaHome `
        -SourceName 'local'
    $publishedMetadata = Get-ApkManifestMetadata `
        -ApkPath $remoteApkPath `
        -ApkAnalyzerPath $apkAnalyzer `
        -JavaHome $javaHome `
        -SourceName 'published'
    Assert-AndroidPackageIdentity `
        -Metadata $localMetadata `
        -PackageName $PackageName `
        -SourceName 'local'
    Assert-AndroidPackageIdentity `
        -Metadata $publishedMetadata `
        -PackageName $PackageName `
        -SourceName 'published'

    $localVersionCode = $localMetadata.VersionCode
    $publishedVersionCode = $publishedMetadata.VersionCode
    if ($localVersionCode -le $publishedVersionCode) {
        Write-Host 'android_signing_continuity=FAIL'
        throw "Local APK versionCode must be greater than the published APK versionCode. local=$localVersionCode published=$publishedVersionCode"
    }

    $localCertificate = Get-ApkSigningCertificate -ApkPath $LocalApkPath -ApkSignerPath $apkSigner -JavaHome $javaHome
    $remoteCertificate = Get-ApkSigningCertificate -ApkPath $remoteApkPath -ApkSignerPath $apkSigner -JavaHome $javaHome

    Write-Host "android_signing_continuity_base_url=$resolvedBaseUrl"
    Write-Host "android_signing_continuity_manifest=$manifestUrl"
    Write-Host "android_signing_continuity_local_apk=$LocalApkPath"
    Write-Host "android_signing_continuity_remote_apk=$($effectivePackageUri.AbsoluteUri)"
    Write-Host "android_package_name=$PackageName"
    Write-Host "android_manifest_file_name=$androidFileName"
    Write-Host "android_manifest_sha256=$manifestSha256"
    Write-Host "android_downloaded_sha256=$downloadedSha256"
    Write-Host "local_application_id=$($localMetadata.ApplicationId)"
    Write-Host "published_application_id=$($publishedMetadata.ApplicationId)"
    Write-Host "local_version_code=$localVersionCode"
    Write-Host "published_version_code=$publishedVersionCode"
    Write-Host "local_certificate_dn=$($localCertificate.CertificateDn)"
    Write-Host "local_certificate_sha256=$($localCertificate.CertificateSha256)"
    Write-Host "remote_certificate_dn=$($remoteCertificate.CertificateDn)"
    Write-Host "remote_certificate_sha256=$($remoteCertificate.CertificateSha256)"

    if (-not [string]::Equals($localCertificate.CertificateSha256, $remoteCertificate.CertificateSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        $message = 'Release APK signing certificate differs from the currently published Android package; existing installed APK cannot be updated in place without uninstall/reinstall or an explicit signing-certificate migration plan.'
        if ($AcceptCertificateChange) {
            Write-Warning $message
            Write-Host 'android_signing_continuity=ACCEPTED_CERTIFICATE_CHANGE'
            return
        }

        Write-Host 'android_signing_continuity=FAIL'
        throw $message
    }

    if ($localCertificate.IsDebugSigning) {
        Write-Warning 'Android APK signing continuity passed, but the continuing certificate is a debug signing certificate.'
    }

    Write-Host 'android_signing_continuity=PASS'
}
finally {
    if (Test-Path -LiteralPath $probeRunDirectory -PathType Container) {
        Remove-Item -LiteralPath $probeRunDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
