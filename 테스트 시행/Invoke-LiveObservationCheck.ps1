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
    [switch]$SkipPackageProbe,
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
        }

        if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
            $arguments.BearerToken = $BearerToken
        }
        else {
            $arguments.Username = $ProbeUsername
            $arguments.Password = $ProbePassword
        }

        $output = & $checkerPath @arguments 2>&1
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

        return [pscustomobject]@{
            Success = $true
            Skipped = $false
            Message = 'OK'
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
    $manifestResult = Invoke-WebProbe -Uri $manifestUrl -Method Get

    $desktopVersion = ""
    $packageUrl = ""
    $minimumSupportedVersion = ""
    if ($manifestResult.Success -and -not [string]::IsNullOrWhiteSpace($manifestResult.Content)) {
        try {
            $manifest = $manifestResult.Content | ConvertFrom-Json
            $desktopVersion = [string]$manifest.desktop.version
            $packageUrl = [string]$manifest.desktop.packageUrl
            $minimumSupportedVersion = [string]$manifest.desktop.minimumSupportedVersion
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

    $packageResult = if ($SkipPackageProbe) {
        [pscustomobject]@{
            Success = $true
            StatusCode = 0
            Content = ""
            Error = "패키지 점검 건너뜀"
            ProbeMode = 'skip'
            AuthUsed = $false
            Skipped = $true
        }
    }
    else {
        Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $packageUrl -AuthHeaders $packageAuthHeaders
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
        DesktopVersion = $desktopVersion
        MinimumSupportedVersion = $minimumSupportedVersion
        PackageUrl = $packageUrl
        PackageOk = $packageResult.Success
        PackageStatusCode = $packageResult.StatusCode
        PackageMessage = if ($packageResult.Success) { "OK" } else { $packageResult.Error }
        PackageProbeMode = [string]$packageResult.ProbeMode
        PackageAuthUsed = [bool]$packageResult.AuthUsed
        PackageProbeSkipped = [bool]$packageResult.Skipped
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

$localCacheResult = Test-LocalCacheConsistencyProbe -ProjectRoot $ProjectRoot -BaseUrl $resolvedBaseUrl -ProbeUsername $ProbeUsername -ProbePassword $ProbePassword -BearerToken $BearerToken -LocalCacheAppDataRoot $LocalCacheAppDataRoot -LocalCacheEvidenceDirectory $LocalCacheEvidenceDirectory -SkipLocalCacheConsistencyCheck ([bool]$SkipLocalCacheConsistencyCheck)

$failedSamples = $samples | Where-Object { -not $_.HealthOk -or -not $_.ManifestOk -or -not $_.PackageOk -or -not $_.DataOk }
$overallStatus = if ($failedSamples.Count -eq 0 -and $localCacheResult.Success) { "PASS" } else { "FAIL" }

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
$lines.Add("- package probe skip: $([bool]$SkipPackageProbe)") | Out-Null
$localCacheSummary = if ($localCacheResult.Skipped) { "SKIP - $($localCacheResult.Message)" } elseif ($localCacheResult.Success) { "OK - $($localCacheResult.Report)" } else { "FAIL - $($localCacheResult.Message)" }
$lines.Add("- 로컬 캐시 점검: $localCacheSummary") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("| 회차 | 시각 | healthz | manifest | desktop 버전 | minimumSupportedVersion | package | 거래처/거래내역 | packageUrl |") | Out-Null
$lines.Add("| ---: | --- | --- | --- | --- | --- | --- | --- | --- |") | Out-Null

foreach ($sample in $samples) {
    $healthCell = if ($sample.HealthOk) { "OK ($($sample.HealthStatusCode))" } else { "FAIL ($($sample.HealthStatusCode)) $($sample.HealthMessage)" }
    $manifestCell = if ($sample.ManifestOk) { "OK ($($sample.ManifestStatusCode))" } else { "FAIL ($($sample.ManifestStatusCode)) $($sample.ManifestMessage)" }
    $packageMode = if ($sample.PackageAuthUsed) { "$($sample.PackageProbeMode), auth" } elseif ([string]::IsNullOrWhiteSpace($sample.PackageProbeMode)) { '-' } else { $sample.PackageProbeMode }
    $packageCell = if ($sample.PackageProbeSkipped) { "SKIP" } elseif ($sample.PackageOk) { "OK ($($sample.PackageStatusCode); $packageMode)" } else { "FAIL ($($sample.PackageStatusCode); $packageMode) $($sample.PackageMessage)" }
    $dataCell = if ($sample.DataProbeSkipped) {
        "SKIP"
    }
    elseif ($sample.DataOk) {
        "OK (거래처 $($sample.CustomerProbeCount), 거래내역 $($sample.InvoiceProbeCount))"
    }
    else {
        "FAIL $($sample.DataMessage)"
    }
    $packageUrlCell = if ([string]::IsNullOrWhiteSpace($sample.PackageUrl)) { "-" } else { $sample.PackageUrl.Replace("|", "\|") }
    $lines.Add("| $($sample.Index) | $($sample.SampledAt.ToString('yyyy-MM-dd HH:mm:ss')) | $healthCell | $manifestCell | $($sample.DesktopVersion) | $($sample.MinimumSupportedVersion) | $packageCell | $dataCell | $packageUrlCell |") | Out-Null
}

$lines.Add("") | Out-Null
$lines.Add("## 로컬 캐시 점검") | Out-Null
$lines.Add("") | Out-Null
if ($localCacheResult.Skipped) {
    $lines.Add("- SKIP: $($localCacheResult.Message)") | Out-Null
}
elseif ($localCacheResult.Success) {
    $lines.Add("- PASS: 서버 pull과 지정한 PC 로컬 캐시의 핵심 목록 건수가 일치했습니다.") | Out-Null
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
if ($failedSamples.Count -eq 0 -and $localCacheResult.Success) {
    if ($SkipPackageProbe) {
        $lines.Add("- healthz, manifest가 정상 응답했습니다. package 다운로드 경로 점검은 옵션으로 건너뛰었습니다.") | Out-Null
    }
    else {
        $lines.Add("- healthz, manifest, package 다운로드 경로가 모두 정상 응답했습니다.") | Out-Null
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
else {
    $lines.Add("- 일부 샘플에서 실패가 확인되었습니다.") | Out-Null
    $lines.Add("- NAS 반영 후 실제 사용자 안내 전에 서버/manifest/package/거래처/거래내역 경로를 다시 점검하세요.") | Out-Null
}

[System.IO.File]::WriteAllText(
    $OutputPath,
    ($lines -join [Environment]::NewLine),
    (New-Object System.Text.UTF8Encoding($true)))

Write-Host "live 관찰 리포트 저장: $OutputPath"
Write-Host "결과: $overallStatus"

if ($overallStatus -ne "PASS") {
    throw "live 관찰 점검에서 실패가 확인되었습니다. 리포트: $OutputPath"
}
