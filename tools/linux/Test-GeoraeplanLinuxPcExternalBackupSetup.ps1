[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$scriptPath = Join-Path $PSScriptRoot 'Invoke-GeoraeplanLinuxPcExternalBackupSetup.ps1'
Assert-True (Test-Path -LiteralPath $scriptPath -PathType Leaf) 'External backup setup orchestrator is missing.'
$source = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8

foreach ($required in @(
        'This orchestrator requires the explicit -Apply boundary.',
        'Get-Credential',
        '-SudoCredential $credential',
        'Install-GeoraeplanLinuxPcLocalBackupReplicaMount.ps1',
        'Install-GeoraeplanLinuxPcBackupSchedule.ps1',
        'Install-GeoraeplanLinuxPcBackupReplica.ps1',
        'Install-GeoraeplanLinuxPcBackupRestoreDrill.ps1',
        '-RunAfterInstall',
        'Invoke-SshSudoReadCommand',
        "sudo -S -k -p '' sh -s",
        'external_backup_replica_id=preserved',
        'external_backup_setup_mode=restore_only',
        'external_backup_setup_mode=refresh_and_restore',
        'external_backup_setup_mode=replica_and_restore',
        'Resume requires an existing protected replica ID.',
        'systemctl start georaeplan-backup.service',
        'source_backup_refresh=ok',
        'central_business_count_sha256',
        'business_business_count_sha256',
        "grep -Eq '^[0-9a-f]{64}`$'",
        'RandomNumberGenerator]::Fill',
        'external_backup_setup=PASS',
        '$credential.Password.Dispose()')) {
    Assert-True ($source.Contains($required)) "External backup setup is missing contract text: $required"
}

Assert-True ([regex]::Matches($source, 'Get-Credential').Count -eq 1) 'The orchestrator must prompt for sudo credentials exactly once.'
Assert-True (-not $source.Contains('[0-9a-f][0-9a-f][0-9a-f][0-9a-f]')) 'The orchestrator must not hand-expand fixed-length SHA-256 patterns.'
Assert-True ([regex]::Matches($source, '-SudoCredential \$credential').Count -eq 4) 'The one credential must be reused by all four installers.'
Assert-True ($source.Contains('-Credential $credential')) 'The same one credential must be reused for the root-only replica ID lookup.'
Assert-True ($source.Contains('ZeroFreeBSTR')) 'The root-only replica ID lookup must zero its temporary plaintext credential buffer.'
foreach ($forbidden in @('Export-Clixml', 'ConvertFrom-SecureString', 'docker compose down', 'systemctl restart', 'rm -rf')) {
    Assert-True (-not $source.Contains($forbidden)) "External backup setup contains forbidden text: $forbidden"
}

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
Assert-True ($parseErrors.Count -eq 0) "External backup setup has PowerShell parse errors: $($parseErrors -join '; ')"

Write-Host 'external_backup_setup_static_test=PASS'
