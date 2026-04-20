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

    $packageResult = Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $packageUrl -AuthHeaders $packageAuthHeaders

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
    }) | Out-Null

    if ($index -lt $SampleCount) {
        Start-Sleep -Seconds $IntervalSeconds
    }
}

$failedSamples = $samples | Where-Object { -not $_.HealthOk -or -not $_.ManifestOk -or -not $_.PackageOk }
$overallStatus = if ($failedSamples.Count -eq 0) { "PASS" } else { "FAIL" }

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
$lines.Add("") | Out-Null
$lines.Add("| 회차 | 시각 | healthz | manifest | desktop 버전 | minimumSupportedVersion | package | packageUrl |") | Out-Null
$lines.Add("| ---: | --- | --- | --- | --- | --- | --- | --- |") | Out-Null

foreach ($sample in $samples) {
    $healthCell = if ($sample.HealthOk) { "OK ($($sample.HealthStatusCode))" } else { "FAIL ($($sample.HealthStatusCode)) $($sample.HealthMessage)" }
    $manifestCell = if ($sample.ManifestOk) { "OK ($($sample.ManifestStatusCode))" } else { "FAIL ($($sample.ManifestStatusCode)) $($sample.ManifestMessage)" }
    $packageMode = if ($sample.PackageAuthUsed) { "$($sample.PackageProbeMode), auth" } elseif ([string]::IsNullOrWhiteSpace($sample.PackageProbeMode)) { '-' } else { $sample.PackageProbeMode }
    $packageCell = if ($sample.PackageOk) { "OK ($($sample.PackageStatusCode); $packageMode)" } else { "FAIL ($($sample.PackageStatusCode); $packageMode) $($sample.PackageMessage)" }
    $packageUrlCell = if ([string]::IsNullOrWhiteSpace($sample.PackageUrl)) { "-" } else { $sample.PackageUrl.Replace("|", "\|") }
    $lines.Add("| $($sample.Index) | $($sample.SampledAt.ToString('yyyy-MM-dd HH:mm:ss')) | $healthCell | $manifestCell | $($sample.DesktopVersion) | $($sample.MinimumSupportedVersion) | $packageCell | $packageUrlCell |") | Out-Null
}

$lines.Add("") | Out-Null
$lines.Add("## 판정") | Out-Null
$lines.Add("") | Out-Null
if ($failedSamples.Count -eq 0) {
    $lines.Add("- healthz, manifest, package 다운로드 경로가 모두 정상 응답했습니다.") | Out-Null
    $lines.Add("- live 반영 직후 최소한의 관찰 기준은 충족했습니다.") | Out-Null
}
else {
    $lines.Add("- 일부 샘플에서 실패가 확인되었습니다.") | Out-Null
    $lines.Add("- NAS 반영 후 실제 사용자 안내 전에 서버/manifest/package 경로를 다시 점검하세요.") | Out-Null
}

[System.IO.File]::WriteAllText(
    $OutputPath,
    ($lines -join [Environment]::NewLine),
    (New-Object System.Text.UTF8Encoding($true)))

Write-Host "live 관찰 리포트 저장: $OutputPath"
Write-Host "결과: $overallStatus"

if ($failedSamples.Count -gt 0) {
    throw "live 관찰 점검에서 실패가 확인되었습니다. 리포트: $OutputPath"
}
