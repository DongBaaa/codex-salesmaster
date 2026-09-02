[CmdletBinding()]
param(
    [string]$AssetRoot = '',
    [string]$BashPath = '',
    [switch]$SkipBehavior
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($AssetRoot)) {
    $AssetRoot = Join-Path $PSScriptRoot 'assets\georaeplan-backup'
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-True `
        -Condition ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) `
        -Message "$Label is missing required text: $Expected"
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Forbidden,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-True `
        -Condition ($Text.IndexOf($Forbidden, [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        -Message "$Label contains forbidden text: $Forbidden"
}

function Resolve-Bash {
    if (-not [string]::IsNullOrWhiteSpace($BashPath)) {
        if (-not (Test-Path -LiteralPath $BashPath -PathType Leaf)) {
            throw "Configured bash was not found: $BashPath"
        }
        return (Resolve-Path -LiteralPath $BashPath).Path
    }

    foreach ($candidate in @(
            'C:\Program Files\Git\bin\bash.exe',
            'C:\Program Files\Git\usr\bin\bash.exe')) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    $command = Get-Command bash.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    return ''
}

function Convert-ToBashPath {
    param(
        [Parameter(Mandatory = $true)][string]$BashExe,
        [Parameter(Mandatory = $true)][string]$WindowsPath
    )

    $resolved = (Resolve-Path -LiteralPath $WindowsPath).Path
    $converted = & $BashExe -lc "cygpath -u -- '$($resolved -replace "'", "'\''")'" 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($converted -join ''))) {
        return ($converted -join '').Trim()
    }

    $converted = & $BashExe -lc "wslpath -a -- '$($resolved -replace "'", "'\''")'" 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($converted -join ''))) {
        return ($converted -join '').Trim()
    }

    throw 'Unable to convert the Windows test path for bash.'
}

function Convert-ToBashLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-BashAllowFailure {
    param(
        [Parameter(Mandatory = $true)][string]$BashExe,
        [Parameter(Mandatory = $true)][string]$Command
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $BashExe -lc $Command 2>&1
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = @($output)
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

$backupScriptPath = Join-Path $AssetRoot 'georaeplan-backup.sh'
$servicePath = Join-Path $AssetRoot 'georaeplan-backup.service'
$timerPath = Join-Path $AssetRoot 'georaeplan-backup.timer'
$installerPath = Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupSchedule.ps1'

foreach ($requiredPath in @(
        $backupScriptPath,
        $servicePath,
        $timerPath,
        $installerPath)) {
    Assert-True `
        -Condition (Test-Path -LiteralPath $requiredPath -PathType Leaf) `
        -Message "Required backup schedule file is missing: $requiredPath"
}

foreach ($powerShellPath in @($installerPath, $PSCommandPath)) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $powerShellPath,
        [ref]$tokens,
        [ref]$errors)
    Assert-True `
        -Condition ($errors.Count -eq 0) `
        -Message "PowerShell parser errors in ${powerShellPath}: $($errors -join '; ')"
}

$backupScript = Get-Content -LiteralPath $backupScriptPath -Raw -Encoding UTF8
$service = Get-Content -LiteralPath $servicePath -Raw -Encoding UTF8
$timer = Get-Content -LiteralPath $timerPath -Raw -Encoding UTF8
$installer = Get-Content -LiteralPath $installerPath -Raw -Encoding UTF8

foreach ($required in @(
        'flock -n 9',
        '--services postgres',
        '--services api',
        'exec -T postgres',
        'exec -T api',
        "stat -Lc '%d:%i'",
        'georaeplan_itworld',
        'pg_dump',
        'pg_restore -l',
        'pg_current_snapshot()',
        'database_snapshot_drift',
        'backup_database_snapshot_consistency=ok',
        'database_snapshot_consistency=unchanged_across_all_dumps',
        'databases.txt',
        'georaeplan_usenet',
        'georaeplan_org_',
        'database_inventory',
        'database_count',
        'database_list_sha256',
        'central_business_count_sha256',
        'business_business_count_sha256',
        'backup_business_count_digest_consistency=ok',
        'business_count_digest_drift',
        'config --environment',
        'ITWORLD_POSTGRES_DB',
        'ConnectionStrings__Default',
        'ConnectionStrings__ITWORLD',
        'api_database_identity_drift',
        'backup_api_database_identity=ok',
        'port api 8080',
        'fileDeletionLeaseProtocol',
        'api_file_deletion_lease_protocol_mismatch',
        'api_not_ready',
        'api_process_changed_before_capture',
        'api_process_changed_during_capture',
        'backup_api_runtime_stable=before_capture',
        'backup_api_runtime_stable=after_capture',
        '/readyz',
        'backup_api_file_deletion_lease_protocol=shared-flock-v1',
        'pg_database_size',
        'realpath -m',
        'GEORAEPLAN_BACKUP_MIN_FREE_BYTES:-2147483648',
        'GEORAEPLAN_BACKUP_MIN_FREE_INODES:-1024',
        'backup_capacity_ok',
        'tar -tzf',
        'SHA256SUMS',
        'sha256sum -c',
        '.staging',
        'mv -T -- "$staging_dir" "$final_dir"',
        'backup=ok',
        'backup=failed',
        'replica=disabled',
        'GEORAEPLAN_BACKUP_RETENTION_DAYS:-14',
        '-mtime "+$RETENTION_DAYS"')) {
    Assert-Contains -Text $backupScript -Expected $required -Label 'backup script'
}

foreach ($forbidden in @(
        'docker compose down',
        'docker compose up',
        'docker compose restart',
        'docker system prune',
        'systemctl restart',
        'replica=ok')) {
    Assert-NotContains -Text $backupScript -Forbidden $forbidden -Label 'backup script'
}

Assert-Contains -Text $service -Expected 'Type=oneshot' -Label 'service unit'
Assert-Contains -Text $service -Expected 'ExecStart=/usr/local/sbin/georaeplan-backup.sh' -Label 'service unit'
Assert-Contains -Text $service -Expected 'ReadOnlyPaths=/srv/georaeplan/storage' -Label 'service unit'
Assert-NotContains -Text $service -Forbidden 'Restart=' -Label 'service unit'
Assert-NotContains -Text $service -Forbidden 'Requires=docker.service' -Label 'service unit'
Assert-NotContains -Text $service -Forbidden 'Wants=docker.service' -Label 'service unit'
Assert-NotContains -Text $service -Forbidden 'ConditionPathExists=' -Label 'service unit'
Assert-Contains -Text $timer -Expected 'OnCalendar=' -Label 'timer unit'
Assert-Contains -Text $timer -Expected 'Persistent=true' -Label 'timer unit'
Assert-Contains -Text $timer -Expected 'RandomizedDelaySec=' -Label 'timer unit'

$applyBoundary = $installer.IndexOf('if (-not $Apply)', [StringComparison]::Ordinal)
$firstRemoteWrite = $installer.IndexOf(
    'Invoke-SshCommand -SshExe $sshExe -Command "install -d -m 0700 $quotedStaging"',
    [StringComparison]::Ordinal)
Assert-True -Condition ($applyBoundary -ge 0) -Message 'Installer has no explicit -Apply plan boundary.'
Assert-True `
    -Condition ($firstRemoteWrite -gt $applyBoundary) `
    -Message 'Installer remote mutation is not guarded behind the -Apply plan boundary.'
Assert-Contains -Text $installer -Expected 'backup_schedule_remote_mutation=none' -Label 'installer'
Assert-Contains -Text $installer -Expected '# georaeplan-sudo-command-end' -Label 'installer'
Assert-Contains -Text $installer -Expected '[System.Management.Automation.PSCredential]$SudoCredential' -Label 'installer'
Assert-Contains -Text $installer -Expected '-Credential $SudoCredential' -Label 'installer'
Assert-Contains -Text $installer -Expected '-PromptForSudoCredential cannot be combined with -SudoCredential.' -Label 'installer'
Assert-Contains -Text $installer -Expected 'backup_schedule_remote_assets=ok' -Label 'installer'
Assert-Contains -Text $installer -Expected '[switch]$RunAfterInstall' -Label 'installer'
Assert-Contains -Text $installer -Expected "systemctl start georaeplan-backup.service" -Label 'installer'
Assert-Contains -Text $installer -Expected 'backup_schedule_executed=' -Label 'installer'
Assert-Contains -Text $installer -Expected 'installed_unreadable=%n mode=%a uid=%u gid=%g' -Label 'installer'
Assert-Contains -Text $installer -Expected 'config --services | grep -qx postgres' -Label 'installer'
Assert-Contains -Text $installer -Expected 'database_identity=ok' -Label 'installer'
Assert-NotContains -Text $installer -Forbidden 'systemctl restart' -Label 'installer'
Assert-NotContains -Text $backupScript -Forbidden 'GEORAEPLAN_BUSINESS_DATABASE' -Label 'backup script'

Write-Host 'backup_schedule_static_checks=ok'

$bashExe = Resolve-Bash
if ([string]::IsNullOrWhiteSpace($bashExe)) {
    if ($SkipBehavior) {
        Write-Host 'backup_schedule_bash_checks=skipped reason=bash_not_found'
        return
    }
    throw 'bash was not found. Install Git Bash or pass -BashPath.'
}

& $bashExe -n $backupScriptPath
if ($LASTEXITCODE -ne 0) {
    throw "bash -n failed: $backupScriptPath"
}
Write-Host "backup_schedule_bash_parse=ok bash=$bashExe"

if ($SkipBehavior) {
    Write-Host 'backup_schedule_behavior=skipped'
    return
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'georaeplan-backup-schedule-' + [Guid]::NewGuid().ToString('N'))
$fakeBin = Join-Path $testRoot 'fake-bin'
$opsRoot = Join-Path $testRoot 'ops'
$filesRoot = Join-Path $testRoot 'storage\files'
$keyringRoot = Join-Path $testRoot 'storage\data-protection-keys'
$oldSet = Join-Path $testRoot 'backups\automatic\sets\backup_20000101T000000Z-1.complete'
$tracePath = Join-Path $testRoot 'docker-trace.log'
$failureFlag = Join-Path $testRoot 'fail-pg-dump'
$snapshotDriftFlag = Join-Path $testRoot 'snapshot-drift'
$snapshotCountFile = Join-Path $testRoot 'snapshot-count'
$businessCountDriftFlag = Join-Path $testRoot 'business-count-drift'
$businessCountFile = Join-Path $testRoot 'business-count'
$fakeDockerPath = Join-Path $fakeBin 'docker'
$fakeFlockPath = Join-Path $fakeBin 'flock'
$fakeDuPath = Join-Path $fakeBin 'du'
$fakeDfPath = Join-Path $fakeBin 'df'
$fakeStatPath = Join-Path $fakeBin 'stat'
$fakeCurlPath = Join-Path $fakeBin 'curl'
$lowCapacityFlag = Join-Path $testRoot 'low-capacity'

try {
    foreach ($directory in @($fakeBin, $opsRoot, $filesRoot, $keyringRoot, $oldSet)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    Set-Content -LiteralPath (Join-Path $opsRoot 'docker-compose.yml') -Value 'services: { postgres: {}, api: {} }' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $opsRoot '.env') -Value @(
        'POSTGRES_DB=georaeplan',
        'ITWORLD_POSTGRES_DB=georaeplan_itworld'
    ) -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $filesRoot 'attachment.txt') -Value 'file-payload' -Encoding UTF8
    New-Item -ItemType File -Force -Path (Join-Path $filesRoot '.georaeplan-backup-delete.lock') | Out-Null
    Set-Content -LiteralPath (Join-Path $keyringRoot 'key.xml') -Value '<key>test-only</key>' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $oldSet 'COMPLETE') -Value 'old' -Encoding UTF8
    (Get-Item -LiteralPath $oldSet).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-30)

    $fakeDocker = @'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$GEORAEPLAN_FAKE_DOCKER_TRACE"
original_args="$*"
selected_service=''

while (( $# > 0 )); do
  case "$1" in
    config)
      if [[ "$*" == *'--environment'* ]]; then
        printf 'ITWORLD_POSTGRES_DB=georaeplan_itworld\n'
        exit 0
      fi
      if [[ "$*" == *'--services'* ]]; then
        printf 'postgres\napi\n'
        exit 0
      fi
      exit 92
      ;;
    ps)
      if [[ "$original_args" == *'--services api'* ]]; then
        printf 'api\n'
      else
        printf 'postgres\n'
      fi
      exit 0
      ;;
    port)
      [[ "$*" == "port api 8080" ]] || exit 94
      printf '127.0.0.1:18082\n'
      exit 0
      ;;
    exec)
      shift
      [[ "${1:-}" == "-T" ]] && shift
      selected_service="${1:-}"
      [[ "$selected_service" == "postgres" || "$selected_service" == "api" ]] || exit 90
      shift
      break
      ;;
    *)
      shift
      ;;
  esac
done

if [[ "$selected_service" == "api" ]]; then
  if [[ "${1:-}" == "stat" ]]; then
    printf '42:42\n'
    exit 0
  fi
  if [[ "${1:-}" == "sh" ]]; then
    if [[ "$*" == *'/proc/1/stat'* ]]; then
      count=0
      if [[ -f "$GEORAEPLAN_FAKE_API_PROCESS_COUNT_FILE" ]]; then
        count="$(cat "$GEORAEPLAN_FAKE_API_PROCESS_COUNT_FILE")"
      fi
      count=$((count + 1))
      printf '%s\n' "$count" > "$GEORAEPLAN_FAKE_API_PROCESS_COUNT_FILE"
      if [[ -f "$GEORAEPLAN_FAKE_API_PROCESS_CHANGE_FILE" ]]; then
        change_threshold="$(
          tr -cd '0-9' < "$GEORAEPLAN_FAKE_API_PROCESS_CHANGE_FILE"
        )"
        if [[ "$change_threshold" =~ ^[0-9]+$ && "$count" -ge "$change_threshold" ]]; then
          printf '222222\n'
          exit 0
        fi
      fi
      printf '111111\n'
      exit 0
    fi
    printf 'Default=georaeplan\n'
    if [[ -f "$GEORAEPLAN_FAKE_API_DB_DRIFT_FILE" ]]; then
      printf 'ITWORLD=GEORAEPLAN_ITWORLD\n'
    else
      printf 'ITWORLD=georaeplan_itworld\n'
    fi
    exit 0
  fi
  exit 93
fi

case "${1:-}" in
  sh)
    if [[ "$*" == *'POSTGRES_USER'* ]]; then
      printf 'georaeplan'
    else
      printf 'georaeplan'
    fi
    ;;
  pg_dump)
    if [[ -f "$GEORAEPLAN_FAKE_DOCKER_FAIL_FILE" ]]; then
      exit 42
    fi
    printf 'PGDUMP-TEST-%s\n' "$*"
    ;;
  psql)
    if [[ "$*" == *'SELECT datname FROM pg_database'* ]]; then
      printf '%s\n' georaeplan georaeplan_itworld georaeplan_org_branch01 georaeplan_usenet
      exit 0
    fi
    if [[ "$*" == *'pg_current_snapshot()'* ]]; then
      count=0
      if [[ -f "$GEORAEPLAN_FAKE_SNAPSHOT_COUNT_FILE" ]]; then
        count="$(cat "$GEORAEPLAN_FAKE_SNAPSHOT_COUNT_FILE")"
      fi
      count=$((count + 1))
      printf '%s\n' "$count" > "$GEORAEPLAN_FAKE_SNAPSHOT_COUNT_FILE"
      if [[ -f "$GEORAEPLAN_FAKE_SNAPSHOT_DRIFT_FILE" && "$count" -ge 2 ]]; then
        printf '100:103:\n'
      else
        printf '100:102:\n'
      fi
      exit 0
    fi
    if [[ "$*" == *'customers='* ]]; then
      count=0
      if [[ -f "$GEORAEPLAN_FAKE_BUSINESS_COUNT_FILE" ]]; then
        count="$(cat "$GEORAEPLAN_FAKE_BUSINESS_COUNT_FILE")"
      fi
      count=$((count + 1))
      printf '%s\n' "$count" > "$GEORAEPLAN_FAKE_BUSINESS_COUNT_FILE"
      payments=1
      if [[ -f "$GEORAEPLAN_FAKE_BUSINESS_COUNT_DRIFT_FILE" && "$count" -ge 2 ]]; then
        payments=2
      fi
      printf '%s\n' users=1 customers=1 items=1 transactions=1 rental_assets=1 invoices=1 "payments=$payments"
      exit 0
    fi
    printf '4096\n'
    ;;
  pg_restore)
    grep -q '^PGDUMP-TEST-' -
    printf 'fake restore list\n'
    ;;
  *)
    exit 91
    ;;
esac
'@
    $fakeDu = @'
#!/usr/bin/env bash
set -euo pipefail
printf '2048\t%s\n' "${@: -1}"
'@
    $fakeDf = @'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$*" == *'-PB1'* ]]; then
  printf 'Filesystem 1-blocks Used Available Capacity Mounted on\n'
  if [[ -f "$GEORAEPLAN_FAKE_LOW_CAPACITY_FILE" ]]; then
    printf 'fake 1000 999 1 100%% /fake\n'
  else
    printf 'fake 20000000000 1000 19999999000 1%% /fake\n'
  fi
else
  printf 'Filesystem Inodes IUsed IFree IUse%% Mounted on\n'
  printf 'fake 1000000 100 999900 1%% /fake\n'
fi
'@
    $fakeStat = @'
#!/usr/bin/env bash
set -euo pipefail
printf '42:42\n'
'@
$fakeCurl = @'
#!/usr/bin/env bash
set -euo pipefail
if [[ -f "$GEORAEPLAN_FAKE_API_NOT_READY_FILE" ]]; then
  exit 22
elif [[ -f "$GEORAEPLAN_FAKE_API_PROTOCOL_MISMATCH_FILE" ]]; then
  printf '{"status":"ready"}\n'
else
  printf '{"status":"ready","fileDeletionLeaseProtocol":"shared-flock-v1"}\n'
fi
'@
    [IO.File]::WriteAllText(
        $fakeDockerPath,
        $fakeDocker,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $fakeFlockPath,
        "#!/usr/bin/env bash`nexit 0`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $fakeDuPath,
        $fakeDu,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $fakeDfPath,
        $fakeDf,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $fakeStatPath,
        $fakeStat,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $fakeCurlPath,
        $fakeCurl,
        [Text.UTF8Encoding]::new($false))

    $bashTestRoot = Convert-ToBashPath -BashExe $bashExe -WindowsPath $testRoot
    $bashScript = Convert-ToBashPath -BashExe $bashExe -WindowsPath $backupScriptPath
    $bashFakeDocker = Convert-ToBashPath -BashExe $bashExe -WindowsPath $fakeDockerPath
    $bashFakeFlock = Convert-ToBashPath -BashExe $bashExe -WindowsPath $fakeFlockPath
    $bashFakeDu = Convert-ToBashPath -BashExe $bashExe -WindowsPath $fakeDuPath
    $bashFakeDf = Convert-ToBashPath -BashExe $bashExe -WindowsPath $fakeDfPath
    $bashFakeStat = Convert-ToBashPath -BashExe $bashExe -WindowsPath $fakeStatPath
    $bashFakeCurl = Convert-ToBashPath -BashExe $bashExe -WindowsPath $fakeCurlPath
    $bashTrace = "$bashTestRoot/docker-trace.log"
    $bashFailureFlag = "$bashTestRoot/fail-pg-dump"
    $bashSnapshotDriftFlag = "$bashTestRoot/snapshot-drift"
    $bashSnapshotCountFile = "$bashTestRoot/snapshot-count"
    $bashBusinessCountDriftFlag = "$bashTestRoot/business-count-drift"
    $bashBusinessCountFile = "$bashTestRoot/business-count"
    $bashLowCapacityFlag = "$bashTestRoot/low-capacity"
    $bashApiDatabaseDriftFlag = "$bashTestRoot/api-database-drift"
    $bashApiProtocolMismatchFlag = "$bashTestRoot/api-protocol-mismatch"
    $bashApiNotReadyFlag = "$bashTestRoot/api-not-ready"
    $bashApiProcessChangeFlag = "$bashTestRoot/api-process-change"
    $bashApiProcessCountFile = "$bashTestRoot/api-process-count"

    & $bashExe -lc "chmod 0755 $(Convert-ToBashLiteral $bashFakeDocker) $(Convert-ToBashLiteral $bashFakeFlock) $(Convert-ToBashLiteral $bashFakeDu) $(Convert-ToBashLiteral $bashFakeDf) $(Convert-ToBashLiteral $bashFakeStat) $(Convert-ToBashLiteral $bashFakeCurl)"
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to mark the fake docker executable.'
    }

    $environmentPrefix = @(
        "PATH=$(Convert-ToBashLiteral "$bashTestRoot/fake-bin"):`$PATH",
        "GEORAEPLAN_ROOT=$(Convert-ToBashLiteral $bashTestRoot)",
        "GEORAEPLAN_OPS_ROOT=$(Convert-ToBashLiteral "$bashTestRoot/ops")",
        "GEORAEPLAN_BACKUP_ROOT=$(Convert-ToBashLiteral "$bashTestRoot/backups/automatic")",
        "GEORAEPLAN_BACKUP_STATE_ROOT=$(Convert-ToBashLiteral "$bashTestRoot/ops/state")",
        "GEORAEPLAN_FILES_ROOT=$(Convert-ToBashLiteral "$bashTestRoot/storage/files")",
        "GEORAEPLAN_KEYRING_ROOT=$(Convert-ToBashLiteral "$bashTestRoot/storage/data-protection-keys")",
        "GEORAEPLAN_BACKUP_RETENTION_DAYS=14",
        "GEORAEPLAN_BACKUP_MIN_FREE_BYTES=1073741824",
        "GEORAEPLAN_BACKUP_MIN_FREE_INODES=128",
        "GEORAEPLAN_FAKE_DOCKER_TRACE=$(Convert-ToBashLiteral $bashTrace)",
        "GEORAEPLAN_FAKE_DOCKER_FAIL_FILE=$(Convert-ToBashLiteral $bashFailureFlag)",
        "GEORAEPLAN_FAKE_SNAPSHOT_DRIFT_FILE=$(Convert-ToBashLiteral $bashSnapshotDriftFlag)",
        "GEORAEPLAN_FAKE_SNAPSHOT_COUNT_FILE=$(Convert-ToBashLiteral $bashSnapshotCountFile)",
        "GEORAEPLAN_FAKE_BUSINESS_COUNT_DRIFT_FILE=$(Convert-ToBashLiteral $bashBusinessCountDriftFlag)",
        "GEORAEPLAN_FAKE_BUSINESS_COUNT_FILE=$(Convert-ToBashLiteral $bashBusinessCountFile)",
        "GEORAEPLAN_FAKE_LOW_CAPACITY_FILE=$(Convert-ToBashLiteral $bashLowCapacityFlag)",
        "GEORAEPLAN_FAKE_API_DB_DRIFT_FILE=$(Convert-ToBashLiteral $bashApiDatabaseDriftFlag)",
        "GEORAEPLAN_FAKE_API_PROTOCOL_MISMATCH_FILE=$(Convert-ToBashLiteral $bashApiProtocolMismatchFlag)",
        "GEORAEPLAN_FAKE_API_NOT_READY_FILE=$(Convert-ToBashLiteral $bashApiNotReadyFlag)",
        "GEORAEPLAN_FAKE_API_PROCESS_CHANGE_FILE=$(Convert-ToBashLiteral $bashApiProcessChangeFlag)",
        "GEORAEPLAN_FAKE_API_PROCESS_COUNT_FILE=$(Convert-ToBashLiteral $bashApiProcessCountFile)"
    ) -join ' '

    $traversalRoot = "$bashTestRoot/backups/../escape"
    $traversalCommand = "$environmentPrefix GEORAEPLAN_BACKUP_ROOT=$(Convert-ToBashLiteral $traversalRoot) bash $(Convert-ToBashLiteral $bashScript)"
    $traversalResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $traversalCommand
    Assert-True -Condition ($traversalResult.ExitCode -ne 0) -Message 'A dot-segment backup path unexpectedly succeeded.'
    Assert-Contains `
        -Text ($traversalResult.Output -join [Environment]::NewLine) `
        -Expected 'backup_configuration_invalid' `
        -Label 'dot-segment path rejection'
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $testRoot 'escape'))) `
        -Message 'A rejected dot-segment path created an output directory.'

    $successCommand = "$environmentPrefix bash $(Convert-ToBashLiteral $bashScript)"
    $successOutput = & $bashExe -lc $successCommand 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Backup behavior success run failed: $($successOutput -join [Environment]::NewLine)"
    }

    $completedSets = @(Get-ChildItem -LiteralPath (Join-Path $testRoot 'backups\automatic\sets') -Directory -Filter 'backup_*.complete')
    Assert-True -Condition ($completedSets.Count -eq 1) -Message 'Exactly one retained completed set was expected.'
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath $oldSet)) `
        -Message "Expired completed set was not removed. output=$($successOutput -join ' | ')"
    foreach ($name in @(
            'georaeplan.dump',
            'georaeplan_itworld.dump',
            'georaeplan_org_branch01.dump',
            'georaeplan_usenet.dump',
            'databases.txt',
            'files.tar.gz',
            'data-protection-keys.tar.gz',
            'metadata.txt',
            'SHA256SUMS',
            'COMPLETE')) {
        $artifact = Join-Path $completedSets[0].FullName $name
        Assert-True -Condition (Test-Path -LiteralPath $artifact -PathType Leaf) -Message "Completed set artifact is missing: $name"
        Assert-True -Condition ((Get-Item -LiteralPath $artifact).Length -gt 0) -Message "Completed set artifact is empty: $name"
    }

    $bashCompletedSet = Convert-ToBashPath -BashExe $bashExe -WindowsPath $completedSets[0].FullName
    & $bashExe -lc "cd $(Convert-ToBashLiteral $bashCompletedSet) && sha256sum -c SHA256SUMS >/dev/null"
    if ($LASTEXITCODE -ne 0) {
        throw 'Completed set SHA256SUMS validation failed.'
    }

    $successStatusPath = Join-Path $testRoot 'ops\state\backup-status.txt'
    $successStatus = Get-Content -LiteralPath $successStatusPath -Raw -Encoding UTF8
    Assert-Contains -Text $successStatus -Expected 'backup=ok' -Label 'success status'
    Assert-Contains -Text $successStatus -Expected 'replica=disabled' -Label 'success status'
    Assert-Contains -Text $successStatus -Expected 'required_available_bytes=' -Label 'success status'
    Assert-Contains -Text $successStatus -Expected 'file_deletion_lease=exclusive_during_database_and_file_capture' -Label 'success status'
    Assert-Contains -Text $successStatus -Expected 'database_snapshot_consistency=unchanged_across_all_dumps' -Label 'success status'
    Assert-Contains -Text $successStatus -Expected 'database_count=4' -Label 'success status'
    Assert-Contains -Text $successStatus -Expected 'database_list_sha256=' -Label 'success status'
    Assert-Contains -Text $successStatus -Expected 'database_snapshot_sha256=' -Label 'success status'
    Assert-NotContains -Text $successStatus -Forbidden 'replica=ok' -Label 'success status'
    $successStatusHash = (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash

    $successMetadata = Get-Content -LiteralPath (Join-Path $completedSets[0].FullName 'metadata.txt') -Raw -Encoding UTF8
    Assert-Contains -Text $successMetadata -Expected 'database_snapshot_consistency=unchanged_across_all_dumps' -Label 'backup metadata'
    Assert-Contains -Text $successMetadata -Expected 'database_manifest=databases.txt' -Label 'backup metadata'
    Assert-Contains -Text $successMetadata -Expected 'database_count=4' -Label 'backup metadata'
    Assert-Contains -Text $successMetadata -Expected 'database_snapshot_sha256=' -Label 'backup metadata'
    Assert-Contains -Text $successMetadata -Expected 'central_business_count_sha256=' -Label 'backup metadata'
    Assert-Contains -Text $successMetadata -Expected 'business_business_count_sha256=' -Label 'backup metadata'
    $databaseManifest = Get-Content -LiteralPath (Join-Path $completedSets[0].FullName 'databases.txt') -Raw -Encoding UTF8
    Assert-Contains -Text $databaseManifest -Expected "georaeplan_org_branch01`tgeoraeplan_org_branch01.dump`t" -Label 'database manifest'
    Assert-Contains -Text $databaseManifest -Expected "georaeplan_usenet`tgeoraeplan_usenet.dump`t" -Label 'database manifest'

    Remove-Item -LiteralPath $businessCountFile -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath $businessCountDriftFlag -Value 'drift' -Encoding UTF8
    $businessCountDriftResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    Assert-True -Condition ($businessCountDriftResult.ExitCode -ne 0) -Message 'A business-count drift unexpectedly published a backup.'
    Assert-Contains `
        -Text ($businessCountDriftResult.Output -join [Environment]::NewLine) `
        -Expected 'reason=business_count_digest_drift' `
        -Label 'business count digest drift failure'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'A business-count drift overwrote the last successful backup status.'
    Assert-True `
        -Condition (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'backups\automatic\sets') -Directory -Filter 'backup_*.complete').Count -eq 1) `
        -Message 'A business-count drift published an additional complete set.'
    Assert-True `
        -Condition (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'backups\automatic\.staging') -Force).Count -eq 0) `
        -Message 'A business-count drift left a staging directory behind.'
    Remove-Item -LiteralPath $businessCountDriftFlag -Force
    Remove-Item -LiteralPath $businessCountFile -Force -ErrorAction SilentlyContinue

    Remove-Item -LiteralPath $snapshotCountFile -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath $snapshotDriftFlag -Value 'drift' -Encoding UTF8
    $preSnapshotDriftTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    $snapshotDriftResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    Assert-True -Condition ($snapshotDriftResult.ExitCode -ne 0) -Message 'A cross-database snapshot drift unexpectedly published a backup.'
    Assert-Contains `
        -Text ($snapshotDriftResult.Output -join [Environment]::NewLine) `
        -Expected 'reason=database_snapshot_drift' `
        -Label 'database snapshot drift failure'
    $postSnapshotDriftTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    Assert-True `
        -Condition (([regex]::Matches($postSnapshotDriftTrace, ' pg_dump ')).Count -eq
                    ([regex]::Matches($preSnapshotDriftTrace, ' pg_dump ')).Count + 4) `
        -Message 'The snapshot drift harness did not enclose all discovered database dumps.'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'A snapshot-drift run overwrote the last successful backup status.'
    Assert-True `
        -Condition (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'backups\automatic\sets') -Directory -Filter 'backup_*.complete').Count -eq 1) `
        -Message 'A snapshot-drift run published an additional complete set.'
    Assert-True `
        -Condition (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'backups\automatic\.staging') -Force).Count -eq 0) `
        -Message 'A snapshot-drift run left a staging directory behind.'
    Remove-Item -LiteralPath $snapshotDriftFlag -Force
    Remove-Item -LiteralPath $snapshotCountFile -Force -ErrorAction SilentlyContinue

    Set-Content -LiteralPath (Join-Path $testRoot 'api-not-ready') -Value 'not-ready' -Encoding UTF8
    $preNotReadyTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    $notReadyResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    Assert-True -Condition ($notReadyResult.ExitCode -ne 0) -Message 'A non-ready API unexpectedly allowed a backup.'
    Assert-Contains `
        -Text ($notReadyResult.Output -join [Environment]::NewLine) `
        -Expected 'reason=api_not_ready' `
        -Label 'API readiness failure'
    $postNotReadyTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    Assert-True `
        -Condition (([regex]::Matches($preNotReadyTrace, ' pg_dump ')).Count -eq
                    ([regex]::Matches($postNotReadyTrace, ' pg_dump ')).Count) `
        -Message 'A non-ready API reached pg_dump.'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'A non-ready API overwrote the last successful backup status.'
    Remove-Item -LiteralPath (Join-Path $testRoot 'api-not-ready') -Force

    Set-Content -LiteralPath (Join-Path $testRoot 'api-protocol-mismatch') -Value 'mismatch' -Encoding UTF8
    $preProtocolTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    $protocolResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    Assert-True -Condition ($protocolResult.ExitCode -ne 0) -Message 'An incompatible API deletion-lease protocol unexpectedly succeeded.'
    Assert-Contains `
        -Text ($protocolResult.Output -join [Environment]::NewLine) `
        -Expected 'reason=api_file_deletion_lease_protocol_mismatch' `
        -Label 'API deletion-lease protocol failure'
    $postProtocolTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    Assert-True `
        -Condition (([regex]::Matches($preProtocolTrace, ' pg_dump ')).Count -eq
                    ([regex]::Matches($postProtocolTrace, ' pg_dump ')).Count) `
        -Message 'An incompatible API deletion-lease protocol reached pg_dump.'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'An incompatible API deletion-lease protocol overwrote the last successful backup status.'
    Remove-Item -LiteralPath (Join-Path $testRoot 'api-protocol-mismatch') -Force

    Set-Content -LiteralPath (Join-Path $testRoot 'api-database-drift') -Value 'drift' -Encoding UTF8
    $preDriftTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    $driftResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    Assert-True -Condition ($driftResult.ExitCode -ne 0) -Message 'Injected API database drift unexpectedly succeeded.'
    Assert-Contains `
        -Text ($driftResult.Output -join [Environment]::NewLine) `
        -Expected 'reason=api_database_identity_drift' `
        -Label 'API database identity drift failure'
    $postDriftTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    Assert-True `
        -Condition (([regex]::Matches($preDriftTrace, ' pg_dump ')).Count -eq
                    ([regex]::Matches($postDriftTrace, ' pg_dump ')).Count) `
        -Message 'API database identity drift reached pg_dump.'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'API database identity drift overwrote the last successful backup status.'
    Remove-Item -LiteralPath (Join-Path $testRoot 'api-database-drift') -Force

    Remove-Item -LiteralPath (Join-Path $testRoot 'api-process-count') -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath (Join-Path $testRoot 'api-process-change') -Value '2' -Encoding UTF8
    $preProcessChangeTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    $processChangeResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    $processCountDiagnostic = if (Test-Path -LiteralPath (Join-Path $testRoot 'api-process-count')) {
        Get-Content -LiteralPath (Join-Path $testRoot 'api-process-count') -Raw
    }
    else {
        'missing'
    }
    Assert-True `
        -Condition ($processChangeResult.ExitCode -ne 0) `
        -Message "An API process change during preflight unexpectedly succeeded. count=$processCountDiagnostic output=$($processChangeResult.Output -join ' | ')"
    Assert-Contains `
        -Text ($processChangeResult.Output -join [Environment]::NewLine) `
        -Expected 'reason=api_process_changed_before_capture' `
        -Label 'API process identity failure'
    $postProcessChangeTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    Assert-True `
        -Condition (([regex]::Matches($preProcessChangeTrace, ' pg_dump ')).Count -eq
                    ([regex]::Matches($postProcessChangeTrace, ' pg_dump ')).Count) `
        -Message 'An API process change during preflight reached pg_dump.'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'An API process change overwrote the last successful backup status.'
    Remove-Item -LiteralPath (Join-Path $testRoot 'api-process-change') -Force
    Remove-Item -LiteralPath (Join-Path $testRoot 'api-process-count') -Force -ErrorAction SilentlyContinue

    Set-Content -LiteralPath (Join-Path $testRoot 'api-process-change') -Value '3' -Encoding UTF8
    $processDuringCaptureResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    Assert-True -Condition ($processDuringCaptureResult.ExitCode -ne 0) -Message 'An API process change during capture unexpectedly succeeded.'
    Assert-Contains `
        -Text ($processDuringCaptureResult.Output -join [Environment]::NewLine) `
        -Expected 'reason=api_process_changed_during_capture' `
        -Label 'API process identity change during capture'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'An API process change during capture overwrote the last successful backup status.'
    Assert-True `
        -Condition (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'backups\automatic\sets') -Directory -Filter 'backup_*.complete').Count -eq 1) `
        -Message 'An API process change during capture published an additional complete set.'
    Remove-Item -LiteralPath (Join-Path $testRoot 'api-process-change') -Force
    Remove-Item -LiteralPath (Join-Path $testRoot 'api-process-count') -Force -ErrorAction SilentlyContinue

    $filesArchivePath = Join-Path $completedSets[0].FullName 'files.tar.gz'
    $bashFilesArchive = Convert-ToBashPath -BashExe $bashExe -WindowsPath $filesArchivePath
    $archiveEntries = @(& $bashExe -lc "tar -tzf $(Convert-ToBashLiteral $bashFilesArchive)")
    if ($LASTEXITCODE -ne 0) {
        throw 'Completed files archive listing failed.'
    }
    Assert-True `
        -Condition (-not ($archiveEntries -match '\.georaeplan-backup-delete\.lock$')) `
        -Message 'The coordination lock leaked into the files archive.'
    Assert-True `
        -Condition (-not ($archiveEntries -match '/\.[^/]+\.tmp$')) `
        -Message 'An unpublished staging file leaked into the files archive.'

    Set-Content -LiteralPath $failureFlag -Value 'fail' -Encoding UTF8
    $failureResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    $failureOutput = $failureResult.Output
    Assert-True -Condition ($failureResult.ExitCode -ne 0) -Message 'Injected pg_dump failure unexpectedly succeeded.'
    $postFailureHash = (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash
    Assert-True `
        -Condition ($successStatusHash -eq $postFailureHash) `
        -Message 'A failed run overwrote the last successful backup status.'

    $failureStatus = Get-Content -LiteralPath (Join-Path $testRoot 'ops\state\backup-failure-status.txt') -Raw -Encoding UTF8
    Assert-Contains -Text $failureStatus -Expected 'backup=failed' -Label 'failure status'
    Assert-Contains -Text $failureStatus -Expected 'replica=disabled' -Label 'failure status'
    Assert-True `
        -Condition (@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'backups\automatic\.staging') -Force).Count -eq 0) `
        -Message 'Failed run left a staging directory behind.'

    Remove-Item -LiteralPath $failureFlag -Force
    Set-Content -LiteralPath $lowCapacityFlag -Value 'low' -Encoding UTF8
    $preCapacityTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    $capacityResult = Invoke-BashAllowFailure -BashExe $bashExe -Command $successCommand
    $capacityOutput = $capacityResult.Output
    Assert-True -Condition ($capacityResult.ExitCode -ne 0) -Message 'Injected low-capacity run unexpectedly succeeded.'
    Assert-Contains `
        -Text ($capacityOutput -join [Environment]::NewLine) `
        -Expected 'reason=insufficient_capacity' `
        -Label 'capacity failure'
    $postCapacityTrace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    Assert-True `
        -Condition (([regex]::Matches($preCapacityTrace, ' pg_dump ')).Count -eq
                    ([regex]::Matches($postCapacityTrace, ' pg_dump ')).Count) `
        -Message 'Low-capacity preflight reached pg_dump.'
    Assert-True `
        -Condition ($successStatusHash -eq (Get-FileHash -LiteralPath $successStatusPath -Algorithm SHA256).Hash) `
        -Message 'A low-capacity run overwrote the last successful backup status.'

    $trace = Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8
    Assert-Contains -Text $trace -Expected 'postgres' -Label 'docker trace'
    Assert-Contains -Text $trace -Expected 'exec -T api stat -Lc %d:%i' -Label 'docker trace'
    Assert-Contains -Text $trace -Expected 'exec -T api sh -ceu' -Label 'docker trace'
    foreach ($forbidden in @(' up ', ' down ', ' restart ', ' system prune')) {
        Assert-NotContains -Text $trace -Forbidden $forbidden -Label 'docker trace'
    }

    Write-Host 'backup_schedule_behavior=ok'
    Write-Host "backup_schedule_success_set=$($completedSets[0].FullName)"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
