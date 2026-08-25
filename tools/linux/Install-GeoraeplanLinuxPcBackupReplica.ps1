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
        '-T',
        '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=yes',
        '-o', 'ConnectTimeout=15',
        '-p', $LinuxSshPort.ToString(),
        '-i', $LinuxSshKeyPath,
        "$LinuxSshUser@$LinuxSshHost",
        "printf '%s' '$encoded' | base64 -d | sh")
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $SshExe @arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $oldPreference }
    if (-not $IgnoreExitCode -and $exitCode -ne 0) {
        throw "Remote command failed with exit ${exitCode}: $($output -join [Environment]::NewLine)"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-SshSudoCommand {
    param([string]$SshExe, [string]$Command)
    if ($null -eq $SudoCredential -and -not $PromptForSudoCredential) {
        $wrapped = "sudo -n sh -ceu " + (Convert-ToShellLiteral $Command)
        return Invoke-SshCommand -SshExe $SshExe -Command $wrapped
    }

    $credential = $SudoCredential
    $ownsCredential = $false
    if ($null -eq $credential) {
        $credential = Get-Credential `
            -UserName $LinuxSshUser `
            -Message 'Enter the Linux PC sudo password for the backup replica installer.'
        $ownsCredential = $true
    }
    if ($null -eq $credential -or $null -eq $credential.Password) {
        throw 'Sudo credential entry was cancelled.'
    }
    $pointer = [IntPtr]::Zero
    $plain = $null
    $payload = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
            $credential.Password)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        $payload = $plain + "`n" +
            $Command.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd("`n") +
            "`n# replica-installer-end"
        $arguments = @(
            '-T',
            '-o', 'BatchMode=yes',
            '-o', 'StrictHostKeyChecking=yes',
            '-o', 'ConnectTimeout=15',
            '-p', $LinuxSshPort.ToString(),
            '-i', $LinuxSshKeyPath,
            "$LinuxSshUser@$LinuxSshHost",
            "sudo -S -k -p '' sh -s")
        $oldPreference = $ErrorActionPreference
        $oldEncoding = $OutputEncoding
        try {
            $ErrorActionPreference = 'Continue'
            $OutputEncoding = New-Object Text.UTF8Encoding($false)
            $output = @($payload | & $SshExe @arguments 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $oldPreference
            $OutputEncoding = $oldEncoding
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

$assetRoot = Join-Path $PSScriptRoot 'assets\georaeplan-backup-replica'
$assetNames = @(
    'georaeplan-backup-replica.sh',
    'georaeplan-backup-replica.service',
    'georaeplan-backup-replica.timer')
$assets = foreach ($name in $assetNames) {
    $path = Join-Path $assetRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Replica asset is missing: $path"
    }
    [pscustomobject]@{
        Name = $name
        Path = (Resolve-Path -LiteralPath $path).Path
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$shellAsset = $assets | Where-Object Name -eq 'georaeplan-backup-replica.sh'
$shellText = Get-Content -LiteralPath $shellAsset.Path -Raw -Encoding UTF8
foreach ($forbidden in @(
        'docker compose down',
        'docker compose restart',
        'systemctl restart',
        '/mnt/itworld-rental-contracts')) {
    if ($shellText.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Replica asset contains forbidden text: $forbidden"
    }
}

$quotedReplicaRoot = Convert-ToShellLiteral $ReplicaRoot
$quotedReplicaId = Convert-ToShellLiteral $ReplicaId
$preflight = @"
set -eu
root=$quotedReplicaRoot
replica_id=$quotedReplicaId
if ! test -d "`$root"; then
  echo "backup_replica_preflight_failed reason=mount_root_missing path=`$root" >&2
  exit 20
fi
if test -L "`$root"; then
  echo "backup_replica_preflight_failed reason=mount_root_reparse path=`$root" >&2
  exit 20
fi
mount_target=`$(findmnt -T "`$root" -n -o TARGET)
mount_source=`$(findmnt -T "`$root" -n -o SOURCE)
mount_fstype=`$(findmnt -T "`$root" -n -o FSTYPE)
command -v lsblk >/dev/null
case "`$mount_fstype" in
  cifs|nfs|nfs4) ;;
  ext4)
    replica_block_source=`${mount_source%%\[*}
    source_mount_source=`$(findmnt -T /srv/georaeplan/backups/automatic -n -o SOURCE)
    source_block_source=`${source_mount_source%%\[*}
    if ! test -b "`$replica_block_source" || ! test -b "`$source_block_source"; then
      echo "replica_mount_invalid reason=local_block_source_invalid" >&2
      exit 21
    fi
    replica_disk=`$(lsblk -srno NAME "`$replica_block_source" | awk 'NF { value=`$1 } END { print value }')
    source_disk=`$(lsblk -srno NAME "`$source_block_source" | awk 'NF { value=`$1 } END { print value }')
    if test -z "`$replica_disk" || test -z "`$source_disk" || test "`$replica_disk" = "`$source_disk"; then
      echo "replica_mount_invalid reason=same_physical_disk" >&2
      exit 21
    fi
    ;;
  *) echo "replica_mount_invalid fstype=`$mount_fstype" >&2; exit 21 ;;
esac
test -n "`$mount_source"
test "`$mount_target" != '/'
if test "`$(stat -Lc '%d' /srv/georaeplan/backups/automatic)" = "`$(stat -Lc '%d' "`$root")"; then
  echo "backup_replica_preflight_failed reason=same_device" >&2
  exit 21
fi
if ! test -r /srv/georaeplan/ops/state/backup-status.txt; then
  echo "backup_replica_preflight_failed reason=backup_status_missing" >&2
  exit 22
fi
required=`$(awk -F= '`$1=="required_available_bytes" {print `$2}' /srv/georaeplan/ops/state/backup-status.txt)
case "`$required" in ''|*[!0-9]*) echo "backup_replica_preflight_failed reason=capacity_invalid field=required_available_bytes" >&2; exit 22;; esac
available=`$(df -B1 --output=avail "`$root" | tail -n 1 | tr -d ' ')
case "`$available" in ''|*[!0-9]*) echo "backup_replica_preflight_failed reason=capacity_invalid field=available_bytes" >&2; exit 22;; esac
if ! test "`$available" -ge "`$required"; then
  echo "backup_replica_preflight_failed reason=capacity_insufficient" >&2
  exit 22
fi
marker="`$root/.georaeplan-replica-root"
lock="`$root/.georaeplan-replica.lock"
if test -e "`$marker"; then
  test -f "`$marker"
  test ! -L "`$marker"
  test "`$(stat -Lc '%h' "`$marker")" = 1
  test "`$(awk -F= '`$1=="schema_version" {print `$2}' "`$marker")" = 1
  test "`$(awk -F= '`$1=="owner" {print `$2}' "`$marker")" = georaeplan-external-backup-replica
  test "`$(awk -F= '`$1=="replica_id" {print `$2}' "`$marker")" = "`$replica_id"
  test "`$(wc -l < "`$marker" | tr -d ' ')" = 3
else
  test "`$(find "`$root" -mindepth 1 -maxdepth 1 -print | wc -l)" = 0
fi
if test -e "`$lock"; then test -f "`$lock"; test ! -L "`$lock"; test "`$(stat -Lc '%h' "`$lock")" = 1; fi
if test -e "`$marker"; then
  while IFS= read -r entry; do
    name=`${entry##*/}
    case "`$name" in
      .georaeplan-replica-root|.georaeplan-replica.lock) test -f "`$entry"; test ! -L "`$entry" ;;
      sets|.staging) test -d "`$entry"; test ! -L "`$entry" ;;
      *) echo "replica_root_unknown name=`$name" >&2; exit 23 ;;
    esac
  done <<EOF
`$(find "`$root" -mindepth 1 -maxdepth 1 -print)
EOF
fi
command -v pg_restore >/dev/null
command -v flock >/dev/null
command -v findmnt >/dev/null
printf 'replica_mount=ok\nreplica_mount_target=%s\nreplica_mount_fstype=%s\nreplica_capacity=ok\n' "`$mount_target" "`$mount_fstype"
"@
$preflightResult = Invoke-SshCommand -SshExe $sshExe -Command $preflight
$preflightResult.Output | ForEach-Object { Write-Host $_ }
Write-Host 'backup_replica_remote_readonly_preflight=ok'
foreach ($asset in $assets) {
    Write-Host "backup_replica_asset name=$($asset.Name) sha256=$($asset.Sha256)"
}

if (-not $Apply) {
    Write-Host 'backup_replica_mode=plan'
    Write-Host 'backup_replica_apply_required=true'
    Write-Host 'backup_replica_remote_mutation=none'
    return
}

Write-Warning 'Explicit -Apply boundary crossed: replica assets and timer will be installed.'
$remoteStage = "/tmp/georaeplan-backup-replica-install-$([Guid]::NewGuid().ToString('N'))"
$quotedStage = Convert-ToShellLiteral $remoteStage
Invoke-SshCommand -SshExe $sshExe -Command "set -eu; install -d -m 0700 $quotedStage" | Out-Null
try {
    $scpArgs = @(
        '-q',
        '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=yes',
        '-P', $LinuxSshPort.ToString(),
        '-i', $LinuxSshKeyPath)
    foreach ($asset in $assets) {
        $oldPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $scpExe @scpArgs $asset.Path "${LinuxSshUser}@${LinuxSshHost}:$remoteStage/$($asset.Name)" 2>&1 | Out-Null
            $copyExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $oldPreference }
        if ($copyExit -ne 0) { throw "Failed to upload replica asset: $($asset.Name)" }
    }

    $envText = @(
        'GEORAEPLAN_SOURCE_BACKUP_ROOT=/srv/georaeplan/backups/automatic',
        'GEORAEPLAN_BACKUP_STATE_ROOT=/srv/georaeplan/ops/state',
        "GEORAEPLAN_REPLICA_ROOT=$ReplicaRoot",
        "GEORAEPLAN_REPLICA_ID=$ReplicaId",
        '') -join "`n"
    $envBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($envText))
    $expectedScriptHash = ($assets | Where-Object Name -eq 'georaeplan-backup-replica.sh').Sha256
    $expectedServiceHash = ($assets | Where-Object Name -eq 'georaeplan-backup-replica.service').Sha256
    $expectedTimerHash = ($assets | Where-Object Name -eq 'georaeplan-backup-replica.timer').Sha256
    $runFlag = if ($RunAfterInstall) { 'true' } else { 'false' }
    $applyCommand = @"
set -eu
stage=$quotedStage
root=$quotedReplicaRoot
replica_id=$quotedReplicaId
test -d "`$root"
test ! -L "`$root"
mount_target=`$(findmnt -T "`$root" -n -o TARGET)
mount_source=`$(findmnt -T "`$root" -n -o SOURCE)
mount_fstype=`$(findmnt -T "`$root" -n -o FSTYPE)
command -v lsblk >/dev/null
case "`$mount_fstype" in
  cifs|nfs|nfs4) ;;
  ext4)
    replica_block_source=`${mount_source%%\[*}
    source_mount_source=`$(findmnt -T /srv/georaeplan/backups/automatic -n -o SOURCE)
    source_block_source=`${source_mount_source%%\[*}
    if ! test -b "`$replica_block_source" || ! test -b "`$source_block_source"; then
      echo "replica_mount_invalid reason=local_block_source_invalid" >&2
      exit 21
    fi
    replica_disk=`$(lsblk -srno NAME "`$replica_block_source" | awk 'NF { value=`$1 } END { print value }')
    source_disk=`$(lsblk -srno NAME "`$source_block_source" | awk 'NF { value=`$1 } END { print value }')
    if test -z "`$replica_disk" || test -z "`$source_disk" || test "`$replica_disk" = "`$source_disk"; then
      echo "replica_mount_invalid reason=same_physical_disk" >&2
      exit 21
    fi
    ;;
  *) echo "replica_mount_invalid fstype=`$mount_fstype" >&2; exit 21 ;;
esac
test -n "`$mount_source"
test "`$mount_target" != '/'
test "`$(stat -Lc '%d' /srv/georaeplan/backups/automatic)" != "`$(stat -Lc '%d' "`$root")"
marker="`$root/.georaeplan-replica-root"
lock="`$root/.georaeplan-replica.lock"
if test -e "`$marker"; then
  test -f "`$marker"
  test ! -L "`$marker"
  test "`$(stat -Lc '%h' "`$marker")" = 1
  test "`$(awk -F= '`$1=="schema_version" {print `$2}' "`$marker")" = 1
  test "`$(awk -F= '`$1=="owner" {print `$2}' "`$marker")" = georaeplan-external-backup-replica
  test "`$(awk -F= '`$1=="replica_id" {print `$2}' "`$marker")" = "`$replica_id"
  test "`$(wc -l < "`$marker" | tr -d ' ')" = 3
  while IFS= read -r entry; do
    name=`${entry##*/}
    case "`$name" in
      .georaeplan-replica-root|.georaeplan-replica.lock) test -f "`$entry"; test ! -L "`$entry" ;;
      sets|.staging) test -d "`$entry"; test ! -L "`$entry" ;;
      *) echo "replica_root_unknown name=`$name" >&2; exit 23 ;;
    esac
  done <<EOF
`$(find "`$root" -mindepth 1 -maxdepth 1 -print)
EOF
else
  test "`$(find "`$root" -mindepth 1 -maxdepth 1 -print | wc -l)" = 0
fi
if test -e "`$lock"; then test -f "`$lock"; test ! -L "`$lock"; test "`$(stat -Lc '%h' "`$lock")" = 1; fi
echo replica_apply_preflight=ok
test "`$(sha256sum "`$stage/georaeplan-backup-replica.sh" | awk '{print `$1}')" = '$expectedScriptHash'
test "`$(sha256sum "`$stage/georaeplan-backup-replica.service" | awk '{print `$1}')" = '$expectedServiceHash'
test "`$(sha256sum "`$stage/georaeplan-backup-replica.timer" | awk '{print `$1}')" = '$expectedTimerHash'
bash -n "`$stage/georaeplan-backup-replica.sh"
if ! test -e "`$marker"; then
  temporary="`$root/.georaeplan-replica-root.new.`$`$"
  printf '%s\n' 'schema_version=1' 'owner=georaeplan-external-backup-replica' "replica_id=`$replica_id" > "`$temporary"
  chmod 0600 "`$temporary" 2>/dev/null || true
  mv -T -- "`$temporary" "`$marker"
fi
if ! test -e "`$lock"; then
  : > "`$lock"
  chmod 0600 "`$lock" 2>/dev/null || true
fi
mkdir -p "`$root/sets" "`$root/.staging"
chmod 0700 "`$root/sets" "`$root/.staging" 2>/dev/null || true
install -d -m 0755 /etc/georaeplan
printf '%s' '$envBase64' | base64 -d > /etc/georaeplan/backup-replica.env.new
chmod 0600 /etc/georaeplan/backup-replica.env.new
mv -f /etc/georaeplan/backup-replica.env.new /etc/georaeplan/backup-replica.env
install -m 0750 "`$stage/georaeplan-backup-replica.sh" /usr/local/sbin/georaeplan-backup-replica.sh.new
mv -f /usr/local/sbin/georaeplan-backup-replica.sh.new /usr/local/sbin/georaeplan-backup-replica.sh
install -m 0644 "`$stage/georaeplan-backup-replica.service" /etc/systemd/system/georaeplan-backup-replica.service.new
mv -f /etc/systemd/system/georaeplan-backup-replica.service.new /etc/systemd/system/georaeplan-backup-replica.service
install -m 0644 "`$stage/georaeplan-backup-replica.timer" /etc/systemd/system/georaeplan-backup-replica.timer.new
mv -f /etc/systemd/system/georaeplan-backup-replica.timer.new /etc/systemd/system/georaeplan-backup-replica.timer
systemctl daemon-reload
systemctl enable --now georaeplan-backup-replica.timer
systemctl is-enabled georaeplan-backup-replica.timer
systemctl is-active georaeplan-backup-replica.timer
if test '$runFlag' = true; then
  systemctl start georaeplan-backup-replica.service
fi
test "`$(sha256sum /usr/local/sbin/georaeplan-backup-replica.sh | awk '{print `$1}')" = '$expectedScriptHash'
test "`$(sha256sum /etc/systemd/system/georaeplan-backup-replica.service | awk '{print `$1}')" = '$expectedServiceHash'
test "`$(sha256sum /etc/systemd/system/georaeplan-backup-replica.timer | awk '{print `$1}')" = '$expectedTimerHash'
test "`$(stat -Lc '%a:%U:%G' /usr/local/sbin/georaeplan-backup-replica.sh)" = '750:root:root'
test "`$(stat -Lc '%a:%U:%G' /etc/georaeplan/backup-replica.env)" = '600:root:root'
echo backup_replica_remote_assets=ok
"@
    $applyResult = Invoke-SshSudoCommand -SshExe $sshExe -Command $applyCommand
    $applyResult.Output | ForEach-Object { Write-Host $_ }
}
finally {
    $cleanupCommand = @"
set -eu
stage=$quotedStage
case "`$stage" in /tmp/georaeplan-backup-replica-install-[0-9a-f][0-9a-f]*) ;; *) exit 31;; esac
rm -f -- \
  "`$stage/georaeplan-backup-replica.sh" \
  "`$stage/georaeplan-backup-replica.service" \
  "`$stage/georaeplan-backup-replica.timer"
rmdir -- "`$stage"
"@
    $stageCleanupResult = Invoke-SshCommand -SshExe $sshExe -Command $cleanupCommand -IgnoreExitCode
}
if ($stageCleanupResult.ExitCode -ne 0) {
    throw "Remote replica installer staging cleanup failed with exit $($stageCleanupResult.ExitCode)."
}

Write-Host 'backup_replica_mode=applied'
