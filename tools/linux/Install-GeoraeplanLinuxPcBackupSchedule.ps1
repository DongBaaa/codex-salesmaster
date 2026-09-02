[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$RunAfterInstall,
    [switch]$PromptForSudoCredential,
    [System.Management.Automation.PSCredential]$SudoCredential,
    [switch]$SkipRemoteReadOnlyCheck,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$LinuxRemoteRoot = '/srv/georaeplan'
)

$ErrorActionPreference = 'Stop'

function Resolve-Executable {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$PreferredPath = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath) -and
        (Test-Path -LiteralPath $PreferredPath -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $PreferredPath).Path
    }

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$Name executable was not found."
    }

    return $command.Source
}

function Convert-ToShellLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-SshCommand {
    param(
        [Parameter(Mandatory = $true)][string]$SshExe,
        [Parameter(Mandatory = $true)][string]$Command
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        throw 'Linux PC SSH command cannot be empty.'
    }

    $normalizedCommand = $Command.Replace("`r`n", "`n").Replace("`r", "`n")
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($normalizedCommand))
    $remoteShellCommand = "printf '%s' '$encodedCommand' | base64 -d | sh"

    $arguments = @(
        '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'ConnectTimeout=15',
        '-p', $LinuxSshPort.ToString(),
        '-i', $LinuxSshKeyPath,
        "$LinuxSshUser@$LinuxSshHost",
        $remoteShellCommand
    )
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $SshExe @arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Linux PC SSH command failed with exit code ${exitCode}: $($output -join [Environment]::NewLine)"
    }

    return @($output)
}

function Invoke-SshSudoCommand {
    param(
        [Parameter(Mandatory = $true)][string]$SshExe,
        [Parameter(Mandatory = $true)][string]$Command,
        [System.Management.Automation.PSCredential]$Credential
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        throw 'Linux PC sudo command cannot be empty.'
    }

    $ownsCredential = $false
    if ($null -eq $Credential) {
        $Credential = Get-Credential `
            -UserName $LinuxSshUser `
            -Message 'Linux PC 거래플랜 백업 설치용 sudo 비밀번호를 입력하세요.'
        $ownsCredential = $true
    }
    if ($null -eq $Credential -or $null -eq $Credential.Password) {
        throw 'Linux PC sudo credential entry was cancelled.'
    }

    $passwordPointer = [IntPtr]::Zero
    $plainPassword = $null
    $stdinPayload = $null
    try {
                $passwordPointer =
                    [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
                $Credential.Password)
        $plainPassword =
            [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
                $passwordPointer)
        $normalizedCommand =
            $Command.Replace("`r`n", "`n").Replace("`r", "`n")
        # PowerShell appends CRLF when piping a native-command input object.
        # Keep that CR on a final shell comment instead of the last command.
        $stdinPayload =
            $plainPassword + "`n" +
            $normalizedCommand.TrimEnd("`n") +
            "`n# georaeplan-sudo-command-end"
        $arguments = @(
            '-T',
            '-o', 'BatchMode=yes',
            '-o', 'StrictHostKeyChecking=accept-new',
            '-o', 'ConnectTimeout=15',
            '-p', $LinuxSshPort.ToString(),
            '-i', $LinuxSshKeyPath,
            "$LinuxSshUser@$LinuxSshHost",
            "sudo -S -k -p '' sh -s"
        )
        $previousErrorActionPreference = $ErrorActionPreference
        $previousOutputEncoding = $OutputEncoding
        try {
            $ErrorActionPreference = 'Continue'
            $OutputEncoding = New-Object Text.UTF8Encoding($false)
            $output =
                $stdinPayload |
                    & $SshExe @arguments 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
            $OutputEncoding = $previousOutputEncoding
        }
        if ($exitCode -ne 0) {
            throw (
                'Linux PC sudo SSH command failed with exit code ' +
                "${exitCode}: $($output -join [Environment]::NewLine)")
        }

        return @($output)
    }
    finally {
        $stdinPayload = $null
        $plainPassword = $null
        if ($passwordPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR(
                $passwordPointer)
        }
        if ($ownsCredential -and $null -ne $Credential.Password) {
            $Credential.Password.Dispose()
        }
        $Credential = $null
    }
}

if ($LinuxSshHost -notmatch '^[A-Za-z0-9.-]+$' -or
    $LinuxSshUser -notmatch '^[A-Za-z_][A-Za-z0-9_-]*$') {
    throw 'Linux SSH host or user contains unsupported characters.'
}
if ($LinuxSshPort -lt 1 -or $LinuxSshPort -gt 65535) {
    throw 'Linux SSH port is outside the valid range.'
}
if ($LinuxRemoteRoot -notmatch '^/[A-Za-z0-9._/-]+$' -or
    $LinuxRemoteRoot -eq '/' -or
    $LinuxRemoteRoot.EndsWith('/', [StringComparison]::Ordinal) -or
    $LinuxRemoteRoot.IndexOf('//', [StringComparison]::Ordinal) -ge 0 -or
    $LinuxRemoteRoot -match '(^|/)\.{1,2}(/|$)') {
    throw 'Linux remote root must be a normalized absolute path.'
}
if (-not [string]::Equals($LinuxRemoteRoot, '/srv/georaeplan', [StringComparison]::Ordinal)) {
    throw 'LinuxRemoteRoot must remain /srv/georaeplan because the installed backup script and systemd unit use that fixed production root.'
}
if ($Apply -and $SkipRemoteReadOnlyCheck) {
    throw '-Apply requires the remote read-only preflight.'
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
    throw '-PromptForSudoCredential cannot be combined with -SudoCredential.'
}

$assetRoot = Join-Path $PSScriptRoot 'assets\georaeplan-backup'
$assetNames = @(
    'georaeplan-backup.sh',
    'georaeplan-backup.service',
    'georaeplan-backup.timer'
)
$assets = foreach ($name in $assetNames) {
    $path = Join-Path $assetRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Backup schedule asset is missing: $path"
    }

    [pscustomobject]@{
        Name = $name
        Path = (Resolve-Path -LiteralPath $path).Path
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

$backupScriptAsset = $assets |
    Where-Object Name -eq 'georaeplan-backup.sh' |
    Select-Object -First 1
$shellSource = Get-Content -LiteralPath $backupScriptAsset.Path -Raw -Encoding UTF8
foreach ($forbidden in @(
        'docker compose down',
        'docker compose up',
        'docker compose restart',
        'docker system prune')) {
    if ($shellSource.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Backup asset contains a forbidden operation: $forbidden"
    }
}

Write-Host 'backup_schedule_local_assets=ok'
foreach ($asset in $assets) {
    Write-Host "backup_schedule_asset name=$($asset.Name) sha256=$($asset.Sha256)"
}

$sshExe = $null
if (-not $SkipRemoteReadOnlyCheck) {
    if (-not (Test-Path -LiteralPath $LinuxSshKeyPath -PathType Leaf)) {
        throw "Linux PC SSH key was not found: $LinuxSshKeyPath"
    }
    $LinuxSshKeyPath = (Resolve-Path -LiteralPath $LinuxSshKeyPath).Path
    $sshExe = Resolve-Executable -Name 'ssh.exe' -PreferredPath 'C:\Windows\System32\OpenSSH\ssh.exe'

    $quotedRoot = Convert-ToShellLiteral $LinuxRemoteRoot
    $apiDatabaseIdentityProbe = @'
extract_database_name() {
  connection_string="$1"
  previous_ifs="$IFS"
  IFS=";"
  set -f
  for segment in $connection_string; do
    key="${segment%%=*}"
    value="${segment#*=}"
    case "$key" in
      Database|database|DATABASE)
        case "$value" in
          ""|*[!A-Za-z0-9_.-]*) exit 12 ;;
        esac
        printf "%s\n" "$value"
        IFS="$previous_ifs"
        set +f
        return 0
        ;;
    esac
  done
  IFS="$previous_ifs"
  set +f
  return 11
}
api_central_database="$(extract_database_name "${ConnectionStrings__Default:-}")"
api_business_database="$(extract_database_name "${ConnectionStrings__ITWORLD:-}")"
if [ "$api_central_database" != "$1" ] ||
   [ "$api_business_database" != "$2" ]; then
  echo "api_database_identity_drift" >&2
  exit 13
fi
'@
    $quotedApiDatabaseIdentityProbe = Convert-ToShellLiteral $apiDatabaseIdentityProbe
    $readOnlyCommand = @"
set -eu
root=$quotedRoot
ops="`$root/ops"
test -d "`$root"
test -f "`$ops/docker-compose.yml"
test -f "`$ops/.env"
docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" config --services | grep -qx postgres
docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" config --services | grep -qx api
docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" ps --status running --services postgres | grep -qx postgres
docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" ps --status running --services api | grep -qx api
host_files_identity=`$(stat -Lc '%d:%i' "`$root/storage/files")
api_files_identity=`$(docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" exec -T api stat -Lc '%d:%i' /storage/files)
test -n "`$host_files_identity"
test "`$host_files_identity" = "`$api_files_identity"
central_database=`$(docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" config --environment | grep -m1 '^POSTGRES_DB=' | cut -d= -f2- || true)
central_database=`$`{central_database:-georaeplan}
business_database=`$(docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" config --environment | grep -m1 '^ITWORLD_POSTGRES_DB=' | cut -d= -f2- || true)
business_database=`$`{business_database:-georaeplan_itworld}
case "`$central_database:`$business_database" in
  *[!A-Za-z0-9_.:-]*|:*|*:) exit 3 ;;
esac
test "`$central_database" != "`$business_database"
docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" exec -T api sh -ceu $quotedApiDatabaseIdentityProbe sh "`$central_database" "`$business_database"
api_port_binding=`$(docker compose --env-file "`$ops/.env" -f "`$ops/docker-compose.yml" --project-directory "`$ops" port api 8080 | head -n 1 | tr -d '\r')
case "`$api_port_binding" in
  127.0.0.1:*) ;;
  *) echo "api_health_endpoint_not_loopback" >&2; exit 14 ;;
esac
api_host_port=`$`{api_port_binding#127.0.0.1:}
case "`$api_host_port" in
  ""|*[!0-9]*) echo "api_health_port_invalid" >&2; exit 15 ;;
esac
test "`$api_host_port" -ge 1
test "`$api_host_port" -le 65535
command -v curl >/dev/null 2>&1
ready_payload=`$(curl --fail --silent --show-error --max-time 10 "http://127.0.0.1:`$api_host_port/readyz")
if ! printf '%s' "`$ready_payload" | grep -Eq '"status"[[:space:]]*:[[:space:]]*"ready"'; then
  echo "api_ready_contract_mismatch" >&2
  exit 16
fi
if ! printf '%s' "`$ready_payload" | grep -Eq '"fileDeletionLeaseProtocol"[[:space:]]*:[[:space:]]*"shared-flock-v1"'; then
  echo "api_file_deletion_lease_protocol_mismatch" >&2
  exit 16
fi
printf 'remote_root=ok\ncompose_postgres=ok\ncompose_api=ok\napi_ready=ok\nstorage_bind_identity=ok\ndatabase_identity=ok\napi_database_identity=ok\nfile_deletion_lease_protocol=shared-flock-v1\n'
if command -v systemctl >/dev/null 2>&1; then
  systemctl is-enabled georaeplan-backup.timer 2>/dev/null || true
  systemctl is-active georaeplan-backup.timer 2>/dev/null || true
fi
for file in /usr/local/sbin/georaeplan-backup.sh /etc/systemd/system/georaeplan-backup.service /etc/systemd/system/georaeplan-backup.timer; do
  if ! test -f "`$file"; then
    printf 'not_installed=%s\n' "`$file"
  elif test -r "`$file"; then
    sha256sum "`$file"
  else
    stat -Lc 'installed_unreadable=%n mode=%a uid=%u gid=%g' "`$file"
  fi
done
"@
    $remoteOutput = Invoke-SshCommand -SshExe $sshExe -Command $readOnlyCommand
    $remoteOutput | ForEach-Object { Write-Host $_ }
    Write-Host 'backup_schedule_remote_readonly_preflight=ok'
}

if (-not $Apply) {
    Write-Host 'backup_schedule_mode=plan'
    Write-Host 'backup_schedule_apply_required=true'
    Write-Host 'backup_schedule_remote_mutation=none'
    return
}

Write-Host 'backup_schedule_mode=apply'
Write-Warning 'Explicit -Apply boundary crossed: Linux PC backup assets and systemd timer will be installed.'

$scpExe = Resolve-Executable -Name 'scp.exe' -PreferredPath 'C:\Windows\System32\OpenSSH\scp.exe'
$remoteStaging = "/tmp/georaeplan-backup-install-$([Guid]::NewGuid().ToString('N'))"
$quotedStaging = Convert-ToShellLiteral $remoteStaging
$remoteTarget = "${LinuxSshUser}@${LinuxSshHost}:$remoteStaging/"

try {
    Invoke-SshCommand -SshExe $sshExe -Command "install -d -m 0700 $quotedStaging" | Out-Null

    $scpArguments = @(
        '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=accept-new',
        '-P', $LinuxSshPort.ToString(),
        '-i', $LinuxSshKeyPath
    )
    $scpArguments += @($assets.Path)
    $scpArguments += $remoteTarget
    $scpOutput = & $scpExe @scpArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Linux PC SCP upload failed with exit code ${LASTEXITCODE}: $($scpOutput -join [Environment]::NewLine)"
    }

    $quotedRoot = Convert-ToShellLiteral $LinuxRemoteRoot
    $expectedBackupScriptSha256 = ($assets |
        Where-Object Name -eq 'georaeplan-backup.sh' |
        Select-Object -First 1).Sha256.ToLowerInvariant()
    $expectedServiceSha256 = ($assets |
        Where-Object Name -eq 'georaeplan-backup.service' |
        Select-Object -First 1).Sha256.ToLowerInvariant()
    $expectedTimerSha256 = ($assets |
        Where-Object Name -eq 'georaeplan-backup.timer' |
        Select-Object -First 1).Sha256.ToLowerInvariant()
    $runFlag = if ($RunAfterInstall) { 'true' } else { 'false' }
    $applyCommand = @"
set -eu
root=$quotedRoot
stage=$quotedStaging
bash -n "`$stage/georaeplan-backup.sh"
sudo -n install -d -m 0700 "`$root/backups/automatic"
sudo -n install -d -m 0755 "`$root/ops/state"
lock="`$root/storage/files/.georaeplan-backup-delete.lock"
if sudo -n test -L "`$lock"; then
  echo "Unsafe stored-file deletion lock symlink: `$lock" >&2
  exit 4
fi
if ! sudo -n test -e "`$lock"; then
  sudo -n touch "`$lock"
fi
sudo -n test -f "`$lock"
sudo -n chmod 0644 "`$lock"
sudo -n install -m 0750 "`$stage/georaeplan-backup.sh" /usr/local/sbin/georaeplan-backup.sh.new
sudo -n mv -f /usr/local/sbin/georaeplan-backup.sh.new /usr/local/sbin/georaeplan-backup.sh
sudo -n install -m 0644 "`$stage/georaeplan-backup.service" /etc/systemd/system/georaeplan-backup.service.new
sudo -n mv -f /etc/systemd/system/georaeplan-backup.service.new /etc/systemd/system/georaeplan-backup.service
sudo -n install -m 0644 "`$stage/georaeplan-backup.timer" /etc/systemd/system/georaeplan-backup.timer.new
sudo -n mv -f /etc/systemd/system/georaeplan-backup.timer.new /etc/systemd/system/georaeplan-backup.timer
sudo -n systemctl daemon-reload
sudo -n systemctl enable --now georaeplan-backup.timer
sudo -n systemctl is-enabled georaeplan-backup.timer
sudo -n systemctl is-active georaeplan-backup.timer
if [ '$runFlag' = 'true' ]; then
  sudo -n systemctl start georaeplan-backup.service
  test "`$(sudo -n systemctl show georaeplan-backup.service --property=Result --value)" = 'success'
  test "`$(sudo -n systemctl show georaeplan-backup.service --property=ExecMainStatus --value)" = '0'
fi
backup_script_hash=`$(sudo -n sha256sum /usr/local/sbin/georaeplan-backup.sh)
backup_script_hash=`$`{backup_script_hash%% *}
service_hash=`$(sudo -n sha256sum /etc/systemd/system/georaeplan-backup.service)
service_hash=`$`{service_hash%% *}
timer_hash=`$(sudo -n sha256sum /etc/systemd/system/georaeplan-backup.timer)
timer_hash=`$`{timer_hash%% *}
test "`$backup_script_hash" = '$expectedBackupScriptSha256'
test "`$service_hash" = '$expectedServiceSha256'
test "`$timer_hash" = '$expectedTimerSha256'
test "`$(sudo -n stat -Lc '%a:%U:%G' /usr/local/sbin/georaeplan-backup.sh)" = '750:root:root'
test "`$(sudo -n stat -Lc '%a:%U:%G' /etc/systemd/system/georaeplan-backup.service)" = '644:root:root'
test "`$(sudo -n stat -Lc '%a:%U:%G' /etc/systemd/system/georaeplan-backup.timer)" = '644:root:root'
printf 'backup_schedule_remote_assets=ok\n'
"@
    if ($PromptForSudoCredential -or $null -ne $SudoCredential) {
        $promptedApplyCommand =
            $applyCommand.Replace('sudo -n ', '')
        if ($promptedApplyCommand -match '(?im)(^|[;&|])[ \t]*sudo\b') {
            throw 'Prompted backup install command retained a nested sudo call.'
        }
        $applyOutput =
            Invoke-SshSudoCommand `
                -SshExe $sshExe `
                -Command $promptedApplyCommand `
                -Credential $SudoCredential
    }
    else {
        $applyOutput =
            Invoke-SshCommand -SshExe $sshExe -Command $applyCommand
    }
    $applyOutput | ForEach-Object { Write-Host $_ }
    Write-Host 'backup_schedule_apply=ok'
    Write-Host "backup_schedule_executed=$($RunAfterInstall.ToString().ToLowerInvariant())"
}
finally {
    if ($null -ne $sshExe) {
        try {
            Invoke-SshCommand -SshExe $sshExe -Command "rm -rf -- $quotedStaging" | Out-Null
        }
        catch {
            Write-Warning "Remote staging cleanup failed: $($_.Exception.Message)"
        }
    }
}
