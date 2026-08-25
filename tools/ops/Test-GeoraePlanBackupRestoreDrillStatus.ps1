[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PlatformStateRoot,

    [ValidateRange(1, 8760)]
    [int]$MaximumAgeHours = 168,

    [ValidateSet('Object', 'Json')]
    [string]$OutputFormat = 'Object'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-RestoreDrillValidationResult {
    param(
        [ValidateSet('PASS', 'FAIL')][string]$Status,
        [string]$Reason,
        [string]$Detail,
        [string]$SourceRunId = '',
        [string]$SourceManifestSha256 = '',
        [string]$ReplicaManifestSha256 = '',
        [string]$CompletedAt = ''
    )
    return [pscustomobject]@{
        Status = $Status
        Reason = $Reason
        Detail = $Detail
        SourceRunId = $SourceRunId
        SourceManifestSha256 = $SourceManifestSha256
        ReplicaManifestSha256 = $ReplicaManifestSha256
        CompletedAt = $CompletedAt
    }
}

function Test-IsReparsePoint {
    param([Parameter(Mandatory = $true)]$Item)
    if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        return $true
    }
    $linkType = $Item.PSObject.Properties['LinkType']
    return $null -ne $linkType -and
        -not [string]::IsNullOrWhiteSpace([string]$linkType.Value)
}

function Read-StrictKeyValueFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$ExactKeys = @()
    )
    try {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.PSIsContainer -or (Test-IsReparsePoint -Item $item)) {
            throw 'path is not a regular non-reparse file'
        }
        $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
        $text = [IO.File]::ReadAllText($item.FullName, $strictUtf8)
    }
    catch {
        return [pscustomobject]@{
            Success = $false
            Values = @{}
            Error = "unable to read strict status file '$Path': $($_.Exception.Message)"
        }
    }
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
        $text = $text.Substring(1)
    }
    if ($text.Contains("`r") -and $text -match "`r(?!`n)") {
        return [pscustomobject]@{ Success = $false; Values = @{}; Error = 'invalid line ending' }
    }
    $lines = @([regex]::Split($text.Replace("`r`n", "`n"), "`n"))
    if ($lines.Count -gt 0 -and $lines[-1].Length -eq 0) {
        $lines = @($lines | Select-Object -First ($lines.Count - 1))
    }
    $values = @{}
    foreach ($line in $lines) {
        $match = [regex]::Match($line, '^(?<key>[a-z][a-z0-9_]*)=(?<value>[^\r\n]*)$')
        if (-not $match.Success) {
            return [pscustomobject]@{ Success = $false; Values = @{}; Error = 'malformed status line' }
        }
        $key = $match.Groups['key'].Value
        if ($values.ContainsKey($key)) {
            return [pscustomobject]@{ Success = $false; Values = @{}; Error = "duplicate status key '$key'" }
        }
        $values[$key] = $match.Groups['value'].Value
    }
    if ($ExactKeys.Count -gt 0) {
        $actual = @($values.Keys | Sort-Object)
        $expected = @($ExactKeys | Sort-Object)
        if (($actual -join "`n") -cne ($expected -join "`n")) {
            return [pscustomobject]@{ Success = $false; Values = @{}; Error = 'unexpected status key set' }
        }
    }
    return [pscustomobject]@{ Success = $true; Values = $values; Error = '' }
}

function Try-ParseTimestamp {
    param([string]$Value, [ref]$ParsedValue)
    if ($Value -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$') {
        return $false
    }
    $candidate = [DateTimeOffset]::MinValue
    $ok = [DateTimeOffset]::TryParse(
        $Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$candidate)
    if ($ok) { $ParsedValue.Value = $candidate }
    return $ok
}

function Invoke-RestoreDrillStatusValidation {
    try {
        if (-not (Test-Path -LiteralPath $PlatformStateRoot -PathType Container)) {
            return New-RestoreDrillValidationResult FAIL platform_state_root_invalid 'platform state root is not accessible'
        }
        $state = Get-Item -LiteralPath $PlatformStateRoot -Force
        if ((Test-IsReparsePoint -Item $state) -or
            $state.Name -cne 'state' -or
            $null -eq $state.Parent -or
            $state.Parent.Name -cne 'ops') {
            return New-RestoreDrillValidationResult FAIL platform_state_root_invalid 'platform state root must be a non-reparse ops/state directory'
        }

        $backup = Read-StrictKeyValueFile -Path (Join-Path $state.FullName 'backup-status.txt')
        if (-not $backup.Success) {
            return New-RestoreDrillValidationResult FAIL backup_status_invalid $backup.Error
        }
        foreach ($key in @('backup', 'run_id', 'completed_at', 'manifest_sha256')) {
            if (-not $backup.Values.ContainsKey($key) -or
                [string]::IsNullOrWhiteSpace([string]$backup.Values[$key])) {
                return New-RestoreDrillValidationResult FAIL backup_status_invalid "backup status is missing '$key'"
            }
        }
        if ([string]$backup.Values['backup'] -cne 'ok') {
            return New-RestoreDrillValidationResult FAIL backup_status_not_ok 'backup status is not ok'
        }
        $sourceRunId = [string]$backup.Values['run_id']
        $sourceManifest = [string]$backup.Values['manifest_sha256']
        if ($sourceRunId -notmatch '^\d{8}T\d{6}Z-\d+$' -or
            $sourceManifest -notmatch '^[0-9a-f]{64}$') {
            return New-RestoreDrillValidationResult FAIL backup_binding_invalid 'backup run or manifest binding is invalid'
        }
        $backupCompletedAt = [DateTimeOffset]::MinValue
        if (-not (Try-ParseTimestamp ([string]$backup.Values['completed_at']) ([ref]$backupCompletedAt))) {
            return New-RestoreDrillValidationResult FAIL backup_completed_at_invalid 'backup completed_at is invalid'
        }

        $replicaKeys = @(
            'replica', 'replica_id', 'source_run_id', 'source_manifest_sha256',
            'replica_set_path', 'replica_manifest_sha256', 'verified_at',
            'restore_catalog_validation', 'archive_validation')
        $replica = Read-StrictKeyValueFile `
            -Path (Join-Path $state.FullName 'external-replica-status.txt') `
            -ExactKeys $replicaKeys
        if (-not $replica.Success) {
            return New-RestoreDrillValidationResult FAIL replica_status_invalid $replica.Error
        }
        $replicaManifest = [string]$replica.Values['replica_manifest_sha256']
        if ([string]$replica.Values['replica'] -cne 'ok' -or
            [string]$replica.Values['source_run_id'] -cne $sourceRunId -or
            [string]$replica.Values['source_manifest_sha256'] -cne $sourceManifest -or
            [string]$replica.Values['restore_catalog_validation'] -cne 'ok' -or
            [string]$replica.Values['archive_validation'] -cne 'ok' -or
            $replicaManifest -notmatch '^[0-9a-f]{64}$') {
            return New-RestoreDrillValidationResult FAIL replica_source_mismatch 'replica does not describe the current successful backup'
        }
        $replicaVerifiedAt = [DateTimeOffset]::MinValue
        if (-not (Try-ParseTimestamp ([string]$replica.Values['verified_at']) ([ref]$replicaVerifiedAt)) -or
            $replicaVerifiedAt.ToUniversalTime() -lt $backupCompletedAt.ToUniversalTime()) {
            return New-RestoreDrillValidationResult FAIL replica_verified_at_invalid 'replica verification time is invalid'
        }

        $drillKeys = @(
            'restore_drill', 'replica_id', 'source_run_id',
            'source_manifest_sha256', 'replica_manifest_sha256', 'image_id',
            'central_schema_sha256', 'business_schema_sha256',
            'business_count_digest_contract', 'network_mode',
            'completed_at')
        $drill = Read-StrictKeyValueFile `
            -Path (Join-Path $state.FullName 'backup-restore-drill-status.txt') `
            -ExactKeys $drillKeys
        if (-not $drill.Success) {
            return New-RestoreDrillValidationResult FAIL restore_drill_status_invalid $drill.Error
        }
        if ([string]$drill.Values['restore_drill'] -cne 'ok' -or
            [string]$drill.Values['network_mode'] -cne 'none' -or
            [string]$drill.Values['business_count_digest_contract'] -cne 'source_metadata_match') {
            return New-RestoreDrillValidationResult FAIL restore_drill_status_not_ok 'restore drill or network isolation status is not ok'
        }
        if ([string]$drill.Values['replica_id'] -cne [string]$replica.Values['replica_id'] -or
            [string]$drill.Values['source_run_id'] -cne $sourceRunId -or
            [string]$drill.Values['source_manifest_sha256'] -cne $sourceManifest -or
            [string]$drill.Values['replica_manifest_sha256'] -cne $replicaManifest) {
            return New-RestoreDrillValidationResult FAIL restore_drill_source_mismatch 'restore drill is not bound to the current replica'
        }
        foreach ($key in @('central_schema_sha256', 'business_schema_sha256')) {
            if ([string]$drill.Values[$key] -notmatch '^[0-9a-f]{64}$') {
                return New-RestoreDrillValidationResult FAIL restore_drill_schema_digest_invalid "restore drill field '$key' is invalid"
            }
        }
        if ([string]$drill.Values['image_id'] -notmatch '^sha256:[0-9a-f]{64}$') {
            return New-RestoreDrillValidationResult FAIL restore_drill_image_invalid 'restore drill image id is not content-addressed'
        }

        $completedAt = [DateTimeOffset]::MinValue
        if (-not (Try-ParseTimestamp ([string]$drill.Values['completed_at']) ([ref]$completedAt))) {
            return New-RestoreDrillValidationResult FAIL restore_drill_completed_at_invalid 'restore drill completed_at is invalid'
        }
        $now = [DateTimeOffset]::UtcNow
        if ($completedAt.ToUniversalTime() -gt $now -or
            $completedAt.ToUniversalTime() -lt $replicaVerifiedAt.ToUniversalTime() -or
            ($now - $completedAt.ToUniversalTime()).TotalHours -gt $MaximumAgeHours) {
            return New-RestoreDrillValidationResult FAIL restore_drill_status_stale 'restore drill verification is stale'
        }

        $failurePath = Join-Path $state.FullName 'backup-restore-drill-failure-status.txt'
        if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
            $failure = Read-StrictKeyValueFile `
                -Path $failurePath `
                -ExactKeys @(
                    'restore_drill', 'replica_id', 'source_run_id',
                    'source_manifest_sha256', 'replica_manifest_sha256',
                    'failed_at', 'reason')
            if (-not $failure.Success -or -not $failure.Values.ContainsKey('failed_at')) {
                return New-RestoreDrillValidationResult FAIL restore_drill_failure_status_invalid 'restore drill failure status is invalid'
            }
            if ([string]$failure.Values['restore_drill'] -cne 'failed' -or
                [string]$failure.Values['replica_id'] -cne [string]$replica.Values['replica_id'] -or
                [string]$failure.Values['source_run_id'] -cne $sourceRunId -or
                [string]$failure.Values['source_manifest_sha256'] -cne $sourceManifest -or
                [string]$failure.Values['replica_manifest_sha256'] -cne $replicaManifest -or
                [string]::IsNullOrWhiteSpace([string]$failure.Values['reason'])) {
                return New-RestoreDrillValidationResult FAIL restore_drill_failure_status_invalid 'restore drill failure status binding is invalid'
            }
            $failedAt = [DateTimeOffset]::MinValue
            if (-not (Try-ParseTimestamp ([string]$failure.Values['failed_at']) ([ref]$failedAt))) {
                return New-RestoreDrillValidationResult FAIL restore_drill_failure_status_invalid 'restore drill failure timestamp is invalid'
            }
            if ($failedAt.ToUniversalTime() -gt $completedAt.ToUniversalTime()) {
                return New-RestoreDrillValidationResult FAIL newer_restore_drill_failure 'a restore drill failure is newer than the last success'
            }
        }

        return New-RestoreDrillValidationResult `
            PASS `
            restore_drill_verified `
            'networkless restore drill exactly matches the current verified replica' `
            $sourceRunId `
            $sourceManifest `
            $replicaManifest `
            $completedAt.ToString('o')
    }
    catch {
        return New-RestoreDrillValidationResult FAIL restore_drill_validation_exception $_.Exception.Message
    }
}

$result = Invoke-RestoreDrillStatusValidation
if ($OutputFormat -eq 'Json') {
    $result | ConvertTo-Json -Compress
}
else {
    Write-Output -NoEnumerate $result
}
