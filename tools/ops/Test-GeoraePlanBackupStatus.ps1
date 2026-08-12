[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PlatformStateRoot,

    [string]$PlatformBackupRoot = "",

    [ValidateRange(1, 8760)]
    [int]$MaximumAgeHours = 36,

    [ValidateSet('Object', 'Json')]
    [string]$OutputFormat = 'Object'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-BackupValidationResult {
    param(
        [ValidateSet('PASS', 'FAIL')]
        [string]$Status,

        [string]$Reason,

        [string]$Detail,

        [string]$CompletedAt = '',

        [string]$SetPath = '',

        [string]$LocalSetPath = '',

        [string]$ManifestSha256 = ''
    )

    return [pscustomobject]@{
        Status = $Status
        Reason = $Reason
        Detail = $Detail
        CompletedAt = $CompletedAt
        SetPath = $SetPath
        LocalSetPath = $LocalSetPath
        ManifestSha256 = $ManifestSha256
    }
}

function Read-StrictUtf8Text {
    param([Parameter(Mandatory = $true)][string]$Path)

    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    return [System.IO.File]::ReadAllText($Path, $strictUtf8)
}

function Read-ExactKeyValueFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $text = Read-StrictUtf8Text -Path $Path
    }
    catch {
        return [pscustomobject]@{
            Success = $false
            Values = @{}
            Error = "unable to read strict UTF-8 key/value file '$Path': $($_.Exception.Message)"
        }
    }

    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
        $text = $text.Substring(1)
    }

    if ($text.Contains("`r") -and $text -match "`r(?!`n)") {
        return [pscustomobject]@{
            Success = $false
            Values = @{}
            Error = "key/value file contains an invalid line ending: $Path"
        }
    }

    $normalized = $text.Replace("`r`n", "`n")
    $lines = @([regex]::Split($normalized, "`n"))
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Length -eq 0) {
        $lines = @($lines | Select-Object -First ($lines.Count - 1))
    }

    if ($lines.Count -eq 0) {
        return [pscustomobject]@{
            Success = $false
            Values = @{}
            Error = "key/value file is empty: $Path"
        }
    }

    $values = @{}
    foreach ($line in $lines) {
        if ([string]::IsNullOrEmpty($line)) {
            return [pscustomobject]@{
                Success = $false
                Values = @{}
                Error = "key/value file contains a blank line: $Path"
            }
        }

        $match = [regex]::Match($line, '^(?<key>[a-z][a-z0-9_]*)=(?<value>.*)$')
        if (-not $match.Success) {
            return [pscustomobject]@{
                Success = $false
                Values = @{}
                Error = "key/value file contains a malformed line: $Path"
            }
        }

        $key = $match.Groups['key'].Value
        if ($values.ContainsKey($key)) {
            return [pscustomobject]@{
                Success = $false
                Values = @{}
                Error = "key/value file contains duplicate key '$key': $Path"
            }
        }

        $values[$key] = $match.Groups['value'].Value
    }

    return [pscustomobject]@{
        Success = $true
        Values = $values
        Error = ''
    }
}

function Try-ParseStatusTimestamp {
    param(
        [string]$Value,
        [ref]$ParsedValue
    )

    if ($Value -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$') {
        return $false
    }

    $candidate = [DateTimeOffset]::MinValue
    $parsed = [DateTimeOffset]::TryParse(
        $Value,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None,
        [ref]$candidate)
    if ($parsed) {
        $ParsedValue.Value = $candidate
    }

    return $parsed
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256
    return $hash.Hash.ToLowerInvariant()
}

function Test-IsReparsePoint {
    param([Parameter(Mandatory = $true)]$Item)

    if (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return $true
    }

    $linkTypeProperty = $Item.PSObject.Properties['LinkType']
    return $null -ne $linkTypeProperty -and
        -not [string]::IsNullOrWhiteSpace([string]$linkTypeProperty.Value
        )
}

function Test-PathIsStrictChild {
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $candidateFullPath = [System.IO.Path]::GetFullPath($CandidatePath)
    $parentFullPath = [System.IO.Path]::GetFullPath($ParentPath)
    $trimCharacters = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parentPrefix = $parentFullPath.TrimEnd($trimCharacters) +
        [System.IO.Path]::DirectorySeparatorChar
    $comparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }

    return $candidateFullPath.StartsWith($parentPrefix, $comparison)
}

function Test-SafeManifestRelativePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath.StartsWith('/') -or
        $RelativePath.Contains('\') -or
        $RelativePath.Contains(':') -or
        $RelativePath -match '^[A-Za-z]:') {
        return $false
    }

    $segments = @($RelativePath.Split('/'))
    if ($segments.Count -eq 0) {
        return $false
    }

    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -eq '.' -or $segment -eq '..') {
            return $false
        }

        foreach ($character in $segment.ToCharArray()) {
            if ([char]::IsControl($character)) {
                return $false
            }
        }
    }

    return $true
}

function Test-PathChainForReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $currentPath = $RootPath
    foreach ($segment in $RelativePath.Split('/')) {
        $currentPath = Join-Path $currentPath $segment
        $item = Get-Item -LiteralPath $currentPath -Force
        if (Test-IsReparsePoint -Item $item) {
            return $false
        }
    }

    return $true
}

function Read-Sha256Manifest {
    param([Parameter(Mandatory = $true)][string]$ManifestText)

    if ($ManifestText.Length -gt 0 -and $ManifestText[0] -eq [char]0xFEFF) {
        $ManifestText = $ManifestText.Substring(1)
    }

    if ($ManifestText.Contains("`r") -and $ManifestText -match "`r(?!`n)") {
        return [pscustomobject]@{
            Success = $false
            Entries = @()
            Reason = 'manifest_invalid'
            Error = 'SHA256SUMS contains an invalid line ending'
        }
    }

    $normalized = $ManifestText.Replace("`r`n", "`n")
    $lines = @([regex]::Split($normalized, "`n"))
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Length -eq 0) {
        $lines = @($lines | Select-Object -First ($lines.Count - 1))
    }

    if ($lines.Count -eq 0) {
        return [pscustomobject]@{
            Success = $false
            Entries = @()
            Reason = 'manifest_invalid'
            Error = 'SHA256SUMS is empty'
        }
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $paths = @{}
    foreach ($line in $lines) {
        $match = [regex]::Match(
            $line,
            '^(?<hash>[0-9A-Fa-f]{64}) (?<mode>[ *])(?<path>.+)$')
        if (-not $match.Success) {
            return [pscustomobject]@{
                Success = $false
                Entries = @()
                Reason = 'manifest_invalid'
                Error = 'SHA256SUMS contains a malformed entry'
            }
        }

        $relativePath = $match.Groups['path'].Value
        if (-not (Test-SafeManifestRelativePath -RelativePath $relativePath)) {
            return [pscustomobject]@{
                Success = $false
                Entries = @()
                Reason = 'manifest_path_escape'
                Error = "SHA256SUMS contains an unsafe path: $relativePath"
            }
        }

        if ($paths.ContainsKey($relativePath)) {
            return [pscustomobject]@{
                Success = $false
                Entries = @()
                Reason = 'manifest_invalid'
                Error = "SHA256SUMS contains duplicate path: $relativePath"
            }
        }

        $paths[$relativePath] = $true
        $entries.Add([pscustomobject]@{
            Sha256 = $match.Groups['hash'].Value.ToLowerInvariant()
            RelativePath = $relativePath
        }) | Out-Null
    }

    return [pscustomobject]@{
        Success = $true
        Entries = $entries.ToArray()
        Reason = ''
        Error = ''
    }
}

function Invoke-BackupStatusValidation {
    param(
        [string]$StateRoot,
        [string]$ExplicitBackupRoot,
        [int]$MaxAgeHours
    )

    try {
        if ([string]::IsNullOrWhiteSpace($StateRoot) -or
            -not (Test-Path -LiteralPath $StateRoot -PathType Container)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'platform_state_root_invalid' `
                -Detail "platform state root is not an accessible directory: $StateRoot"
        }

        $stateItem = Get-Item -LiteralPath $StateRoot -Force
        if (Test-IsReparsePoint -Item $stateItem) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'platform_state_root_invalid' `
                -Detail 'platform state root must not be a symbolic link or reparse point'
        }

        if (-not [string]::Equals(
                $stateItem.Name,
                'state',
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'platform_state_root_invalid' `
                -Detail 'platform state root must end in ops/state'
        }

        $opsDirectory = $stateItem.Parent
        if ($null -eq $opsDirectory -or
            -not [string]::Equals(
                $opsDirectory.Name,
                'ops',
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'platform_state_root_invalid' `
                -Detail 'platform state root must end in ops/state'
        }

        $platformProjectRoot = $opsDirectory.Parent
        if ($null -eq $platformProjectRoot) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'platform_state_root_invalid' `
                -Detail 'unable to derive the platform project root from ops/state'
        }

        $approvedBackupRootPath = if ([string]::IsNullOrWhiteSpace($ExplicitBackupRoot)) {
            Join-Path $platformProjectRoot.FullName 'backups\automatic'
        }
        else {
            $ExplicitBackupRoot
        }

        if (-not (Test-Path -LiteralPath $approvedBackupRootPath -PathType Container)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_root_invalid' `
                -Detail "approved backup root is not an accessible directory: $approvedBackupRootPath"
        }

        $approvedBackupRoot = Get-Item -LiteralPath $approvedBackupRootPath -Force
        if (Test-IsReparsePoint -Item $approvedBackupRoot) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_root_invalid' `
                -Detail 'approved backup root must not be a symbolic link or reparse point'
        }

        $setsRootPath = Join-Path $approvedBackupRoot.FullName 'sets'
        if (-not (Test-Path -LiteralPath $setsRootPath -PathType Container)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_sets_root_invalid' `
                -Detail "backup sets root is not an accessible directory: $setsRootPath"
        }

        $setsRoot = Get-Item -LiteralPath $setsRootPath -Force
        if (Test-IsReparsePoint -Item $setsRoot) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_sets_root_invalid' `
                -Detail 'backup sets root must not be a symbolic link or reparse point'
        }

        $successStatusPath = Join-Path $stateItem.FullName 'backup-status.txt'
        if (-not (Test-Path -LiteralPath $successStatusPath -PathType Leaf)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_status_missing' `
                -Detail "backup status file is missing: $successStatusPath"
        }

        $successStatus = Read-ExactKeyValueFile -Path $successStatusPath
        if (-not $successStatus.Success) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_status_invalid' `
                -Detail $successStatus.Error
        }

        foreach ($requiredKey in @('backup', 'completed_at', 'set_path', 'manifest_sha256')) {
            if (-not $successStatus.Values.ContainsKey($requiredKey) -or
                [string]::IsNullOrEmpty([string]$successStatus.Values[$requiredKey])) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'backup_status_invalid' `
                    -Detail "backup status is missing required key '$requiredKey'"
            }
        }

        if (-not [string]::Equals(
                [string]$successStatus.Values['backup'],
                'ok',
                [System.StringComparison]::Ordinal)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_status_not_ok' `
                -Detail 'backup status must contain the exact value backup=ok'
        }

        $completedAt = [DateTimeOffset]::MinValue
        if (-not (Try-ParseStatusTimestamp `
                -Value ([string]$successStatus.Values['completed_at']) `
                -ParsedValue ([ref]$completedAt))) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_completed_at_invalid' `
                -Detail 'completed_at must be a valid ISO-8601 timestamp with an explicit timezone'
        }

        $now = [DateTimeOffset]::UtcNow
        $completedAtUtc = $completedAt.ToUniversalTime()
        if ($completedAtUtc -gt $now) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_completed_at_future' `
                -Detail "completed_at is in the future: $($completedAt.ToString('o'))" `
                -CompletedAt $completedAt.ToString('o')
        }

        $age = $now - $completedAtUtc
        if ($age.TotalHours -gt $MaxAgeHours) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_status_stale' `
                -Detail ("last completed backup is {0:N2} hours old; maximum is {1} hours" -f $age.TotalHours, $MaxAgeHours) `
                -CompletedAt $completedAt.ToString('o')
        }

        $setPath = [string]$successStatus.Values['set_path']
        $expectedSetPrefix = '/srv/georaeplan/backups/automatic/sets/'
        if (-not $setPath.StartsWith(
                $expectedSetPrefix,
                [System.StringComparison]::Ordinal)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_set_path_invalid' `
                -Detail "set_path must be below $expectedSetPrefix" `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath
        }

        $setName = $setPath.Substring($expectedSetPrefix.Length)
        if ($setName -notmatch '^backup_[A-Za-z0-9][A-Za-z0-9_-]*\.complete$') {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_set_path_invalid' `
                -Detail 'set_path must identify exactly one backup_*.complete directory' `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath
        }

        $localSetPath = Join-Path $setsRoot.FullName $setName
        if (-not (Test-PathIsStrictChild `
                -CandidatePath $localSetPath `
                -ParentPath $setsRoot.FullName) -or
            -not (Test-Path -LiteralPath $localSetPath -PathType Container)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_set_missing' `
                -Detail "referenced completed backup set is missing from the approved root: $setName" `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath
        }

        $localSet = Get-Item -LiteralPath $localSetPath -Force
        if (Test-IsReparsePoint -Item $localSet) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'backup_set_path_invalid' `
                -Detail 'referenced completed backup set must not be a symbolic link or reparse point' `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath
        }

        $completePath = Join-Path $localSet.FullName 'COMPLETE'
        $manifestPath = Join-Path $localSet.FullName 'SHA256SUMS'
        foreach ($requiredFile in @($completePath, $manifestPath)) {
            if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'backup_set_incomplete' `
                    -Detail "completed backup set is missing required file: $([System.IO.Path]::GetFileName($requiredFile))" `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName
            }

            $requiredItem = Get-Item -LiteralPath $requiredFile -Force
            if (Test-IsReparsePoint -Item $requiredItem) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'backup_set_path_invalid' `
                    -Detail "required backup file must not be a symbolic link or reparse point: $($requiredItem.Name)" `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName
            }
        }

        $expectedManifestHash = [string]$successStatus.Values['manifest_sha256']
        if ($expectedManifestHash -notmatch '^[0-9A-Fa-f]{64}$') {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'manifest_hash_invalid' `
                -Detail 'manifest_sha256 must contain exactly 64 hexadecimal characters' `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath `
                -LocalSetPath $localSet.FullName
        }

        $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
        $actualManifestHash = Get-Sha256Hex -Bytes $manifestBytes
        if (-not [string]::Equals(
                $expectedManifestHash,
                $actualManifestHash,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'manifest_hash_mismatch' `
                -Detail 'backup status manifest_sha256 does not match SHA256SUMS' `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath `
                -LocalSetPath $localSet.FullName `
                -ManifestSha256 $actualManifestHash
        }

        $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
        try {
            $manifestText = $strictUtf8.GetString($manifestBytes)
        }
        catch {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'manifest_invalid' `
                -Detail 'SHA256SUMS is not valid UTF-8' `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath `
                -LocalSetPath $localSet.FullName `
                -ManifestSha256 $actualManifestHash
        }

        $manifest = Read-Sha256Manifest -ManifestText $manifestText
        if (-not $manifest.Success) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason $manifest.Reason `
                -Detail $manifest.Error `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath `
                -LocalSetPath $localSet.FullName `
                -ManifestSha256 $actualManifestHash
        }

        foreach ($entry in $manifest.Entries) {
            $localRelativePath = $entry.RelativePath.Replace(
                '/',
                [System.IO.Path]::DirectorySeparatorChar)
            $artifactPath = Join-Path $localSet.FullName $localRelativePath
            if (-not (Test-PathIsStrictChild `
                    -CandidatePath $artifactPath `
                    -ParentPath $localSet.FullName)) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'manifest_path_escape' `
                    -Detail "manifest entry escapes the completed set: $($entry.RelativePath)" `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }

            if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'manifest_file_missing' `
                    -Detail "manifest entry is missing: $($entry.RelativePath)" `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }

            if (-not (Test-PathChainForReparsePoint `
                    -RootPath $localSet.FullName `
                    -RelativePath $entry.RelativePath)) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'manifest_path_escape' `
                    -Detail "manifest entry traverses a symbolic link or reparse point: $($entry.RelativePath)" `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }

            $actualFileHash = Get-FileSha256Hex -Path $artifactPath
            if (-not [string]::Equals(
                    [string]$entry.Sha256,
                    $actualFileHash,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'manifest_file_hash_mismatch' `
                    -Detail "manifest hash mismatch: $($entry.RelativePath)" `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }
        }

        $finalManifestHash = Get-FileSha256Hex -Path $manifestPath
        if (-not [string]::Equals(
                $actualManifestHash,
                $finalManifestHash,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return New-BackupValidationResult `
                -Status 'FAIL' `
                -Reason 'manifest_changed' `
                -Detail 'SHA256SUMS changed while the backup set was being verified' `
                -CompletedAt $completedAt.ToString('o') `
                -SetPath $setPath `
                -LocalSetPath $localSet.FullName `
                -ManifestSha256 $finalManifestHash
        }

        $failureStatusPath = Join-Path $stateItem.FullName 'backup-failure-status.txt'
        if (Test-Path -LiteralPath $failureStatusPath -PathType Leaf) {
            $failureStatus = Read-ExactKeyValueFile -Path $failureStatusPath
            if (-not $failureStatus.Success) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'failure_status_invalid' `
                    -Detail $failureStatus.Error `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }

            if (-not $failureStatus.Values.ContainsKey('backup') -or
                -not [string]::Equals(
                    [string]$failureStatus.Values['backup'],
                    'failed',
                    [System.StringComparison]::Ordinal) -or
                -not $failureStatus.Values.ContainsKey('failed_at')) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'failure_status_invalid' `
                    -Detail 'backup-failure-status.txt must contain exact backup=failed and failed_at values' `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }

            $failedAt = [DateTimeOffset]::MinValue
            if (-not (Try-ParseStatusTimestamp `
                    -Value ([string]$failureStatus.Values['failed_at']) `
                    -ParsedValue ([ref]$failedAt))) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'failure_status_invalid' `
                    -Detail 'failed_at must be a valid ISO-8601 timestamp with an explicit timezone' `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }

            if ($failedAt.ToUniversalTime() -gt $completedAtUtc) {
                return New-BackupValidationResult `
                    -Status 'FAIL' `
                    -Reason 'newer_failure_status' `
                    -Detail "a backup failure at $($failedAt.ToString('o')) is newer than the last completed backup" `
                    -CompletedAt $completedAt.ToString('o') `
                    -SetPath $setPath `
                    -LocalSetPath $localSet.FullName `
                    -ManifestSha256 $actualManifestHash
            }
        }

        return New-BackupValidationResult `
            -Status 'PASS' `
            -Reason 'backup_verified' `
            -Detail ("completed backup set verified; age={0:N2}h; files={1}" -f $age.TotalHours, $manifest.Entries.Count) `
            -CompletedAt $completedAt.ToString('o') `
            -SetPath $setPath `
            -LocalSetPath $localSet.FullName `
            -ManifestSha256 $actualManifestHash
    }
    catch {
        return New-BackupValidationResult `
            -Status 'FAIL' `
            -Reason 'backup_validation_error' `
            -Detail $_.Exception.Message
    }
}

$validationResult = Invoke-BackupStatusValidation `
    -StateRoot $PlatformStateRoot `
    -ExplicitBackupRoot $PlatformBackupRoot `
    -MaxAgeHours $MaximumAgeHours

if ($OutputFormat -eq 'Json') {
    $validationResult | ConvertTo-Json -Compress -Depth 4
}
else {
    $validationResult
}
