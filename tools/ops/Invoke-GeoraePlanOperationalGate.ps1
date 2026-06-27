[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$BaseUrl = "https://trade.2884.kr",
    [string]$Channel = "stable",
    [string]$SecretPath = "D:\거래플랜-운영검증-secrets.json",
    [string]$ApprovedTargetsPath = "",
    [string]$PlatformStateRoot = "",
    [string]$OutputDirectory = "",
    [switch]$FailOnIntegrityWarnings,
    [string[]]$AllowedIntegrityWarningCodes = @(),
    [switch]$SkipWriteSafetyChecks,
    [switch]$UseEphemeralOperationalWrites,
    [switch]$AllowOperationalWrites
)

$ErrorActionPreference = "Stop"

function Resolve-ProjectRoot {
    param([string]$ExplicitProjectRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitProjectRoot)) {
        return (Resolve-Path -LiteralPath $ExplicitProjectRoot).Path
    }

    $scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    return (Resolve-Path -LiteralPath (Join-Path $scriptRoot '..\..')).Path
}

function Get-JsonPropertyValue {
    param($Object, [string]$Name)

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function First-NonEmpty {
    param([string[]]$Values)

    foreach ($value in $Values) {
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return ""
}

function Mask-Value {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "missing"
    }

    $trimmed = $Value.Trim()
    if ($trimmed.Length -le 2) {
        return "**"
    }

    return ($trimmed.Substring(0, 1) + "***" + $trimmed.Substring($trimmed.Length - 1, 1))
}

function Add-Check {
    param(
        [System.Collections.Generic.List[object]]$Checks,
        [string]$Name,
        [ValidateSet('PASS','WARN','BLOCKED','FAIL')][string]$Status,
        [string]$Detail
    )

    $Checks.Add([pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
    }) | Out-Null
}

function Resolve-MarkdownResultStatus {
    param(
        [string]$ReportPath,
        [string]$DefaultStatus = 'PASS'
    )

    if ([string]::IsNullOrWhiteSpace($ReportPath) -or -not (Test-Path -LiteralPath $ReportPath)) {
        return $DefaultStatus
    }

    $content = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8
    $match = [regex]::Match($content, '-\s*결과:\s*\*\*(?<status>[^*]+)\*\*')
    if ($match.Success) {
        $status = $match.Groups['status'].Value.Trim().ToUpperInvariant()
        if ($status -in @('PASS', 'WARN', 'BLOCKED', 'FAIL')) {
            return $status
        }
    }

    return $DefaultStatus
}

function Invoke-TextProbe {
    param([string]$Uri)

    try {
        $started = Get-Date
        $response = Invoke-WebRequest -Uri $Uri -Method Get -UseBasicParsing -TimeoutSec 20
        $elapsedMs = [int]((Get-Date) - $started).TotalMilliseconds
        return [pscustomobject]@{
            Success = $true
            StatusCode = [int]$response.StatusCode
            ElapsedMs = $elapsedMs
            Content = [string]$response.Content
            Error = ""
        }
    }
    catch {
        $statusCode = 0
        if ($_.Exception.Response) {
            try { $statusCode = [int]$_.Exception.Response.StatusCode } catch { $statusCode = 0 }
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $statusCode
            ElapsedMs = 0
            Content = ""
            Error = $_.Exception.Message
        }
    }
}

function Test-ReadyProbeSemantic {
    param($Probe)

    if (-not $Probe.Success -or $Probe.StatusCode -ne 200) {
        return [pscustomobject]@{
            Status = 'FAIL'
            Detail = ("status={0}, error={1}" -f $Probe.StatusCode, $Probe.Error)
        }
    }

    try {
        $readyJson = $Probe.Content | ConvertFrom-Json
        $status = [string](Get-JsonPropertyValue -Object $readyJson -Name 'status')
        $databaseInitialization = Get-JsonPropertyValue -Object $readyJson -Name 'databaseInitialization'
        $dbStarted = Get-JsonPropertyValue -Object $databaseInitialization -Name 'started'
        $dbCompleted = Get-JsonPropertyValue -Object $databaseInitialization -Name 'completed'
        $dbFailed = Get-JsonPropertyValue -Object $databaseInitialization -Name 'failed'

        if ($status -eq 'ready' -and $dbStarted -eq $true -and $dbCompleted -eq $true -and $dbFailed -eq $false) {
            return [pscustomobject]@{
                Status = 'PASS'
                Detail = ("200 OK, {0}ms, status=ready, databaseInitialization.started=true/completed=true/failed=false" -f $Probe.ElapsedMs)
            }
        }

        return [pscustomobject]@{
            Status = 'FAIL'
            Detail = ("200 OK but readiness body is not ready: status={0}, databaseInitialization.started={1}, completed={2}, failed={3}" -f $status, $dbStarted, $dbCompleted, $dbFailed)
        }
    }
    catch {
        return [pscustomobject]@{
            Status = 'FAIL'
            Detail = ("200 OK but readyz body parse failed: {0}" -f $_.Exception.Message)
        }
    }
}

function Invoke-ReadyProbeWithRetry {
    param(
        [string]$Uri,
        [string]$LogPath,
        [int]$TimeoutSec = 60,
        [int]$DelaySec = 3
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $attempt = 0
    $lastProbe = $null
    $lastSemanticResult = $null

    do {
        $attempt += 1
        $lastProbe = Invoke-TextProbe -Uri $Uri
        $lastSemanticResult = Test-ReadyProbeSemantic -Probe $lastProbe
        Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value ("readyz attempt={0} semantic={1} status={2} error={3} body={4}" -f $attempt, $lastSemanticResult.Status, $lastProbe.StatusCode, $lastProbe.Error, $lastProbe.Content)

        if ($lastSemanticResult.Status -eq 'PASS') {
            $lastSemanticResult.Detail = ("{0}; attempts={1}" -f $lastSemanticResult.Detail, $attempt)
            return [pscustomobject]@{
                Probe = $lastProbe
                SemanticResult = $lastSemanticResult
                Attempts = $attempt
            }
        }

        if ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds $DelaySec
        }
    } while ((Get-Date) -lt $deadline)

    if ($null -ne $lastSemanticResult) {
        $lastSemanticResult.Detail = ("{0}; attempts={1}; waited up to {2}s" -f $lastSemanticResult.Detail, $attempt, $TimeoutSec)
    }

    return [pscustomobject]@{
        Probe = $lastProbe
        SemanticResult = $lastSemanticResult
        Attempts = $attempt
    }
}

function Resolve-AbsolutePackageUri {
    param(
        [string]$BaseUrl,
        [string]$PackageUrl
    )

    if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
        return ""
    }

    try {
        $baseUri = [Uri]::new($BaseUrl.TrimEnd('/') + '/')
        return ([Uri]::new($baseUri, $PackageUrl)).AbsoluteUri
    }
    catch {
        return ""
    }
}

function Invoke-UpdatePackageHeaderProbe {
    param(
        [ValidateSet('HEAD','GET')][string]$Method,
        [string]$Uri,
        [int]$TimeoutSec = 30
    )

    try {
        Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue | Out-Null

        $httpMethod = if ($Method -eq 'HEAD') {
            [System.Net.Http.HttpMethod]::Head
        }
        else {
            [System.Net.Http.HttpMethod]::Get
        }

        $client = [System.Net.Http.HttpClient]::new()
        $request = [System.Net.Http.HttpRequestMessage]::new($httpMethod, $Uri)
        $response = $null
        try {
            $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
            $started = Get-Date
            $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $elapsedMs = [int]((Get-Date) - $started).TotalMilliseconds
            $contentLength = $null
            if ($null -ne $response.Content.Headers.ContentLength) {
                $contentLength = [int64]$response.Content.Headers.ContentLength
            }

            return [pscustomobject]@{
                Success = [bool]$response.IsSuccessStatusCode
                StatusCode = [int]$response.StatusCode
                ContentLength = $contentLength
                ContentType = [string]$response.Content.Headers.ContentType
                ElapsedMs = $elapsedMs
                Error = ""
            }
        }
        finally {
            if ($null -ne $response) { $response.Dispose() }
            $request.Dispose()
            $client.Dispose()
        }
    }
    catch {
        $statusCode = 0
        try {
            if ($_.Exception.StatusCode) {
                $statusCode = [int]$_.Exception.StatusCode
            }
        }
        catch {
            $statusCode = 0
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $statusCode
            ContentLength = $null
            ContentType = ""
            ElapsedMs = 0
            Error = $_.Exception.Message
        }
    }
}

function Test-UpdatePackageDownloadHeaders {
    param(
        [string]$BaseUrl,
        [string]$Platform,
        $Package
    )

    $packageUrl = [string](Get-JsonPropertyValue -Object $Package -Name 'packageUrl')
    $version = [string](Get-JsonPropertyValue -Object $Package -Name 'version')
    $fileName = [string](Get-JsonPropertyValue -Object $Package -Name 'fileName')
    $expectedFileSize = 0L
    $fileSizeValue = Get-JsonPropertyValue -Object $Package -Name 'fileSize'
    [void][int64]::TryParse([string]$fileSizeValue, [ref]$expectedFileSize)
    $absoluteUri = Resolve-AbsolutePackageUri -BaseUrl $BaseUrl -PackageUrl $packageUrl
    $issues = New-Object System.Collections.Generic.List[string]

    if ([string]::IsNullOrWhiteSpace($absoluteUri)) {
        $issues.Add('packageUrl is missing or invalid') | Out-Null
        return [pscustomobject]@{
            Platform = $Platform
            Version = $version
            FileName = $fileName
            PackageUrl = $packageUrl
            AbsoluteUri = $absoluteUri
            ExpectedFileSize = $expectedFileSize
            HeadStatus = 0
            HeadContentLength = $null
            HeadContentType = ""
            HeadElapsedMs = 0
            GetStatus = 0
            GetContentLength = $null
            GetContentType = ""
            GetElapsedMs = 0
            Result = 'FAIL'
            Issues = @($issues)
        }
    }

    if ($expectedFileSize -le 0) {
        $issues.Add('manifest fileSize is missing or not positive') | Out-Null
    }

    $head = Invoke-UpdatePackageHeaderProbe -Method HEAD -Uri $absoluteUri
    $get = Invoke-UpdatePackageHeaderProbe -Method GET -Uri $absoluteUri

    if (-not $head.Success -or $head.StatusCode -ne 200) {
        $issues.Add(("HEAD status={0} error={1}" -f $head.StatusCode, $head.Error)) | Out-Null
    }

    if (-not $get.Success -or $get.StatusCode -ne 200) {
        $issues.Add(("GET status={0} error={1}" -f $get.StatusCode, $get.Error)) | Out-Null
    }

    if ($expectedFileSize -gt 0) {
        if ($null -eq $head.ContentLength -or [int64]$head.ContentLength -ne $expectedFileSize) {
            $issues.Add(("HEAD Content-Length={0}, manifest fileSize={1}" -f $head.ContentLength, $expectedFileSize)) | Out-Null
        }

        if ($null -eq $get.ContentLength -or [int64]$get.ContentLength -ne $expectedFileSize) {
            $issues.Add(("GET Content-Length={0}, manifest fileSize={1}" -f $get.ContentLength, $expectedFileSize)) | Out-Null
        }
    }

    return [pscustomobject]@{
        Platform = $Platform
        Version = $version
        FileName = $fileName
        PackageUrl = $packageUrl
        AbsoluteUri = $absoluteUri
        ExpectedFileSize = $expectedFileSize
        HeadStatus = $head.StatusCode
        HeadContentLength = $head.ContentLength
        HeadContentType = $head.ContentType
        HeadElapsedMs = $head.ElapsedMs
        GetStatus = $get.StatusCode
        GetContentLength = $get.ContentLength
        GetContentType = $get.ContentType
        GetElapsedMs = $get.ElapsedMs
        Result = if ($issues.Count -eq 0) { 'PASS' } else { 'FAIL' }
        Issues = @($issues)
    }
}

function Read-ResponseBody {
    param($Response)

    if ($null -eq $Response) {
        return ''
    }

    try {
        $stream = $Response.GetResponseStream()
        if ($null -eq $stream) {
            return ''
        }

        $reader = New-Object System.IO.StreamReader($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    catch {
        return ''
    }
}

function Invoke-JsonApi {
    param(
        [ValidateSet('GET','POST')][string]$Method,
        [string]$Uri,
        [hashtable]$Headers = @{},
        [object]$Body = $null,
        [int]$TimeoutSec = 30
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
    }

    if ($Headers.Count -gt 0) {
        $parameters.Headers = $Headers
    }

    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = ($Body | ConvertTo-Json -Depth 40 -Compress)
    }

    try {
        $response = Invoke-WebRequest @parameters
        $raw = [string]$response.Content
        $parsed = $null
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            try {
                $parsed = $raw | ConvertFrom-Json
            }
            catch {
                $parsed = $raw
            }
        }

        return [pscustomobject]@{
            Success = $true
            StatusCode = [int]$response.StatusCode
            Body = $parsed
            Raw = $raw
            Error = ''
        }
    }
    catch {
        $statusCode = 0
        $raw = ''
        if ($_.Exception.Response) {
            try { $statusCode = [int]$_.Exception.Response.StatusCode } catch { $statusCode = 0 }
            $raw = Read-ResponseBody -Response $_.Exception.Response
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $statusCode
            Body = $null
            Raw = $raw
            Error = $_.Exception.Message
        }
    }
}

function Read-SecretFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-AccountCredential {
    param(
        $Secrets,
        [string]$Alias,
        [string]$UsernameEnvName,
        [string]$PasswordEnvName
    )

    $section = Get-JsonPropertyValue -Object $Secrets -Name $Alias
    $usernameFromFile = [string](Get-JsonPropertyValue -Object $section -Name 'username')
    $passwordFromFile = [string](Get-JsonPropertyValue -Object $section -Name 'password')

    return [pscustomobject]@{
        Alias = $Alias.ToUpperInvariant()
        Username = First-NonEmpty @([string][Environment]::GetEnvironmentVariable($UsernameEnvName), $usernameFromFile)
        Password = First-NonEmpty @([string][Environment]::GetEnvironmentVariable($PasswordEnvName), $passwordFromFile)
    }
}

function Get-TargetCount {
    param($Targets, [string]$PropertyName)

    $value = Get-JsonPropertyValue -Object $Targets -Name $PropertyName
    if ($null -eq $value) {
        return 0
    }

    return @($value).Count
}

function Get-NormalizedIntegrityWarningCodes {
    param([string[]]$Codes)

    $result = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Codes)) {
        foreach ($part in ([string]$entry -split ',')) {
            $code = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($code) -and $seen.Add($code)) {
                $result.Add($code) | Out-Null
            }
        }
    }

    return @($result)
}

function Test-RequiredIntegrityAccount {
    param([string]$Alias)

    return [string]::Equals($Alias, 'ADMIN', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($Alias, 'ITWORLD', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($Alias, 'USENET', [System.StringComparison]::OrdinalIgnoreCase)
}

$resolvedRoot = Resolve-ProjectRoot -ExplicitProjectRoot $ProjectRoot
if ([string]::IsNullOrWhiteSpace($ApprovedTargetsPath)) {
    $ApprovedTargetsPath = Join-Path $resolvedRoot '운영검증-승인대상.json'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $resolvedRoot ("audit-output\operational-gate-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedRoot 'audit-output') | Out-Null
Set-Content -LiteralPath (Join-Path $resolvedRoot 'audit-output\latest-operational-gate-dir.txt') -Value $OutputDirectory -Encoding UTF8

$logPath = Join-Path $OutputDirectory 'operational-gate.log'
$reportPath = Join-Path $OutputDirectory 'operational-gate-report.md'
$checks = New-Object System.Collections.Generic.List[object]
$BaseUrl = $BaseUrl.TrimEnd('/')
$normalizedAllowedIntegrityWarningCodes = @(Get-NormalizedIntegrityWarningCodes -Codes $AllowedIntegrityWarningCodes)

"# operational gate log $(Get-Date -Format o)" | Set-Content -LiteralPath $logPath -Encoding UTF8
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ProjectRoot=$resolvedRoot"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "BaseUrl=$BaseUrl"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "Channel=$Channel"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "FailOnIntegrityWarnings=$([bool]$FailOnIntegrityWarnings)"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "AllowedIntegrityWarningCodes=$($normalizedAllowedIntegrityWarningCodes -join ',')"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "SkipWriteSafetyChecks=$([bool]$SkipWriteSafetyChecks)"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "SecretPathExists=$(Test-Path -LiteralPath $SecretPath)"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ApprovedTargetsPath=$ApprovedTargetsPath exists=$(Test-Path -LiteralPath $ApprovedTargetsPath)"

$health = Invoke-TextProbe -Uri ($BaseUrl + '/healthz')
if ($health.Success -and $health.StatusCode -eq 200) {
    Add-Check -Checks $checks -Name 'live healthz' -Status 'PASS' -Detail ("200 OK, {0}ms" -f $health.ElapsedMs)
}
else {
    Add-Check -Checks $checks -Name 'live healthz' -Status 'FAIL' -Detail ("status={0}, error={1}" -f $health.StatusCode, $health.Error)
}
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ("healthz status={0} error={1} body={2}" -f $health.StatusCode, $health.Error, $health.Content)

$readyProbeResult = Invoke-ReadyProbeWithRetry -Uri ($BaseUrl + '/readyz') -LogPath $logPath
$ready = $readyProbeResult.Probe
$readySemanticResult = $readyProbeResult.SemanticResult
Add-Check -Checks $checks -Name 'live readyz' -Status $readySemanticResult.Status -Detail $readySemanticResult.Detail
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ("readyz status={0} error={1} body={2}" -f $ready.StatusCode, $ready.Error, $ready.Content)

$manifest = Invoke-TextProbe -Uri ($BaseUrl + "/updates/manifest?channel=$Channel")
$desktopVersion = ''
$androidVersion = ''
$desktopPackageUrl = ''
$androidPackageUrl = ''
$manifestJson = $null
if ($manifest.Success -and $manifest.StatusCode -eq 200) {
    try {
        $manifestJson = $manifest.Content | ConvertFrom-Json
        $desktop = Get-JsonPropertyValue -Object $manifestJson -Name 'desktop'
        $android = Get-JsonPropertyValue -Object $manifestJson -Name 'android'
        $desktopVersion = [string](Get-JsonPropertyValue -Object $desktop -Name 'version')
        $desktopPackageUrl = [string](Get-JsonPropertyValue -Object $desktop -Name 'packageUrl')
        $androidVersion = [string](Get-JsonPropertyValue -Object $android -Name 'version')
        $androidPackageUrl = [string](Get-JsonPropertyValue -Object $android -Name 'packageUrl')
        Add-Check -Checks $checks -Name 'stable manifest' -Status 'PASS' -Detail ("desktop={0}, android={1}" -f $desktopVersion, $androidVersion)
    }
    catch {
        Add-Check -Checks $checks -Name 'stable manifest' -Status 'FAIL' -Detail ("manifest parse failed: {0}" -f $_.Exception.Message)
    }
}
else {
    Add-Check -Checks $checks -Name 'stable manifest' -Status 'FAIL' -Detail ("status={0}, error={1}" -f $manifest.StatusCode, $manifest.Error)
}
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ("manifest status={0} error={1}" -f $manifest.StatusCode, $manifest.Error)

$updateDownloadReportPath = Join-Path $OutputDirectory 'update-downloads.md'
$updateDownloadJsonPath = Join-Path $OutputDirectory 'update-downloads.json'
if ($null -eq $manifestJson) {
    Add-Check -Checks $checks -Name 'update package downloads' -Status 'BLOCKED' -Detail 'stable manifest was not parsed; package download header checks skipped'
}
else {
    $downloadChecks = New-Object System.Collections.Generic.List[object]
    $desktopPackage = Get-JsonPropertyValue -Object $manifestJson -Name 'desktop'
    $androidPackage = Get-JsonPropertyValue -Object $manifestJson -Name 'android'
    $downloadChecks.Add((Test-UpdatePackageDownloadHeaders -BaseUrl $BaseUrl -Platform 'desktop' -Package $desktopPackage)) | Out-Null
    $downloadChecks.Add((Test-UpdatePackageDownloadHeaders -BaseUrl $BaseUrl -Platform 'android' -Package $androidPackage)) | Out-Null
    $downloadChecks | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $updateDownloadJsonPath -Encoding UTF8

    $updateDownloadLines = New-Object System.Collections.Generic.List[string]
    $updateDownloadLines.Add('# 업데이트 패키지 다운로드 헤더 검증') | Out-Null
    $updateDownloadLines.Add('') | Out-Null
    $updateDownloadLines.Add(('- 실행시각: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))) | Out-Null
    $updateDownloadLines.Add(('- BaseUrl: `{0}`' -f $BaseUrl)) | Out-Null
    $updateDownloadLines.Add(('- Channel: `{0}`' -f $Channel)) | Out-Null
    $updateDownloadLines.Add('') | Out-Null
    $updateDownloadLines.Add('| 플랫폼 | 결과 | 버전 | HEAD | HEAD length | GET | GET length | manifest size | 파일 |') | Out-Null
    $updateDownloadLines.Add('|---|---|---:|---:|---:|---:|---:|---:|---|') | Out-Null
    foreach ($row in $downloadChecks) {
        $updateDownloadLines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |' -f $row.Platform, $row.Result, $row.Version, $row.HeadStatus, $row.HeadContentLength, $row.GetStatus, $row.GetContentLength, $row.ExpectedFileSize, $row.FileName)) | Out-Null
        if ($row.Issues.Count -gt 0) {
            foreach ($issue in $row.Issues) {
                $updateDownloadLines.Add(('- {0}: {1}' -f $row.Platform, $issue)) | Out-Null
            }
        }
    }
    $updateDownloadLines.Add('') | Out-Null
    $updateDownloadLines.Add(('JSON: `{0}`' -f $updateDownloadJsonPath)) | Out-Null
    Set-Content -LiteralPath $updateDownloadReportPath -Encoding UTF8 -Value $updateDownloadLines

    $failedDownloads = @($downloadChecks | Where-Object { $_.Result -ne 'PASS' })
    if ($failedDownloads.Count -gt 0) {
        $failedSummary = @($failedDownloads | ForEach-Object { "{0}: {1}" -f $_.Platform, ($_.Issues -join '; ') }) -join ' / '
        Add-Check -Checks $checks -Name 'update package downloads' -Status 'FAIL' -Detail ("{0}; {1}" -f $failedSummary, $updateDownloadReportPath)
    }
    else {
        $downloadSummary = @($downloadChecks | ForEach-Object { "{0}=HEAD {1}/GET {2}/size {3}" -f $_.Platform, $_.HeadStatus, $_.GetStatus, $_.ExpectedFileSize }) -join ', '
        Add-Check -Checks $checks -Name 'update package downloads' -Status 'PASS' -Detail ("{0}; {1}" -f $downloadSummary, $updateDownloadReportPath)
    }
}

$liveObservationScript = Join-Path $resolvedRoot '테스트 시행\Invoke-LiveObservationCheck.ps1'
$liveObservationReport = Join-Path $OutputDirectory 'live-observation.md'
if (Test-Path -LiteralPath $liveObservationScript) {
    try {
        $scriptOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $liveObservationScript -ProjectRoot $resolvedRoot -BaseUrl $BaseUrl -Channel $Channel -SampleCount 2 -IntervalSeconds 5 -OutputPath $liveObservationReport 2>&1 | Out-String -Width 4096
        Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## live observation script"
        Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $scriptOutput
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $liveObservationReport)) {
            $liveObservationStatus = Resolve-MarkdownResultStatus -ReportPath $liveObservationReport -DefaultStatus 'PASS'
            Add-Check -Checks $checks -Name 'live observation' -Status $liveObservationStatus -Detail $liveObservationReport
        }
        else {
            Add-Check -Checks $checks -Name 'live observation' -Status 'FAIL' -Detail ("exit={0}" -f $LASTEXITCODE)
        }
    }
    catch {
        Add-Check -Checks $checks -Name 'live observation' -Status 'FAIL' -Detail $_.Exception.Message
    }
}
else {
    Add-Check -Checks $checks -Name 'live observation' -Status 'WARN' -Detail 'script not found'
}

if (-not [string]::IsNullOrWhiteSpace($PlatformStateRoot) -and (Test-Path -LiteralPath $PlatformStateRoot)) {
    $dailyPath = Join-Path $PlatformStateRoot 'daily-check-status.txt'
    $backupPath = Join-Path $PlatformStateRoot 'backup-status.txt'
    $replicaPath = Join-Path $PlatformStateRoot 'external-replica-status.txt'
    $certPath = Join-Path $PlatformStateRoot 'cert-status.txt'

    $daily = if (Test-Path -LiteralPath $dailyPath) { Get-Content -LiteralPath $dailyPath -Raw } else { '' }
    $backup = if (Test-Path -LiteralPath $backupPath) { Get-Content -LiteralPath $backupPath -Raw } else { '' }
    $replica = if (Test-Path -LiteralPath $replicaPath) { Get-Content -LiteralPath $replicaPath -Raw } else { '' }
    $cert = if (Test-Path -LiteralPath $certPath) { Get-Content -LiteralPath $certPath -Raw } else { '' }

    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## platform state"
    Get-ChildItem -LiteralPath $PlatformStateRoot -File | Sort-Object Name | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize | Out-String -Width 4096 | Add-Content -LiteralPath $logPath -Encoding UTF8
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "daily=$daily"
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "backup=$backup"
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "replica=$replica"
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "cert=$cert"

    $dailyEndpointOk = ($daily -match 'healthz=ok') -or ($daily -match 'readyz=ok')
    $platformOk = $dailyEndpointOk -and ($daily -match 'manifest=ok') -and ($daily -match 'backup=ok') -and ($daily -match 'replica=ok') -and ($backup -match 'backup=ok') -and ($replica -match 'replica=ok') -and ($cert -match 'cert=ok')
    if ($platformOk) {
        Add-Check -Checks $checks -Name 'platform status files' -Status 'PASS' -Detail 'daily endpoint/manifest/backup/replica/cert ok'
    }
    else {
        Add-Check -Checks $checks -Name 'platform status files' -Status 'WARN' -Detail 'state files readable but one or more ok markers missing'
    }
}
elseif ([string]::IsNullOrWhiteSpace($PlatformStateRoot)) {
    Add-Check -Checks $checks -Name 'platform status files' -Status 'PASS' -Detail 'SKIP: Linux PC platform state root is not configured; live health/manifest checks are used instead'
}
else {
    Add-Check -Checks $checks -Name 'platform status files' -Status 'WARN' -Detail ("platform state root is not accessible: {0}" -f $PlatformStateRoot)
}

$secrets = Read-SecretFile -Path $SecretPath
$itworld = Get-AccountCredential -Secrets $secrets -Alias 'itworld' -UsernameEnvName 'GEORAEPLAN_SCOPE_ITWORLD_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_ITWORLD_PASSWORD'
$usenet = Get-AccountCredential -Secrets $secrets -Alias 'usenet' -UsernameEnvName 'GEORAEPLAN_SCOPE_USENET_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_USENET_PASSWORD'
$yeonsu = Get-AccountCredential -Secrets $secrets -Alias 'yeonsu' -UsernameEnvName 'GEORAEPLAN_SCOPE_YEONSU_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_YEONSU_PASSWORD'
$admin = Get-AccountCredential -Secrets $secrets -Alias 'admin' -UsernameEnvName 'GEORAEPLAN_SCOPE_ADMIN_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_ADMIN_PASSWORD'
$accounts = @($admin, $itworld, $usenet, $yeonsu)

$integrityReportJsonPath = Join-Path $OutputDirectory 'integrity-report.json'
$integrityReportSummaryPath = Join-Path $OutputDirectory 'integrity-report-summary.md'
$integrityCredential = @($usenet, $admin, $itworld, $yeonsu) |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Username) -and -not [string]::IsNullOrWhiteSpace([string]$_.Password) } |
    Select-Object -First 1
if ($null -eq $integrityCredential) {
    Add-Check -Checks $checks -Name 'integrity report' -Status 'BLOCKED' -Detail 'credentials are required to query /integrity/report'
}
else {
    try {
        $login = Invoke-JsonApi `
            -Method 'POST' `
            -Uri ($BaseUrl + '/auth/login') `
            -Body @{ username = [string]$integrityCredential.Username; password = [string]$integrityCredential.Password } `
            -TimeoutSec 20

        $token = if ($login.Success -and $null -ne $login.Body) { [string](Get-JsonPropertyValue -Object $login.Body -Name 'token') } else { '' }
        if (-not $login.Success -or [string]::IsNullOrWhiteSpace($token)) {
            Add-Check -Checks $checks -Name 'integrity report' -Status 'FAIL' -Detail ("login failed for {0}: status={1}, error={2}" -f $integrityCredential.Alias, $login.StatusCode, $login.Error)
        }
        else {
            $integrity = Invoke-JsonApi `
                -Method 'GET' `
                -Uri ($BaseUrl + '/integrity/report') `
                -Headers @{ Authorization = "Bearer $token" } `
                -TimeoutSec 120

            if (-not $integrity.Success -or $null -eq $integrity.Body) {
                Add-Check -Checks $checks -Name 'integrity report' -Status 'FAIL' -Detail ("query failed: status={0}, error={1}" -f $integrity.StatusCode, $integrity.Error)
            }
            else {
                $integrity.Body | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $integrityReportJsonPath -Encoding UTF8
                $integrityIssues = @($integrity.Body.issues)
                $integrityErrors = @($integrityIssues | Where-Object { [string]$_.severity -eq 'Error' })
                $integrityWarnings = @($integrityIssues | Where-Object { [string]$_.severity -eq 'Warning' })
                $integrityInfos = @($integrityIssues | Where-Object { [string]$_.severity -eq 'Info' })
                $allowedWarningCodeSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                foreach ($code in $normalizedAllowedIntegrityWarningCodes) {
                    if (-not [string]::IsNullOrWhiteSpace($code)) {
                        [void]$allowedWarningCodeSet.Add($code.Trim())
                    }
                }
                $blockingIntegrityWarnings = @($integrityWarnings | Where-Object {
                    $code = [string]$_.code
                    [string]::IsNullOrWhiteSpace($code) -or -not $allowedWarningCodeSet.Contains($code.Trim())
                })

                $summaryLines = New-Object System.Collections.Generic.List[string]
                $summaryLines.Add('# 무결성 리포트 요약') | Out-Null
                $summaryLines.Add('') | Out-Null
                $summaryLines.Add(('- 실행시각: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))) | Out-Null
                $summaryLines.Add(('- 조회 계정: `{0}`' -f (Mask-Value ([string]$integrityCredential.Username)))) | Out-Null
                $summaryLines.Add(('- TenantCode: `{0}`' -f ([string](Get-JsonPropertyValue -Object $integrity.Body -Name 'tenantCode')))) | Out-Null
                $summaryLines.Add(('- OfficeCode: `{0}`' -f ([string](Get-JsonPropertyValue -Object $integrity.Body -Name 'officeCode')))) | Out-Null
                $summaryLines.Add(('- Error: `{0}` / Warning: `{1}` / Info: `{2}`' -f $integrityErrors.Count, $integrityWarnings.Count, $integrityInfos.Count)) | Out-Null
                $summaryLines.Add(('- Warning 실패 처리: `{0}` / 차단 Warning: `{1}`' -f ([bool]$FailOnIntegrityWarnings), $blockingIntegrityWarnings.Count)) | Out-Null
                if ($normalizedAllowedIntegrityWarningCodes.Count -gt 0) {
                    $summaryLines.Add(('- 허용 Warning 코드: `{0}`' -f ($normalizedAllowedIntegrityWarningCodes -join ', '))) | Out-Null
                }
                $summaryLines.Add('') | Out-Null
                $summaryLines.Add('| 심각도 | 코드 | 건수 | 메시지 |') | Out-Null
                $summaryLines.Add('| --- | --- | ---: | --- |') | Out-Null
                foreach ($issue in $integrityIssues) {
                    $summaryLines.Add(('| {0} | {1} | {2} | {3} |' -f ([string]$issue.severity), ([string]$issue.code), ([int]$issue.count), ([string]$issue.message).Replace('|', '\|'))) | Out-Null
                }
                Set-Content -LiteralPath $integrityReportSummaryPath -Value $summaryLines -Encoding UTF8
                Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ("integrity_report account={0} errors={1} warnings={2} infos={3} report={4}" -f $integrityCredential.Alias, $integrityErrors.Count, $integrityWarnings.Count, $integrityInfos.Count, $integrityReportSummaryPath)

                if ($integrityErrors.Count -gt 0) {
                    Add-Check -Checks $checks -Name 'integrity report' -Status 'FAIL' -Detail ("Error={0}, Warning={1}; {2}" -f $integrityErrors.Count, $integrityWarnings.Count, $integrityReportSummaryPath)
                }
                elseif ($FailOnIntegrityWarnings -and $blockingIntegrityWarnings.Count -gt 0) {
                    $warningSummary = (@($blockingIntegrityWarnings | ForEach-Object { '{0}({1})' -f ([string]$_.code), ([int]$_.count) }) -join ', ')
                    Add-Check -Checks $checks -Name 'integrity report' -Status 'FAIL' -Detail ("Warning={0}, blocking={1}; {2}; {3}" -f $integrityWarnings.Count, $blockingIntegrityWarnings.Count, $warningSummary, $integrityReportSummaryPath)
                }
                elseif ($integrityWarnings.Count -gt 0) {
                    Add-Check -Checks $checks -Name 'integrity report' -Status 'WARN' -Detail ("Warning={0}; {1}" -f $integrityWarnings.Count, $integrityReportSummaryPath)
                }
                else {
                    Add-Check -Checks $checks -Name 'integrity report' -Status 'PASS' -Detail ("Error=0, Warning=0; Info={0}; {1}" -f $integrityInfos.Count, $integrityReportSummaryPath)
                }
            }
        }
    }
    catch {
        Add-Check -Checks $checks -Name 'integrity report' -Status 'FAIL' -Detail $_.Exception.Message
    }
}

$integrityScopeReportDirectory = Join-Path $OutputDirectory 'integrity-scope-reports'
$integrityScopeSummaryPath = Join-Path $OutputDirectory 'integrity-scope-summary.md'
$integrityScopeRows = New-Object System.Collections.Generic.List[object]
$integrityScopeFailures = New-Object System.Collections.Generic.List[string]
$integrityScopeBlockingErrors = New-Object System.Collections.Generic.List[string]
$integrityScopeBlockingWarnings = New-Object System.Collections.Generic.List[string]
$integrityScopeAccessibleReportCount = 0
$integrityScopeWarningCount = 0
$integrityScopeAllowedWarningCodes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($code in $normalizedAllowedIntegrityWarningCodes) {
    if (-not [string]::IsNullOrWhiteSpace($code)) {
        [void]$integrityScopeAllowedWarningCodes.Add($code.Trim())
    }
}

New-Item -ItemType Directory -Force -Path $integrityScopeReportDirectory | Out-Null
foreach ($credential in @($itworld, $usenet, $yeonsu, $admin)) {
    if ($null -eq $credential -or
        [string]::IsNullOrWhiteSpace([string]$credential.Username) -or
        [string]::IsNullOrWhiteSpace([string]$credential.Password)) {
        continue
    }

    $alias = [string]$credential.Alias
    try {
        $login = Invoke-JsonApi `
            -Method 'POST' `
            -Uri ($BaseUrl + '/auth/login') `
            -Body @{ username = [string]$credential.Username; password = [string]$credential.Password } `
            -TimeoutSec 20

        $token = if ($login.Success -and $null -ne $login.Body) { [string](Get-JsonPropertyValue -Object $login.Body -Name 'token') } else { '' }
        if (-not $login.Success -or [string]::IsNullOrWhiteSpace($token)) {
            $status = if (Test-RequiredIntegrityAccount -Alias $alias) { 'FAIL' } else { 'SKIP' }
            $detail = "login failed status=$($login.StatusCode) error=$($login.Error)"
            $integrityScopeRows.Add([pscustomobject]@{
                Account = $alias
                Status = $status
                Http = $login.StatusCode
                Tenant = ''
                Office = ''
                ErrorCount = $null
                WarningCount = $null
                InfoCount = $null
                Issues = $detail
            }) | Out-Null
            if ($status -eq 'FAIL') {
                $integrityScopeFailures.Add("$alias $detail") | Out-Null
            }
            continue
        }

        $integrity = Invoke-JsonApi `
            -Method 'GET' `
            -Uri ($BaseUrl + '/integrity/report') `
            -Headers @{ Authorization = "Bearer $token" } `
            -TimeoutSec 120

        if (-not $integrity.Success -or $null -eq $integrity.Body) {
            $isRequiredIntegrityAccount = Test-RequiredIntegrityAccount -Alias $alias
            $status = if ($integrity.StatusCode -eq 403 -and -not $isRequiredIntegrityAccount) { 'SKIP' } else { 'FAIL' }
            $detail = if ($integrity.StatusCode -eq 403 -and -not $isRequiredIntegrityAccount) {
                'integrity/report permission denied; account is not expected to run settings integrity checks'
            }
            elseif ($integrity.StatusCode -eq 403) {
                'integrity/report permission denied for required integrity account'
            }
            else {
                "query failed status=$($integrity.StatusCode) error=$($integrity.Error)"
            }
            $integrityScopeRows.Add([pscustomobject]@{
                Account = $alias
                Status = $status
                Http = $integrity.StatusCode
                Tenant = ''
                Office = ''
                ErrorCount = $null
                WarningCount = $null
                InfoCount = $null
                Issues = $detail
            }) | Out-Null
            if ($status -eq 'FAIL') {
                $integrityScopeFailures.Add("$alias $detail") | Out-Null
            }
            continue
        }

        $integrityScopeAccessibleReportCount++
        $issues = @($integrity.Body.issues)
        $errors = @($issues | Where-Object { [string]$_.severity -eq 'Error' })
        $warnings = @($issues | Where-Object { [string]$_.severity -eq 'Warning' })
        $infos = @($issues | Where-Object { [string]$_.severity -eq 'Info' })
        $blockingWarnings = @($warnings | Where-Object {
            $code = [string]$_.code
            [string]::IsNullOrWhiteSpace($code) -or -not $integrityScopeAllowedWarningCodes.Contains($code.Trim())
        })
        $integrityScopeWarningCount += $warnings.Count

        $accountReportPath = Join-Path $integrityScopeReportDirectory ("integrity-$($alias.ToLowerInvariant()).json")
        $integrity.Body | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $accountReportPath -Encoding UTF8

        foreach ($errorIssue in $errors) {
            $integrityScopeBlockingErrors.Add(('{0}:{1}({2})' -f $alias, [string]$errorIssue.code, [int]$errorIssue.count)) | Out-Null
        }
        if ($FailOnIntegrityWarnings) {
            foreach ($warningIssue in $blockingWarnings) {
                $integrityScopeBlockingWarnings.Add(('{0}:{1}({2})' -f $alias, [string]$warningIssue.code, [int]$warningIssue.count)) | Out-Null
            }
        }

        $issueText = (@($issues | ForEach-Object { '{0}:{1}={2}' -f ([string]$_.severity), ([string]$_.code), ([int]$_.count) }) -join '; ')
        $integrityScopeRows.Add([pscustomobject]@{
            Account = $alias
            Status = 'OK'
            Http = 200
            Tenant = [string](Get-JsonPropertyValue -Object $integrity.Body -Name 'tenantCode')
            Office = [string](Get-JsonPropertyValue -Object $integrity.Body -Name 'officeCode')
            ErrorCount = $errors.Count
            WarningCount = $warnings.Count
            InfoCount = $infos.Count
            Issues = $issueText
        }) | Out-Null
    }
    catch {
        $detail = $_.Exception.Message
        $status = if ($alias -in @('ITWORLD', 'USENET')) { 'FAIL' } else { 'SKIP' }
        $integrityScopeRows.Add([pscustomobject]@{
            Account = $alias
            Status = $status
            Http = 0
            Tenant = ''
            Office = ''
            ErrorCount = $null
            WarningCount = $null
            InfoCount = $null
            Issues = $detail
        }) | Out-Null
        if ($status -eq 'FAIL') {
            $integrityScopeFailures.Add("$alias $detail") | Out-Null
        }
    }
}

$integrityScopeLines = New-Object System.Collections.Generic.List[string]
$integrityScopeLines.Add('# 계정별 무결성 리포트 요약') | Out-Null
$integrityScopeLines.Add('') | Out-Null
$integrityScopeLines.Add(('- 실행시각: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))) | Out-Null
$integrityScopeLines.Add(('- 접근 가능 리포트: `{0}`' -f $integrityScopeAccessibleReportCount)) | Out-Null
$integrityScopeLines.Add(('- Warning 실패 처리: `{0}`' -f ([bool]$FailOnIntegrityWarnings))) | Out-Null
if ($normalizedAllowedIntegrityWarningCodes.Count -gt 0) {
    $integrityScopeLines.Add(('- 허용 Warning 코드: `{0}`' -f ($normalizedAllowedIntegrityWarningCodes -join ', '))) | Out-Null
}
$integrityScopeLines.Add('') | Out-Null
$integrityScopeLines.Add('| 계정 | 상태 | HTTP | Tenant | Office | Error | Warning | Info | Issues |') | Out-Null
$integrityScopeLines.Add('| --- | --- | ---: | --- | --- | ---: | ---: | ---: | --- |') | Out-Null
foreach ($row in $integrityScopeRows) {
    $issues = ([string]$row.Issues).Replace('|', '\|')
    $integrityScopeLines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |' -f $row.Account, $row.Status, $row.Http, $row.Tenant, $row.Office, $row.ErrorCount, $row.WarningCount, $row.InfoCount, $issues)) | Out-Null
}
Set-Content -LiteralPath $integrityScopeSummaryPath -Value $integrityScopeLines -Encoding UTF8
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ("integrity_scope_reports accessible={0} failures={1} errors={2} warnings={3} summary={4}" -f $integrityScopeAccessibleReportCount, $integrityScopeFailures.Count, $integrityScopeBlockingErrors.Count, $integrityScopeBlockingWarnings.Count, $integrityScopeSummaryPath)

if ($integrityScopeFailures.Count -gt 0) {
    Add-Check -Checks $checks -Name 'integrity report by account' -Status 'FAIL' -Detail ("query failures: {0}; {1}" -f ($integrityScopeFailures -join ', '), $integrityScopeSummaryPath)
}
elseif ($integrityScopeAccessibleReportCount -eq 0) {
    Add-Check -Checks $checks -Name 'integrity report by account' -Status 'BLOCKED' -Detail ("no account could access /integrity/report; {0}" -f $integrityScopeSummaryPath)
}
elseif ($integrityScopeBlockingErrors.Count -gt 0) {
    Add-Check -Checks $checks -Name 'integrity report by account' -Status 'FAIL' -Detail ("errors: {0}; {1}" -f ($integrityScopeBlockingErrors -join ', '), $integrityScopeSummaryPath)
}
elseif ($FailOnIntegrityWarnings -and $integrityScopeBlockingWarnings.Count -gt 0) {
    Add-Check -Checks $checks -Name 'integrity report by account' -Status 'FAIL' -Detail ("blocking warnings: {0}; {1}" -f ($integrityScopeBlockingWarnings -join ', '), $integrityScopeSummaryPath)
}
elseif ($integrityScopeWarningCount -gt 0) {
    Add-Check -Checks $checks -Name 'integrity report by account' -Status 'WARN' -Detail ("warnings={0}; {1}" -f $integrityScopeWarningCount, $integrityScopeSummaryPath)
}
else {
    Add-Check -Checks $checks -Name 'integrity report by account' -Status 'PASS' -Detail ("accessible={0}; no errors/warnings; {1}" -f $integrityScopeAccessibleReportCount, $integrityScopeSummaryPath)
}

$rentalMonthlyRepairScript = Join-Path $resolvedRoot 'tools\maintenance\Invoke-GeoraePlanRentalMonthlyRepair.ps1'
$rentalMonthlyRepairDirectory = Join-Path $OutputDirectory 'rental-monthly-repair'
if (Test-Path -LiteralPath $rentalMonthlyRepairScript) {
    if ([string]::IsNullOrWhiteSpace([string]$usenet.Username) -or [string]::IsNullOrWhiteSpace([string]$usenet.Password)) {
        Add-Check -Checks $checks -Name 'rental monthly amount consistency' -Status 'BLOCKED' -Detail 'USENET credentials are required to compare rental profile monthly amounts with linked asset monthly amounts'
    }
    else {
        New-Item -ItemType Directory -Force -Path $rentalMonthlyRepairDirectory | Out-Null
        try {
            $repairArgs = @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', $rentalMonthlyRepairScript,
                '-BaseUrl', $BaseUrl,
                '-Username', ([string]$usenet.Username),
                '-Password', ([string]$usenet.Password),
                '-EvidenceDirectory', $rentalMonthlyRepairDirectory
            )
            $scriptOutput = & powershell @repairArgs 2>&1 | Out-String -Width 4096
            $repairExitCode = $LASTEXITCODE
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## rental monthly amount consistency"
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $scriptOutput

            $reportLine = @($scriptOutput -split "`r?`n" | Where-Object { $_ -like 'rental_monthly_repair_report=*' } | Select-Object -Last 1)
            $jsonLine = @($scriptOutput -split "`r?`n" | Where-Object { $_ -like 'rental_monthly_repair_json=*' } | Select-Object -Last 1)
            $countLine = @($scriptOutput -split "`r?`n" | Where-Object { $_ -like 'candidate_count=*' } | Select-Object -Last 1)

            $rentalMonthlyRepairReport = ''
            if ($reportLine.Count -gt 0) {
                $rentalMonthlyRepairReport = ([string]$reportLine[0]).Substring('rental_monthly_repair_report='.Length).Trim()
            }
            elseif (Test-Path -LiteralPath $rentalMonthlyRepairDirectory) {
                $latestReport = Get-ChildItem -LiteralPath $rentalMonthlyRepairDirectory -File -Filter 'rental-monthly-repair-*.md' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($null -ne $latestReport) {
                    $rentalMonthlyRepairReport = $latestReport.FullName
                }
            }

            $rentalMonthlyRepairJson = ''
            if ($jsonLine.Count -gt 0) {
                $rentalMonthlyRepairJson = ([string]$jsonLine[0]).Substring('rental_monthly_repair_json='.Length).Trim()
            }
            elseif (Test-Path -LiteralPath $rentalMonthlyRepairDirectory) {
                $latestJson = Get-ChildItem -LiteralPath $rentalMonthlyRepairDirectory -File -Filter 'rental-monthly-repair-*.json' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($null -ne $latestJson) {
                    $rentalMonthlyRepairJson = $latestJson.FullName
                }
            }

            $candidateCount = $null
            $candidatePreview = ''
            if (-not [string]::IsNullOrWhiteSpace($rentalMonthlyRepairJson) -and (Test-Path -LiteralPath $rentalMonthlyRepairJson)) {
                try {
                    $repairJson = Get-Content -LiteralPath $rentalMonthlyRepairJson -Raw -Encoding UTF8 | ConvertFrom-Json
                    $candidateCount = [int](Get-JsonPropertyValue -Object $repairJson -Name 'CandidateCount')
                    $repairRows = @(Get-JsonPropertyValue -Object $repairJson -Name 'Rows')
                    if ($repairRows.Count -gt 0) {
                        $firstRow = $repairRows[0]
                        $customerName = [string](Get-JsonPropertyValue -Object $firstRow -Name 'CustomerName')
                        $difference = Get-JsonPropertyValue -Object $firstRow -Name 'Difference'
                        $missingCount = Get-JsonPropertyValue -Object $firstRow -Name 'MissingAssetCount'
                        $missingMonthly = Get-JsonPropertyValue -Object $firstRow -Name 'MissingAssetMonthlyAmount'
                        $profileId = [string](Get-JsonPropertyValue -Object $firstRow -Name 'ProfileId')
                        $candidatePreview = ("top={0}, diff={1:N0}, missingAssets={2}, missingMonthly={3:N0}, profileId={4}" -f $customerName, [decimal]$difference, [int]$missingCount, [decimal]$missingMonthly, $profileId)
                    }
                }
                catch {
                    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ("rental_monthly_repair_json_parse_failed={0}" -f $_.Exception.Message)
                }
            }
            if ($null -eq $candidateCount -and $countLine.Count -gt 0) {
                $rawCount = ([string]$countLine[0]).Substring('candidate_count='.Length).Trim()
                $parsedCount = 0
                if ([int]::TryParse($rawCount, [ref]$parsedCount)) {
                    $candidateCount = $parsedCount
                }
            }

            $candidateCountText = 'unknown'
            if ($null -ne $candidateCount) {
                $candidateCountText = [string]$candidateCount
            }

            $repairDetail = if (-not [string]::IsNullOrWhiteSpace($rentalMonthlyRepairReport)) {
                ("candidate_count={0}; report={1}" -f $candidateCountText, $rentalMonthlyRepairReport)
            }
            else {
                ("candidate_count={0}; report=missing" -f $candidateCountText)
            }
            if (-not [string]::IsNullOrWhiteSpace($candidatePreview)) {
                $repairDetail = "$repairDetail; $candidatePreview"
            }

            if ($repairExitCode -eq 0 -and $null -ne $candidateCount -and $candidateCount -eq 0) {
                Add-Check -Checks $checks -Name 'rental monthly amount consistency' -Status 'PASS' -Detail $repairDetail
            }
            elseif ($null -ne $candidateCount -and $candidateCount -gt 0) {
                Add-Check -Checks $checks -Name 'rental monthly amount consistency' -Status 'FAIL' -Detail ("repair candidates remain; {0}" -f $repairDetail)
            }
            else {
                Add-Check -Checks $checks -Name 'rental monthly amount consistency' -Status 'FAIL' -Detail ("repair check failed, exit={0}; {1}" -f $repairExitCode, $repairDetail)
            }
        }
        catch {
            Add-Check -Checks $checks -Name 'rental monthly amount consistency' -Status 'FAIL' -Detail $_.Exception.Message
        }
    }
}
else {
    Add-Check -Checks $checks -Name 'rental monthly amount consistency' -Status 'WARN' -Detail 'repair inspection script not found'
}

$availableScopeAccounts = @(@($itworld, $usenet, $yeonsu) | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Username) -and -not [string]::IsNullOrWhiteSpace($_.Password) })
if (@($availableScopeAccounts).Count -gt 0) {
    $accountScopeScript = Join-Path $resolvedRoot '테스트 시행\Invoke-AccountScopeRegressionCheck.ps1'
    $accountScopeReport = Join-Path $OutputDirectory 'account-scope-regression.md'
    if (Test-Path -LiteralPath $accountScopeScript) {
        try {
            $accountScopeArgs = @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', $accountScopeScript,
                '-ProjectRoot', $resolvedRoot,
                '-BaseUrl', $BaseUrl
            )
            if (-not [string]::IsNullOrWhiteSpace([string]$itworld.Username)) {
                $accountScopeArgs += @('-ItworldUsername', ([string]$itworld.Username))
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$itworld.Password)) {
                $accountScopeArgs += @('-ItworldPassword', ([string]$itworld.Password))
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$usenet.Username)) {
                $accountScopeArgs += @('-UsenetUsername', ([string]$usenet.Username))
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$usenet.Password)) {
                $accountScopeArgs += @('-UsenetPassword', ([string]$usenet.Password))
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$yeonsu.Username)) {
                $accountScopeArgs += @('-YeonsuUsername', ([string]$yeonsu.Username))
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$yeonsu.Password)) {
                $accountScopeArgs += @('-YeonsuPassword', ([string]$yeonsu.Password))
            }
            $accountScopeArgs += @('-OutputPath', $accountScopeReport)
            $scriptOutput = & powershell @accountScopeArgs 2>&1 | Out-String -Width 4096
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## account scope regression"
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $scriptOutput
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $accountScopeReport)) {
                $reportText = Get-Content -LiteralPath $accountScopeReport -Raw -Encoding UTF8
                if ($reportText -match '결과: \*\*PASS\*\*') {
                    Add-Check -Checks $checks -Name 'account scope regression' -Status 'PASS' -Detail $accountScopeReport
                }
                elseif ($reportText -match '결과: \*\*WARN\*\*') {
                    Add-Check -Checks $checks -Name 'account scope regression' -Status 'WARN' -Detail $accountScopeReport
                }
                else {
                    Add-Check -Checks $checks -Name 'account scope regression' -Status 'FAIL' -Detail $accountScopeReport
                }
            }
            else {
                Add-Check -Checks $checks -Name 'account scope regression' -Status 'FAIL' -Detail ("exit={0}" -f $LASTEXITCODE)
            }
        }
        catch {
            Add-Check -Checks $checks -Name 'account scope regression' -Status 'FAIL' -Detail $_.Exception.Message
        }
    }
    else {
        Add-Check -Checks $checks -Name 'account scope regression' -Status 'WARN' -Detail 'script not found'
    }
}
else {
    Add-Check -Checks $checks -Name 'account scope regression' -Status 'BLOCKED' -Detail 'ITWORLD/USENET/YEONSU credentials are missing in env or secret file'
}

$targets = $null
$targetCounts = @{}
$safetyBackupConfirmed = $false
$safetyRestorePossible = $false
$safetyApprovedBy = ''
$hasApprovedTargets = $false
if ($SkipWriteSafetyChecks.IsPresent) {
    Add-Check -Checks $checks -Name 'approved target file' -Status 'PASS' -Detail 'SKIP: read-only gate mode'
    Add-Check -Checks $checks -Name 'write safety metadata' -Status 'PASS' -Detail 'SKIP: read-only gate mode'
}
elseif (Test-Path -LiteralPath $ApprovedTargetsPath) {
    try {
        $targets = Get-Content -LiteralPath $ApprovedTargetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $safety = Get-JsonPropertyValue -Object $targets -Name 'safety'
        $safetyBackupConfirmed = [bool](Get-JsonPropertyValue -Object $safety -Name 'backupConfirmed')
        $safetyRestorePossible = [bool](Get-JsonPropertyValue -Object $safety -Name 'restorePossible')
        $safetyApprovedBy = [string](Get-JsonPropertyValue -Object $safety -Name 'approvedBy')
        foreach ($name in @('customers','items','invoices','payments','rentalAssets','rentalBillingProfiles','inventoryTransfers')) {
            $targetCounts[$name] = Get-TargetCount -Targets $targets -PropertyName $name
        }
        $hasApprovedTargets = (($targetCounts.Values | Measure-Object -Sum).Sum -gt 0)
        Add-Check -Checks $checks -Name 'approved target file' -Status 'PASS' -Detail 'approved target JSON parsed'
    }
    catch {
        Add-Check -Checks $checks -Name 'approved target file' -Status 'FAIL' -Detail $_.Exception.Message
    }
}
else {
    if ($UseEphemeralOperationalWrites.IsPresent) {
        Add-Check -Checks $checks -Name 'approved target file' -Status 'WARN' -Detail ("not found: {0}; existing-data write skipped, ephemeral write smoke requested" -f $ApprovedTargetsPath)
    }
    else {
        Add-Check -Checks $checks -Name 'approved target file' -Status 'BLOCKED' -Detail ("not found: {0}" -f $ApprovedTargetsPath)
    }
}

if ($SkipWriteSafetyChecks.IsPresent) {
}
elseif ($null -ne $targets) {
    if ($safetyBackupConfirmed -and $safetyRestorePossible -and -not [string]::IsNullOrWhiteSpace($safetyApprovedBy)) {
        Add-Check -Checks $checks -Name 'write safety metadata' -Status 'PASS' -Detail 'backupConfirmed/restorePossible/approvedBy present'
    }
    else {
        Add-Check -Checks $checks -Name 'write safety metadata' -Status 'BLOCKED' -Detail 'backupConfirmed, restorePossible, or approvedBy is missing/false'
    }
}
else {
    if ($UseEphemeralOperationalWrites.IsPresent) {
        Add-Check -Checks $checks -Name 'write safety metadata' -Status 'WARN' -Detail 'approved target file is unavailable; only temporary create/update/delete smoke will run'
    }
    else {
        Add-Check -Checks $checks -Name 'write safety metadata' -Status 'BLOCKED' -Detail 'approved target file is unavailable'
    }
}

$ephemeralOperationalWritesReport = ''
$ephemeralOperationalWritesAccount = ''
$approvedOperationalWritesReport = ''
if ($SkipWriteSafetyChecks.IsPresent) {
    Add-Check -Checks $checks -Name 'operational writes' -Status 'PASS' -Detail 'SKIP: read-only gate mode'
}
elseif ($UseEphemeralOperationalWrites.IsPresent) {
    $ephemeralScript = Join-Path $resolvedRoot 'tools\verification\Invoke-GeoraePlanRepeatedSaveSmoke.ps1'
    if (-not (Test-Path -LiteralPath $ephemeralScript)) {
        Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail 'ephemeral write smoke script not found'
    }
    elseif ([string]::IsNullOrWhiteSpace([string]$usenet.Username) -or [string]::IsNullOrWhiteSpace([string]$usenet.Password)) {
        Add-Check -Checks $checks -Name 'operational writes' -Status 'BLOCKED' -Detail 'USENET credentials are required for ephemeral operational write smoke'
    }
    else {
        $ephemeralDirectory = Join-Path $OutputDirectory 'ephemeral-operational-writes'
        New-Item -ItemType Directory -Force -Path $ephemeralDirectory | Out-Null
        $ephemeralOperationalWritesAccount = [string]$usenet.Username
        try {
            $ephemeralArgs = @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', $ephemeralScript,
                '-BaseUrl', $BaseUrl,
                '-Username', ([string]$usenet.Username),
                '-Password', ([string]$usenet.Password),
                '-EvidenceDirectory', $ephemeralDirectory,
                '-RepeatCount', '2'
            )
            $scriptOutput = & powershell @ephemeralArgs 2>&1 | Out-String -Width 4096
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## ephemeral operational writes"
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $scriptOutput
            $reportLine = @($scriptOutput -split "`r?`n" | Where-Object { $_ -like 'repeated_save_smoke_report=*' } | Select-Object -Last 1)
            if ($reportLine.Count -gt 0) {
                $ephemeralOperationalWritesReport = ([string]$reportLine[0]).Substring('repeated_save_smoke_report='.Length).Trim()
            }
            elseif (Test-Path -LiteralPath $ephemeralDirectory) {
                $latestReport = Get-ChildItem -LiteralPath $ephemeralDirectory -File -Filter '*.md' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($null -ne $latestReport) {
                    $ephemeralOperationalWritesReport = $latestReport.FullName
                }
            }

            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($ephemeralOperationalWritesReport) -and (Test-Path -LiteralPath $ephemeralOperationalWritesReport)) {
                $reportText = Get-Content -LiteralPath $ephemeralOperationalWritesReport -Raw -Encoding UTF8
                if ($reportText -match '결과: \*\*PASS\*\*') {
                    Add-Check -Checks $checks -Name 'operational writes' -Status 'PASS' -Detail ("temporary create/update/delete smoke passed: {0}" -f $ephemeralOperationalWritesReport)
                }
                else {
                    Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail ("temporary write smoke report is not PASS: {0}" -f $ephemeralOperationalWritesReport)
                }
            }
            else {
                Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail ("temporary write smoke failed, exit={0}" -f $LASTEXITCODE)
            }
        }
        catch {
            Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail $_.Exception.Message
        }
    }
}
elseif ($AllowOperationalWrites.IsPresent) {
    $approvedWriteScript = Join-Path $resolvedRoot 'tools\ops\Invoke-GeoraePlanApprovedWriteRollback.ps1'
    if (-not (Test-Path -LiteralPath $approvedWriteScript)) {
        Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail 'approved write/rollback script not found'
    }
    elseif ($null -eq $targets -or -not $hasApprovedTargets) {
        Add-Check -Checks $checks -Name 'operational writes' -Status 'BLOCKED' -Detail 'approved target file has no existing-data targets'
    }
    elseif (-not $safetyBackupConfirmed -or -not $safetyRestorePossible -or [string]::IsNullOrWhiteSpace($safetyApprovedBy)) {
        Add-Check -Checks $checks -Name 'operational writes' -Status 'BLOCKED' -Detail 'write safety metadata is incomplete'
    }
    elseif ([string]::IsNullOrWhiteSpace([string]$usenet.Username) -or [string]::IsNullOrWhiteSpace([string]$usenet.Password)) {
        Add-Check -Checks $checks -Name 'operational writes' -Status 'BLOCKED' -Detail 'USENET credentials are required for approved write/rollback'
    }
    else {
        $approvedWritesDirectory = Join-Path $OutputDirectory 'approved-operational-writes'
        New-Item -ItemType Directory -Force -Path $approvedWritesDirectory | Out-Null
        try {
            $approvedWriteArgs = @(
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-File', $approvedWriteScript,
                '-BaseUrl', $BaseUrl,
                '-Username', ([string]$usenet.Username),
                '-Password', ([string]$usenet.Password),
                '-ApprovedTargetsPath', $ApprovedTargetsPath,
                '-EvidenceDirectory', $approvedWritesDirectory,
                '-Apply'
            )
            $scriptOutput = & powershell @approvedWriteArgs 2>&1 | Out-String -Width 4096
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## approved operational writes"
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $scriptOutput
            $reportLine = @($scriptOutput -split "`r?`n" | Where-Object { $_ -like 'approved_write_rollback_report=*' } | Select-Object -Last 1)
            $approvedWriteReport = ''
            if ($reportLine.Count -gt 0) {
                $approvedWriteReport = ([string]$reportLine[0]).Substring('approved_write_rollback_report='.Length).Trim()
            }
            elseif (Test-Path -LiteralPath $approvedWritesDirectory) {
                $latestReport = Get-ChildItem -LiteralPath $approvedWritesDirectory -File -Filter '*.md' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($null -ne $latestReport) {
                    $approvedWriteReport = $latestReport.FullName
                }
            }

            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($approvedWriteReport) -and (Test-Path -LiteralPath $approvedWriteReport)) {
                $approvedOperationalWritesReport = $approvedWriteReport
                $reportText = Get-Content -LiteralPath $approvedWriteReport -Raw -Encoding UTF8
                if ($reportText -match '결과: \*\*PASS\*\*') {
                    Add-Check -Checks $checks -Name 'operational writes' -Status 'PASS' -Detail ("approved write/rollback passed: {0}" -f $approvedWriteReport)
                }
                else {
                    Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail ("approved write/rollback report is not PASS: {0}" -f $approvedWriteReport)
                }
            }
            else {
                Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail ("approved write/rollback failed, exit={0}" -f $LASTEXITCODE)
            }
        }
        catch {
            Add-Check -Checks $checks -Name 'operational writes' -Status 'FAIL' -Detail $_.Exception.Message
        }
    }
}
else {
    Add-Check -Checks $checks -Name 'operational writes' -Status 'BLOCKED' -Detail 'AllowOperationalWrites not set; no production mutation attempted'
}

$overallStatus = 'PASS'
if (@($checks | Where-Object { $_.Status -eq 'FAIL' }).Count -gt 0) {
    $overallStatus = 'FAIL'
}
elseif (@($checks | Where-Object { $_.Status -eq 'BLOCKED' }).Count -gt 0) {
    $overallStatus = 'BLOCKED'
}
elseif (@($checks | Where-Object { $_.Status -eq 'WARN' }).Count -gt 0) {
    $overallStatus = 'WARN'
}

$reportLines = New-Object System.Collections.Generic.List[string]
$reportLines.Add('# 거래플랜 운영 검증 게이트 리포트') | Out-Null
$reportLines.Add('') | Out-Null
$reportLines.Add(('- 실행시각: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))) | Out-Null
$reportLines.Add(('- 결과: **{0}**' -f $overallStatus)) | Out-Null
$reportLines.Add(('- ProjectRoot: `{0}`' -f $resolvedRoot)) | Out-Null
$reportLines.Add(('- BaseUrl: `{0}`' -f $BaseUrl)) | Out-Null
$reportLines.Add(('- Channel: `{0}`' -f $Channel)) | Out-Null
$reportLines.Add(('- OutputDirectory: `{0}`' -f $OutputDirectory)) | Out-Null
$reportLines.Add(('- 무결성 Warning 실패 처리: `{0}`' -f ([bool]$FailOnIntegrityWarnings))) | Out-Null
$reportLines.Add(('- 쓰기 안전성 점검 생략: `{0}`' -f ([bool]$SkipWriteSafetyChecks))) | Out-Null
if ($normalizedAllowedIntegrityWarningCodes.Count -gt 0) {
    $reportLines.Add(('- 허용 Warning 코드: `{0}`' -f ($normalizedAllowedIntegrityWarningCodes -join ', '))) | Out-Null
}
$reportLines.Add('') | Out-Null
$reportLines.Add('## 1. 체크 결과') | Out-Null
$reportLines.Add('') | Out-Null
$reportLines.Add('| 결과 | 항목 | 상세 |') | Out-Null
$reportLines.Add('| --- | --- | --- |') | Out-Null
foreach ($check in $checks) {
    $detail = ([string]$check.Detail).Replace('|', '\|')
    $reportLines.Add(('| {0} | {1} | {2} |' -f $check.Status, $check.Name, $detail)) | Out-Null
}

$reportLines.Add('') | Out-Null
$reportLines.Add('## 2. 계정 입력 상태') | Out-Null
$reportLines.Add('') | Out-Null
$reportLines.Add(('- SecretPath 존재: `{0}`' -f (Test-Path -LiteralPath $SecretPath))) | Out-Null
$reportLines.Add('') | Out-Null
$reportLines.Add('| 계정 | 사용자명 | 비밀번호 |') | Out-Null
$reportLines.Add('| --- | --- | --- |') | Out-Null
foreach ($account in $accounts) {
    $passwordState = 'present'
    if ([string]::IsNullOrWhiteSpace($account.Password)) {
        $passwordState = 'missing'
    }

    $reportLines.Add(('| {0} | {1} | {2} |' -f $account.Alias, (Mask-Value $account.Username), $passwordState)) | Out-Null
}

$reportLines.Add('') | Out-Null
$reportLines.Add('## 3. 승인 대상 상태') | Out-Null
$reportLines.Add('') | Out-Null
$reportLines.Add(('- ApprovedTargetsPath 존재: `{0}`' -f (Test-Path -LiteralPath $ApprovedTargetsPath))) | Out-Null
if ($null -ne $targets) {
    $reportLines.Add(('- backupConfirmed: `{0}`' -f $safetyBackupConfirmed)) | Out-Null
    $reportLines.Add(('- restorePossible: `{0}`' -f $safetyRestorePossible)) | Out-Null
    $approvedByState = 'present'
    if ([string]::IsNullOrWhiteSpace($safetyApprovedBy)) {
        $approvedByState = 'missing'
    }

    $reportLines.Add(('- approvedBy: `{0}`' -f $approvedByState)) | Out-Null
    $reportLines.Add('') | Out-Null
    $reportLines.Add('| 대상 | 개수 |') | Out-Null
    $reportLines.Add('| --- | ---: |') | Out-Null
    foreach ($name in @('customers','items','invoices','payments','rentalAssets','rentalBillingProfiles','inventoryTransfers')) {
        $reportLines.Add(('| {0} | {1} |' -f $name, $targetCounts[$name])) | Out-Null
    }
}
else {
    if ($SkipWriteSafetyChecks.IsPresent) {
        $reportLines.Add('- 읽기 전용 게이트 모드로 승인 대상 JSON/운영 쓰기 검증을 생략함') | Out-Null
    }
    elseif ($UseEphemeralOperationalWrites.IsPresent) {
        $reportLines.Add('- 승인 대상 JSON이 없어 기존 운영 데이터 직접 수정/원복 검증은 생략하고, 임시 데이터 생성/수정/삭제 smoke만 수행함') | Out-Null
    }
    else {
        $reportLines.Add('- 승인 대상 JSON이 없어 운영 쓰기 검증은 차단됨') | Out-Null
    }
}

$reportLines.Add('') | Out-Null
$reportLines.Add('## 4. 운영 데이터 변경 여부') | Out-Null
$reportLines.Add('') | Out-Null
if ($SkipWriteSafetyChecks.IsPresent) {
    $reportLines.Add('- 이 게이트는 읽기 전용 모드로 실행되었으며 운영 데이터를 변경하지 않았다.') | Out-Null
    $reportLines.Add('- 읽기 전용 게이트 모드로 운영 데이터 쓰기/원복 검증을 생략했다.') | Out-Null
}
elseif ($UseEphemeralOperationalWrites.IsPresent) {
    $reportLines.Add('- 이 게이트는 임시 데이터만 생성/수정/삭제하고 기존 운영 데이터는 직접 수정하지 않는다.') | Out-Null
    $reportLines.Add(('- 임시 데이터 생성/반복수정/삭제 smoke 실행: `{0}`' -f (-not [string]::IsNullOrWhiteSpace($ephemeralOperationalWritesReport)))) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($ephemeralOperationalWritesReport)) {
        $reportLines.Add(('- 임시 쓰기 검증 리포트: `{0}`' -f $ephemeralOperationalWritesReport)) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($ephemeralOperationalWritesAccount)) {
        $reportLines.Add(('- 임시 쓰기 검증 계정: `{0}`' -f (Mask-Value $ephemeralOperationalWritesAccount))) | Out-Null
    }
    $reportLines.Add(('- 기존 운영 데이터 승인 대상 수정/원복은 수행하지 않음: `{0}`' -f (-not $hasApprovedTargets))) | Out-Null
}
elseif ($AllowOperationalWrites.IsPresent) {
    $reportLines.Add('- 이 게이트는 승인 대상 JSON의 메모성 필드만 일시 변경한 뒤 즉시 원복한다.') | Out-Null
    $reportLines.Add(('- 승인 대상 쓰기/원복 실행: `{0}`' -f (-not [string]::IsNullOrWhiteSpace($approvedOperationalWritesReport)))) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($approvedOperationalWritesReport)) {
        $reportLines.Add(('- 승인 대상 쓰기/원복 리포트: `{0}`' -f $approvedOperationalWritesReport)) | Out-Null
    }
}
else {
    $reportLines.Add('- 이 게이트는 기본적으로 운영 데이터를 변경하지 않는다.') | Out-Null
    $reportLines.Add('- 현재 실행에서 운영 데이터 쓰기/원복은 수행되지 않았다.') | Out-Null
}
$reportLines.Add('- 비밀번호 원문은 보고서와 로그에 기록하지 않는다.') | Out-Null

Set-Content -LiteralPath $reportPath -Value $reportLines -Encoding UTF8

Write-Host "Operational gate report: $reportPath"
Write-Host "Result: $overallStatus"
if ($overallStatus -eq 'FAIL') { exit 1 }
if ($overallStatus -eq 'BLOCKED') { exit 2 }
exit 0
