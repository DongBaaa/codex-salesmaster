[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$PromptForSudoCredential,
    [System.Management.Automation.PSCredential]$SudoCredential,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$BackingMount = '/mnt/ssd2',
    [string]$BackingRoot = '/mnt/ssd2/backups/georaeplan/external-replica',
    [string]$ReplicaRoot = '/mnt/georaeplan-backup-replica'
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
            -Message 'Enter the Linux PC sudo password once for the external backup setup.'
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
            "`n# local-replica-mount-installer-end"
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
if ($BackingMount -cne '/mnt/ssd2' -or
    $BackingRoot -cne '/mnt/ssd2/backups/georaeplan/external-replica' -or
    $ReplicaRoot -cne '/mnt/georaeplan-backup-replica') {
    throw 'The local replica paths are fixed by the reviewed Linux PC contract.'
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
$quotedBackingMount = Convert-ToShellLiteral $BackingMount
$quotedBackingRoot = Convert-ToShellLiteral $BackingRoot
$quotedReplicaRoot = Convert-ToShellLiteral $ReplicaRoot

$preflight = @"
set -eu
backing_mount=$quotedBackingMount
backing=$quotedBackingRoot
target=$quotedReplicaRoot
command -v findmnt >/dev/null
command -v lsblk >/dev/null
command -v stat >/dev/null
test -d "`$backing_mount"
test ! -L "`$backing_mount"
backing_mount_target=`$(findmnt -M "`$backing_mount" -n -o TARGET)
backing_mount_source=`$(findmnt -M "`$backing_mount" -n -o SOURCE)
backing_mount_fstype=`$(findmnt -M "`$backing_mount" -n -o FSTYPE)
test "`$backing_mount_target" = "`$backing_mount"
test "`$backing_mount_fstype" = ext4
backing_block_source=`${backing_mount_source%%\[*}
root_mount_source=`$(findmnt -T /srv/georaeplan/backups/automatic -n -o SOURCE)
root_block_source=`${root_mount_source%%\[*}
if ! test -b "`$backing_block_source" || ! test -b "`$root_block_source"; then
  echo "local_replica_mount_preflight_failed reason=block_source_invalid" >&2
  exit 20
fi
backing_disk=`$(lsblk -srno NAME "`$backing_block_source" | awk 'NF { value=`$1 } END { print value }')
root_disk=`$(lsblk -srno NAME "`$root_block_source" | awk 'NF { value=`$1 } END { print value }')
if test -z "`$backing_disk" || test -z "`$root_disk" || test "`$backing_disk" = "`$root_disk"; then
  echo "local_replica_mount_preflight_failed reason=same_physical_disk" >&2
  exit 20
fi
if test -e "`$backing"; then
  test -d "`$backing"
  test ! -L "`$backing"
  test "`$(stat -Lc '%d' "`$backing")" = "`$(stat -Lc '%d' "`$backing_mount")"
  if ! findmnt -M "`$target" >/dev/null 2>&1; then
    test "`$(find "`$backing" -mindepth 1 -maxdepth 1 -print | wc -l)" = 0
  fi
fi
if test -e "`$target"; then
  test -d "`$target"
  test ! -L "`$target"
  if findmnt -M "`$target" >/dev/null 2>&1; then
    target_fstype=`$(findmnt -M "`$target" -n -o FSTYPE)
    target_source=`$(findmnt -M "`$target" -n -o SOURCE)
    target_block_source=`${target_source%%\[*}
    test "`$target_fstype" = ext4
    test "`$target_block_source" = "`$backing_block_source"
    test "`$(stat -Lc '%d' "`$target")" = "`$(stat -Lc '%d' "`$backing")"
  else
    test "`$(find "`$target" -mindepth 1 -maxdepth 1 -print | wc -l)" = 0
  fi
fi
conflicts=`$(awk -v source="`$backing" -v target="`$target" '
  /^[[:space:]]*#/ || NF == 0 { next }
  (`$1 == source && `$2 == target && `$3 == "none" && `$4 == "bind,nofail") { exact++ ; next }
  (`$1 == source || `$2 == target) { conflict++ }
  END { print (exact + 0) ":" (conflict + 0) }
' /etc/fstab)
case "`$conflicts" in 0:0|1:0) ;; *) echo "local_replica_mount_preflight_failed reason=fstab_conflict counts=`$conflicts" >&2; exit 21;; esac
printf 'local_replica_mount_preflight=ok\nbacking_disk=%s\nsource_disk=%s\nfstab_counts=%s\n' "`$backing_disk" "`$root_disk" "`$conflicts"
"@
$preflightResult = Invoke-SshCommand -SshExe $sshExe -Command $preflight
$preflightResult.Output | ForEach-Object { Write-Host $_ }

if (-not $Apply) {
    Write-Host 'local_replica_mount_mode=plan'
    Write-Host 'local_replica_mount_remote_mutation=none'
    Write-Host 'local_replica_mount_apply_required=true'
    return
}

Write-Warning 'Explicit -Apply boundary crossed: the reviewed backup disk bind mount will be installed.'
$applyCommand = @"
set -eu
backing_mount=$quotedBackingMount
backing=$quotedBackingRoot
target=$quotedReplicaRoot
fstab_line="`$backing `$target none bind,nofail 0 0"
backing_created=false
target_created=false
mounted_by_script=false
fstab_changed=false
fstab_backup=`$(mktemp /etc/fstab.georaeplan-backup.XXXXXX)
cp --preserve=all /etc/fstab "`$fstab_backup"
rollback() {
  result=`$?
  trap - EXIT HUP INT TERM
  if test "`$result" -ne 0; then
    if test "`$mounted_by_script" = true; then umount "`$target" 2>/dev/null || true; fi
    if test "`$fstab_changed" = true; then cp --preserve=all "`$fstab_backup" /etc/fstab; fi
    if test "`$target_created" = true; then rmdir "`$target" 2>/dev/null || true; fi
    if test "`$backing_created" = true; then rmdir "`$backing" 2>/dev/null || true; fi
  fi
  rm -f "`$fstab_backup"
  exit "`$result"
}
trap rollback EXIT HUP INT TERM
test "`$(findmnt -M "`$backing_mount" -n -o FSTYPE)" = ext4
backing_mount_source=`$(findmnt -M "`$backing_mount" -n -o SOURCE)
backing_block_source=`${backing_mount_source%%\[*}
root_mount_source=`$(findmnt -T /srv/georaeplan/backups/automatic -n -o SOURCE)
root_block_source=`${root_mount_source%%\[*}
test -b "`$backing_block_source"
test -b "`$root_block_source"
backing_disk=`$(lsblk -srno NAME "`$backing_block_source" | awk 'NF { value=`$1 } END { print value }')
root_disk=`$(lsblk -srno NAME "`$root_block_source" | awk 'NF { value=`$1 } END { print value }')
test -n "`$backing_disk"
test -n "`$root_disk"
test "`$backing_disk" != "`$root_disk"
if ! test -e "`$backing"; then install -d -m 0700 -o root -g root "`$backing"; backing_created=true; fi
test -d "`$backing"
test ! -L "`$backing"
test "`$(stat -Lc '%d' "`$backing")" = "`$(stat -Lc '%d' "`$backing_mount")"
if ! test -e "`$target"; then install -d -m 0700 -o root -g root "`$target"; target_created=true; fi
test -d "`$target"
test ! -L "`$target"
conflicts=`$(awk -v source="`$backing" -v target="`$target" '
  /^[[:space:]]*#/ || NF == 0 { next }
  (`$1 == source && `$2 == target && `$3 == "none" && `$4 == "bind,nofail") { exact++ ; next }
  (`$1 == source || `$2 == target) { conflict++ }
  END { print (exact + 0) ":" (conflict + 0) }
' /etc/fstab)
case "`$conflicts" in
  0:0)
    fstab_temp=`$(mktemp /etc/fstab.georaeplan-new.XXXXXX)
    cat /etc/fstab > "`$fstab_temp"
    printf '%s\n' "`$fstab_line" >> "`$fstab_temp"
    chown --reference=/etc/fstab "`$fstab_temp"
    chmod --reference=/etc/fstab "`$fstab_temp"
    mv -f "`$fstab_temp" /etc/fstab
    fstab_changed=true
    ;;
  1:0) ;;
  *) echo "local_replica_mount_apply_failed reason=fstab_conflict counts=`$conflicts" >&2; exit 21 ;;
esac
if ! findmnt -M "`$target" >/dev/null 2>&1; then
  test "`$(find "`$backing" -mindepth 1 -maxdepth 1 -print | wc -l)" = 0
  test "`$(find "`$target" -mindepth 1 -maxdepth 1 -print | wc -l)" = 0
  mount --bind "`$backing" "`$target"
  mounted_by_script=true
fi
test "`$(findmnt -M "`$target" -n -o TARGET)" = "`$target"
test "`$(findmnt -M "`$target" -n -o FSTYPE)" = ext4
target_source=`$(findmnt -M "`$target" -n -o SOURCE)
target_block_source=`${target_source%%\[*}
test "`$target_block_source" = "`$backing_block_source"
test "`$(stat -Lc '%d' "`$target")" = "`$(stat -Lc '%d' "`$backing")"
test "`$(stat -Lc '%d' "`$target")" != "`$(stat -Lc '%d' /srv/georaeplan/backups/automatic)"
test "`$(awk -v source="`$backing" -v target="`$target" '(`$1 == source && `$2 == target && `$3 == "none" && `$4 == "bind,nofail") { count++ } END { print count + 0 }' /etc/fstab)" = 1
echo local_replica_mount_apply=ok
echo local_replica_mount_backing_disk="`$backing_disk"
echo local_replica_mount_source_disk="`$root_disk"
"@
$applyResult = Invoke-SshSudoCommand -SshExe $sshExe -Command $applyCommand
$applyResult.Output | ForEach-Object { Write-Host $_ }

$postflightResult = Invoke-SshCommand -SshExe $sshExe -Command $preflight
$postflightResult.Output | ForEach-Object { Write-Host $_ }
Write-Host 'local_replica_mount_postflight=ok'
