[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ChecklistPath,
    [string]$ChangedFilesPath,
    [string]$LogRoot,
    [string]$CommitMessage,
    [string]$Remote = 'origin',
    [string[]]$IncludeUntrackedPaths = @(),
    [switch]$SkipNas,
    [switch]$SkipGit,
    [switch]$SkipPush,
    [switch]$DryRun,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [string]$NasSshHost = '192.168.0.199',
    [string]$NasSshUser = 'itw',
    [int]$NasSshPort = 2222,
    [string]$NasSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$NasRemoteOpsPath = '/srv/georaeplan/ops',
    [string]$NasStateRoot = '',
    [switch]$SkipPreDeployOperationalGate,
    [string]$PreDeployBaseUrl = '',
    [string]$PreDeploySecretPath = '',
    [string]$PreDeployOutputDirectory = '',
    [string[]]$PreDeployAllowedIntegrityWarningCodes = @()
)

$ErrorActionPreference = 'Stop'

function Write-Info {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host $Message -ForegroundColor Cyan
}

function Resolve-ProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptRoot)
    return (Resolve-Path (Join-Path $ScriptRoot '..')).Path
}

function New-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding($false)
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, (New-Utf8NoBomEncoding))
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        throw 'git 명령을 찾지 못했습니다.'
    }

    Push-Location $ProjectRoot
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & $git.Source @Arguments 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $joined = (@($output) | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "git 명령이 실패했습니다. args=$($Arguments -join ' ')`n$joined"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = (@($output) | ForEach-Object { $_.ToString() })
        Text = ((@($output) | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

function Invoke-PowerShellFile {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & powershell -NoProfile -ExecutionPolicy Bypass -File $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "스크립트 실행이 실패했습니다: $FilePath"
    }
}

function Get-FirstConfiguredValue {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return $candidate.Trim()
        }
    }

    return ''
}

function Invoke-PreDeployOperationalGate {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OperationalGateScript,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [string]$SecretPath = '',
        [string]$NasStateRoot = '',
        [string[]]$AllowedIntegrityWarningCodes = @()
    )

    if (-not (Test-Path -LiteralPath $OperationalGateScript)) {
        throw "운영 게이트 스크립트를 찾지 못했습니다: $OperationalGateScript"
    }
    if ([string]::IsNullOrWhiteSpace($BaseUrl) -or $BaseUrl -eq 'https://api.example.invalid') {
        throw 'live 반영 전 운영 게이트 BaseUrl을 확인할 수 없습니다. -PreDeployBaseUrl 또는 PUBLIC_BASE_URL을 지정하세요.'
    }

    $arguments = @(
        '-ProjectRoot', $ProjectRoot,
        '-BaseUrl', $BaseUrl,
        '-OutputDirectory', $OutputDirectory,
        '-FailOnIntegrityWarnings',
        '-SkipWriteSafetyChecks'
    )
    if (-not [string]::IsNullOrWhiteSpace($NasStateRoot)) {
        $arguments += @('-NasStateRoot', $NasStateRoot)
    }
    if (-not [string]::IsNullOrWhiteSpace($SecretPath)) {
        $arguments += @('-SecretPath', $SecretPath)
    }
    if ($AllowedIntegrityWarningCodes.Count -gt 0) {
        $arguments += '-AllowedIntegrityWarningCodes'
        $arguments += $AllowedIntegrityWarningCodes
    }

    Invoke-PowerShellFile -FilePath $OperationalGateScript -Arguments $arguments
}

function Test-ChecklistChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $pattern = '(?m)^- \[(x|X)\]\s+' + [regex]::Escape($Label) + '\s*$'
    return [regex]::IsMatch($Content, $pattern)
}

function Normalize-StatusPath {
    param([Parameter(Mandatory = $true)][string]$PathText)

    $normalized = $PathText.Trim()
    if ($normalized.Length -ge 2 -and $normalized.StartsWith('"') -and $normalized.EndsWith('"')) {
        $normalized = $normalized.Substring(1, $normalized.Length - 2)
        $normalized = $normalized.Replace('\\"', '"')
        $normalized = $normalized.Replace('\\\\', '\\')
    }

    return $normalized
}

function Get-StatusEntries {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $result = Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('-c', 'core.quotepath=false', 'status', '--porcelain=v1')
    $entries = New-Object System.Collections.Generic.List[object]

    foreach ($line in ($result.Output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        if ($line.StartsWith('?? ')) {
            $entries.Add([pscustomobject]@{
                Status = '??'
                Path = Normalize-StatusPath -PathText $line.Substring(3)
                IsUntracked = $true
            }) | Out-Null
            continue
        }

        if ($line.Length -lt 4) {
            continue
        }

        $entries.Add([pscustomobject]@{
            Status = $line.Substring(0, 2)
            Path = Normalize-StatusPath -PathText $line.Substring(3)
            IsUntracked = $false
        }) | Out-Null
    }

    return $entries
}

function Test-PathInsideRoot {
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $candidate = [System.IO.Path]::GetFullPath($CandidatePath).TrimEnd('\')
    $root = [System.IO.Path]::GetFullPath($RootPath).TrimEnd('\')
    if ([string]::Equals($candidate, $root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $candidate.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-IncludePaths {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [AllowEmptyCollection()][string[]]$Paths = @()
    )

    $resolved = New-Object System.Collections.Generic.List[object]
    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $fullPath = if ([System.IO.Path]::IsPathRooted($path)) {
            [System.IO.Path]::GetFullPath($path)
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot $path))
        }

        if (-not (Test-PathInsideRoot -CandidatePath $fullPath -RootPath $ProjectRoot)) {
            throw "IncludeUntrackedPaths 경로가 저장소 밖을 가리킵니다: $path"
        }

        $gitPath = $path
        if ([System.IO.Path]::IsPathRooted($path)) {
            $gitPath = $fullPath.Substring([System.IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\').Length).TrimStart('\')
        }

        $resolved.Add([pscustomobject]@{
            InputPath = $path
            FullPath = $fullPath.TrimEnd('\')
            GitPath = $gitPath
        }) | Out-Null
    }

    return $resolved
}

function Get-UntrackedEntriesToStage {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [AllowNull()][AllowEmptyCollection()]$Entries = @(),
        [AllowNull()][AllowEmptyCollection()]$ResolvedIncludePaths = @()
    )

    $allowed = New-Object System.Collections.Generic.List[object]
    $blocked = New-Object System.Collections.Generic.List[object]
    $safeEntries = @($Entries)
    $safeResolvedIncludePaths = @($ResolvedIncludePaths)

    foreach ($entry in ($safeEntries | Where-Object { $_.IsUntracked })) {
        $entryFullPath = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot $entry.Path))
        $isAllowed = $false
        foreach ($includePath in $safeResolvedIncludePaths) {
            if (Test-PathInsideRoot -CandidatePath $entryFullPath -RootPath $includePath.FullPath) {
                $isAllowed = $true
                break
            }
        }

        if ($isAllowed) {
            $allowed.Add($entry) | Out-Null
        }
        else {
            $blocked.Add($entry) | Out-Null
        }
    }

    return [pscustomobject]@{
        Allowed = $allowed
        Blocked = $blocked
    }
}

function Get-CurrentBranch {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    return (Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')).Text.Trim()
}

function Get-CurrentCommit {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    return (Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('rev-parse', 'HEAD')).Text.Trim()
}

function Test-HasStagedChanges {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    $result = Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('diff', '--cached', '--quiet') -AllowFailure
    return $result.ExitCode -ne 0
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptRoot $scriptRoot
}
if ([string]::IsNullOrWhiteSpace($ChecklistPath)) {
    $ChecklistPath = Join-Path $scriptRoot '검증 체크리스트.md'
}
if ([string]::IsNullOrWhiteSpace($ChangedFilesPath)) {
    $ChangedFilesPath = Join-Path $scriptRoot '최근 수정 파일.md'
}
if ([string]::IsNullOrWhiteSpace($LogRoot)) {
    $LogRoot = Join-Path $scriptRoot '기록'
}

foreach ($path in @($ChecklistPath, $ChangedFilesPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "필수 파일을 찾지 못했습니다: $path"
    }
}

$buildInstallerScript = Join-Path $ProjectRoot 'tools\release\Build-GeoraePlanDesktopInstaller.ps1'
$updateAssetsScript = Join-Path $ProjectRoot 'tools\release\Publish-GeoraePlanUpdateAssets.ps1'
$nasPublishScript = Join-Path $ProjectRoot 'tools\linux\Publish-GeoraeplanLinuxPcRelease.ps1'
$operationalGateScript = Join-Path $ProjectRoot 'tools\ops\Invoke-GeoraePlanOperationalGate.ps1'
$liveReadinessScript = Join-Path $scriptRoot 'Invoke-LiveReleaseReadinessCheck.ps1'
if (-not $SkipNas) {
    foreach ($path in @($buildInstallerScript, $updateAssetsScript, $nasPublishScript, $operationalGateScript, $liveReadinessScript)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "배포 스크립트를 찾지 못했습니다: $path"
        }
    }
}

$checklistContent = Get-Content -LiteralPath $ChecklistPath -Raw
if ((Test-ChecklistChecked -Content $checklistContent -Label '이슈 있음 → NAS/Git 반영 보류') -or
    (Test-ChecklistChecked -Content $checklistContent -Label '이슈 있음 → live/Git 반영 보류')) {
    throw '체크리스트에 이슈 있음 항목이 체크되어 있어 반영을 진행할 수 없습니다.'
}
if (-not $SkipNas -and
    -not (Test-ChecklistChecked -Content $checklistContent -Label '문제 없음 → NAS 반영 가능') -and
    -not (Test-ChecklistChecked -Content $checklistContent -Label '문제 없음 → live 반영 가능')) {
    throw '체크리스트에서 "문제 없음 → live 반영 가능" 항목이 체크되지 않았습니다.'
}
if (-not $SkipGit -and -not (Test-ChecklistChecked -Content $checklistContent -Label '문제 없음 → Git 반영 가능')) {
    throw '체크리스트에서 "문제 없음 → Git 반영 가능" 항목이 체크되지 않았습니다.'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$sessionRoot = Join-Path $LogRoot ("deploy-" + $timestamp)
$livePreflightReport = Join-Path $sessionRoot 'live-preflight.md'
$livePostflightReport = Join-Path $sessionRoot 'live-postflight.md'
$preDeployOperationalGateDirectory = if ([string]::IsNullOrWhiteSpace($PreDeployOutputDirectory)) {
    Join-Path $sessionRoot 'pre-deploy-operational-gate'
}
else {
    $PreDeployOutputDirectory
}
New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null
Copy-Item -LiteralPath $ChecklistPath -Destination (Join-Path $sessionRoot '검증 체크리스트.md') -Force
Copy-Item -LiteralPath $ChangedFilesPath -Destination (Join-Path $sessionRoot '최근 수정 파일.md') -Force

$branch = Get-CurrentBranch -ProjectRoot $ProjectRoot
if ([string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') {
    throw '현재 브랜치를 확인할 수 없습니다. detached HEAD 상태에서는 자동 반영을 진행하지 않습니다.'
}
$beforeCommit = Get-CurrentCommit -ProjectRoot $ProjectRoot
$statusEntries = @(Get-StatusEntries -ProjectRoot $ProjectRoot)
$trackedEntries = @($statusEntries | Where-Object { -not $_.IsUntracked })
$resolvedIncludePaths = @(Resolve-IncludePaths -ProjectRoot $ProjectRoot -Paths $IncludeUntrackedPaths)
$untrackedResolution = Get-UntrackedEntriesToStage -ProjectRoot $ProjectRoot -Entries $statusEntries -ResolvedIncludePaths $resolvedIncludePaths

if (-not $SkipGit -and $untrackedResolution.Blocked.Count -gt 0) {
    $blockedPaths = ($untrackedResolution.Blocked | ForEach-Object { '- ' + $_.Path }) -join [Environment]::NewLine
    throw "아래 untracked 파일/폴더는 자동 Git 반영 대상이 아닙니다. -IncludeUntrackedPaths 로 명시하세요.`n$blockedPaths"
}

$summaryBuilder = New-Object System.Text.StringBuilder
[void]$summaryBuilder.AppendLine('# 검증완료 반영 로그')
[void]$summaryBuilder.AppendLine()
[void]$summaryBuilder.AppendLine("- 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$summaryBuilder.AppendLine("- 브랜치: $branch")
[void]$summaryBuilder.AppendLine("- 반영 전 커밋: $beforeCommit")
[void]$summaryBuilder.AppendLine("- DryRun: $DryRun")
[void]$summaryBuilder.AppendLine("- SkipNas: $SkipNas")
[void]$summaryBuilder.AppendLine("- SkipGit: $SkipGit")
[void]$summaryBuilder.AppendLine("- SkipPush: $SkipPush")
[void]$summaryBuilder.AppendLine()

if ($trackedEntries.Count -gt 0 -or $untrackedResolution.Allowed.Count -gt 0) {
    [void]$summaryBuilder.AppendLine('## Git 반영 대상')
    [void]$summaryBuilder.AppendLine()
    foreach ($entry in $trackedEntries) {
        [void]$summaryBuilder.AppendLine("- [$($entry.Status)] $($entry.Path)")
    }
    foreach ($entry in $untrackedResolution.Allowed) {
        [void]$summaryBuilder.AppendLine("- [??] $($entry.Path)")
    }
    [void]$summaryBuilder.AppendLine()
}

if ($untrackedResolution.Blocked.Count -gt 0) {
    [void]$summaryBuilder.AppendLine('## 미포함 untracked 경로')
    [void]$summaryBuilder.AppendLine()
    foreach ($entry in $untrackedResolution.Blocked) {
        [void]$summaryBuilder.AppendLine("- $($entry.Path)")
    }
    [void]$summaryBuilder.AppendLine()
}

Write-Utf8File -Path (Join-Path $sessionRoot '반영 로그.md') -Content ($summaryBuilder.ToString().TrimEnd())

if (-not $SkipNas) {
    Write-Info 'live 반영 사전 점검을 실행합니다.'
    Invoke-PowerShellFile -FilePath $liveReadinessScript -Arguments @(
        '-ProjectRoot', $ProjectRoot,
        '-Mode', 'Pre',
        '-Channel', 'stable',
        '-OutputPath', $livePreflightReport)

    if (-not $SkipPreDeployOperationalGate) {
        $resolvedPreDeployBaseUrl = Get-FirstConfiguredValue @(
            $PreDeployBaseUrl,
            [string][Environment]::GetEnvironmentVariable('PUBLIC_BASE_URL'),
            'https://trade.2884.kr'
        )
        Write-Info 'live 반영 전 운영 게이트를 실행합니다. 실패 시 Git/live 반영을 시작하지 않습니다.'
        Invoke-PreDeployOperationalGate `
            -ProjectRoot $ProjectRoot `
            -OperationalGateScript $operationalGateScript `
            -BaseUrl $resolvedPreDeployBaseUrl `
            -OutputDirectory $preDeployOperationalGateDirectory `
            -SecretPath $PreDeploySecretPath `
            -NasStateRoot $NasStateRoot `
            -AllowedIntegrityWarningCodes $PreDeployAllowedIntegrityWarningCodes
    }
    else {
        Write-Warning 'live 반영 전 운영 게이트를 SkipPreDeployOperationalGate 옵션으로 생략했습니다. 별도 엄격 게이트가 이미 통과한 경우에만 사용하세요.'
    }
}

if ($DryRun) {
    $dryRunSummary = @(
        '# 검증완료 반영 결과',
        '',
        "- 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "- 브랜치: $branch",
        "- 반영 전 커밋: $beforeCommit",
        '- 실행 결과: DryRun 완료',
        "- Linux PC 메인(live/stable) 반영 예정: $([bool](-not $SkipNas))",
        "- Git 반영 예정: $([bool](-not $SkipGit))",
        "- Git push 예정: $([bool](-not $SkipGit -and -not $SkipPush))",
        "- live 사전 점검 리포트: $(if ($SkipNas) { '-' } else { $livePreflightReport })",
        "- live 전 운영 게이트 리포트: $(if ($SkipNas -or $SkipPreDeployOperationalGate) { '-' } else { Join-Path $preDeployOperationalGateDirectory 'operational-gate-report.md' })"
    ) -join [Environment]::NewLine
    Write-Utf8File -Path (Join-Path $sessionRoot '반영 결과.md') -Content $dryRunSummary

    Write-Info 'DryRun 모드입니다. 실제 live/Git 반영은 수행하지 않았습니다.'
    Write-Info "- 브랜치: $branch"
    Write-Info "- 반영 전 커밋: $beforeCommit"
    if (-not $SkipGit) {
        Write-Info "- Git 반영 대상 수: $($trackedEntries.Count + $untrackedResolution.Allowed.Count)"
    }
    if (-not $SkipNas) {
        Write-Info '- Linux PC 메인(live/stable) 반영 스크립트가 실행될 준비가 되어 있습니다.'
    }
    Write-Info "- 로그 폴더: $sessionRoot"
    if (-not $SkipNas) {
        Write-Info "- live 사전 점검 리포트: $livePreflightReport"
    }
    exit 0
}

if (-not $SkipGit) {
    $hasPotentialCommit = $trackedEntries.Count -gt 0 -or $untrackedResolution.Allowed.Count -gt 0
    if ($hasPotentialCommit -and [string]::IsNullOrWhiteSpace($CommitMessage)) {
        $CommitMessage = Read-Host 'Git 커밋 메시지를 입력하세요'
        if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
            throw 'Git 반영 대상이 있으므로 커밋 메시지를 입력해야 합니다.'
        }
    }

    if ($trackedEntries.Count -gt 0) {
        Write-Info 'tracked 변경 파일을 stage 합니다.'
        Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('add', '-u') | Out-Null
    }

    foreach ($includePath in $resolvedIncludePaths) {
        Write-Info "untracked 경로를 stage 합니다: $($includePath.GitPath)"
        Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('add', '--', $includePath.GitPath) | Out-Null
    }

    if (Test-HasStagedChanges -ProjectRoot $ProjectRoot) {
        Write-Info 'Git 커밋을 생성합니다.'
        Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('commit', '-m', $CommitMessage) | Out-Null
    }
    else {
        Write-Info '새로 커밋할 Git 변경사항이 없습니다.'
    }

    if (-not $SkipPush) {
        Write-Info "원격 저장소로 push 합니다: $Remote/$branch"
        Invoke-Git -ProjectRoot $ProjectRoot -Arguments @('push', $Remote, $branch) | Out-Null
    }
}

if (-not $SkipNas) {
    Write-Info '데스크톱 설치 패키지를 생성합니다.'
    Invoke-PowerShellFile -FilePath $buildInstallerScript -Arguments @('-ProjectRoot', $ProjectRoot)

    Write-Info '메인(live) 업데이트 자산(stable 채널)을 생성합니다.'
    Invoke-PowerShellFile -FilePath $updateAssetsScript -Arguments @('-ProjectRoot', $ProjectRoot, '-Channel', 'stable')

    $nasArgs = @('-ProjectRoot', $ProjectRoot, '-MirrorToLive')
    if ($AllowLegacyLiveMirror) {
        $nasArgs += '-AllowLegacyLiveMirror'
    }
    if ($AllowScheduledApplyTrigger) {
        $nasArgs += '-AllowScheduledApplyTrigger'
    }
    if (-not [string]::IsNullOrWhiteSpace($NasSshHost)) {
        $nasArgs += @('-LinuxSshHost', $NasSshHost)
    }
    if (-not [string]::IsNullOrWhiteSpace($NasSshUser)) {
        $nasArgs += @('-LinuxSshUser', $NasSshUser)
    }
    if ($NasSshPort -gt 0) {
        $nasArgs += @('-LinuxSshPort', $NasSshPort.ToString())
    }
    if (-not [string]::IsNullOrWhiteSpace($NasSshKeyPath)) {
        $nasArgs += @('-LinuxSshKeyPath', $NasSshKeyPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($NasRemoteOpsPath)) {
        $nasArgs += @('-LinuxRemoteOpsPath', $NasRemoteOpsPath)
    }
    if ($SkipPreDeployOperationalGate) {
        $nasArgs += '-SkipPreDeployOperationalGate'
    }
    if (-not [string]::IsNullOrWhiteSpace($PreDeployBaseUrl)) {
        $nasArgs += @('-PreDeployBaseUrl', $PreDeployBaseUrl)
    }
    if (-not [string]::IsNullOrWhiteSpace($PreDeploySecretPath)) {
        $nasArgs += @('-PreDeploySecretPath', $PreDeploySecretPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($PreDeployOutputDirectory)) {
        $nasArgs += @('-PreDeployOutputDirectory', $PreDeployOutputDirectory)
    }
    if ($PreDeployAllowedIntegrityWarningCodes.Count -gt 0) {
        $nasArgs += '-PreDeployAllowedIntegrityWarningCodes'
        $nasArgs += $PreDeployAllowedIntegrityWarningCodes
    }

    Write-Info '메인(live) stable 배포본의 Linux PC 반영을 진행합니다.'
    Invoke-PowerShellFile -FilePath $nasPublishScript -Arguments $nasArgs

    Write-Info 'live 반영 사후 점검을 실행합니다.'
    Invoke-PowerShellFile -FilePath $liveReadinessScript -Arguments @(
        '-ProjectRoot', $ProjectRoot,
        '-Mode', 'Post',
        '-Channel', 'stable',
        '-OutputPath', $livePostflightReport)
}

$afterCommit = Get-CurrentCommit -ProjectRoot $ProjectRoot
$finalSummary = @(
    '# 검증완료 반영 결과',
    '',
    "- 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "- 브랜치: $branch",
    "- 반영 전 커밋: $beforeCommit",
    "- 반영 후 커밋: $afterCommit",
    "- Linux PC 메인(live/stable) 반영 수행: $([bool](-not $SkipNas))",
    "- Git 반영 수행: $([bool](-not $SkipGit))",
    "- Git push 수행: $([bool](-not $SkipGit -and -not $SkipPush))",
    "- live 사전 점검 리포트: $(if ($SkipNas) { '-' } else { $livePreflightReport })",
    "- live 전 운영 게이트 리포트: $(if ($SkipNas -or $SkipPreDeployOperationalGate) { '-' } else { Join-Path $preDeployOperationalGateDirectory 'operational-gate-report.md' })",
    "- live 사후 점검 리포트: $(if ($SkipNas) { '-' } else { $livePostflightReport })"
) -join [Environment]::NewLine
Write-Utf8File -Path (Join-Path $sessionRoot '반영 결과.md') -Content $finalSummary

Write-Host '검증완료 반영이 끝났습니다.' -ForegroundColor Green
Write-Host "- 로그 폴더: $sessionRoot" -ForegroundColor Green
Write-Host "- 현재 커밋: $afterCommit" -ForegroundColor Green
if (-not $SkipNas) {
    Write-Host "- live 사전 점검 리포트: $livePreflightReport" -ForegroundColor Green
    if (-not $SkipPreDeployOperationalGate) {
        Write-Host "- live 전 운영 게이트 리포트: $(Join-Path $preDeployOperationalGateDirectory 'operational-gate-report.md')" -ForegroundColor Green
    }
    Write-Host "- live 사후 점검 리포트: $livePostflightReport" -ForegroundColor Green
}
