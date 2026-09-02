[CmdletBinding()]
param([string]$BashPath = '')

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Resolve-Bash {
    if (-not [string]::IsNullOrWhiteSpace($BashPath)) {
        return (Resolve-Path -LiteralPath $BashPath).Path
    }
    foreach ($candidate in @(
            'C:\Program Files\Git\bin\bash.exe',
            'C:\Program Files\Git\usr\bin\bash.exe')) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'Git Bash was not found.'
}

function Convert-ToBashPath {
    param([string]$BashExe, [string]$WindowsPath)
    $resolved = (Resolve-Path -LiteralPath $WindowsPath).Path
    $escaped = $resolved -replace "'", "'\''"
    $converted = & $BashExe -lc "cygpath -u -- '$escaped'"
    if ($LASTEXITCODE -ne 0) { throw "Unable to convert test path: $WindowsPath" }
    return ($converted -join '').Trim()
}

function Convert-ToBashLiteral {
    param([string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-Bash {
    param([string]$BashExe, [string]$Command, [switch]$AllowFailure)
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $BashExe -lc $Command 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "Bash failed (${exitCode}): $($output -join [Environment]::NewLine)"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

$scriptPath = Join-Path $PSScriptRoot 'assets\georaeplan-backup-restore-drill\georaeplan-backup-restore-drill.sh'
$installerPath = Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupRestoreDrill.ps1'
$validatorPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'ops\Test-GeoraePlanBackupRestoreDrillStatus.ps1'
foreach ($path in @($scriptPath, $installerPath, $validatorPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Restore drill asset is missing: $path"
}

$tokens = $null
$errors = $null
foreach ($path in @($PSCommandPath, $installerPath, $validatorPath)) {
    [void][Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) "PowerShell parser errors in ${path}: $($errors -join '; ')"
}

$source = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
$installer = Get-Content -LiteralPath $installerPath -Raw -Encoding UTF8
foreach ($required in @(
        '--network none', '--read-only', 'dst=/var/lib/postgresql/data',
        'pg_restore --exit-on-error --no-owner --no-privileges',
        'restore_drill=ok', 'network_mode=none', 'docker_exec',
        'assert_replica_set', 'cleanup_container',
        'central_business_count_sha256', 'business_business_count_sha256',
        'restore_drill_business_count_digest_mismatch',
        'docker_exec -i "$container_id" psql --no-password -X -q',
        'business_count_digest_contract=source_metadata_match',
        'resolve_trusted_system_executable',
        'realpath -e -- "$requested"',
        "stat -Lc '%u:%a'",
        'reason=untrusted_root',
        'reason=unsafe_metadata',
        'cifs|nfs|nfs4)', 'ext4)',
        'restore_drill_mount_invalid reason=local_block_source_invalid',
        'restore_drill_mount_invalid reason=same_physical_disk',
        'lsblk -srno NAME',
        'replica_bind_source="$replica_set"',
        'cleanup_restore_workdir',
        'restore_work_capacity_insufficient',
        'container_mount_contract_invalid',
        '{{.HostConfig.NetworkMode}}|{{.HostConfig.ReadonlyRootfs}}')) {
    Assert-True ($source.Contains($required)) "Restore drill source is missing: $required"
}
Assert-True `
    (-not $source.Contains('replica_bind_source="/proc/$$/fd/7"')) `
    'Restore drill must not pass a proc-fd directory to Docker as a bind source.'
foreach ($required in @(
        'backup_restore_drill_preflight_failed reason=mount_root_missing',
        'backup_restore_drill_preflight_failed reason=mount_root_reparse',
        'backup_restore_drill_preflight_failed reason=mount_target_invalid',
        'backup_restore_drill_preflight_failed reason=same_device',
        'backup_restore_drill_preflight_failed reason=replica_marker_missing',
        'backup_restore_drill_preflight_failed reason=backup_status_missing',
        'backup_restore_drill_preflight_failed reason=replica_status_missing',
        'cifs|nfs|nfs4)', 'ext4)',
        'restore_drill_mount_invalid reason=local_block_source_invalid',
        'restore_drill_mount_invalid reason=same_physical_disk',
        'lsblk -srno NAME',
        '[System.Management.Automation.PSCredential]$SudoCredential',
        'backup_restore_drill_preflight_privilege=provided_sudo',
        'if ($Apply -and $null -ne $SudoCredential)',
        'Use either -PromptForSudoCredential or -SudoCredential, not both.')) {
    Assert-True ($installer.Contains($required)) "Restore drill installer is missing diagnostic contract: $required"
}
foreach ($forbidden in @(
        'docker compose down', 'docker compose restart', 'docker system prune',
        'systemctl restart', 'georaeplan-postgres')) {
    Assert-True `
        ($source.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "Restore drill source contains forbidden operation: $forbidden"
}

$bashExe = Resolve-Bash
$bashScript = Convert-ToBashPath $bashExe $scriptPath
$syntax = Invoke-Bash -BashExe $bashExe -Command "bash -n $(Convert-ToBashLiteral $bashScript)"
Assert-True ($syntax.ExitCode -eq 0) 'Restore drill shell syntax is invalid.'

$fixtureRoot = Join-Path $env:TEMP ('georaeplan-restore-drill-test-' + [Guid]::NewGuid().ToString('N'))
$opsRoot = Join-Path $fixtureRoot 'ops'
$stateRoot = Join-Path $opsRoot 'state'
$replicaRoot = Join-Path $fixtureRoot 'replica'
$replicaStaging = Join-Path $replicaRoot '.staging'
$fakeBin = Join-Path $fixtureRoot 'bin'
$runId = '20260812T041836Z-62161'
$replicaId = '0123456789abcdef0123456789abcdef'
$sourceManifest = $null
$replicaManifest = $null
$replicaSet = Join-Path $replicaRoot "sets\replica_${runId}.complete"
$utf8 = [Text.UTF8Encoding]::new($false)
$businessCountText = @(
    'users=1',
    'customers=1',
    'items=1',
    'transactions=1',
    'rental_assets=1',
    'invoices=1',
    'payments=1') -join "`n"
$businessCountSha256 = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($businessCountText))).ToLowerInvariant()
$databaseNames = @('georaeplan', 'georaeplan_itworld', 'georaeplan_org_branch01', 'georaeplan_usenet')
$databaseManifestText = (($databaseNames | ForEach-Object { "$_`t$_.dump`t$businessCountSha256" }) -join "`n") + "`n"
$databaseListText = ($databaseNames -join "`n") + "`n"
$databaseDigestSetText = (($databaseNames | ForEach-Object { "$_=$businessCountSha256" }) -join "`n") + "`n"
$databaseListSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($databaseListText))).ToLowerInvariant()
$databaseDigestSetSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($databaseDigestSetText))).ToLowerInvariant()

try {
    foreach ($path in @($stateRoot, $replicaSet, $replicaStaging, $fakeBin)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $stateRoot 'backup-restore-drill.lock'), '', $utf8)
    [IO.File]::WriteAllText((Join-Path $replicaRoot '.georaeplan-replica.lock'), '', $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $replicaRoot '.georaeplan-replica-root'),
        "schema_version=1`nowner=georaeplan-external-backup-replica`nreplica_id=$replicaId`n",
        $utf8)
    foreach ($entry in @(
            'georaeplan.dump', 'georaeplan_itworld.dump', 'georaeplan_org_branch01.dump',
            'georaeplan_usenet.dump', 'files.tar.gz', 'data-protection-keys.tar.gz')) {
        [IO.File]::WriteAllText((Join-Path $replicaSet $entry), "fixture-$entry", $utf8)
    }
    [IO.File]::WriteAllText((Join-Path $replicaSet 'databases.txt'), $databaseManifestText, $utf8)
    $databaseManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $replicaSet 'databases.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        (Join-Path $replicaSet 'metadata.txt'),
        "backup=georaeplan`nrun_id=$runId`ncreated_at=$([DateTimeOffset]::UtcNow.AddHours(-2).ToString('o'))`ncentral_database=georaeplan`nbusiness_database=georaeplan_itworld`ndatabase_manifest=databases.txt`ndatabase_count=4`ndatabase_list_sha256=$databaseListSha256`ndatabase_manifest_sha256=$databaseManifestSha256`ndatabase_digest_set_sha256=$databaseDigestSetSha256`nfiles_archive=files.tar.gz`nkeyring_archive=data-protection-keys.tar.gz`nestimated_source_bytes=1`nrequired_available_bytes=1`nfile_deletion_lease=exclusive_during_database_and_file_capture`ndatabase_snapshot_consistency=unchanged_across_all_dumps`ndatabase_snapshot_sha256=$('a' * 64)`ncentral_business_count_sha256=$businessCountSha256`nbusiness_business_count_sha256=$businessCountSha256`nreplica=disabled`n",
        $utf8)

    $bashReplicaSet = Convert-ToBashPath $bashExe $replicaSet
    Invoke-Bash -BashExe $bashExe -Command (
        "cd $(Convert-ToBashLiteral $bashReplicaSet) && " +
        "sha256sum georaeplan.dump georaeplan_itworld.dump georaeplan_org_branch01.dump georaeplan_usenet.dump databases.txt files.tar.gz data-protection-keys.tar.gz metadata.txt > SHA256SUMS") | Out-Null
    $sourceManifest = (Get-FileHash -LiteralPath (Join-Path $replicaSet 'SHA256SUMS') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        (Join-Path $replicaSet 'COMPLETE'),
        "backup=complete`nrun_id=$runId`nverified_at=$([DateTimeOffset]::UtcNow.AddHours(-2).ToString('o'))`nmanifest_sha256=$sourceManifest`n",
        $utf8)
    $replicaManifestOutput = Invoke-Bash -BashExe $bashExe -Command (
        "cd $(Convert-ToBashLiteral $bashReplicaSet) && " +
        "sha256sum COMPLETE SHA256SUMS data-protection-keys.tar.gz databases.txt files.tar.gz georaeplan.dump georaeplan_itworld.dump georaeplan_org_branch01.dump georaeplan_usenet.dump metadata.txt | sha256sum | awk '{print `$1}'")
    $replicaManifest = (($replicaManifestOutput.Output | Select-Object -Last 1) -join '').Trim()
    [IO.File]::WriteAllText(
        (Join-Path $replicaSet 'REPLICA'),
        "replica=complete`nreplica_id=$replicaId`nsource_run_id=$runId`nsource_manifest_sha256=$sourceManifest`nreplica_manifest_sha256=$replicaManifest`nreplicated_at=$([DateTimeOffset]::UtcNow.AddHours(-1).ToString('o'))`n",
        $utf8)

    $bashStateRoot = Convert-ToBashPath $bashExe $stateRoot
    $bashReplicaRoot = Convert-ToBashPath $bashExe $replicaRoot
    $bashFakeBin = Convert-ToBashPath $bashExe $fakeBin
    $bashLog = "$bashFakeBin/docker.log"
    [IO.File]::WriteAllText(
        (Join-Path $stateRoot 'backup-status.txt'),
        "backup=ok`nreplica=disabled`nrun_id=$runId`ncompleted_at=$([DateTimeOffset]::UtcNow.AddHours(-3).ToString('o'))`nset_path=/unused`nmanifest_sha256=$sourceManifest`n",
        $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $stateRoot 'external-replica-status.txt'),
        "replica=ok`nreplica_id=$replicaId`nsource_run_id=$runId`nsource_manifest_sha256=$sourceManifest`ndatabase_count=4`ndatabase_list_sha256=$databaseListSha256`ndatabase_manifest_sha256=$databaseManifestSha256`ndatabase_digest_set_sha256=$databaseDigestSetSha256`nreplica_set_path=$bashReplicaSet`nreplica_manifest_sha256=$replicaManifest`nverified_at=$([DateTimeOffset]::UtcNow.AddHours(-1).ToString('o'))`nrestore_catalog_validation=ok`narchive_validation=ok`n",
        $utf8)

    [IO.File]::WriteAllText(
        (Join-Path $fakeBin 'docker'),
        @'
#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" >> "$FAKE_DOCKER_LOG"
case "$1" in
  create)
    data_source=''
    for argument in "$@"; do
      case "$argument" in
        type=bind,src=*,dst=/var/lib/postgresql/data)
          data_source="${argument#type=bind,src=}"
          data_source="${data_source%,dst=/var/lib/postgresql/data}"
          ;;
      esac
    done
    [[ -n "$data_source" ]]
    printf '%s\n' "$data_source" > "$FAKE_DOCKER_DATA_SOURCE_FILE"
    printf '%064d\n' 0 | tr '0' f
    ;;
  inspect)
    if [[ "${FAKE_DOCKER_INSPECT_MISMATCH:-0}" == 1 ]]; then
      printf '%s\n' 'invalid'
    elif [[ "$*" == *'/var/lib/postgresql/data'* ]]; then
      printf 'bind|%s|true\n' "$(cat "$FAKE_DOCKER_DATA_SOURCE_FILE")"
    elif [[ "$*" == *'/restore'* ]]; then
      printf 'bind|%s|false\n' "$FAKE_DOCKER_REPLICA_SOURCE"
    else
      printf '%s\n' 'none|true'
    fi
    ;;
  start) exit 0 ;;
  rm) exit 0 ;;
  exec)
    shift
    if [[ "${1:-}" == -i ]]; then shift; fi
    shift
    case "$1" in
      pg_isready|createdb) exit 0 ;;
      pg_restore)
        if [[ "${FAKE_DOCKER_FAIL_BUSINESS_RESTORE:-0}" == 1 && "$*" == *'/restore/georaeplan_itworld.dump'* ]]; then exit 42; fi
        exit 0
        ;;
      psql)
        cat >/dev/null
        payments=1
        if [[ "${FAKE_DOCKER_COUNT_MISMATCH:-0}" == 1 ]]; then payments=2; fi
        printf '%s\n' users=1 customers=1 items=1 transactions=1 rental_assets=1 invoices=1 "payments=$payments"
        ;;
      *) exit 43 ;;
    esac
    ;;
  *) exit 44 ;;
esac
'@,
        $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $fakeBin 'timeout'),
        "#!/usr/bin/env bash`nset -eu`nshift`nexec `"`$@`"`n",
        $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $fakeBin 'pg_restore'),
        "#!/usr/bin/env bash`n[[ `"`$1`" == -l && -f `"`$2`" ]]`n",
        $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $fakeBin 'flock'),
        "#!/usr/bin/env bash`nexit 0`n",
        $utf8)

    $common = @(
        "chmod +x $(Convert-ToBashLiteral $bashFakeBin)/docker $(Convert-ToBashLiteral $bashFakeBin)/timeout $(Convert-ToBashLiteral $bashFakeBin)/pg_restore $(Convert-ToBashLiteral $bashFakeBin)/flock &&",
        "PATH=$(Convert-ToBashLiteral $bashFakeBin):`$PATH",
        "FAKE_DOCKER_LOG=$(Convert-ToBashLiteral $bashLog)",
        "FAKE_DOCKER_DATA_SOURCE_FILE=$(Convert-ToBashLiteral "$bashFakeBin/docker-data-source.txt")",
        "FAKE_DOCKER_REPLICA_SOURCE=$(Convert-ToBashLiteral $bashReplicaSet)",
        "GEORAEPLAN_BACKUP_STATE_ROOT=$(Convert-ToBashLiteral $bashStateRoot)",
        "GEORAEPLAN_REPLICA_ROOT=$(Convert-ToBashLiteral $bashReplicaRoot)",
        "GEORAEPLAN_REPLICA_ID=$replicaId",
        "GEORAEPLAN_RESTORE_DRILL_IMAGE_ID=sha256:$('c' * 64)",
        'GEORAEPLAN_RESTORE_DRILL_WORK_RESERVE_BYTES=0',
        "GEORAEPLAN_RESTORE_DRILL_DOCKER_BIN=$(Convert-ToBashLiteral "$bashFakeBin/docker")",
        "GEORAEPLAN_RESTORE_DRILL_TIMEOUT_BIN=$(Convert-ToBashLiteral "$bashFakeBin/timeout")") -join ' '

    $successResult = Invoke-Bash -BashExe $bashExe -AllowFailure -Command `
        "$common bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem"
    $happyPathFailureStatus = Join-Path $stateRoot 'backup-restore-drill-failure-status.txt'
    $happyPathFailureDetail = if (Test-Path -LiteralPath $happyPathFailureStatus -PathType Leaf) {
        Get-Content -LiteralPath $happyPathFailureStatus -Raw -Encoding UTF8
    }
    else { '(failure status missing)' }
    Assert-True `
        ($successResult.ExitCode -eq 0) `
        "Restore drill happy path failed: $($successResult.Output -join [Environment]::NewLine) $happyPathFailureDetail"
    $successPath = Join-Path $stateRoot 'backup-restore-drill-status.txt'
    Assert-True (Test-Path -LiteralPath $successPath -PathType Leaf) 'Restore drill success status was not published.'
    $success = Get-Content -LiteralPath $successPath -Raw -Encoding UTF8
    Assert-True ($success.Contains('restore_drill=ok')) 'Restore drill did not publish success.'
    Assert-True ($success.Contains('network_mode=none')) 'Restore drill did not prove network isolation.'
    Assert-True ($success.Contains('business_count_digest_contract=source_metadata_match')) 'Restore drill did not publish the source-bound count digest contract.'
    Assert-True ($success.Contains('database_count=4')) 'Restored database count was not recorded.'
    Assert-True ($success.Contains("restored_database_set_sha256=$databaseDigestSetSha256")) 'Restored database digest set was not recorded.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $stateRoot 'backup-restore-drill-failure-status.txt'))) 'Failure status remained after success.'
    $dockerLog = Get-Content -LiteralPath (Join-Path $fakeBin 'docker.log') -Raw -Encoding UTF8
    Assert-True ($dockerLog.Contains('create --name')) 'Ephemeral container was not created.'
    Assert-True ($dockerLog.Contains('--network none')) 'Ephemeral container was not networkless.'
    Assert-True ($dockerLog.Contains('dst=/var/lib/postgresql/data')) 'Ephemeral restore data workspace was not mounted.'
    Assert-True ($dockerLog.Contains('inspect --format')) 'Ephemeral container contract was not inspected.'
    Assert-True ($dockerLog.Contains('restore_000 /restore/georaeplan.dump')) 'Central dump was not restored.'
    Assert-True ($dockerLog.Contains('restore_001 /restore/georaeplan_itworld.dump')) 'ITWORLD dump was not restored.'
    Assert-True ($dockerLog.Contains('restore_002 /restore/georaeplan_org_branch01.dump')) 'Organization dump was not restored.'
    Assert-True ($dockerLog.Contains('restore_003 /restore/georaeplan_usenet.dump')) 'USENET dump was not restored.'
    Assert-True ($dockerLog.Contains('exec -i')) 'Business-count query did not keep Docker stdin attached.'
    Assert-True ($dockerLog.Contains('rm -f')) 'Ephemeral container was not removed.'
    Assert-True `
        (@(Get-ChildItem -LiteralPath $replicaStaging -Directory -Filter 'restore-drill-*').Count -eq 0) `
        'Successful restore drill left an external restore workspace behind.'

    $successBytes = [IO.File]::ReadAllBytes($successPath)
    $mountMismatchResult = Invoke-Bash -BashExe $bashExe -AllowFailure -Command (
        "$common FAKE_DOCKER_INSPECT_MISMATCH=1 " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem")
    Assert-True ($mountMismatchResult.ExitCode -ne 0) 'An invalid container mount contract was accepted.'
    Assert-True `
        (($mountMismatchResult.Output -join [Environment]::NewLine).Contains('restore_drill_container_mount_contract_invalid')) `
        'The invalid container mount contract did not fail with the bounded reason.'
    Assert-True `
        ([Convert]::ToBase64String([IO.File]::ReadAllBytes($successPath)) -eq
            [Convert]::ToBase64String($successBytes)) `
        'An invalid container mount contract overwrote the previous success status.'
    Assert-True `
        (@(Get-ChildItem -LiteralPath $replicaStaging -Directory -Filter 'restore-drill-*').Count -eq 0) `
        'Invalid mount contract left an external restore workspace behind.'

    $countMismatchResult = Invoke-Bash -BashExe $bashExe -AllowFailure -Command (
        "$common FAKE_DOCKER_COUNT_MISMATCH=1 " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem")
    Assert-True ($countMismatchResult.ExitCode -ne 0) 'A restored business-count digest mismatch was accepted.'
    Assert-True `
        (($countMismatchResult.Output -join [Environment]::NewLine).Contains('restore_drill_business_count_digest_mismatch')) `
        'The restored business-count digest mismatch did not fail with the bounded reason.'
    Assert-True `
        ([Convert]::ToBase64String([IO.File]::ReadAllBytes($successPath)) -eq
            [Convert]::ToBase64String($successBytes)) `
        'A restored business-count digest mismatch overwrote the previous success status.'
    Assert-True `
        (@(Get-ChildItem -LiteralPath $replicaStaging -Directory -Filter 'restore-drill-*').Count -eq 0) `
        'Business-count mismatch left an external restore workspace behind.'

    $failureResult = Invoke-Bash -BashExe $bashExe -AllowFailure -Command (
        "$common FAKE_DOCKER_FAIL_BUSINESS_RESTORE=1 " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem")
    Assert-True ($failureResult.ExitCode -ne 0) 'Injected restore failure was accepted.'
    Assert-True `
        ([Convert]::ToBase64String([IO.File]::ReadAllBytes($successPath)) -eq
            [Convert]::ToBase64String($successBytes)) `
        'Restore failure overwrote the previous success status.'
    $failureStatus = Get-Content -LiteralPath (Join-Path $stateRoot 'backup-restore-drill-failure-status.txt') -Raw -Encoding UTF8
    Assert-True ($failureStatus.Contains('restore_drill=failed')) 'Restore failure status was not published.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $fakeBin 'docker.log') -Raw -Encoding UTF8).Contains('rm -f')) 'Failure did not attempt bounded container cleanup.'
    Assert-True `
        (@(Get-ChildItem -LiteralPath $replicaStaging -Directory -Filter 'restore-drill-*').Count -eq 0) `
        'Injected restore failure left an external restore workspace behind.'

    Write-Host 'backup_restore_drill_static_checks=ok'
    Write-Host 'backup_restore_drill_behavior_checks=ok'
    Write-Host "backup_restore_drill_source_run_id=$runId"
    Write-Host "backup_restore_drill_replica_manifest_sha256=$replicaManifest"
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
