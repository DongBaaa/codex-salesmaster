[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [switch]$Apply,
    [switch]$RestoreOnly,
    [switch]$RefreshAndRestore,
    [switch]$ReplicaAndRestore,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519')
)

$ErrorActionPreference = 'Stop'

function Invoke-SshSudoReadCommand {
    param(
        [Parameter(Mandatory = $true)][string]$SshExe,
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][System.Management.Automation.PSCredential]$Credential
    )

    $pointer = [IntPtr]::Zero
    $plain = $null
    $payload = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Credential.Password)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        $payload = $plain + "`n" +
            $Command.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd("`n") +
            "`n# external-backup-sudo-read-end"
        $arguments = @(
            '-T', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
            '-o', 'ConnectTimeout=15', '-p', $LinuxSshPort.ToString(),
            '-i', $LinuxSshKeyPath, "$LinuxSshUser@$LinuxSshHost",
            "sudo -S -k -p '' sh -s")
        $previousPreference = $ErrorActionPreference
        $previousEncoding = $OutputEncoding
        try {
            $ErrorActionPreference = 'Continue'
            $OutputEncoding = New-Object Text.UTF8Encoding($false)
            $output = @($payload | & $SshExe @arguments 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
            $OutputEncoding = $previousEncoding
        }
        if ($exitCode -ne 0) {
            throw "Remote sudo read failed with exit ${exitCode}: $($output -join [Environment]::NewLine)"
        }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
    }
    finally {
        $payload = $null
        $plain = $null
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

if (-not $Apply) {
    throw 'This orchestrator requires the explicit -Apply boundary.'
}
$selectedResumeModes = @($RestoreOnly, $RefreshAndRestore, $ReplicaAndRestore) |
    Where-Object { $_ }
if ($selectedResumeModes.Count -gt 1) {
    throw '-RestoreOnly, -RefreshAndRestore, and -ReplicaAndRestore are mutually exclusive.'
}
if ($LinuxSshHost -notmatch '^[A-Za-z0-9.-]+$' -or
    $LinuxSshUser -notmatch '^[A-Za-z_][A-Za-z0-9_-]*$' -or
    $LinuxSshPort -lt 1 -or $LinuxSshPort -gt 65535) {
    throw 'Linux SSH target is invalid.'
}
if (-not (Test-Path -LiteralPath $LinuxSshKeyPath -PathType Leaf)) {
    throw "Linux SSH key was not found: $LinuxSshKeyPath"
}

$LinuxSshKeyPath = (Resolve-Path -LiteralPath $LinuxSshKeyPath).Path
$sshExe = 'C:\Windows\System32\OpenSSH\ssh.exe'
if (-not (Test-Path -LiteralPath $sshExe -PathType Leaf)) {
    throw "OpenSSH client was not found: $sshExe"
}

$credential = Get-Credential `
    -UserName $LinuxSshUser `
    -Message '거래플랜 외부 백업 설정용 Linux PC sudo 암호 — 이번 작업에서 한 번만 입력합니다.'
if ($null -eq $credential -or $null -eq $credential.Password) {
    throw 'Sudo credential entry was cancelled.'
}

try {
    if ($RestoreOnly) {
        Write-Host 'external_backup_setup_mode=restore_only'
    }
    elseif ($RefreshAndRestore) {
        Write-Host 'external_backup_setup_mode=refresh_and_restore'
    }
    elseif ($ReplicaAndRestore) {
        Write-Host 'external_backup_setup_mode=replica_and_restore'
    }
    else {
        Write-Host 'external_backup_setup_step=local_mount'
        & (Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcLocalBackupReplicaMount.ps1') `
            -Apply `
            -SudoCredential $credential `
            -LinuxSshHost $LinuxSshHost `
            -LinuxSshUser $LinuxSshUser `
            -LinuxSshPort $LinuxSshPort `
            -LinuxSshKeyPath $LinuxSshKeyPath
    }

    $replicaIdCommand = @'
set -eu
if test -f /mnt/georaeplan-backup-replica/.georaeplan-replica-root; then
  awk -F= '$1=="replica_id" {print $2}' /mnt/georaeplan-backup-replica/.georaeplan-replica-root
fi
'@
    $replicaIdResult = Invoke-SshSudoReadCommand `
        -SshExe $sshExe `
        -Command $replicaIdCommand `
        -Credential $credential
    $replicaIdOutput = @($replicaIdResult.Output)
    $replicaId = [string](@(
            $replicaIdOutput |
                Where-Object { [string]$_ -cmatch '^[0-9a-f]{32}$' }
        ) | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($replicaId)) {
        if ($RestoreOnly -or $RefreshAndRestore -or $ReplicaAndRestore) {
            throw 'Resume requires an existing protected replica ID.'
        }
        $randomBytes = New-Object byte[] 16
        [Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
        $replicaId = [Convert]::ToHexString($randomBytes).ToLowerInvariant()
        Write-Host 'external_backup_replica_id=generated'
    }
    else {
        Write-Host 'external_backup_replica_id=preserved'
    }

    if ($RefreshAndRestore) {
        Write-Host 'external_backup_setup_step=source_backup_refresh'
        & (Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupSchedule.ps1') `
            -Apply `
            -SudoCredential $credential `
            -LinuxSshHost $LinuxSshHost `
            -LinuxSshUser $LinuxSshUser `
            -LinuxSshPort $LinuxSshPort `
            -LinuxSshKeyPath $LinuxSshKeyPath

        $sourceBackupRefreshCommand = @'
set -eu
systemctl start georaeplan-backup.service
test "$(systemctl show georaeplan-backup.service -p Result --value)" = success
test "$(systemctl show georaeplan-backup.service -p ExecMainStatus --value)" = 0
status=/srv/georaeplan/ops/state/backup-status.txt
test -f "$status"
test "$(awk -F= '$1=="backup" {print $2}' "$status")" = ok
run_id="$(awk -F= '$1=="run_id" {print $2}' "$status")"
manifest_sha256="$(awk -F= '$1=="manifest_sha256" {print $2}' "$status")"
set_path="$(awk -F= '$1=="set_path" {print $2}' "$status")"
printf '%s\n' "$run_id" | grep -Eq '^[0-9]{8}T[0-9]{6}Z-[0-9]+$' || {
  echo source_backup_refresh_invalid field=run_id >&2
  exit 3
}
printf '%s\n' "$manifest_sha256" | grep -Eq '^[0-9a-f]{64}$' || {
  echo source_backup_refresh_invalid field=manifest_sha256 >&2
  exit 3
}
expected_set="/srv/georaeplan/backups/automatic/sets/backup_${run_id}.complete"
test "$set_path" = "$expected_set"
test -d "$set_path"
test ! -L "$set_path"
metadata="$set_path/metadata.txt"
test -f "$metadata"
test ! -L "$metadata"
expected_keys='backup
business_business_count_sha256
business_database
central_business_count_sha256
central_database
created_at
database_snapshot_consistency
database_snapshot_sha256
estimated_source_bytes
file_deletion_lease
files_archive
keyring_archive
replica
required_available_bytes
run_id'
actual_keys="$(awk -F= 'NF >= 2 {print $1}' "$metadata" | LC_ALL=C sort)"
test "$actual_keys" = "$expected_keys"
central_digest="$(awk -F= '$1=="central_business_count_sha256" {print $2}' "$metadata")"
business_digest="$(awk -F= '$1=="business_business_count_sha256" {print $2}' "$metadata")"
test "$(printf '%s\n%s\n' "$central_digest" "$business_digest" | grep -Ec '^[0-9a-f]{64}$')" = 2
printf 'source_backup_refresh=ok\nsource_run_id=%s\nsource_manifest_sha256=%s\n' "$run_id" "$manifest_sha256"
'@
        $sourceBackupResult = Invoke-SshSudoReadCommand `
            -SshExe $sshExe `
            -Command $sourceBackupRefreshCommand `
            -Credential $credential
        @($sourceBackupResult.Output) | ForEach-Object { Write-Host $_ }
    }

    if (-not $RestoreOnly) {
        Write-Host 'external_backup_setup_step=replica'
        & (Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupReplica.ps1') `
            -Apply `
            -RunAfterInstall `
            -SudoCredential $credential `
            -ReplicaId $replicaId `
            -LinuxSshHost $LinuxSshHost `
            -LinuxSshUser $LinuxSshUser `
            -LinuxSshPort $LinuxSshPort `
            -LinuxSshKeyPath $LinuxSshKeyPath
    }

    Write-Host 'external_backup_setup_step=restore_drill'
    & (Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupRestoreDrill.ps1') `
        -Apply `
        -RunAfterInstall `
        -SudoCredential $credential `
        -ReplicaId $replicaId `
        -LinuxSshHost $LinuxSshHost `
        -LinuxSshUser $LinuxSshUser `
        -LinuxSshPort $LinuxSshPort `
        -LinuxSshKeyPath $LinuxSshKeyPath

    Write-Host 'external_backup_setup=PASS'
}
finally {
    if ($null -ne $credential.Password) {
        $credential.Password.Dispose()
    }
}
