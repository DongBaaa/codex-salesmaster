[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PlatformStateRoot,

    [ValidateRange(1, 8760)]
    [int]$MaximumAgeHours = 36,

    [ValidateSet('Object', 'Json')]
    [string]$OutputFormat = 'Object'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-ReplicaValidationResult {
    param(
        [ValidateSet('PASS', 'FAIL')][string]$Status,
        [string]$Reason,
        [string]$Detail,
        [string]$SourceRunId = '',
        [string]$SourceManifestSha256 = '',
        [string]$ReplicaManifestSha256 = '',
        [string]$VerifiedAt = ''
    )
    return [pscustomobject]@{
        Status = $Status
        Reason = $Reason
        Detail = $Detail
        SourceRunId = $SourceRunId
        SourceManifestSha256 = $SourceManifestSha256
        ReplicaManifestSha256 = $ReplicaManifestSha256
        VerifiedAt = $VerifiedAt
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
        if (-not $item.PSIsContainer -and -not (Test-IsReparsePoint $item)) {
            $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
            $text = [IO.File]::ReadAllText($item.FullName, $strictUtf8)
        }
        else {
            throw 'path is not a regular non-reparse file'
        }
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

function Invoke-ReplicaStatusValidation {
    try {
        if (-not (Test-Path -LiteralPath $PlatformStateRoot -PathType Container)) {
            return New-ReplicaValidationResult FAIL platform_state_root_invalid 'platform state root is not accessible'
        }
        $state = Get-Item -LiteralPath $PlatformStateRoot -Force
        if ((Test-IsReparsePoint -Item $state) -or
            $state.Name -cne 'state' -or
            $null -eq $state.Parent -or
            $state.Parent.Name -cne 'ops') {
            return New-ReplicaValidationResult FAIL platform_state_root_invalid 'platform state root must be a non-reparse ops/state directory'
        }

        $backupPath = Join-Path $state.FullName 'backup-status.txt'
        $replicaPath = Join-Path $state.FullName 'external-replica-status.txt'
        if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            return New-ReplicaValidationResult FAIL backup_status_missing 'backup-status.txt is missing'
        }
        if (-not (Test-Path -LiteralPath $replicaPath -PathType Leaf)) {
            return New-ReplicaValidationResult FAIL replica_status_missing 'external-replica-status.txt is missing'
        }

        $backup = Read-StrictKeyValueFile -Path $backupPath
        if (-not $backup.Success) {
            return New-ReplicaValidationResult FAIL backup_status_invalid $backup.Error
        }
        foreach ($key in @('backup', 'run_id', 'completed_at', 'manifest_sha256')) {
            if (-not $backup.Values.ContainsKey($key) -or
                [string]::IsNullOrWhiteSpace([string]$backup.Values[$key])) {
                return New-ReplicaValidationResult FAIL backup_status_invalid "backup status is missing '$key'"
            }
        }
        if ([string]$backup.Values['backup'] -cne 'ok') {
            return New-ReplicaValidationResult FAIL backup_status_not_ok 'backup status is not ok'
        }
        $sourceRunId = [string]$backup.Values['run_id']
        $sourceManifest = [string]$backup.Values['manifest_sha256']
        if ($sourceRunId -notmatch '^\d{8}T\d{6}Z-\d+$' -or
            $sourceManifest -notmatch '^[0-9a-f]{64}$') {
            return New-ReplicaValidationResult FAIL backup_binding_invalid 'backup run or manifest binding is invalid'
        }
        $backupCompletedAt = [DateTimeOffset]::MinValue
        if (-not (Try-ParseTimestamp ([string]$backup.Values['completed_at']) ([ref]$backupCompletedAt))) {
            return New-ReplicaValidationResult FAIL backup_completed_at_invalid 'backup completed_at is invalid'
        }

        $replicaKeys = @(
            'replica',
            'replica_id',
            'source_run_id',
            'source_manifest_sha256',
            'replica_set_path',
            'replica_manifest_sha256',
            'verified_at',
            'restore_catalog_validation',
            'archive_validation')
        $replica = Read-StrictKeyValueFile -Path $replicaPath -ExactKeys $replicaKeys
        if (-not $replica.Success) {
            return New-ReplicaValidationResult FAIL replica_status_invalid $replica.Error
        }
        if ([string]$replica.Values['replica'] -cne 'ok' -or
            [string]$replica.Values['restore_catalog_validation'] -cne 'ok' -or
            [string]$replica.Values['archive_validation'] -cne 'ok') {
            return New-ReplicaValidationResult FAIL replica_status_not_ok 'replica/archive/catalog status is not ok'
        }
        if ([string]$replica.Values['replica_id'] -notmatch '^[0-9a-f]{32}$' -or
            [string]$replica.Values['replica_manifest_sha256'] -notmatch '^[0-9a-f]{64}$') {
            return New-ReplicaValidationResult FAIL replica_binding_invalid 'replica id or manifest binding is invalid'
        }
        if ([string]$replica.Values['source_run_id'] -cne $sourceRunId -or
            [string]$replica.Values['source_manifest_sha256'] -cne $sourceManifest) {
            return New-ReplicaValidationResult FAIL replica_source_mismatch 'replica does not describe the current successful backup'
        }
        $expectedSuffix = "/sets/replica_${sourceRunId}.complete"
        $replicaSetPath = [string]$replica.Values['replica_set_path']
        if ($replicaSetPath -notmatch '^/[A-Za-z0-9._/-]+$' -or
            $replicaSetPath.Contains('//') -or
            $replicaSetPath -match '(^|/)\.{1,2}(/|$)' -or
            -not $replicaSetPath.EndsWith($expectedSuffix, [StringComparison]::Ordinal)) {
            return New-ReplicaValidationResult FAIL replica_set_path_invalid 'replica set path is not bound to the current run'
        }

        $verifiedAt = [DateTimeOffset]::MinValue
        if (-not (Try-ParseTimestamp ([string]$replica.Values['verified_at']) ([ref]$verifiedAt))) {
            return New-ReplicaValidationResult FAIL replica_verified_at_invalid 'replica verified_at is invalid'
        }
        $now = [DateTimeOffset]::UtcNow
        if ($verifiedAt.ToUniversalTime() -gt $now -or
            $verifiedAt.ToUniversalTime() -lt $backupCompletedAt.ToUniversalTime()) {
            return New-ReplicaValidationResult FAIL replica_verified_at_invalid 'replica verification time is outside the backup-to-now interval'
        }
        if (($now - $verifiedAt.ToUniversalTime()).TotalHours -gt $MaximumAgeHours) {
            return New-ReplicaValidationResult FAIL replica_status_stale 'replica verification is stale'
        }

        $failurePath = Join-Path $state.FullName 'external-replica-failure-status.txt'
        if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
            $failure = Read-StrictKeyValueFile -Path $failurePath
            if (-not $failure.Success -or -not $failure.Values.ContainsKey('failed_at')) {
                return New-ReplicaValidationResult FAIL replica_failure_status_invalid 'replica failure status is invalid'
            }
            $failedAt = [DateTimeOffset]::MinValue
            if (-not (Try-ParseTimestamp ([string]$failure.Values['failed_at']) ([ref]$failedAt))) {
                return New-ReplicaValidationResult FAIL replica_failure_status_invalid 'replica failure timestamp is invalid'
            }
            if ($failedAt.ToUniversalTime() -gt $verifiedAt.ToUniversalTime()) {
                return New-ReplicaValidationResult FAIL newer_replica_failure 'a replica failure is newer than the last verified replica'
            }
        }

        return New-ReplicaValidationResult `
            PASS `
            replica_verified `
            'external replica status exactly matches the current successful backup' `
            $sourceRunId `
            $sourceManifest `
            ([string]$replica.Values['replica_manifest_sha256']) `
            $verifiedAt.ToString('o')
    }
    catch {
        return New-ReplicaValidationResult FAIL replica_validation_exception $_.Exception.Message
    }
}

$result = Invoke-ReplicaStatusValidation
if ($OutputFormat -eq 'Json') {
    $result | ConvertTo-Json -Compress
}
else {
    Write-Output -NoEnumerate $result
}
