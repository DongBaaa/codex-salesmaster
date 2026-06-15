[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$Configuration = 'Release',
    [string]$ReleaseId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipBuild,
    [switch]$MirrorToLive,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$LinuxRemoteOpsPath = '/srv/georaeplan/ops',
    [int]$KeepReleaseCount = 2,
    [switch]$SkipConfigSync,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [switch]$SkipPreDeployOperationalGate,
    [switch]$SkipPostDeployOperationalGate,
    [switch]$SkipPlatformHealthChecks,
    [string]$PreDeployBaseUrl = '',
    [string]$PreDeploySecretPath = '',
    [string]$PreDeployOutputDirectory = '',
    [string[]]$PreDeployAllowedIntegrityWarningCodes = @(),
    [string]$PostDeployBaseUrl = '',
    [string]$PostDeploySecretPath = '',
    [string]$PostDeployOutputDirectory = '',
    [string[]]$PostDeployAllowedIntegrityWarningCodes = @()
)

$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([string]$ExplicitProjectRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitProjectRoot)) {
        return (Resolve-Path -LiteralPath $ExplicitProjectRoot).Path
    }

    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
}

function Resolve-SshExecutable {
    $windowsSsh = 'C:\Windows\System32\OpenSSH\ssh.exe'
    if (Test-Path -LiteralPath $windowsSsh) {
        return $windowsSsh
    }

    $ssh = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -ne $ssh) {
        return $ssh.Source
    }

    throw 'ssh 실행 파일을 찾지 못했습니다.'
}

function Convert-ToSingleQuotedShellLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-PublicHealthCheck {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Url
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 15
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
            throw "status=$($response.StatusCode)"
        }

        Write-Host "linux_pc_public_health_ok name=$Name status=$($response.StatusCode) url=$Url"
    }
    catch {
        throw "Linux PC 배포 전후 공개 URL 확인 실패: name=$Name url=$Url error=$($_.Exception.Message)"
    }
}

function Invoke-RemoteReadOnlyCheck {
    param(
        [Parameter(Mandatory = $true)][string]$SshExe,
        [Parameter(Mandatory = $true)][string]$HostName,
        [Parameter(Mandatory = $true)][string]$UserName,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$KeyPath,
        [Parameter(Mandatory = $true)][string]$RemoteOpsPath
    )

    if (-not (Test-Path -LiteralPath $KeyPath)) {
        throw "Linux PC SSH 키를 찾지 못했습니다: $KeyPath"
    }

    $quotedOpsPath = Convert-ToSingleQuotedShellLiteral -Value $RemoteOpsPath
    $remoteCommand = @(
        'set -e',
        "test -d $quotedOpsPath",
        "test -f $quotedOpsPath/apply-release.sh",
        "bash -n $quotedOpsPath/apply-release.sh",
        "docker ps --format '{{.Names}} {{.Status}}' | grep -E 'georaeplan|workplan' || true"
    ) -join '; '

    $output = & $SshExe `
        -i $KeyPath `
        -p $Port `
        -o BatchMode=yes `
        -o StrictHostKeyChecking=accept-new `
        -o ConnectTimeout=10 `
        "$UserName@$HostName" `
        $remoteCommand 2>&1

    if ($LASTEXITCODE -ne 0) {
        $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        throw "Linux PC SSH 사전 확인 실패: $text"
    }

    Write-Host 'linux_pc_remote_readonly_check_ok'
    $output | ForEach-Object { Write-Host $_ }
}

$ProjectRoot = Resolve-ProjectRoot -ExplicitProjectRoot $ProjectRoot
$nasPublishScript = Join-Path $ProjectRoot 'tools\nas\Publish-GeoraeplanNasRelease.ps1'
if (-not (Test-Path -LiteralPath $nasPublishScript)) {
    throw "기존 release apply 스크립트를 찾지 못했습니다: $nasPublishScript"
}

if ([string]::IsNullOrWhiteSpace($LinuxSshHost) -or
    [string]::IsNullOrWhiteSpace($LinuxSshUser) -or
    [string]::IsNullOrWhiteSpace($LinuxRemoteOpsPath)) {
    throw 'Linux PC SSH host/user/remote ops path 설정이 필요합니다.'
}

$sshExe = Resolve-SshExecutable

if ($AllowLegacyLiveMirror) {
    Write-Warning 'AllowLegacyLiveMirror는 Linux PC 배포 래퍼에서 사용하지 않습니다. apply-release.sh 기반 반영만 허용합니다.'
}
if ($AllowScheduledApplyTrigger) {
    Write-Warning 'AllowScheduledApplyTrigger는 Linux PC 배포 래퍼에서 사용하지 않습니다. SSH 직접 apply-release.sh 실행을 사용합니다.'
}

if ($MirrorToLive -and -not $SkipPlatformHealthChecks) {
    Invoke-PublicHealthCheck -Name 'trade' -Url 'https://trade.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'work' -Url 'https://work.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'itw' -Url 'https://itw.2884.kr/'
    Invoke-RemoteReadOnlyCheck `
        -SshExe $sshExe `
        -HostName $LinuxSshHost `
        -UserName $LinuxSshUser `
        -Port $LinuxSshPort `
        -KeyPath $LinuxSshKeyPath `
        -RemoteOpsPath $LinuxRemoteOpsPath
}

$nasArgs = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $nasPublishScript,
    '-ProjectRoot', $ProjectRoot,
    '-Configuration', $Configuration,
    '-ReleaseId', $ReleaseId,
    '-NasSshHost', $LinuxSshHost,
    '-NasSshUser', $LinuxSshUser,
    '-NasSshPort', $LinuxSshPort.ToString(),
    '-NasSshKeyPath', $LinuxSshKeyPath,
    '-NasRemoteOpsPath', $LinuxRemoteOpsPath,
    '-KeepNasReleaseCount', $KeepReleaseCount.ToString(),
    '-DeploymentTargetName', 'Linux PC',
    '-LogPrefix', 'linux_pc',
    '-SkipConfigSync'
)

if ($SkipBuild) {
    $nasArgs += '-SkipBuild'
}
if ($MirrorToLive) {
    $nasArgs += '-MirrorToLive'
}
if ($SkipPreDeployOperationalGate) {
    $nasArgs += '-SkipPreDeployOperationalGate'
}
if ($SkipPostDeployOperationalGate) {
    $nasArgs += '-SkipPostDeployOperationalGate'
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
if (-not [string]::IsNullOrWhiteSpace($PostDeployBaseUrl)) {
    $nasArgs += @('-PostDeployBaseUrl', $PostDeployBaseUrl)
}
if (-not [string]::IsNullOrWhiteSpace($PostDeploySecretPath)) {
    $nasArgs += @('-PostDeploySecretPath', $PostDeploySecretPath)
}
if (-not [string]::IsNullOrWhiteSpace($PostDeployOutputDirectory)) {
    $nasArgs += @('-PostDeployOutputDirectory', $PostDeployOutputDirectory)
}
if ($PostDeployAllowedIntegrityWarningCodes.Count -gt 0) {
    $nasArgs += '-PostDeployAllowedIntegrityWarningCodes'
    $nasArgs += $PostDeployAllowedIntegrityWarningCodes
}

Write-Host "linux_pc_release_start host=$LinuxSshHost user=$LinuxSshUser port=$LinuxSshPort ops=$LinuxRemoteOpsPath release_id=$ReleaseId"
& powershell @nasArgs
if ($LASTEXITCODE -ne 0) {
    throw "Linux PC release failed with exit code $LASTEXITCODE."
}
Write-Host "linux_pc_release_done release_id=$ReleaseId"

if ($MirrorToLive -and -not $SkipPlatformHealthChecks) {
    Invoke-PublicHealthCheck -Name 'trade' -Url 'https://trade.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'work' -Url 'https://work.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'itw' -Url 'https://itw.2884.kr/'
}
