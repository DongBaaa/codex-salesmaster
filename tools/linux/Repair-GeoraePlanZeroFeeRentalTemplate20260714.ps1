[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Rollback,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteRoot = '/srv/georaeplan'
)

$ErrorActionPreference = 'Stop'
if ($Apply -and $Rollback) { throw '-Apply and -Rollback cannot be used together.' }

$ssh = 'C:\Windows\System32\OpenSSH\ssh.exe'
$scp = 'C:\Windows\System32\OpenSSH\scp.exe'
$target = "$LinuxSshUser@$LinuxSshHost"
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$remoteDirectory = "/tmp/georaeplan-zero-fee-template-$timestamp"
$applySql = Join-Path $PSScriptRoot 'Repair-GeoraePlanZeroFeeRentalTemplate20260714.sql'
$rollbackSql = Join-Path $PSScriptRoot 'Repair-GeoraePlanZeroFeeRentalTemplate20260714.rollback.sql'
$sshArgs = @('-i', $LinuxSshKeyPath, '-p', $LinuxSshPort, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes')
$scpArgs = @('-i', $LinuxSshKeyPath, '-P', $LinuxSshPort, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes')

function Invoke-Remote([string]$Command) {
    & $ssh @sshArgs $target $Command
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed with exit code $LASTEXITCODE" }
}

Invoke-RestMethod 'https://trade.2884.kr/healthz' -TimeoutSec 30 | Out-Null
Invoke-Remote "mkdir -p '$remoteDirectory'; chmod 700 '$remoteDirectory'"
try {
    foreach ($path in @($applySql, $rollbackSql)) {
        & $scp @scpArgs $path "${target}:$remoteDirectory/$([IO.Path]::GetFileName($path))"
        if ($LASTEXITCODE -ne 0) { throw "Upload failed: $path" }
    }

    if ($Rollback) {
        Invoke-Remote "docker exec -i georaeplan-postgres-1 psql -U georaeplan -d georaeplan -v ON_ERROR_STOP=1 -P pager=off < '$remoteDirectory/$([IO.Path]::GetFileName($rollbackSql))'"
        return
    }

    if ($Apply) {
        $backupDirectory = "$RemoteRoot/backups/maintenance/$timestamp-zero-fee-rental-template"
        Invoke-Remote "set -euo pipefail; mkdir -p '$backupDirectory'; chmod 700 '$backupDirectory'; docker exec georaeplan-postgres-1 pg_dump -U georaeplan -d georaeplan -Fc > '$backupDirectory/georaeplan.dump'; docker exec -i georaeplan-postgres-1 pg_restore -l < '$backupDirectory/georaeplan.dump' > /dev/null; sha256sum '$backupDirectory/georaeplan.dump' | tee '$backupDirectory/SHA256SUMS'; chmod 600 '$backupDirectory/'*"
    }

    $applyValue = if ($Apply) { 1 } else { 0 }
    Invoke-Remote "docker exec -i georaeplan-postgres-1 psql -U georaeplan -d georaeplan -v ON_ERROR_STOP=1 -v apply=$applyValue -P pager=off < '$remoteDirectory/$([IO.Path]::GetFileName($applySql))'"
    Invoke-RestMethod 'https://trade.2884.kr/healthz' -TimeoutSec 30 | Out-Null
}
finally {
    Invoke-Remote "rm -rf '$remoteDirectory'"
}
