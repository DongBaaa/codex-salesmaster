[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$BaseUrl = "https://api.example.invalid",
    [string]$Channel = "stable",
    [string]$SecretPath = "D:\거래플랜-운영검증-secrets.json",
    [string]$ApprovedTargetsPath = "",
    [string]$NasStateRoot = "\\192.0.2.10\docker\georaeplan\ops\state",
    [string]$OutputDirectory = "",
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

"# operational gate log $(Get-Date -Format o)" | Set-Content -LiteralPath $logPath -Encoding UTF8
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ProjectRoot=$resolvedRoot"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "BaseUrl=$BaseUrl"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "Channel=$Channel"
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

$manifest = Invoke-TextProbe -Uri ($BaseUrl + "/updates/manifest?channel=$Channel")
$desktopVersion = ''
$androidVersion = ''
$desktopPackageUrl = ''
$androidPackageUrl = ''
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

$liveObservationScript = Join-Path $resolvedRoot '테스트 시행\Invoke-LiveObservationCheck.ps1'
$liveObservationReport = Join-Path $OutputDirectory 'live-observation.md'
if (Test-Path -LiteralPath $liveObservationScript) {
    try {
        $scriptOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $liveObservationScript -ProjectRoot $resolvedRoot -BaseUrl $BaseUrl -Channel $Channel -SampleCount 2 -IntervalSeconds 5 -OutputPath $liveObservationReport 2>&1 | Out-String -Width 4096
        Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## live observation script"
        Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $scriptOutput
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $liveObservationReport)) {
            Add-Check -Checks $checks -Name 'live observation' -Status 'PASS' -Detail $liveObservationReport
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

if (Test-Path -LiteralPath $NasStateRoot) {
    $dailyPath = Join-Path $NasStateRoot 'daily-check-status.txt'
    $backupPath = Join-Path $NasStateRoot 'backup-status.txt'
    $replicaPath = Join-Path $NasStateRoot 'external-replica-status.txt'
    $certPath = Join-Path $NasStateRoot 'cert-status.txt'

    $daily = if (Test-Path -LiteralPath $dailyPath) { Get-Content -LiteralPath $dailyPath -Raw } else { '' }
    $backup = if (Test-Path -LiteralPath $backupPath) { Get-Content -LiteralPath $backupPath -Raw } else { '' }
    $replica = if (Test-Path -LiteralPath $replicaPath) { Get-Content -LiteralPath $replicaPath -Raw } else { '' }
    $cert = if (Test-Path -LiteralPath $certPath) { Get-Content -LiteralPath $certPath -Raw } else { '' }

    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "`n## NAS state"
    Get-ChildItem -LiteralPath $NasStateRoot -File | Sort-Object Name | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize | Out-String -Width 4096 | Add-Content -LiteralPath $logPath -Encoding UTF8
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "daily=$daily"
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "backup=$backup"
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "replica=$replica"
    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "cert=$cert"

    $dailyEndpointOk = ($daily -match 'healthz=ok') -or ($daily -match 'readyz=ok')
    $nasOk = $dailyEndpointOk -and ($daily -match 'manifest=ok') -and ($daily -match 'backup=ok') -and ($daily -match 'replica=ok') -and ($backup -match 'backup=ok') -and ($replica -match 'replica=ok') -and ($cert -match 'cert=ok')
    if ($nasOk) {
        Add-Check -Checks $checks -Name 'NAS status files' -Status 'PASS' -Detail 'daily endpoint/manifest/backup/replica/cert ok'
    }
    else {
        Add-Check -Checks $checks -Name 'NAS status files' -Status 'WARN' -Detail 'NAS files readable but one or more ok markers missing'
    }
}
else {
    Add-Check -Checks $checks -Name 'NAS status files' -Status 'WARN' -Detail ("NAS state root not accessible: {0}" -f $NasStateRoot)
}

$secrets = Read-SecretFile -Path $SecretPath
$itworld = Get-AccountCredential -Secrets $secrets -Alias 'itworld' -UsernameEnvName 'GEORAEPLAN_SCOPE_ITWORLD_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_ITWORLD_PASSWORD'
$usenet = Get-AccountCredential -Secrets $secrets -Alias 'usenet' -UsernameEnvName 'GEORAEPLAN_SCOPE_USENET_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_USENET_PASSWORD'
$yeonsu = Get-AccountCredential -Secrets $secrets -Alias 'yeonsu' -UsernameEnvName 'GEORAEPLAN_SCOPE_YEONSU_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_YEONSU_PASSWORD'
$admin = Get-AccountCredential -Secrets $secrets -Alias 'admin' -UsernameEnvName 'GEORAEPLAN_SCOPE_ADMIN_USERNAME' -PasswordEnvName 'GEORAEPLAN_SCOPE_ADMIN_PASSWORD'
$accounts = @($admin, $itworld, $usenet, $yeonsu)

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
if (Test-Path -LiteralPath $ApprovedTargetsPath) {
    try {
        $targets = Get-Content -LiteralPath $ApprovedTargetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $safety = Get-JsonPropertyValue -Object $targets -Name 'safety'
        $safetyBackupConfirmed = [bool](Get-JsonPropertyValue -Object $safety -Name 'backupConfirmed')
        $safetyRestorePossible = [bool](Get-JsonPropertyValue -Object $safety -Name 'restorePossible')
        $safetyApprovedBy = [string](Get-JsonPropertyValue -Object $safety -Name 'approvedBy')
        foreach ($name in @('customers','items','invoices','payments','rentalAssets','rentalBillingProfiles','inventoryTransfers')) {
            $targetCounts[$name] = Get-TargetCount -Targets $targets -PropertyName $name
        }
        Add-Check -Checks $checks -Name 'approved target file' -Status 'PASS' -Detail 'approved target JSON parsed'
    }
    catch {
        Add-Check -Checks $checks -Name 'approved target file' -Status 'FAIL' -Detail $_.Exception.Message
    }
}
else {
    Add-Check -Checks $checks -Name 'approved target file' -Status 'BLOCKED' -Detail ("not found: {0}" -f $ApprovedTargetsPath)
}

if ($null -ne $targets) {
    if ($safetyBackupConfirmed -and $safetyRestorePossible -and -not [string]::IsNullOrWhiteSpace($safetyApprovedBy)) {
        Add-Check -Checks $checks -Name 'write safety metadata' -Status 'PASS' -Detail 'backupConfirmed/restorePossible/approvedBy present'
    }
    else {
        Add-Check -Checks $checks -Name 'write safety metadata' -Status 'BLOCKED' -Detail 'backupConfirmed, restorePossible, or approvedBy is missing/false'
    }
}
else {
    Add-Check -Checks $checks -Name 'write safety metadata' -Status 'BLOCKED' -Detail 'approved target file is unavailable'
}

if ($AllowOperationalWrites.IsPresent) {
    Add-Check -Checks $checks -Name 'operational writes' -Status 'BLOCKED' -Detail 'This gate intentionally does not mutate production data. Use a dedicated approved write/rollback runner after all gates pass.'
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
    $reportLines.Add('- 승인 대상 JSON이 없어 운영 쓰기 검증은 차단됨') | Out-Null
}

$reportLines.Add('') | Out-Null
$reportLines.Add('## 4. 운영 데이터 변경 여부') | Out-Null
$reportLines.Add('') | Out-Null
$reportLines.Add('- 이 게이트는 기본적으로 운영 데이터를 변경하지 않는다.') | Out-Null
$reportLines.Add('- 현재 실행에서 운영 데이터 쓰기/원복은 수행되지 않았다.') | Out-Null
$reportLines.Add('- 비밀번호 원문은 보고서와 로그에 기록하지 않는다.') | Out-Null

Set-Content -LiteralPath $reportPath -Value $reportLines -Encoding UTF8

Write-Host "Operational gate report: $reportPath"
Write-Host "Result: $overallStatus"
if ($overallStatus -eq 'FAIL') { exit 1 }
if ($overallStatus -eq 'BLOCKED') { exit 2 }
exit 0
