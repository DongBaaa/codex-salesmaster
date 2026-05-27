param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\realtime-revision-smoke'),
    [int]$WaitTimeoutSeconds = 10,
    [switch]$KeepTemporaryData
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Read-ErrorResponseBody {
    param([System.Net.WebResponse]$Response)
    if ($null -eq $Response) { return '' }
    try {
        $stream = $Response.GetResponseStream()
        if ($null -eq $stream) { return '' }
        $reader = New-Object System.IO.StreamReader($stream)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    catch {
        return ''
    }
}

function ConvertTo-BodyJson {
    param([object]$Body)
    if ($null -eq $Body) { return $null }
    return ($Body | ConvertTo-Json -Depth 30 -Compress)
}

function Invoke-Api {
    param(
        [ValidateSet('GET','POST','PUT','DELETE')][string]$Method,
        [string]$Path,
        [hashtable]$Headers = @{},
        [object]$Body = $null,
        [int[]]$ExpectedStatus = @(200),
        [int]$TimeoutSec = 30
    )

    $uri = if ($Path.StartsWith('http', [System.StringComparison]::OrdinalIgnoreCase)) { $Path } else { "$BaseUrl/$($Path.TrimStart('/'))" }
    $params = @{
        Method = $Method
        Uri = $uri
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
    }
    if ($Headers) { $params.Headers = $Headers }
    if ($PSBoundParameters.ContainsKey('Body') -and $null -ne $Body) {
        $params.Body = ConvertTo-BodyJson $Body
        $params.ContentType = 'application/json; charset=utf-8'
    }

    $status = 0
    $content = ''
    try {
        $response = Invoke-WebRequest @params
        $status = [int]$response.StatusCode
        $content = [string]$response.Content
    }
    catch {
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            $content = Read-ErrorResponseBody $_.Exception.Response
        }
        else {
            throw
        }
    }

    if ($ExpectedStatus -notcontains $status) {
        throw "API $Method $uri failed. status=$status expected=$($ExpectedStatus -join ',') body=$content"
    }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try { $parsed = $content | ConvertFrom-Json }
        catch { $parsed = $content }
    }

    [pscustomobject]@{
        Status = $status
        Body = $parsed
        Raw = $content
    }
}

function New-WaitJob {
    param(
        [string]$WaitUri,
        [string]$Token,
        [int]$TimeoutSec
    )

    Start-Job -ScriptBlock {
        param($Uri, $BearerToken, $RequestTimeout)
        $headers = @{ Authorization = "Bearer $BearerToken" }
        $response = Invoke-WebRequest -Method GET -Uri $Uri -Headers $headers -UseBasicParsing -TimeoutSec ($RequestTimeout + 5)
        [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Raw = [string]$response.Content
        }
    } -ArgumentList $WaitUri, $Token, $TimeoutSec
}

function Remove-CustomerQuietly {
    param([Guid]$CustomerId, [hashtable]$Headers)
    try {
        $current = Invoke-Api -Method GET -Path "customers/$CustomerId" -Headers $Headers -ExpectedStatus @(200,404)
        if ($null -eq $current.Body) {
            return [pscustomobject]@{ Entity = 'customer'; Result = 'not-found' }
        }

        $revision = [Int64]$current.Body.revision
        Invoke-Api -Method DELETE -Path "customers/$($CustomerId)?expectedRevision=$revision" -Headers $Headers -ExpectedStatus @(204,404) | Out-Null
        [pscustomobject]@{ Entity = 'customer'; Result = 'deleted' }
    }
    catch {
        [pscustomobject]@{ Entity = 'customer'; Result = "cleanup-failed: $($_.Exception.Message)" }
    }
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $EvidenceDirectory "realtime-revision-smoke-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "realtime-revision-smoke-$timestamp.md"
$steps = New-Object System.Collections.Generic.List[object]
$cleanup = New-Object System.Collections.Generic.List[object]
$waitJob = $null
$customerId = [Guid]::NewGuid()
$suffix = $timestamp.Replace('-', '')

try {
    $login = Invoke-Api -Method POST -Path 'auth/login' -Body @{ username = $Username; password = $Password } -ExpectedStatus @(200)
    if ([string]::IsNullOrWhiteSpace($login.Body.token)) {
        throw '로그인 토큰을 받지 못했습니다.'
    }

    $headers = @{ Authorization = "Bearer $($login.Body.token)" }
    $steps.Add([pscustomobject]@{ Step = 'login'; Result = 'PASS' })

    $before = (Invoke-Api -Method GET -Path 'sync/status' -Headers $headers -ExpectedStatus @(200)).Body
    $beforeRevision = [Int64]$before.currentServerRevision
    $steps.Add([pscustomobject]@{ Step = 'sync-status-before'; Result = 'PASS'; Revision = $beforeRevision })

    $waitUri = "$($BaseUrl.TrimEnd('/'))/sync/wait?sinceRev=$beforeRevision&timeoutSeconds=$WaitTimeoutSeconds"
    $waitJob = New-WaitJob -WaitUri $waitUri -Token $login.Body.token -TimeoutSec $WaitTimeoutSeconds
    Start-Sleep -Milliseconds 750
    $steps.Add([pscustomobject]@{ Step = 'wait-started'; Result = 'PASS'; SinceRevision = $beforeRevision })

    $customer = (Invoke-Api -Method POST -Path 'customers' -Headers $headers -Body @{
        id = $customerId
        tenantCode = 'USENET'
        officeCode = 'USENET'
        responsibleOfficeCode = 'USENET'
        nameOriginal = "Realtime revision customer $suffix"
        nameMatchKey = "REALTIMEREVISIONCUSTOMER$suffix"
        tradeType = '매출'
        phone = '000-0000-0000'
        notes = 'created for realtime revision smoke'
    } -ExpectedStatus @(200)).Body
    $steps.Add([pscustomobject]@{ Step = 'fixture-customer-create'; Result = 'PASS'; Id = $customer.id; Revision = [Int64]$customer.revision })

    $completed = Wait-Job -Job $waitJob -Timeout ($WaitTimeoutSeconds + 8)
    if ($null -eq $completed) {
        throw "sync/wait가 제한 시간 내에 완료되지 않았습니다. sinceRev=$beforeRevision timeoutSeconds=$WaitTimeoutSeconds"
    }

    $waitOutput = Receive-Job -Job $waitJob -ErrorAction Stop
    $waitBody = $waitOutput.Raw | ConvertFrom-Json
    $waitRevision = [Int64]$waitBody.currentServerRevision
    if ($waitRevision -le $beforeRevision) {
        throw "sync/wait가 서버 revision 증가를 반환하지 않았습니다. before=$beforeRevision wait=$waitRevision"
    }

    $steps.Add([pscustomobject]@{ Step = 'sync-wait-detected-change'; Result = 'PASS'; BeforeRevision = $beforeRevision; WaitRevision = $waitRevision })

    $after = (Invoke-Api -Method GET -Path 'sync/status' -Headers $headers -ExpectedStatus @(200)).Body
    $afterRevision = [Int64]$after.currentServerRevision
    if ($afterRevision -le $beforeRevision) {
        throw "sync/status가 서버 revision 증가를 반환하지 않았습니다. before=$beforeRevision after=$afterRevision"
    }

    $steps.Add([pscustomobject]@{ Step = 'sync-status-after'; Result = 'PASS'; Revision = $afterRevision })

    if (-not $KeepTemporaryData) {
        $cleanup.Add((Remove-CustomerQuietly -CustomerId $customerId -Headers $headers))
    }

    $result = [pscustomobject][ordered]@{
        Result = 'PASS'
        BaseUrl = $BaseUrl
        BeforeRevision = $beforeRevision
        WaitRevision = $waitRevision
        AfterRevision = $afterRevision
        CreatedCustomerId = $customerId
        Steps = @($steps.ToArray())
        Cleanup = @($cleanup.ToArray())
    }
}
catch {
    $errorMessage = [string]$_.Exception.Message
    if ($waitJob -and $waitJob.State -eq 'Running') {
        Stop-Job -Job $waitJob -Force -ErrorAction SilentlyContinue
    }

    $result = [pscustomobject][ordered]@{
        Result = 'FAIL'
        BaseUrl = $BaseUrl
        CreatedCustomerId = $customerId
        ErrorMessage = $errorMessage
        Steps = @($steps.ToArray())
        Cleanup = @($cleanup.ToArray())
    }
}
finally {
    if ($waitJob) {
        Remove-Job -Job $waitJob -Force -ErrorAction SilentlyContinue
    }
}

$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = @(
    "# Realtime Revision Smoke - $($result.Result)",
    "",
    "- BaseUrl: $BaseUrl",
    "- CreatedCustomerId: $customerId"
)
if ($result.PSObject.Properties.Name -contains 'BeforeRevision') {
    $lines += "- BeforeRevision: $($result.BeforeRevision)"
    $lines += "- WaitRevision: $($result.WaitRevision)"
    $lines += "- AfterRevision: $($result.AfterRevision)"
}
if ($result.Result -ne 'PASS') {
    $lines += "- Error: $($result.ErrorMessage)"
}
$lines += ""
$lines += "## Steps"
foreach ($step in @($result.Steps)) {
    $lines += "- $($step.Step): $($step.Result)"
}
$lines += ""
$lines += "## Cleanup"
foreach ($item in @($result.Cleanup)) {
    $lines += "- $($item.Entity): $($item.Result)"
}
$lines | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "realtime_revision_smoke_report=$mdPath"
Write-Host "realtime_revision_smoke_json=$jsonPath"
Write-Host "result=$($result.Result)"

if ($result.Result -ne 'PASS') {
    throw $result.ErrorMessage
}
