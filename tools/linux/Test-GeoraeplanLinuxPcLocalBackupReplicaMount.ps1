[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$scriptPath = Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcLocalBackupReplicaMount.ps1'
Assert-True (Test-Path -LiteralPath $scriptPath -PathType Leaf) 'Local backup replica mount installer is missing.'
$source = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8

foreach ($required in @(
        "[string]`$BackingMount = '/mnt/ssd2'",
        "[string]`$BackingRoot = '/mnt/ssd2/backups/georaeplan/external-replica'",
        "[string]`$ReplicaRoot = '/mnt/georaeplan-backup-replica'",
        '[System.Management.Automation.PSCredential]$SudoCredential',
        'Use either -PromptForSudoCredential or -SudoCredential, not both.',
        'command -v findmnt',
        'command -v lsblk',
        'same_physical_disk',
        'block_source_invalid',
        'fstab_conflict',
        'none bind,nofail 0 0',
        'cp --preserve=all /etc/fstab',
        'trap rollback EXIT HUP INT TERM',
        'mount --bind',
        'stat -Lc ''%d'' /srv/georaeplan/backups/automatic',
        'local_replica_mount_remote_mutation=none',
        'local_replica_mount_apply=ok',
        'local_replica_mount_postflight=ok')) {
    Assert-True ($source.Contains($required)) "Local mount installer is missing contract text: $required"
}

foreach ($forbidden in @(
        'docker compose down',
        'docker system prune',
        'systemctl restart docker',
        'sudo reboot',
        'rm -rf')) {
    Assert-True (-not $source.Contains($forbidden)) "Local mount installer contains forbidden operation: $forbidden"
}

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
Assert-True ($parseErrors.Count -eq 0) "Local mount installer has PowerShell parse errors: $($parseErrors -join '; ')"

Write-Host 'local_backup_replica_mount_static_test=PASS'
