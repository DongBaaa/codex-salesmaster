[CmdletBinding()]
param(
    [string]$ReplicaId = 'e38c1febbb6866dcc00aa7a6d84044b3',
    [string]$EvidenceLogPath = '',
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ReplicaId) -or $ReplicaId -notmatch '^[a-f0-9]{32}$') {
    throw 'ReplicaId must be the expected 32-character lowercase hexadecimal identifier.'
}

if ([string]::IsNullOrWhiteSpace($EvidenceLogPath)) {
    $EvidenceLogPath = Join-Path `
        (Get-Location).Path `
        ("outputs\multi-database-recovery-readiness-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

$evidenceDirectory = Split-Path -Parent $EvidenceLogPath
if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
}

$scheduleInstaller = Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupSchedule.ps1'
$replicaInstaller = Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupReplica.ps1'
$restoreInstaller = Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupRestoreDrill.ps1'
foreach ($path in @($scheduleInstaller, $replicaInstaller, $restoreInstaller)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required installer was not found: $path"
    }
}

$transcriptStarted = $false
try {
    Start-Transcript -LiteralPath $EvidenceLogPath -Force | Out-Null
    $transcriptStarted = $true

    Write-Host '다중 DB 백업·복제·격리 복구훈련을 시작합니다.'
    Write-Host '이 작업은 거래플랜 DB나 서비스를 재시작하지 않으며, 복구는 격리 임시 DB에서 수행합니다.'
    $credential = Get-Credential `
        -UserName $LinuxSshUser `
        -Message 'Linux PC 관리자 암호를 입력하세요. 백업·복제·격리 복구훈련에만 사용됩니다.'
    if ($null -eq $credential -or $null -eq $credential.Password) {
        throw '관리자 인증 입력이 취소되었습니다.'
    }

    & $scheduleInstaller `
        -Apply `
        -RunAfterInstall `
        -SudoCredential $credential `
        -LinuxSshHost $LinuxSshHost `
        -LinuxSshUser $LinuxSshUser `
        -LinuxSshPort $LinuxSshPort `
        -LinuxSshKeyPath $LinuxSshKeyPath
    if ($LASTEXITCODE -ne 0) {
        throw "Backup schedule installer exited with code $LASTEXITCODE."
    }
    Write-Host 'multi_database_backup_apply_and_run=ok'

    & $replicaInstaller `
        -ReplicaId $ReplicaId `
        -Apply `
        -RunAfterInstall `
        -SudoCredential $credential `
        -LinuxSshHost $LinuxSshHost `
        -LinuxSshUser $LinuxSshUser `
        -LinuxSshPort $LinuxSshPort `
        -LinuxSshKeyPath $LinuxSshKeyPath
    if ($LASTEXITCODE -ne 0) {
        throw "Backup replica installer exited with code $LASTEXITCODE."
    }
    Write-Host 'multi_database_replica_apply_and_run=ok'

    & $restoreInstaller `
        -ReplicaId $ReplicaId `
        -Apply `
        -RunAfterInstall `
        -SudoCredential $credential `
        -LinuxSshHost $LinuxSshHost `
        -LinuxSshUser $LinuxSshUser `
        -LinuxSshPort $LinuxSshPort `
        -LinuxSshKeyPath $LinuxSshKeyPath
    if ($LASTEXITCODE -ne 0) {
        throw "Backup restore drill installer exited with code $LASTEXITCODE."
    }
    Write-Host 'multi_database_restore_drill_apply_and_run=ok'
    Write-Host 'multi_database_recovery_readiness=ok'
}
catch {
    Write-Error $_
    throw
}
finally {
    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
    }
}

Write-Host "evidence_log=$EvidenceLogPath"
