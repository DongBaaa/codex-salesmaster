[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [Parameter(Mandatory = $true)]
    [string]$LocalApkPath,
    [string]$BaseUrl = "",
    [string]$Channel = "stable",
    [string]$ApkSignerPath = "",
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
function Resolve-PackageUrl {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$PackageUrl
    )

    if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
        throw 'manifest android packageUrl is empty.'
    }

    if ($PackageUrl.StartsWith('/')) {
        return $BaseUrl.TrimEnd('/') + $PackageUrl
    }

    return $PackageUrl
}

function Resolve-ApkSignerPath {
    param(
        [string]$ProjectRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath) -and (Test-Path -LiteralPath $RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $sdkCandidates = @(
        $env:ANDROID_SDK_ROOT,
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk'),
        (Join-Path $ProjectRoot '.android-sdk'),
        (Join-Path $ProjectRoot '.tooling\android-sdk')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }

    foreach ($sdkCandidate in $sdkCandidates) {
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

function Resolve-JavaHomeForApkSigner {
    param([string]$RequestedPath)

    $candidates = [System.Collections.Generic.List[string]]::new()

    foreach ($candidate in @($RequestedPath, $env:JAVA_HOME)) {
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
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe'))) {
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
$javaHome = Resolve-JavaHomeForApkSigner -RequestedPath $JavaSdkDirectory
if ([string]::IsNullOrWhiteSpace($javaHome)) {
    throw 'JAVA_HOME/java.exe not found for apksigner. Install JDK 17+ or pass -JavaSdkDirectory.'
}

$probeDirectory = Join-Path $ProjectRoot 'temp\android-signing-continuity'
New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
$remoteApkPath = Join-Path $probeDirectory ("android-current-{0}.apk" -f [Guid]::NewGuid().ToString('N'))

try {
    $manifest = Invoke-RestMethod -Uri $manifestUrl -Method Get -UseBasicParsing -TimeoutSec 30
    $androidPackageUrl = [string]$manifest.android.packageUrl
    $remotePackageUrl = Resolve-PackageUrl -BaseUrl $resolvedBaseUrl -PackageUrl $androidPackageUrl
    Invoke-WebRequest -Uri $remotePackageUrl -OutFile $remoteApkPath -UseBasicParsing -TimeoutSec 180 | Out-Null

    $localCertificate = Get-ApkSigningCertificate -ApkPath $LocalApkPath -ApkSignerPath $apkSigner -JavaHome $javaHome
    $remoteCertificate = Get-ApkSigningCertificate -ApkPath $remoteApkPath -ApkSignerPath $apkSigner -JavaHome $javaHome

    Write-Host "android_signing_continuity_base_url=$resolvedBaseUrl"
    Write-Host "android_signing_continuity_manifest=$manifestUrl"
    Write-Host "android_signing_continuity_local_apk=$LocalApkPath"
    Write-Host "android_signing_continuity_remote_apk=$remotePackageUrl"
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
    if (Test-Path -LiteralPath $remoteApkPath) {
        Remove-Item -LiteralPath $remoteApkPath -Force -ErrorAction SilentlyContinue
    }
}
