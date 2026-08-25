[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$RunAfterInstall,
    [switch]$PromptForSudoCredential,
    [System.Management.Automation.PSCredential]$SudoCredential,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$ReplicaRoot = '/mnt/georaeplan-backup-replica',
    [Parameter(Mandatory = $true)]
    [string]$ReplicaId
)

$ErrorActionPreference = 'Stop'

function Resolve-Executable {
    param([string]$Name, [string]$PreferredPath = '')
    if (-not [string]::IsNullOrWhiteSpace($PreferredPath) -and
        (Test-Path -LiteralPath $PreferredPath -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $PreferredPath).Path
    }
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw "$Name executable was not found." }
    return $command.Source
}

function Convert-ToShellLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-SshCommand {
    param(
        [string]$SshExe,
        [string]$Command,
        [switch]$IgnoreExitCode
    )
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(
            $Command.Replace("`r`n", "`n").Replace("`r", "`n")))
    $arguments = @(
        '-T', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
        '-o', 'ConnectTimeout=15', '-p', $LinuxSshPort.ToString(),
        '-i', $LinuxSshKeyPath, "$LinuxSshUser@$LinuxSshHost",
        "printf '%s' '$encoded' | base64 -d | sh")
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $SshExe @arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if (-not $IgnoreExitCode -and $exitCode -ne 0) {
        throw "Remote command failed with exit ${exitCode}: $($output -join [Environment]::NewLine)"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-SshSudoCommand {
    param([string]$SshExe, [string]$Command)
    if ($null -eq $SudoCredential -and -not $PromptForSudoCredential) {
        return Invoke-SshCommand `
            -SshExe $SshExe `
            -Command ("sudo -n sh -ceu " + (Convert-ToShellLiteral $Command))
    }

    $credential = $SudoCredential
    $ownsCredential = $false
    if ($null -eq $credential) {
        $credential = Get-Credential `
            -UserName $LinuxSshUser `
            -Message 'Enter the Linux PC sudo password for the isolated backup restore drill installer.'
        $ownsCredential = $true
    }
    if ($null -eq $credential -or $null -eq $credential.Password) {
        throw 'Sudo credential entry was cancelled.'
    }
    $pointer = [IntPtr]::Zero
    $plain = $null
    $payload = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($credential.Password)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        $payload = $plain + "`n" +
            $Command.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd("`n") +
            "`n# restore-drill-installer-end"
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
            throw "Remote sudo command failed with exit ${exitCode}: $($output -join [Environment]::NewLine)"
        }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
    }
    finally {
        $payload = $null
        $plain = $null
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
        if ($ownsCredential -and $null -ne $credential.Password) {
            $credential.Password.Dispose()
        }
    }
}

if ($LinuxSshHost -notmatch '^[A-Za-z0-9.-]+$' -or
    $LinuxSshUser -notmatch '^[A-Za-z_][A-Za-z0-9_-]*$' -or
    $LinuxSshPort -lt 1 -or $LinuxSshPort -gt 65535) {
    throw 'Linux SSH target is invalid.'
}
if ($ReplicaRoot -cne '/mnt/georaeplan-backup-replica') {
    throw 'ReplicaRoot must remain /mnt/georaeplan-backup-replica.'
}
if ($ReplicaId -cnotmatch '^[0-9a-f]{32}$') {
    throw 'ReplicaId must be exactly 32 lowercase hexadecimal characters.'
}
if ($RunAfterInstall -and -not $Apply) {
    throw '-RunAfterInstall requires -Apply.'
}
if ($PromptForSudoCredential -and -not $Apply) {
    throw '-PromptForSudoCredential requires -Apply.'
}
if ($null -ne $SudoCredential -and -not $Apply) {
    throw '-SudoCredential requires -Apply.'
}
if ($PromptForSudoCredential -and $null -ne $SudoCredential) {
    throw 'Use either -PromptForSudoCredential or -SudoCredential, not both.'
}
if (-not (Test-Path -LiteralPath $LinuxSshKeyPath -PathType Leaf)) {
    throw "Linux SSH key was not found: $LinuxSshKeyPath"
}
$LinuxSshKeyPath = (Resolve-Path -LiteralPath $LinuxSshKeyPath).Path
$sshExe = Resolve-Executable ssh.exe 'C:\Windows\System32\OpenSSH\ssh.exe'
$scpExe = Resolve-Executable scp.exe 'C:\Windows\System32\OpenSSH\scp.exe'

$assetPath = Join-Path $PSScriptRoot 'assets\georaeplan-backup-restore-drill\georaeplan-backup-restore-drill.sh'
if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
    throw "Restore drill asset is missing: $assetPath"
}
$assetPath = (Resolve-Path -LiteralPath $assetPath).Path
$assetSha256 = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$assetText = Get-Content -LiteralPath $assetPath -Raw -Encoding UTF8
foreach ($forbidden in @(
        'docker compose down', 'docker compose restart', 'docker system prune',
        'systemctl restart', '/mnt/itworld-rental-contracts')) {
    if ($assetText.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Restore drill asset contains forbidden text: $forbidden"
    }
}

$quotedReplicaRoot = Convert-ToShellLiteral $ReplicaRoot
$quotedReplicaId = Convert-ToShellLiteral $ReplicaId
$preflight = @"
set -eu
root=$quotedReplicaRoot
replica_id=$quotedReplicaId
if ! test -d "`$root"; then
  echo "backup_restore_drill_preflight_failed reason=mount_root_missing path=`$root" >&2
  exit 20
fi
if test -L "`$root"; then
  echo "backup_restore_drill_preflight_failed reason=mount_root_reparse path=`$root" >&2
  exit 20
fi
mount_target=`$(findmnt -T "`$root" -n -o TARGET)
if test "`$mount_target" = '/'; then
  echo "backup_restore_drill_preflight_failed reason=mount_target_invalid" >&2
  exit 21
fi
mount_fstype=`$(findmnt -T "`$root" -n -o FSTYPE)
mount_source=`$(findmnt -T "`$root" -n -o SOURCE)
command -v lsblk >/dev/null
case "`$mount_fstype" in
  cifs|nfs|nfs4) ;;
  ext4)
    replica_block_source=`${mount_source%%\[*}
    source_mount_source=`$(findmnt -T /srv/georaeplan/backups/automatic -n -o SOURCE)
    source_block_source=`${source_mount_source%%\[*}
    if ! test -b "`$replica_block_source" || ! test -b "`$source_block_source"; then
      echo "restore_drill_mount_invalid reason=local_block_source_invalid" >&2
      exit 21
    fi
    replica_disk=`$(lsblk -srno NAME "`$replica_block_source" | awk 'NF { value=`$1 } END { print value }')
    source_disk=`$(lsblk -srno NAME "`$source_block_source" | awk 'NF { value=`$1 } END { print value }')
    if test -z "`$replica_disk" || test -z "`$source_disk" || test "`$replica_disk" = "`$source_disk"; then
      echo "restore_drill_mount_invalid reason=same_physical_disk" >&2
      exit 21
    fi
    ;;
  *) echo "restore_drill_mount_invalid fstype=`$mount_fstype" >&2; exit 21 ;;
esac
if test "`$(stat -Lc '%d' /srv/georaeplan/backups/automatic)" = "`$(stat -Lc '%d' "`$root")"; then
  echo "backup_restore_drill_preflight_failed reason=same_device" >&2
  exit 21
fi
marker="`$root/.georaeplan-replica-root"
if ! test -f "`$marker"; then
  echo "backup_restore_drill_preflight_failed reason=replica_marker_missing" >&2
  exit 22
fi
test ! -L "`$marker"
test "`$(stat -Lc '%h' "`$marker")" = 1
test "`$(awk -F= '`$1=="schema_version" {print `$2}' "`$marker")" = 1
test "`$(awk -F= '`$1=="owner" {print `$2}' "`$marker")" = georaeplan-external-backup-replica
test "`$(awk -F= '`$1=="replica_id" {print `$2}' "`$marker")" = "`$replica_id"
if ! test -f /srv/georaeplan/ops/state/backup-status.txt; then
  echo "backup_restore_drill_preflight_failed reason=backup_status_missing" >&2
  exit 22
fi
if ! test -f /srv/georaeplan/ops/state/external-replica-status.txt; then
  echo "backup_restore_drill_preflight_failed reason=replica_status_missing" >&2
  exit 22
fi
command -v /usr/bin/docker >/dev/null
command -v /usr/bin/timeout >/dev/null
ops=/srv/georaeplan/ops
postgres_id=`$(/usr/bin/docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" ps -q postgres)
case "`$postgres_id" in ''|*[!0-9a-f]*) exit 22;; esac
image_id=`$(/usr/bin/docker inspect --format '{{.Image}}' "`$postgres_id")
case "`$image_id" in sha256:????????????????????????????????????????????????????????????????) ;; *) exit 22;; esac
printf 'restore_drill_remote_readonly_preflight=ok\nrestore_drill_image_id=%s\n' "`$image_id"
"@
if ($Apply -and $null -ne $SudoCredential) {
    $preflightResult = Invoke-SshSudoCommand -SshExe $sshExe -Command $preflight
    Write-Host 'backup_restore_drill_preflight_privilege=provided_sudo'
}
else {
    $preflightResult = Invoke-SshCommand -SshExe $sshExe -Command $preflight
    Write-Host 'backup_restore_drill_preflight_privilege=unprivileged'
}
$preflightResult.Output | ForEach-Object { Write-Host $_ }
$imageLine = @($preflightResult.Output | Where-Object { [string]$_ -match '^restore_drill_image_id=sha256:[0-9a-f]{64}$' })
if ($imageLine.Count -ne 1) {
    throw 'Remote preflight did not return one content-addressed restore image ID.'
}
$imageId = ([string]$imageLine[0]).Substring('restore_drill_image_id='.Length)
Write-Host "backup_restore_drill_asset sha256=$assetSha256"

if (-not $Apply) {
    Write-Host 'backup_restore_drill_mode=plan'
    Write-Host 'backup_restore_drill_apply_required=true'
    Write-Host 'backup_restore_drill_remote_mutation=none'
    return
}

Write-Warning 'Explicit -Apply boundary crossed: the isolated restore drill asset will be installed.'
$remoteStage = "/tmp/georaeplan-backup-restore-drill-install-$([Guid]::NewGuid().ToString('N'))"
$quotedStage = Convert-ToShellLiteral $remoteStage
Invoke-SshCommand -SshExe $sshExe -Command "set -eu; install -d -m 0700 $quotedStage" | Out-Null
try {
    $scpArgs = @(
        '-q', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
        '-P', $LinuxSshPort.ToString(), '-i', $LinuxSshKeyPath)
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $scpExe @scpArgs $assetPath "${LinuxSshUser}@${LinuxSshHost}:$remoteStage/georaeplan-backup-restore-drill.sh" 2>&1 | Out-Null
        $copyExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($copyExit -ne 0) { throw 'Failed to upload the restore drill asset.' }

    $envText = @(
        'GEORAEPLAN_BACKUP_STATE_ROOT=/srv/georaeplan/ops/state',
        "GEORAEPLAN_REPLICA_ROOT=$ReplicaRoot",
        "GEORAEPLAN_REPLICA_ID=$ReplicaId",
        "GEORAEPLAN_RESTORE_DRILL_IMAGE_ID=$imageId",
        '') -join "`n"
    $envBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($envText))
    $runFlag = if ($RunAfterInstall) { 'true' } else { 'false' }
    $applyCommand = @"
set -eu
stage=$quotedStage
root=$quotedReplicaRoot
replica_id=$quotedReplicaId
test -d "`$root"
test ! -L "`$root"
mount_fstype=`$(findmnt -T "`$root" -n -o FSTYPE)
mount_source=`$(findmnt -T "`$root" -n -o SOURCE)
command -v lsblk >/dev/null
case "`$mount_fstype" in
  cifs|nfs|nfs4) ;;
  ext4)
    replica_block_source=`${mount_source%%\[*}
    source_mount_source=`$(findmnt -T /srv/georaeplan/backups/automatic -n -o SOURCE)
    source_block_source=`${source_mount_source%%\[*}
    test -b "`$replica_block_source"
    test -b "`$source_block_source"
    replica_disk=`$(lsblk -srno NAME "`$replica_block_source" | awk 'NF { value=`$1 } END { print value }')
    source_disk=`$(lsblk -srno NAME "`$source_block_source" | awk 'NF { value=`$1 } END { print value }')
    test -n "`$replica_disk"
    test -n "`$source_disk"
    test "`$replica_disk" != "`$source_disk"
    ;;
  *) exit 21 ;;
esac
test "`$(stat -Lc '%d' /srv/georaeplan/backups/automatic)" != "`$(stat -Lc '%d' "`$root")"
test "`$(awk -F= '`$1=="replica_id" {print `$2}' "`$root/.georaeplan-replica-root")" = "`$replica_id"
test "`$(sha256sum "`$stage/georaeplan-backup-restore-drill.sh" | awk '{print `$1}')" = '$assetSha256'
bash -n "`$stage/georaeplan-backup-restore-drill.sh"
ops=/srv/georaeplan/ops
postgres_id=`$(/usr/bin/docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" ps -q postgres)
case "`$postgres_id" in ''|*[!0-9a-f]*) exit 22;; esac
current_image_id=`$(/usr/bin/docker inspect --format '{{.Image}}' "`$postgres_id")
test "`$current_image_id" = '$imageId'
/usr/bin/docker image inspect '$imageId' > /dev/null
install -d -m 0755 /etc/georaeplan
printf '%s' '$envBase64' | base64 -d > /etc/georaeplan/backup-restore-drill.env.new
chmod 0600 /etc/georaeplan/backup-restore-drill.env.new
mv -f /etc/georaeplan/backup-restore-drill.env.new /etc/georaeplan/backup-restore-drill.env
install -m 0750 "`$stage/georaeplan-backup-restore-drill.sh" /usr/local/sbin/georaeplan-backup-restore-drill.sh.new
mv -f /usr/local/sbin/georaeplan-backup-restore-drill.sh.new /usr/local/sbin/georaeplan-backup-restore-drill.sh
if ! test -e /srv/georaeplan/ops/state/backup-restore-drill.lock; then
  : > /srv/georaeplan/ops/state/backup-restore-drill.lock
  chmod 0600 /srv/georaeplan/ops/state/backup-restore-drill.lock
fi
test "`$(sha256sum /usr/local/sbin/georaeplan-backup-restore-drill.sh | awk '{print `$1}')" = '$assetSha256'
test "`$(stat -Lc '%a:%U:%G' /usr/local/sbin/georaeplan-backup-restore-drill.sh)" = '750:root:root'
test "`$(stat -Lc '%a:%U:%G' /etc/georaeplan/backup-restore-drill.env)" = '600:root:root'
echo backup_restore_drill_remote_asset=ok
if test '$runFlag' = true; then
  set -a
  . /etc/georaeplan/backup-restore-drill.env
  set +a
  /usr/local/sbin/georaeplan-backup-restore-drill.sh
fi
"@
    $applyResult = Invoke-SshSudoCommand -SshExe $sshExe -Command $applyCommand
    $applyResult.Output | ForEach-Object { Write-Host $_ }
}
finally {
    $cleanupCommand = @"
set -eu
stage=$quotedStage
case "`$stage" in /tmp/georaeplan-backup-restore-drill-install-[0-9a-f][0-9a-f]*) ;; *) exit 31;; esac
rm -f -- "`$stage/georaeplan-backup-restore-drill.sh"
rmdir -- "`$stage"
"@
    $stageCleanupResult = Invoke-SshCommand -SshExe $sshExe -Command $cleanupCommand -IgnoreExitCode
}
if ($stageCleanupResult.ExitCode -ne 0) {
    throw "Remote restore drill installer staging cleanup failed with exit $($stageCleanupResult.ExitCode)."
}

Write-Host 'backup_restore_drill_mode=applied'
Write-Host "backup_restore_drill_executed=$($RunAfterInstall.ToString().ToLowerInvariant())"
