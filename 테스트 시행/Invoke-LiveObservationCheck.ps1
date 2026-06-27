[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$BaseUrl = "",
    [string]$Channel = "stable",
    [int]$SampleCount = 3,
    [int]$IntervalSeconds = 15,
    [string]$ProbeUsername = "",
    [string]$ProbePassword = "",
    [string]$BearerToken = "",
    [switch]$SkipManifestProbe,
    [switch]$SkipPackageProbe,
    [switch]$SkipAndroidSigningProbe,
    [switch]$FailOnAndroidDebugSigning,
    [string]$LocalCacheAppDataRoot = "",
    [string]$LocalCacheEvidenceDirectory = "",
    [switch]$SkipLocalCacheConsistencyCheck,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $ProjectRoot = Split-Path -Parent $scriptRoot
}

function Resolve-AppBaseUrl {
    param(
        [string]$ProjectRoot,
        [string]$ExplicitBaseUrl
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitBaseUrl)) {
        return $ExplicitBaseUrl.TrimEnd("/")
    }

    $candidatePaths = @(
        (Join-Path $ProjectRoot "배포\관리자용\거래플랜-PC-설치패키지\App\appsettings.json"),
        (Join-Path $ProjectRoot "배포\설치패키지\관리자용\거래플랜-PC-설치패키지\App\appsettings.json"),
        (Join-Path $ProjectRoot "Desktop\거래플랜.Desktop.App\appsettings.json")
    )

    foreach ($candidatePath in $candidatePaths) {
        if (-not (Test-Path -LiteralPath $candidatePath)) {
            continue
        }

        try {
            $json = Get-Content -LiteralPath $candidatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            $resolved = [string]$json.Api.BaseUrl
            if (-not [string]::IsNullOrWhiteSpace($resolved)) {
                return $resolved.TrimEnd("/")
            }
        }
        catch {
        }
    }

    throw "BaseUrl을 결정할 수 없습니다. -BaseUrl 값을 직접 지정하세요."
}

function Get-ObservationAuthHeaders {
    param(
        [string]$BaseUrl,
        [string]$ProbeUsername,
        [string]$ProbePassword,
        [string]$BearerToken
    )

    if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
        return @{ Authorization = "Bearer $($BearerToken.Trim())" }
    }

    if ([string]::IsNullOrWhiteSpace($ProbeUsername) -or [string]::IsNullOrWhiteSpace($ProbePassword)) {
        return @{}
    }

    $loginUri = $BaseUrl.TrimEnd('/') + '/auth/login'
    try {
        $payload = @{ username = $ProbeUsername; password = $ProbePassword } | ConvertTo-Json -Compress
        $response = Invoke-RestMethod -Uri $loginUri -Method Post -UseBasicParsing -TimeoutSec 20 -ContentType 'application/json; charset=utf-8' -Body $payload
        $token = [string]$response.accessToken
        if ([string]::IsNullOrWhiteSpace($token)) {
            $token = [string]$response.AccessToken
        }

        if ([string]::IsNullOrWhiteSpace($token)) {
            throw '로그인 응답에 accessToken이 없습니다.'
        }

        return @{ Authorization = "Bearer $token" }
    }
    catch {
        throw "패키지 관찰용 로그인 실패: $($_.Exception.Message)"
    }
}

function Invoke-WebProbe {
    param(
        [string]$Uri,
        [ValidateSet("Get", "Head")]
        [string]$Method = "Get",
        [hashtable]$Headers = @{}
    )

    try {
        $invokeArgs = @{
            Uri = $Uri
            Method = $Method
            UseBasicParsing = $true
            TimeoutSec = 20
        }
        if ($Headers.Count -gt 0) {
            $invokeArgs.Headers = $Headers
        }

        $response = Invoke-WebRequest @invokeArgs
        return [pscustomobject]@{
            Success = $true
            StatusCode = [int]$response.StatusCode
            Content = if ($Method -eq "Get") { [string]$response.Content } else { "" }
            Error = ""
        }
    }
    catch {
        $statusCode = 0
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $statusCode
            Content = ""
            Error = $_.Exception.Message
        }
    }
}

function Invoke-RangeProbe {
    param(
        [string]$Uri,
        [hashtable]$Headers = @{}
    )

    try {
        $request = [System.Net.HttpWebRequest]::Create($Uri)
        $request.Method = "GET"
        $request.Timeout = 30000
        $request.ReadWriteTimeout = 30000
        $request.AllowAutoRedirect = $true
        foreach ($headerKey in $Headers.Keys) {
            if ([string]::Equals($headerKey, 'Authorization', [System.StringComparison]::OrdinalIgnoreCase)) {
                $request.Headers['Authorization'] = [string]$Headers[$headerKey]
            }
            else {
                $request.Headers[$headerKey] = [string]$Headers[$headerKey]
            }
        }
        $request.AddRange(0, 0)

        $response = [System.Net.HttpWebResponse]$request.GetResponse()
        try {
            return [pscustomobject]@{
                Success = $true
                StatusCode = [int]$response.StatusCode
                Content = ""
                Error = ""
            }
        }
        finally {
            if ($response.GetResponseStream()) {
                $response.GetResponseStream().Dispose()
            }

            $response.Close()
        }
    }
    catch [System.Net.WebException] {
        $statusCode = 0
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $statusCode
            Content = ""
            Error = $_.Exception.Message
        }
    }
}

function Test-PackageProbe {
    param(
        [string]$BaseUrl,
        [string]$PackageUrl,
        [hashtable]$AuthHeaders = @{}
    )

    if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
        return [pscustomobject]@{
            Success = $false
            StatusCode = 0
            Error = "manifest에 packageUrl이 없습니다."
        }
    }

    $resolvedPackageUrl = $PackageUrl
    if ($resolvedPackageUrl.StartsWith('/')) {
        $resolvedPackageUrl = $BaseUrl.TrimEnd('/') + $resolvedPackageUrl
    }

    $headResult = Invoke-WebProbe -Uri $resolvedPackageUrl -Method Head
    if ($headResult.Success) {
        return [pscustomobject]@{
            Success = $true
            StatusCode = $headResult.StatusCode
            Content = ""
            Error = ""
            ProbeMode = 'anonymous-head'
            AuthUsed = $false
        }
    }

    $rangeResult = Invoke-RangeProbe -Uri $resolvedPackageUrl
    if ($rangeResult.Success) {
        return [pscustomobject]@{
            Success = $true
            StatusCode = $rangeResult.StatusCode
            Content = ""
            Error = ""
            ProbeMode = 'anonymous-range'
            AuthUsed = $false
        }
    }

    if ($AuthHeaders.Count -gt 0 -and (($headResult.StatusCode -in 401, 403) -or ($rangeResult.StatusCode -in 401, 403))) {
        $authHeadResult = Invoke-WebProbe -Uri $resolvedPackageUrl -Method Head -Headers $AuthHeaders
        if ($authHeadResult.Success) {
            return [pscustomobject]@{
                Success = $true
                StatusCode = $authHeadResult.StatusCode
                Content = ""
                Error = ""
                ProbeMode = 'auth-head'
                AuthUsed = $true
            }
        }

        $authRangeResult = Invoke-RangeProbe -Uri $resolvedPackageUrl -Headers $AuthHeaders
        if ($authRangeResult.Success) {
            return [pscustomobject]@{
                Success = $true
                StatusCode = $authRangeResult.StatusCode
                Content = ""
                Error = ""
                ProbeMode = 'auth-range'
                AuthUsed = $true
            }
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $authRangeResult.StatusCode
            Content = ""
            Error = $authRangeResult.Error
            ProbeMode = 'auth-range'
            AuthUsed = $true
        }
    }

    return [pscustomobject]@{
        Success = $false
        StatusCode = $rangeResult.StatusCode
        Content = ""
        Error = $rangeResult.Error
        ProbeMode = 'anonymous-range'
        AuthUsed = $false
    }
}

function Resolve-PackageUrl {
    param(
        [string]$BaseUrl,
        [string]$PackageUrl
    )

    if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
        return ""
    }

    $resolvedPackageUrl = $PackageUrl
    if ($resolvedPackageUrl.StartsWith('/')) {
        $resolvedPackageUrl = $BaseUrl.TrimEnd('/') + $resolvedPackageUrl
    }

    return $resolvedPackageUrl
}

function Resolve-ApkSignerPath {
    param(
        [string]$ProjectRoot
    )

    $sdkCandidates = @(
        $env:ANDROID_SDK_ROOT,
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk'),
        (Join-Path $ProjectRoot '.android-sdk')
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

    return ""
}

function Resolve-JavaHomeForApkSigner {
    $candidates = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates.Add($env:JAVA_HOME) | Out-Null
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
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return ""
}

function Test-AndroidApkSigningProbe {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$AndroidPackageUrl,
        [hashtable]$AuthHeaders = @{},
        [bool]$SkipAndroidSigningProbe
    )

    if ($SkipAndroidSigningProbe) {
        return [pscustomobject]@{
            Success = $true
            Skipped = $true
            IsDebugSigning = $false
            CertificateDn = ""
            CertificateSha256 = ""
            Message = "사용자 옵션으로 Android APK signing 점검을 건너뜀"
        }
    }

    $resolvedPackageUrl = Resolve-PackageUrl -BaseUrl $BaseUrl -PackageUrl $AndroidPackageUrl
    if ([string]::IsNullOrWhiteSpace($resolvedPackageUrl)) {
        return [pscustomobject]@{
            Success = $true
            Skipped = $true
            IsDebugSigning = $false
            CertificateDn = ""
            CertificateSha256 = ""
            Message = "manifest에 android packageUrl이 없어 signing 점검을 건너뜀"
        }
    }

    $apkSignerPath = Resolve-ApkSignerPath -ProjectRoot $ProjectRoot
    if ([string]::IsNullOrWhiteSpace($apkSignerPath)) {
        return [pscustomobject]@{
            Success = $true
            Skipped = $true
            IsDebugSigning = $false
            CertificateDn = ""
            CertificateSha256 = ""
            Message = "apksigner를 찾지 못해 Android APK signing 점검을 건너뜀"
        }
    }

    $javaHome = Resolve-JavaHomeForApkSigner
    if ([string]::IsNullOrWhiteSpace($javaHome)) {
        return [pscustomobject]@{
            Success = $true
            Skipped = $true
            IsDebugSigning = $false
            CertificateDn = ""
            CertificateSha256 = ""
            Message = "JAVA_HOME/java.exe를 찾지 못해 Android APK signing 점검을 건너뜀"
        }
    }

    $probeDirectory = Join-Path $ProjectRoot 'temp\live-observation-android-signing'
    New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
    $apkPath = Join-Path $probeDirectory ("android-live-{0}.apk" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $previousJavaHome = $env:JAVA_HOME
    $previousPath = $env:PATH

    try {
        $env:JAVA_HOME = $javaHome
        $env:PATH = (Join-Path $javaHome 'bin') + ';' + $env:PATH

        $downloadArgs = @{
            Uri = $resolvedPackageUrl
            OutFile = $apkPath
            UseBasicParsing = $true
            TimeoutSec = 120
        }
        if ($AuthHeaders.Count -gt 0) {
            $downloadArgs.Headers = $AuthHeaders
        }

        Invoke-WebRequest @downloadArgs | Out-Null

        $apkSignerOutput = & $apkSignerPath verify --print-certs $apkPath 2>&1
        $apkSignerExitCode = $LASTEXITCODE
        $apkSignerText = ($apkSignerOutput | Out-String -Width 4096)
        if ($apkSignerExitCode -ne 0) {
            return [pscustomobject]@{
                Success = $false
                Skipped = $false
                IsDebugSigning = $false
                CertificateDn = ""
                CertificateSha256 = ""
                Message = "apksigner verify 실패(exit=$apkSignerExitCode): $apkSignerText"
            }
        }

        $dnMatch = [regex]::Match($apkSignerText, 'Signer\s+#1\s+certificate\s+DN:\s*(?<value>.+)')
        $shaMatch = [regex]::Match($apkSignerText, 'Signer\s+#1\s+certificate\s+SHA-256\s+digest:\s*(?<value>[0-9a-fA-F]+)')
        $certificateDn = if ($dnMatch.Success) { $dnMatch.Groups['value'].Value.Trim() } else { "" }
        $certificateSha256 = if ($shaMatch.Success) { $shaMatch.Groups['value'].Value.Trim() } else { "" }
        $isDebugSigning =
            $certificateDn.IndexOf('CN=Android Debug', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $certificateDn.IndexOf('O=Android', [System.StringComparison]::OrdinalIgnoreCase) -ge 0

        return [pscustomobject]@{
            Success = $true
            Skipped = $false
            IsDebugSigning = $isDebugSigning
            CertificateDn = $certificateDn
            CertificateSha256 = $certificateSha256
            Message = if ($isDebugSigning) { "DEBUG_SIGNING" } else { "OK" }
        }
    }
    catch {
        return [pscustomobject]@{
            Success = $false
            Skipped = $false
            IsDebugSigning = $false
            CertificateDn = ""
            CertificateSha256 = ""
            Message = $_.Exception.Message
        }
    }
    finally {
        $env:JAVA_HOME = $previousJavaHome
        $env:PATH = $previousPath
        if (Test-Path -LiteralPath $apkPath) {
            Remove-Item -LiteralPath $apkPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Format-PackageObservationCell {
    param(
        [bool]$ProbeSkipped,
        [bool]$Ok,
        [int]$StatusCode,
        [string]$ProbeMode,
        [bool]$AuthUsed,
        [string]$Message
    )

    $packageMode = if ($AuthUsed) {
        "$ProbeMode, auth"
    }
    elseif ([string]::IsNullOrWhiteSpace($ProbeMode)) {
        '-'
    }
    else {
        $ProbeMode
    }

    if ($ProbeSkipped) {
        return "SKIP"
    }

    if ($Ok) {
        return "OK ($StatusCode; $packageMode)"
    }

    return "FAIL ($StatusCode; $packageMode) $Message"
}

function Get-JsonArrayCount {
    param(
        [string]$JsonContent
    )

    if ([string]::IsNullOrWhiteSpace($JsonContent)) {
        return 0
    }

    try {
        $parsed = $JsonContent | ConvertFrom-Json
        if ($null -eq $parsed) {
            return 0
        }

        return ($parsed | Measure-Object).Count
    }
    catch {
        return -1
    }
}

function Resolve-MarkdownResultStatus {
    param(
        [string]$ReportPath,
        [string]$DefaultStatus = 'OK'
    )

    if ([string]::IsNullOrWhiteSpace($ReportPath) -or -not (Test-Path -LiteralPath $ReportPath)) {
        return $DefaultStatus
    }

    $content = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8
    $match = [regex]::Match($content, '-\s*결과:\s*\*\*(?<status>[^*]+)\*\*')
    if ($match.Success) {
        return $match.Groups['status'].Value.Trim()
    }

    return $DefaultStatus
}

function Test-AuthenticatedDataProbe {
    param(
        [string]$BaseUrl,
        [hashtable]$AuthHeaders = @{}
    )

    if ($AuthHeaders.Count -eq 0) {
        return [pscustomobject]@{
            Success = $true
            Skipped = $true
            CustomerCount = $null
            InvoiceCount = $null
            Message = "인증 정보가 없어 거래처/거래내역 데이터 점검을 건너뜀"
        }
    }

    $customersUri = $BaseUrl.TrimEnd('/') + '/customers?take=1'
    $invoicesUri = $BaseUrl.TrimEnd('/') + '/invoices?take=1'
    $customersResult = Invoke-WebProbe -Uri $customersUri -Method Get -Headers $AuthHeaders
    $invoicesResult = Invoke-WebProbe -Uri $invoicesUri -Method Get -Headers $AuthHeaders

    $customerCount = if ($customersResult.Success) { Get-JsonArrayCount -JsonContent $customersResult.Content } else { -1 }
    $invoiceCount = if ($invoicesResult.Success) { Get-JsonArrayCount -JsonContent $invoicesResult.Content } else { -1 }
    $messages = New-Object System.Collections.Generic.List[string]

    if (-not $customersResult.Success) {
        $messages.Add("거래처 조회 실패($($customersResult.StatusCode)): $($customersResult.Error)") | Out-Null
    }
    elseif ($customerCount -le 0) {
        $messages.Add("거래처 조회 결과 0건") | Out-Null
    }

    if (-not $invoicesResult.Success) {
        $messages.Add("거래내역 조회 실패($($invoicesResult.StatusCode)): $($invoicesResult.Error)") | Out-Null
    }
    elseif ($invoiceCount -le 0) {
        $messages.Add("거래내역 조회 결과 0건") | Out-Null
    }

    $success = $messages.Count -eq 0
    return [pscustomobject]@{
        Success = $success
        Skipped = $false
        CustomerCount = $customerCount
        InvoiceCount = $invoiceCount
        Message = if ($success) { "OK" } else { ($messages -join " / ") }
    }
}

function Test-LocalCacheConsistencyProbe {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$ProbeUsername,
        [string]$ProbePassword,
        [string]$BearerToken,
        [string]$LocalCacheAppDataRoot,
        [string]$LocalCacheEvidenceDirectory,
        [bool]$SkipLocalCacheConsistencyCheck
    )

    if ($SkipLocalCacheConsistencyCheck) {
        return [pscustomobject]@{
            Success = $true
            Skipped = $true
            Message = '사용자 옵션으로 로컬 캐시 검증을 건너뜀'
            Report = ''
        }
    }

    if ([string]::IsNullOrWhiteSpace($LocalCacheAppDataRoot)) {
        return [pscustomobject]@{
            Success = $true
            Skipped = $true
            Message = 'LocalCacheAppDataRoot가 지정되지 않아 로컬 캐시 검증을 건너뜀'
            Report = ''
        }
    }

    $checkerPath = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanLocalCacheConsistency.ps1'
    if (-not (Test-Path -LiteralPath $checkerPath)) {
        return [pscustomobject]@{
            Success = $false
            Skipped = $false
            Message = "로컬 캐시 검증 스크립트를 찾지 못했습니다: $checkerPath"
            Report = ''
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $LocalCacheAppDataRoot 'data\거래플랜.db'))) {
        return [pscustomobject]@{
            Success = $false
            Skipped = $false
            Message = "로컬 캐시 DB를 찾지 못했습니다: $LocalCacheAppDataRoot"
            Report = ''
        }
    }

    if ([string]::IsNullOrWhiteSpace($BearerToken) -and ([string]::IsNullOrWhiteSpace($ProbeUsername) -or [string]::IsNullOrWhiteSpace($ProbePassword))) {
        return [pscustomobject]@{
            Success = $false
            Skipped = $false
            Message = '로컬 캐시 검증에는 BearerToken 또는 ProbeUsername/ProbePassword가 필요합니다.'
            Report = ''
        }
    }

    if ([string]::IsNullOrWhiteSpace($LocalCacheEvidenceDirectory)) {
        $LocalCacheEvidenceDirectory = Join-Path $ProjectRoot 'output\local-cache-consistency-observation'
    }

    try {
        $arguments = @{
            BaseUrl = $BaseUrl
            AppDataRoot = $LocalCacheAppDataRoot
            EvidenceDirectory = $LocalCacheEvidenceDirectory
            FailOnCountMismatch = $true
        }

        if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
            $arguments.BearerToken = $BearerToken
        }
        else {
            $arguments.Username = $ProbeUsername
            $arguments.Password = $ProbePassword
        }

        $output = & $checkerPath @arguments 2>&1
        $statusLine = @($output | Where-Object { ([string]$_).StartsWith('Local cache consistency:') } | Select-Object -Last 1)
        $cacheStatus = if ($statusLine.Count -gt 0) { ([string]$statusLine[0]).Substring('Local cache consistency:'.Length).Trim() } else { 'OK' }
        $reportLine = @($output | Where-Object { ([string]$_).StartsWith('Report:') } | Select-Object -Last 1)
        $reportPath = if ($reportLine.Count -gt 0) { ([string]$reportLine[0]).Substring('Report:'.Length).Trim() } else { '' }
        if ([string]::IsNullOrWhiteSpace($reportPath) -and (Test-Path -LiteralPath $LocalCacheEvidenceDirectory)) {
            $latestReport = Get-ChildItem -LiteralPath $LocalCacheEvidenceDirectory -Filter 'local-cache-consistency-*.md' -File |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if ($null -ne $latestReport) {
                $reportPath = $latestReport.FullName
            }
        }
        $cacheStatus = Resolve-MarkdownResultStatus -ReportPath $reportPath -DefaultStatus $cacheStatus

        return [pscustomobject]@{
            Success = $true
            Skipped = $false
            Message = $cacheStatus
            Report = $reportPath
        }
    }
    catch {
        return [pscustomobject]@{
            Success = $false
            Skipped = $false
            Message = $_.Exception.Message
            Report = ''
        }
    }
}

if ($SampleCount -lt 1) {
    throw "SampleCount는 1 이상이어야 합니다."
}

if ($IntervalSeconds -lt 1) {
    throw "IntervalSeconds는 1 이상이어야 합니다."
}

$resolvedBaseUrl = Resolve-AppBaseUrl -ProjectRoot $ProjectRoot -ExplicitBaseUrl $BaseUrl
$manifestUrl = "$resolvedBaseUrl/updates/manifest?channel=$Channel"
$healthUrl = "$resolvedBaseUrl/healthz"
$packageAuthHeaders = Get-ObservationAuthHeaders -BaseUrl $resolvedBaseUrl -ProbeUsername $ProbeUsername -ProbePassword $ProbePassword -BearerToken $BearerToken
$packageProbeAuthMode = if ($packageAuthHeaders.Count -gt 0) { 'authenticated-fallback-enabled' } else { 'anonymous-only' }

$samples = New-Object System.Collections.Generic.List[object]

for ($index = 1; $index -le $SampleCount; $index++) {
    $sampledAt = Get-Date
    $healthResult = Invoke-WebProbe -Uri $healthUrl -Method Get
    $manifestResult = if ($SkipManifestProbe) {
        [pscustomobject]@{
            Success = $true
            StatusCode = 0
            Content = ""
            Error = "manifest 점검 건너뜀"
            Skipped = $true
        }
    }
    else {
        Invoke-WebProbe -Uri $manifestUrl -Method Get
    }

    $desktopVersion = ""
    $desktopPackageUrl = ""
    $desktopMinimumSupportedVersion = ""
    $androidVersion = ""
    $androidPackageUrl = ""
    $androidMinimumSupportedVersion = ""
    if ($manifestResult.Success -and -not [string]::IsNullOrWhiteSpace($manifestResult.Content)) {
        try {
            $manifest = $manifestResult.Content | ConvertFrom-Json
            $desktopVersion = [string]$manifest.desktop.version
            $desktopPackageUrl = [string]$manifest.desktop.packageUrl
            $desktopMinimumSupportedVersion = [string]$manifest.desktop.minimumSupportedVersion
            $androidVersion = [string]$manifest.android.version
            $androidPackageUrl = [string]$manifest.android.packageUrl
            $androidMinimumSupportedVersion = [string]$manifest.android.minimumSupportedVersion
        }
        catch {
            $manifestResult = [pscustomobject]@{
                Success = $false
                StatusCode = $manifestResult.StatusCode
                Content = $manifestResult.Content
                Error = "manifest JSON 파싱 실패: $($_.Exception.Message)"
            }
        }
    }

    $skippedPackageResult = [pscustomobject]@{
        Success = $true
        StatusCode = 0
        Content = ""
        Error = "패키지 점검 건너뜀"
        ProbeMode = 'skip'
        AuthUsed = $false
        Skipped = $true
    }

    $desktopPackageResult = if ($SkipPackageProbe) {
        $skippedPackageResult
    }
    else {
        Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $desktopPackageUrl -AuthHeaders $packageAuthHeaders
    }

    $androidPackageResult = if ($SkipPackageProbe) {
        $skippedPackageResult
    }
    else {
        Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $androidPackageUrl -AuthHeaders $packageAuthHeaders
    }

    $dataResult = Test-AuthenticatedDataProbe -BaseUrl $resolvedBaseUrl -AuthHeaders $packageAuthHeaders

    $samples.Add([pscustomobject]@{
        Index = $index
        SampledAt = $sampledAt
        HealthOk = $healthResult.Success
        HealthStatusCode = $healthResult.StatusCode
        HealthMessage = if ($healthResult.Success) { "OK" } else { $healthResult.Error }
        ManifestOk = $manifestResult.Success
        ManifestStatusCode = $manifestResult.StatusCode
        ManifestMessage = if ($manifestResult.Success) { "OK" } else { $manifestResult.Error }
        ManifestProbeSkipped = [bool]$manifestResult.Skipped
        DesktopVersion = $desktopVersion
        DesktopMinimumSupportedVersion = $desktopMinimumSupportedVersion
        AndroidVersion = $androidVersion
        AndroidMinimumSupportedVersion = $androidMinimumSupportedVersion
        DesktopPackageUrl = $desktopPackageUrl
        AndroidPackageUrl = $androidPackageUrl
        DesktopPackageOk = $desktopPackageResult.Success
        DesktopPackageStatusCode = $desktopPackageResult.StatusCode
        DesktopPackageMessage = if ($desktopPackageResult.Success) { "OK" } else { $desktopPackageResult.Error }
        DesktopPackageProbeMode = [string]$desktopPackageResult.ProbeMode
        DesktopPackageAuthUsed = [bool]$desktopPackageResult.AuthUsed
        DesktopPackageProbeSkipped = [bool]$desktopPackageResult.Skipped
        AndroidPackageOk = $androidPackageResult.Success
        AndroidPackageStatusCode = $androidPackageResult.StatusCode
        AndroidPackageMessage = if ($androidPackageResult.Success) { "OK" } else { $androidPackageResult.Error }
        AndroidPackageProbeMode = [string]$androidPackageResult.ProbeMode
        AndroidPackageAuthUsed = [bool]$androidPackageResult.AuthUsed
        AndroidPackageProbeSkipped = [bool]$androidPackageResult.Skipped
        DataOk = [bool]$dataResult.Success
        DataProbeSkipped = [bool]$dataResult.Skipped
        CustomerProbeCount = $dataResult.CustomerCount
        InvoiceProbeCount = $dataResult.InvoiceCount
        DataMessage = [string]$dataResult.Message
    }) | Out-Null

    if ($index -lt $SampleCount) {
        Start-Sleep -Seconds $IntervalSeconds
    }
}

$latestAndroidPackageUrl = @($samples |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.AndroidPackageUrl) } |
        Select-Object -ExpandProperty AndroidPackageUrl -Last 1)
$androidSigningResult = if ($SkipPackageProbe -or $SkipManifestProbe) {
    [pscustomobject]@{
        Success = $true
        Skipped = $true
        IsDebugSigning = $false
        CertificateDn = ""
        CertificateSha256 = ""
        Message = "manifest/package 점검이 skip되어 Android APK signing 점검을 건너뜀"
    }
}
else {
    Test-AndroidApkSigningProbe -ProjectRoot $ProjectRoot -BaseUrl $resolvedBaseUrl -AndroidPackageUrl ([string]$latestAndroidPackageUrl) -AuthHeaders $packageAuthHeaders -SkipAndroidSigningProbe ([bool]$SkipAndroidSigningProbe)
}

$localCacheResult = Test-LocalCacheConsistencyProbe -ProjectRoot $ProjectRoot -BaseUrl $resolvedBaseUrl -ProbeUsername $ProbeUsername -ProbePassword $ProbePassword -BearerToken $BearerToken -LocalCacheAppDataRoot $LocalCacheAppDataRoot -LocalCacheEvidenceDirectory $LocalCacheEvidenceDirectory -SkipLocalCacheConsistencyCheck ([bool]$SkipLocalCacheConsistencyCheck)

$failedSamples = $samples | Where-Object { -not $_.HealthOk -or -not $_.ManifestOk -or -not $_.DesktopPackageOk -or -not $_.AndroidPackageOk -or -not $_.DataOk }
$warningMessages = New-Object System.Collections.Generic.List[string]
if (-not $androidSigningResult.Skipped -and -not $androidSigningResult.Success) {
    $warningMessages.Add("Android APK signing 점검 실패: $($androidSigningResult.Message)") | Out-Null
}
if (-not $androidSigningResult.Skipped -and $androidSigningResult.IsDebugSigning) {
    $warningMessages.Add("Android APK가 debug signing 인증서로 서명되어 있습니다: $($androidSigningResult.CertificateDn)") | Out-Null
}
$androidSigningFailure = $FailOnAndroidDebugSigning -and -not $androidSigningResult.Skipped -and $androidSigningResult.IsDebugSigning
$overallStatus = if ($failedSamples.Count -gt 0 -or -not $localCacheResult.Success -or $androidSigningFailure) {
    "FAIL"
}
elseif ($warningMessages.Count -gt 0) {
    "WARN"
}
else {
    "PASS"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $reportDirectory = Join-Path $ProjectRoot "테스트 시행\기록"
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $OutputPath = Join-Path $reportDirectory ("live-observation-{0}.md" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}
else {
    $reportDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# live 관찰 점검 리포트") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
$lines.Add("- 결과: **$overallStatus**") | Out-Null
$lines.Add("- BaseUrl: $resolvedBaseUrl") | Out-Null
$lines.Add("- 채널: $Channel") | Out-Null
$lines.Add("- 샘플 수: $SampleCount") | Out-Null
$lines.Add("- 샘플 간격(초): $IntervalSeconds") | Out-Null
$lines.Add("- package probe 모드: $packageProbeAuthMode") | Out-Null
$lines.Add("- manifest probe skip: $([bool]$SkipManifestProbe)") | Out-Null
$lines.Add("- package probe skip: $([bool]$SkipPackageProbe)") | Out-Null
$androidSigningSummary = if ($androidSigningResult.Skipped) {
    "SKIP - $($androidSigningResult.Message)"
}
elseif ($androidSigningResult.Success) {
    if ($androidSigningResult.IsDebugSigning) {
        "WARN - debug signing, DN=$($androidSigningResult.CertificateDn), SHA256=$($androidSigningResult.CertificateSha256)"
    }
    else {
        "OK - DN=$($androidSigningResult.CertificateDn), SHA256=$($androidSigningResult.CertificateSha256)"
    }
}
else {
    "WARN - $($androidSigningResult.Message)"
}
$lines.Add("- Android APK signing 점검: $androidSigningSummary") | Out-Null
$localCacheSummary = if ($localCacheResult.Skipped) { "SKIP - $($localCacheResult.Message)" } elseif ($localCacheResult.Success) { "$($localCacheResult.Message) - $($localCacheResult.Report)" } else { "FAIL - $($localCacheResult.Message)" }
$lines.Add("- 로컬 캐시 점검: $localCacheSummary") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("| 회차 | 시각 | healthz | manifest | desktop 버전 | android 버전 | desktop package | android package | 거래처/거래내역 | desktop packageUrl | android packageUrl |") | Out-Null
$lines.Add("| ---: | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null

foreach ($sample in $samples) {
    $healthCell = if ($sample.HealthOk) { "OK ($($sample.HealthStatusCode))" } else { "FAIL ($($sample.HealthStatusCode)) $($sample.HealthMessage)" }
    $manifestCell = if ($sample.ManifestProbeSkipped) { "SKIP" } elseif ($sample.ManifestOk) { "OK ($($sample.ManifestStatusCode))" } else { "FAIL ($($sample.ManifestStatusCode)) $($sample.ManifestMessage)" }
    $desktopPackageCell = Format-PackageObservationCell -ProbeSkipped $sample.DesktopPackageProbeSkipped -Ok $sample.DesktopPackageOk -StatusCode $sample.DesktopPackageStatusCode -ProbeMode $sample.DesktopPackageProbeMode -AuthUsed $sample.DesktopPackageAuthUsed -Message $sample.DesktopPackageMessage
    $androidPackageCell = Format-PackageObservationCell -ProbeSkipped $sample.AndroidPackageProbeSkipped -Ok $sample.AndroidPackageOk -StatusCode $sample.AndroidPackageStatusCode -ProbeMode $sample.AndroidPackageProbeMode -AuthUsed $sample.AndroidPackageAuthUsed -Message $sample.AndroidPackageMessage
    $dataCell = if ($sample.DataProbeSkipped) {
        "SKIP"
    }
    elseif ($sample.DataOk) {
        "OK (거래처 $($sample.CustomerProbeCount), 거래내역 $($sample.InvoiceProbeCount))"
    }
    else {
        "FAIL $($sample.DataMessage)"
    }
    $desktopPackageUrlCell = if ([string]::IsNullOrWhiteSpace($sample.DesktopPackageUrl)) { "-" } else { $sample.DesktopPackageUrl.Replace("|", "\|") }
    $androidPackageUrlCell = if ([string]::IsNullOrWhiteSpace($sample.AndroidPackageUrl)) { "-" } else { $sample.AndroidPackageUrl.Replace("|", "\|") }
    $lines.Add("| $($sample.Index) | $($sample.SampledAt.ToString('yyyy-MM-dd HH:mm:ss')) | $healthCell | $manifestCell | $($sample.DesktopVersion) | $($sample.AndroidVersion) | $desktopPackageCell | $androidPackageCell | $dataCell | $desktopPackageUrlCell | $androidPackageUrlCell |") | Out-Null
}

$lines.Add("") | Out-Null
$lines.Add("## 로컬 캐시 점검") | Out-Null
$lines.Add("") | Out-Null
if ($localCacheResult.Skipped) {
    $lines.Add("- SKIP: $($localCacheResult.Message)") | Out-Null
}
elseif ($localCacheResult.Success) {
    if ([string]::Equals($localCacheResult.Message, 'WARN', [System.StringComparison]::OrdinalIgnoreCase)) {
        $lines.Add("- WARN: 핵심 목록은 조회 가능하지만 일부 비핵심/범위 차이 항목이 확인되었습니다. 세부 리포트를 확인하세요.") | Out-Null
    }
    else {
        $lines.Add("- PASS: 서버 pull과 지정한 PC 로컬 캐시의 핵심 목록 건수가 일치했습니다.") | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($localCacheResult.Report)) {
        $lines.Add("- 세부 리포트: $($localCacheResult.Report)") | Out-Null
    }
}
else {
    $lines.Add("- FAIL: $($localCacheResult.Message)") | Out-Null
}
$lines.Add("") | Out-Null
$lines.Add("## 판정") | Out-Null
$lines.Add("") | Out-Null
if ($overallStatus -eq "PASS") {
    if ($SkipPackageProbe -and $SkipManifestProbe) {
        $lines.Add("- healthz와 인증 데이터 조회가 정상 응답했습니다. 테스트 실행환경 기준으로 manifest/package 다운로드 경로 점검은 옵션으로 건너뛰었습니다.") | Out-Null
    }
    elseif ($SkipPackageProbe) {
        $lines.Add("- healthz, manifest가 정상 응답했습니다. desktop/android package 다운로드 경로 점검은 옵션으로 건너뛰었습니다.") | Out-Null
    }
    else {
        $lines.Add("- healthz, manifest, desktop/android package 다운로드 경로가 모두 정상 응답했습니다.") | Out-Null
    }
    $lines.Add("- 인증 정보를 제공한 경우 거래처/거래내역 조회도 0건이 아닌지 함께 확인했습니다.") | Out-Null
    $lines.Add("- 로컬 캐시 점검을 요청한 경우 서버 데이터와 PC 로컬 캐시 핵심 목록도 함께 확인했습니다.") | Out-Null
    if ($SkipPackageProbe) {
        $lines.Add("- 테스트 실행환경의 서버/데이터/로컬 캐시 관찰 기준은 충족했습니다.") | Out-Null
    }
    else {
        $lines.Add("- live 반영 직후 최소한의 관찰 기준은 충족했습니다.") | Out-Null
    }
}
elseif ($overallStatus -eq "WARN") {
    $lines.Add("- 핵심 응답은 성공했지만 아래 경고가 확인되었습니다.") | Out-Null
    foreach ($warningMessage in $warningMessages) {
        $lines.Add("- WARN: $warningMessage") | Out-Null
    }
}
else {
    $lines.Add("- 일부 샘플에서 실패가 확인되었습니다.") | Out-Null
    $lines.Add("- Linux PC live 반영 후 실제 사용자 안내 전에 서버/manifest/desktop package/android package/거래처/거래내역 경로를 다시 점검하세요.") | Out-Null
}

[System.IO.File]::WriteAllText(
    $OutputPath,
    ($lines -join [Environment]::NewLine),
    (New-Object System.Text.UTF8Encoding($true)))

Write-Host "live 관찰 리포트 저장: $OutputPath"
Write-Host "결과: $overallStatus"

if ($overallStatus -eq "FAIL") {
    throw "live 관찰 점검에서 실패가 확인되었습니다. 리포트: $OutputPath"
}
