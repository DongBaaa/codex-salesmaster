[CmdletBinding()]
param(
    [string]$BashPath = ''
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Resolve-Bash {
    if (-not [string]::IsNullOrWhiteSpace($BashPath)) {
        return (Resolve-Path -LiteralPath $BashPath).Path
    }
    foreach ($candidate in @(
            'C:\Program Files\Git\bin\bash.exe',
            'C:\Program Files\Git\usr\bin\bash.exe')) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    throw 'Git Bash was not found.'
}

function Convert-ToBashPath {
    param(
        [Parameter(Mandatory = $true)][string]$BashExe,
        [Parameter(Mandatory = $true)][string]$WindowsPath
    )
    $resolved = (Resolve-Path -LiteralPath $WindowsPath).Path
    $converted = & $BashExe -lc "cygpath -u -- '$($resolved -replace "'", "'\''")'"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to convert test path: $WindowsPath"
    }
    return ($converted -join '').Trim()
}

function Convert-ToBashLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-Bash {
    param(
        [Parameter(Mandatory = $true)][string]$BashExe,
        [Parameter(Mandatory = $true)][string]$Command,
        [switch]$AllowFailure
    )
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $BashExe -lc $Command 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
    }
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "Bash failed (${exitCode}): $($output -join [Environment]::NewLine)"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

$scriptPath = Join-Path $PSScriptRoot 'assets\georaeplan-backup-replica\georaeplan-backup-replica.sh'
$servicePath = Join-Path $PSScriptRoot 'assets\georaeplan-backup-replica\georaeplan-backup-replica.service'
$timerPath = Join-Path $PSScriptRoot 'assets\georaeplan-backup-replica\georaeplan-backup-replica.timer'
$readmePath = Join-Path $PSScriptRoot 'assets\georaeplan-backup-replica\README.md'
$installerPath = Join-Path $PSScriptRoot 'Install-GeoraeplanLinuxPcBackupReplica.ps1'
Assert-True (Test-Path -LiteralPath $scriptPath -PathType Leaf) 'Replica script is missing.'
foreach ($requiredPath in @($servicePath, $timerPath, $readmePath)) {
    Assert-True (Test-Path -LiteralPath $requiredPath -PathType Leaf) "Replica asset is missing: $requiredPath"
}
Assert-True (Test-Path -LiteralPath $installerPath -PathType Leaf) 'Replica installer is missing.'

$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $PSCommandPath,
    [ref]$tokens,
    [ref]$errors)
Assert-True ($errors.Count -eq 0) "PowerShell parser errors: $($errors -join '; ')"

$source = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
$service = Get-Content -LiteralPath $servicePath -Raw -Encoding UTF8
$timer = Get-Content -LiteralPath $timerPath -Raw -Encoding UTF8
$readme = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
$installer = Get-Content -LiteralPath $installerPath -Raw -Encoding UTF8
foreach ($required in @(
        'replica=ok',
        'replica=failed',
        'database_snapshot_consistency',
        'unchanged_across_both_dumps',
        'findmnt -T',
        'cifs|nfs|nfs4',
        'ext4)',
        'reason=local_block_source_invalid',
        'reason=same_physical_disk',
        'lsblk -srno NAME',
        'reason=same_device',
        '.georaeplan-replica-root',
        'georaeplan-external-backup-replica',
        'flock -s -w',
        'sha256sum -c SHA256SUMS',
        'pg_restore -l',
        'tar -tzf',
        'mv -T -- "$staging_dir" "$final_dir"',
        'external-replica-status.txt',
        'restore_catalog_validation=ok',
        'archive_validation=ok')) {
    Assert-True `
        ($source.IndexOf($required, [StringComparison]::Ordinal) -ge 0) `
        "Replica script is missing contract text: $required"
}
Assert-True ($source.Contains('remove_owned_staging_dir')) 'Replica script must clean only an exact owned staging set.'
Assert-True (-not $source.Contains('rm -rf')) 'Replica script must not recursively delete staging paths.'
$sourceRevalidationCount = [regex]::Matches(
    $source,
    'assert_archive_set "\$source_set" "\$run_id" "\$source_manifest_sha256"').Count
Assert-True ($sourceRevalidationCount -ge 3) 'Replica script must revalidate the full source set before and after publication.'
Assert-True ($source.Contains('exec 8< "$SOURCE_LOCK_FILE"')) 'Replica must open the source backup lock read-only under the service read-only source policy.'
Assert-True (-not $source.Contains('exec 8<> "$SOURCE_LOCK_FILE"')) 'Replica must not request write access to the source backup lock.'
foreach ($required in @(
        'RequiresMountsFor=/mnt/georaeplan-backup-replica',
        'EnvironmentFile=/etc/georaeplan/backup-replica.env',
        'ExecStart=/usr/local/sbin/georaeplan-backup-replica.sh',
        'ReadOnlyPaths=/srv/georaeplan/backups/automatic',
        'ReadWritePaths=/srv/georaeplan/ops/state',
        'ReadWritePaths=/mnt/georaeplan-backup-replica')) {
    Assert-True ($service.Contains($required)) "Replica service is missing: $required"
}
Assert-True (-not $service.Contains('Restart=')) 'Replica service must not restart on failure.'
Assert-True ($timer.Contains('Persistent=true')) 'Replica timer must be persistent.'
Assert-True ($timer.Contains('Unit=georaeplan-backup-replica.service')) 'Replica timer targets the wrong service.'
Assert-True ($readme.Contains('restore_drill=not_proven')) 'Replica documentation must preserve the restore-drill boundary.'
foreach ($required in @(
        "if (-not `$Apply)",
        'backup_replica_remote_mutation=none',
        "ReplicaRoot must remain /mnt/georaeplan-backup-replica",
        'backup_replica_preflight_failed reason=mount_root_missing',
        'backup_replica_preflight_failed reason=mount_root_reparse',
        'backup_replica_preflight_failed reason=same_device',
        'backup_replica_preflight_failed reason=backup_status_missing',
        'backup_replica_preflight_failed reason=capacity_invalid',
        'backup_replica_preflight_failed reason=capacity_insufficient',
        'cifs|nfs|nfs4)',
        'ext4)',
        'replica_mount_invalid reason=local_block_source_invalid',
        'replica_mount_invalid reason=same_physical_disk',
        'lsblk -srno NAME',
        '[System.Management.Automation.PSCredential]$SudoCredential',
        'Use either -PromptForSudoCredential or -SudoCredential, not both.',
        'replica_apply_preflight=ok',
        'systemctl enable --now georaeplan-backup-replica.timer',
        'systemctl start georaeplan-backup-replica.service',
        'Explicit -Apply boundary crossed')) {
    Assert-True ($installer.Contains($required)) "Replica installer is missing: $required"
}
$applyBoundary = $installer.IndexOf('if (-not $Apply)', [StringComparison]::Ordinal)
$firstRemoteWrite = $installer.IndexOf(
    'Invoke-SshCommand -SshExe $sshExe -Command "set -eu; install -d -m 0700 $quotedStage"',
    [StringComparison]::Ordinal)
Assert-True ($applyBoundary -ge 0 -and $firstRemoteWrite -gt $applyBoundary) 'Replica installer can write before -Apply.'
Assert-True (-not $installer.Contains('rm -rf')) 'Replica installer must not recursively delete its staging directory.'
foreach ($forbidden in @(
        'docker compose down',
        'docker compose restart',
        'systemctl restart',
        'replica=ok\n')) {
    if ($forbidden -eq 'replica=ok\n') { continue }
    Assert-True `
        ($source.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -lt 0) `
        "Replica script contains forbidden operation: $forbidden"
}

$bashExe = Resolve-Bash
$syntax = Invoke-Bash -BashExe $bashExe -Command (
    'bash -n ' + (Convert-ToBashLiteral (Convert-ToBashPath $bashExe $scriptPath)))
Assert-True ($syntax.ExitCode -eq 0) 'Replica shell syntax is invalid.'

$fixtureRoot = Join-Path $env:TEMP ('georaeplan-replica-test-' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $fixtureRoot 'source'
$stateRoot = Join-Path $fixtureRoot 'state'
$replicaRoot = Join-Path $fixtureRoot 'replica'
$fakeBin = Join-Path $fixtureRoot 'bin'
$runId = '20260812T041836Z-62161'
$replicaId = '0123456789abcdef0123456789abcdef'
$sourceSet = Join-Path $sourceRoot "sets\backup_${runId}.complete"

try {
    foreach ($path in @($sourceSet, $stateRoot, $replicaRoot, $fakeBin)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    New-Item -ItemType File -Path (Join-Path $sourceRoot 'georaeplan-backup.lock') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $replicaRoot '.georaeplan-replica.lock') -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $replicaRoot '.georaeplan-replica-root'),
        "schema_version=1`nowner=georaeplan-external-backup-replica`nreplica_id=$replicaId`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sourceSet 'georaeplan.dump'), 'central-dump', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sourceSet 'georaeplan_itworld.dump'), 'business-dump', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $sourceSet 'metadata.txt'),
        "backup=georaeplan`nrun_id=$runId`nreplica=disabled`n",
        [Text.UTF8Encoding]::new($false))
    $payload = Join-Path $fixtureRoot 'payload'
    New-Item -ItemType Directory -Path $payload | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'payload.txt'), 'payload', [Text.UTF8Encoding]::new($false))

    $bashSourceSet = Convert-ToBashPath $bashExe $sourceSet
    $bashPayload = Convert-ToBashPath $bashExe $payload
    Invoke-Bash -BashExe $bashExe -Command (
        "tar -czf $(Convert-ToBashLiteral "$bashSourceSet/files.tar.gz") -C $(Convert-ToBashLiteral $bashPayload) . && " +
        "tar -czf $(Convert-ToBashLiteral "$bashSourceSet/data-protection-keys.tar.gz") -C $(Convert-ToBashLiteral $bashPayload) .") | Out-Null
    $manifestCommand = @(
        "cd $(Convert-ToBashLiteral $bashSourceSet)",
        'sha256sum georaeplan.dump georaeplan_itworld.dump files.tar.gz data-protection-keys.tar.gz metadata.txt > SHA256SUMS'
    ) -join ' && '
    Invoke-Bash -BashExe $bashExe -Command $manifestCommand | Out-Null
    $manifestHash = (Get-FileHash -LiteralPath (Join-Path $sourceSet 'SHA256SUMS') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        (Join-Path $sourceSet 'COMPLETE'),
        "backup=complete`nrun_id=$runId`nverified_at=2026-08-12T13:19:32+09:00`nmanifest_sha256=$manifestHash`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $stateRoot 'backup-status.txt'),
        "backup=ok`nreplica=disabled`nrun_id=$runId`ncompleted_at=2026-08-12T13:19:32+09:00`nset_path=$($sourceSet -replace '\\','/')`nmanifest_sha256=$manifestHash`nretention_days=14`nestimated_source_bytes=1`nrequired_available_bytes=1`nfile_deletion_lease=exclusive_during_database_and_file_capture`ndatabase_snapshot_consistency=unchanged_across_both_dumps`ndatabase_snapshot_sha256=$('a' * 64)`n",
        [Text.UTF8Encoding]::new($false))
    $fakePgRestore = Join-Path $fakeBin 'pg_restore'
    [IO.File]::WriteAllText(
        $fakePgRestore,
        @'
#!/usr/bin/env bash
[[ "$1" == '-l' && -f "$2" ]]
'@,
        [Text.UTF8Encoding]::new($false))
    $fakeFlock = Join-Path $fakeBin 'flock'
    [IO.File]::WriteAllText(
        $fakeFlock,
        @'
#!/usr/bin/env bash
exit 0
'@,
        [Text.UTF8Encoding]::new($false))

    $bashScript = Convert-ToBashPath $bashExe $scriptPath
    $bashSourceRoot = Convert-ToBashPath $bashExe $sourceRoot
    $bashStateRoot = Convert-ToBashPath $bashExe $stateRoot
    $bashReplicaRoot = Convert-ToBashPath $bashExe $replicaRoot
    $bashFakeBin = Convert-ToBashPath $bashExe $fakeBin
    $bashStatusPath = "$bashStateRoot/backup-status.txt"
    $normalizedStatus = (Get-Content -LiteralPath (Join-Path $stateRoot 'backup-status.txt') -Raw -Encoding UTF8).
        Replace(($sourceSet -replace '\\','/'), "$bashSourceRoot/sets/backup_${runId}.complete")
    [IO.File]::WriteAllText(
        (Join-Path $stateRoot 'backup-status.txt'),
        $normalizedStatus,
        [Text.UTF8Encoding]::new($false))

    # An unrecognized stale entry must be retained and rejected, never recursively erased.
    $staleStage = Join-Path $replicaRoot ".staging\replica_${replicaId}_stale.staging"
    New-Item -ItemType Directory -Path $staleStage | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $staleStage 'foreign.txt'),
        'foreign',
        [Text.UTF8Encoding]::new($false))
    $staleFailure = Invoke-Bash -BashExe $bashExe -AllowFailure -Command (
        "chmod +x $(Convert-ToBashLiteral $bashFakeBin)/pg_restore $(Convert-ToBashLiteral $bashFakeBin)/flock && " +
        "PATH=$(Convert-ToBashLiteral $bashFakeBin):`$PATH " +
        "GEORAEPLAN_SOURCE_BACKUP_ROOT=$(Convert-ToBashLiteral $bashSourceRoot) " +
        "GEORAEPLAN_BACKUP_STATE_ROOT=$(Convert-ToBashLiteral $bashStateRoot) " +
        "GEORAEPLAN_REPLICA_ROOT=$(Convert-ToBashLiteral $bashReplicaRoot) " +
        "GEORAEPLAN_REPLICA_ID=$replicaId " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem")
    Assert-True ($staleFailure.ExitCode -ne 0) 'Unknown stale staging content was accepted.'
    Assert-True (Test-Path -LiteralPath (Join-Path $staleStage 'foreign.txt') -PathType Leaf) 'Unknown stale staging content was deleted.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $stateRoot 'external-replica-status.txt'))) 'Rejected stale staging published success.'
    Remove-Item -LiteralPath $staleStage -Recurse -Force

    Invoke-Bash -BashExe $bashExe -Command (
        "chmod +x $(Convert-ToBashLiteral $bashFakeBin)/pg_restore $(Convert-ToBashLiteral $bashFakeBin)/flock && " +
        "PATH=$(Convert-ToBashLiteral $bashFakeBin):`$PATH " +
        "GEORAEPLAN_SOURCE_BACKUP_ROOT=$(Convert-ToBashLiteral $bashSourceRoot) " +
        "GEORAEPLAN_BACKUP_STATE_ROOT=$(Convert-ToBashLiteral $bashStateRoot) " +
        "GEORAEPLAN_REPLICA_ROOT=$(Convert-ToBashLiteral $bashReplicaRoot) " +
        "GEORAEPLAN_REPLICA_ID=$replicaId " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem") | Out-Null

    $replicaStatus = Get-Content -LiteralPath (Join-Path $stateRoot 'external-replica-status.txt') -Raw -Encoding UTF8
    Assert-True ($replicaStatus.Contains('replica=ok')) 'Replica success status was not published.'
    Assert-True ($replicaStatus.Contains("source_run_id=$runId")) 'Replica status is not bound to the source run.'
    Assert-True ($replicaStatus.Contains("source_manifest_sha256=$manifestHash")) 'Replica status is not bound to the source manifest.'
    Assert-True ($replicaStatus.Contains('restore_catalog_validation=ok')) 'Replica restore catalog validation is missing.'
    Assert-True ($replicaStatus.Contains('archive_validation=ok')) 'Replica archive validation is missing.'
    $finalReplica = Join-Path $replicaRoot "sets\replica_${runId}.complete"
    Assert-True (Test-Path -LiteralPath $finalReplica -PathType Container) 'Atomic final replica set is missing.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $stateRoot 'external-replica-failure-status.txt'))) 'Failure status remained after success.'

    # Idempotent retry must validate and reuse the same immutable complete set.
    $beforeHash = (Get-FileHash -LiteralPath (Join-Path $finalReplica 'REPLICA') -Algorithm SHA256).Hash
    Invoke-Bash -BashExe $bashExe -Command (
        "PATH=$(Convert-ToBashLiteral $bashFakeBin):`$PATH " +
        "GEORAEPLAN_SOURCE_BACKUP_ROOT=$(Convert-ToBashLiteral $bashSourceRoot) " +
        "GEORAEPLAN_BACKUP_STATE_ROOT=$(Convert-ToBashLiteral $bashStateRoot) " +
        "GEORAEPLAN_REPLICA_ROOT=$(Convert-ToBashLiteral $bashReplicaRoot) " +
        "GEORAEPLAN_REPLICA_ID=$replicaId " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem") | Out-Null
    $afterHash = (Get-FileHash -LiteralPath (Join-Path $finalReplica 'REPLICA') -Algorithm SHA256).Hash
    Assert-True ($beforeHash -eq $afterHash) 'Idempotent retry rewrote the completed replica marker.'

    # Exact COMPLETE keys remain authoritative even if an attacker recomputes the replica marker hash.
    $completePath = Join-Path $finalReplica 'COMPLETE'
    $replicaMarkerPath = Join-Path $finalReplica 'REPLICA'
    $completeBeforeForgery = [IO.File]::ReadAllBytes($completePath)
    $replicaMarkerBeforeForgery = [IO.File]::ReadAllBytes($replicaMarkerPath)
    [IO.File]::AppendAllText($completePath, "unexpected=forged`n", [Text.UTF8Encoding]::new($false))
    $bashFinalReplica = Convert-ToBashPath $bashExe $finalReplica
    $forgedDigestResult = Invoke-Bash -BashExe $bashExe -Command (
        "cd $(Convert-ToBashLiteral $bashFinalReplica) && " +
        "sha256sum COMPLETE SHA256SUMS data-protection-keys.tar.gz files.tar.gz georaeplan.dump georaeplan_itworld.dump metadata.txt | sha256sum | awk '{print `$1}'")
    $forgedDigest = (($forgedDigestResult.Output | Select-Object -Last 1) -join '').Trim()
    Assert-True ($forgedDigest -match '^[0-9a-f]{64}$') 'Unable to compute the forged replica manifest hash.'
    $forgedMarker = [Text.Encoding]::UTF8.GetString($replicaMarkerBeforeForgery)
    $forgedMarker = [regex]::Replace(
        $forgedMarker,
        '(?m)^replica_manifest_sha256=[0-9a-f]{64}$',
        "replica_manifest_sha256=$forgedDigest")
    [IO.File]::WriteAllText($replicaMarkerPath, $forgedMarker, [Text.UTF8Encoding]::new($false))
    $forgedCompleteFailure = Invoke-Bash -BashExe $bashExe -AllowFailure -Command (
        "PATH=$(Convert-ToBashLiteral $bashFakeBin):`$PATH " +
        "GEORAEPLAN_SOURCE_BACKUP_ROOT=$(Convert-ToBashLiteral $bashSourceRoot) " +
        "GEORAEPLAN_BACKUP_STATE_ROOT=$(Convert-ToBashLiteral $bashStateRoot) " +
        "GEORAEPLAN_REPLICA_ROOT=$(Convert-ToBashLiteral $bashReplicaRoot) " +
        "GEORAEPLAN_REPLICA_ID=$replicaId " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem")
    Assert-True ($forgedCompleteFailure.ExitCode -ne 0) 'Forged COMPLETE extra key was accepted after recomputing the replica hash.'
    [IO.File]::WriteAllBytes($completePath, $completeBeforeForgery)
    [IO.File]::WriteAllBytes($replicaMarkerPath, $replicaMarkerBeforeForgery)

    # A source byte mutation must fail closed and preserve the published success record and replica set.
    $successBefore = [IO.File]::ReadAllBytes((Join-Path $stateRoot 'external-replica-status.txt'))
    [IO.File]::AppendAllText((Join-Path $sourceSet 'metadata.txt'), 'tamper', [Text.UTF8Encoding]::new($false))
    $failure = Invoke-Bash -BashExe $bashExe -AllowFailure -Command (
        "PATH=$(Convert-ToBashLiteral $bashFakeBin):`$PATH " +
        "GEORAEPLAN_SOURCE_BACKUP_ROOT=$(Convert-ToBashLiteral $bashSourceRoot) " +
        "GEORAEPLAN_BACKUP_STATE_ROOT=$(Convert-ToBashLiteral $bashStateRoot) " +
        "GEORAEPLAN_REPLICA_ROOT=$(Convert-ToBashLiteral $bashReplicaRoot) " +
        "GEORAEPLAN_REPLICA_ID=$replicaId " +
        "bash $(Convert-ToBashLiteral $bashScript) --test-allow-local-filesystem")
    Assert-True ($failure.ExitCode -ne 0) 'Tampered source backup was accepted.'
    $successAfterFailure = [IO.File]::ReadAllBytes(
        (Join-Path $stateRoot 'external-replica-status.txt'))
    Assert-True `
        ([Convert]::ToBase64String($successAfterFailure) -eq
            [Convert]::ToBase64String($successBefore)) `
        'Failure overwrote the last successful replica status.'
    Assert-True (Test-Path -LiteralPath $finalReplica -PathType Container) 'Failure removed the last successful replica.'
    $failureStatus = Get-Content -LiteralPath (Join-Path $stateRoot 'external-replica-failure-status.txt') -Raw -Encoding UTF8
    Assert-True ($failureStatus.Contains('replica=failed')) 'Failure status was not recorded.'

    Write-Host 'backup_replica_static_checks=ok'
    Write-Host 'backup_replica_behavior_checks=ok'
    Write-Host "backup_replica_source_run_id=$runId"
    Write-Host "backup_replica_manifest_sha256=$manifestHash"
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
