[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SourceFolder,
    [string]$OutputRoot,
    [string]$PackageName,
    [string]$AppDisplayName,
    [string]$ApiBaseUrl = 'https://trade.2884.kr',
    [string]$WindowsSigningConfigPath,
    [switch]$RequireWindowsAuthenticode,
    [switch]$EnableTestHooks,
    [switch]$SkipNativeInstallers
)

$ErrorActionPreference = 'Stop'

$powerShellUtilityModulePath = Join-Path `
    $PSHOME `
    'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1'
if ($null -eq (Get-Command `
        -Name 'Microsoft.PowerShell.Utility\Get-FileHash' `
        -ErrorAction SilentlyContinue)) {
    if (-not (Test-Path `
            -LiteralPath $powerShellUtilityModulePath `
            -PathType Leaf)) {
        throw "Microsoft.PowerShell.Utility module was not found: $powerShellUtilityModulePath"
    }
    Import-Module `
        -Name $powerShellUtilityModulePath `
        -ErrorAction Stop
}
if ($null -eq (Get-Command `
        -Name 'Microsoft.PowerShell.Utility\Get-FileHash' `
        -ErrorAction SilentlyContinue)) {
    throw 'Get-FileHash cmdlet is unavailable after importing Microsoft.PowerShell.Utility.'
}

function Assert-SingleFileName {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must not be empty."
    }
    if (-not [string]::Equals(
            $Value,
            $Value.Trim(),
            [System.StringComparison]::Ordinal)) {
        throw "$Name must not start or end with whitespace."
    }
    if ([System.IO.Path]::IsPathRooted($Value) -or
        $Value.Contains([System.IO.Path]::DirectorySeparatorChar) -or
        $Value.Contains([System.IO.Path]::AltDirectorySeparatorChar) -or
        $Value -eq '.' -or
        $Value -eq '..' -or
        -not [string]::Equals(
            [System.IO.Path]::GetFileName($Value),
            $Value,
            [System.StringComparison]::Ordinal)) {
        throw "$Name must be a single file name without a directory component."
    }
    if ($Value.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "$Name contains invalid file-name characters."
    }
    if ($Value.EndsWith('.', [System.StringComparison]::Ordinal) -or
        $Value.EndsWith(' ', [System.StringComparison]::Ordinal)) {
        throw "$Name must not end with a dot or space."
    }

    $deviceName = $Value.Split('.')[0].ToUpperInvariant()
    if ($deviceName -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
        throw "$Name uses a reserved Windows device name."
    }

    return $Value
}

function ConvertTo-PowerShellSingleQuotedLiteralContent {
    param(
        [Parameter(Mandatory = $true)][string]$Value
    )

    return $Value.Replace("'", "''")
}

function Assert-PowerShellScriptSyntax {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptContent,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput(
        $ScriptContent,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) {
        $parseErrorSummary = @($parseErrors) |
            ForEach-Object { $_.Message } |
            Select-Object -Unique
        throw "$Description contains invalid PowerShell syntax: $($parseErrorSummary -join '; ')"
    }
}

function Get-NormalizedPathForComparison {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals(
            $fullPath,
            $rootPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $rootPath
    }

    return $fullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Get-ContainedDirectChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ChildName,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $safeChildName = Assert-SingleFileName `
        -Name $Description `
        -Value $ChildName
    $parentFullPath = Get-NormalizedPathForComparison -Path $ParentPath
    $childFullPath = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($parentFullPath, $safeChildName))
    $actualParentPath = Get-NormalizedPathForComparison -Path (
        [System.IO.Path]::GetDirectoryName($childFullPath))
    if (-not [string]::Equals(
            $actualParentPath,
            $parentFullPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its intended output directory."
    }

    return $childFullPath
}

function Assert-PathIsNotReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $currentPath = Get-NormalizedPathForComparison -Path $Path
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item `
                -LiteralPath $currentPath `
                -Force `
                -ErrorAction Stop
            if (($item.Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description path chain must not contain a reparse point: $currentPath"
            }
        }

        $parentPath = [System.IO.Path]::GetDirectoryName($currentPath)
        if ([string]::IsNullOrWhiteSpace($parentPath) -or
            [string]::Equals(
                $parentPath,
                $currentPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $currentPath = $parentPath
    }
}

function Assert-DirectoryTreeHasNoReparsePoints {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $rootFullPath = Get-NormalizedPathForComparison -Path $RootPath
    Assert-PathIsNotReparsePoint `
        -Path $rootFullPath `
        -Description $Description
    $rootInfo = Get-Item `
        -LiteralPath $rootFullPath `
        -Force `
        -ErrorAction Stop
    if (-not $rootInfo.PSIsContainer) {
        throw "$Description is not a directory: $rootFullPath"
    }

    $entries = @(
        [System.IO.DirectoryInfo]::new(
            $rootFullPath).EnumerateFileSystemInfos(
                '*',
                [System.IO.SearchOption]::TopDirectoryOnly)
    )
    foreach ($entry in $entries) {
        $entryPath = Get-NormalizedPathForComparison -Path $entry.FullName
        $entryAttributes = [System.IO.File]::GetAttributes($entryPath)
        if (($entryAttributes -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description directory tree must not contain a reparse point: $entryPath"
        }
        if (($entryAttributes -band
                [System.IO.FileAttributes]::Directory) -ne 0) {
            Assert-DirectoryTreeHasNoReparsePoints `
                -RootPath $entryPath `
                -Description $Description
        }
    }

    Assert-PathIsNotReparsePoint `
        -Path $rootFullPath `
        -Description $Description
}

function Remove-DirectoryTreeWithoutFollowingReparsePoints {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $rootFullPath = Get-NormalizedPathForComparison -Path $RootPath
    Assert-PathIsNotReparsePoint `
        -Path $rootFullPath `
        -Description $Description
    $rootInfo = Get-Item `
        -LiteralPath $rootFullPath `
        -Force `
        -ErrorAction Stop
    if (-not $rootInfo.PSIsContainer) {
        throw "$Description recursive deletion root is not a directory: $rootFullPath"
    }

    $entries = @(
        [System.IO.DirectoryInfo]::new(
            $rootFullPath).EnumerateFileSystemInfos(
                '*',
                [System.IO.SearchOption]::TopDirectoryOnly)
    )
    foreach ($entry in $entries) {
        $entryPath = Get-NormalizedPathForComparison -Path $entry.FullName
        $currentAttributes = [System.IO.File]::GetAttributes($entryPath)
        if (($currentAttributes -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a reparse point and cannot be removed recursively: $entryPath"
        }
        if (($currentAttributes -band
                [System.IO.FileAttributes]::Directory) -ne 0) {
            Remove-DirectoryTreeWithoutFollowingReparsePoints `
                -RootPath $entryPath `
                -Description $Description
        }
        else {
            $currentAttributes =
                [System.IO.File]::GetAttributes($entryPath)
            if (($currentAttributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description file changed into a reparse point immediately before deletion: $entryPath"
            }
            if (($currentAttributes -band
                    [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                [System.IO.File]::SetAttributes(
                    $entryPath,
                    ($currentAttributes -band
                        (-bnot [System.IO.FileAttributes]::ReadOnly)))
            }
            [System.IO.File]::Delete($entryPath)
        }
    }

    Assert-PathIsNotReparsePoint `
        -Path $rootFullPath `
        -Description $Description
    [System.IO.Directory]::Delete($rootFullPath, $false)
}

function Remove-ContainedOutputItem {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ChildName,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$Recurse
    )

    $targetPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $ChildName `
        -Description $Description
    if (Test-Path -LiteralPath $targetPath) {
        Assert-PathIsNotReparsePoint `
            -Path $targetPath `
            -Description $Description
        if ($Recurse) {
            if (-not (Test-Path `
                    -LiteralPath $targetPath `
                    -PathType Container)) {
                throw "$Description recursive deletion target is not a directory: $targetPath"
            }
            Remove-DirectoryTreeWithoutFollowingReparsePoints `
                -RootPath $targetPath `
                -Description $Description
        }
        elseif (Test-Path -LiteralPath $targetPath -PathType Container) {
            [System.IO.Directory]::Delete($targetPath, $false)
        }
        else {
            $targetAttributes =
                [System.IO.File]::GetAttributes($targetPath)
            if (($targetAttributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description changed into a reparse point immediately before deletion: $targetPath"
            }
            if (($targetAttributes -band
                    [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                [System.IO.File]::SetAttributes(
                    $targetPath,
                    ($targetAttributes -band
                        (-bnot [System.IO.FileAttributes]::ReadOnly)))
            }
            [System.IO.File]::Delete($targetPath)
        }
        if (Test-Path -LiteralPath $targetPath) {
            throw "Failed to remove $Description`: $targetPath"
        }
    }

    return $targetPath
}

function Get-Sha256FromStream {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileStream]$Stream
    )

    if (-not $Stream.CanRead -or -not $Stream.CanSeek) {
        throw 'SHA-256 artifact stream must be readable and seekable.'
    }

    $Stream.Position = 0
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($Stream)
    }
    finally {
        $sha256.Dispose()
        $Stream.Position = 0
    }
    return -join ($hashBytes | ForEach-Object { $_.ToString('X2') })
}

function Get-Sha256FromBytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($Bytes)
    }
    finally {
        $sha256.Dispose()
    }
    return -join ($hashBytes | ForEach-Object { $_.ToString('X2') })
}

function Assert-FileSha256Equals {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Assert-PathIsNotReparsePoint `
        -Path $Path `
        -Description $Description
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is not a regular file: $Path"
    }
    $actualHash = (
        Get-FileHash `
            -LiteralPath $Path `
            -Algorithm SHA256 `
            -ErrorAction Stop).Hash
    if (-not [string]::Equals(
            $actualHash,
            $ExpectedHash,
            [System.StringComparison]::Ordinal)) {
        throw "$Description hash mismatch: $Path"
    }
}

function Assert-Sha256Sidecar {
    param(
        [Parameter(Mandatory = $true)][string]$SidecarPath,
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    Assert-PathIsNotReparsePoint `
        -Path $SidecarPath `
        -Description 'SHA-256 sidecar'
    if (-not (Test-Path -LiteralPath $SidecarPath -PathType Leaf)) {
        throw "SHA-256 sidecar was not published: $SidecarPath"
    }

    $artifactName = [System.IO.Path]::GetFileName($ArtifactPath)
    $expectedContent = '{0} *{1}' -f
        $ExpectedHash.ToUpperInvariant(),
        $artifactName
    $actualContent = [System.IO.File]::ReadAllText(
        $SidecarPath,
        [System.Text.Encoding]::UTF8).TrimEnd([char[]]"`r`n")
    if (-not [string]::Equals(
            $actualContent,
            $expectedContent,
            [System.StringComparison]::Ordinal)) {
        throw "SHA-256 sidecar content mismatch: $SidecarPath"
    }
}

function Publish-Sha256Sidecar {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)]
        [System.IO.FileStream]$ArtifactStream,
        [Parameter(Mandatory = $true)][string]$ExpectedArtifactHash,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][string]$BackupSidecarName
    )

    if ($ExpectedArtifactHash -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'ExpectedArtifactHash must be a 64-character SHA-256 value.'
    }
    if ($TransactionId -notmatch '^[0-9a-f]{32}$') {
        throw 'TransactionId must be a 32-character lowercase hexadecimal value.'
    }

    $parentFullPath = Get-NormalizedPathForComparison -Path $ParentPath
    $artifactFullPath = Get-NormalizedPathForComparison -Path $ArtifactPath
    $streamPath = Get-NormalizedPathForComparison -Path $ArtifactStream.Name
    if (-not [string]::Equals(
            $streamPath,
            $artifactFullPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'SHA-256 sidecar must be published from the leased artifact path.'
    }
    $artifactParentPath = Get-NormalizedPathForComparison -Path (
        [System.IO.Path]::GetDirectoryName($artifactFullPath))
    if (-not [string]::Equals(
            $artifactParentPath,
            $parentFullPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'SHA-256 sidecar artifact must be a direct child of its output directory.'
    }
    if (-not (Test-Path -LiteralPath $artifactFullPath -PathType Leaf)) {
        throw "SHA-256 sidecar artifact was not found: $artifactFullPath"
    }
    Assert-PathIsNotReparsePoint `
        -Path $artifactFullPath `
        -Description 'SHA-256 sidecar artifact'

    $artifactName = [System.IO.Path]::GetFileName($artifactFullPath)
    $sidecarName = $artifactName + '.sha256.txt'
    $sidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $parentFullPath `
        -ChildName $sidecarName `
        -Description 'SHA-256 sidecar'
    $stagedSidecarName = '.{0}.{1}.staged.sha256.txt' -f
        $artifactName,
        $TransactionId
    $stagedSidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $parentFullPath `
        -ChildName $stagedSidecarName `
        -Description 'staged SHA-256 sidecar'
    $backupSidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $parentFullPath `
        -ChildName $BackupSidecarName `
        -Description 'previous SHA-256 sidecar'
    $prePublishHash = Get-Sha256FromStream -Stream $ArtifactStream
    if (-not [string]::Equals(
            $prePublishHash,
            $ExpectedArtifactHash,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Leased package archive hash mismatch before sidecar publish: $artifactFullPath"
    }

    try {
        $sidecarContent = '{0} *{1}{2}' -f
            $prePublishHash,
            $artifactName,
            [Environment]::NewLine
        $sidecarBytes = [System.Text.UTF8Encoding]::new(
            $false).GetBytes($sidecarContent)
        $sidecarStream = [System.IO.FileStream]::new(
            $stagedSidecarPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None,
            4096,
            [System.IO.FileOptions]::WriteThrough)
        try {
            $sidecarStream.Write(
                $sidecarBytes,
                0,
                $sidecarBytes.Length)
            $sidecarStream.Flush($true)
        }
        finally {
            $sidecarStream.Dispose()
        }
        Assert-Sha256Sidecar `
            -SidecarPath $stagedSidecarPath `
            -ArtifactPath $artifactFullPath `
            -ExpectedHash $prePublishHash

        if (Test-Path -LiteralPath $sidecarPath) {
            Assert-PathIsNotReparsePoint `
                -Path $sidecarPath `
                -Description 'existing SHA-256 sidecar'
            [System.IO.File]::Replace(
                $stagedSidecarPath,
                $sidecarPath,
                $backupSidecarPath,
                $true)
        }
        else {
            [System.IO.File]::Move($stagedSidecarPath, $sidecarPath)
        }

        Assert-Sha256Sidecar `
            -SidecarPath $sidecarPath `
            -ArtifactPath $artifactFullPath `
            -ExpectedHash $prePublishHash
        $postPublishHash = Get-Sha256FromStream -Stream $ArtifactStream
        if (-not [string]::Equals(
                $postPublishHash,
                $prePublishHash,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Leased package archive hash changed during sidecar publish: $artifactFullPath"
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagedSidecarPath) {
            $null = Remove-ContainedOutputItem `
                -ParentPath $parentFullPath `
                -ChildName $stagedSidecarName `
                -Description 'staged SHA-256 sidecar'
        }
    }

    return [PSCustomObject]@{
        Path = $sidecarPath
        Hash = $prePublishHash
    }
}

function Write-DurableBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        4096,
        [System.IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PackageStagedZipOwnerMarkerName {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionId
    )

    $parsedTransactionId = [System.Guid]::Empty
    if ($TransactionId -notmatch '^[0-9a-f]{32}$' -or
        -not [System.Guid]::TryParseExact(
            $TransactionId,
            'N',
            [ref]$parsedTransactionId)) {
        throw 'Staged ZIP owner transaction ID must be a lowercase N-format GUID.'
    }

    return '.georaeplan-package-staged-owner.{0}.json' -f
        $TransactionId
}

function Open-PackageStagedZipOwnerMarkerLease {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$MarkerName
    )

    $markerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $MarkerName `
        -Description 'staged ZIP owner marker'
    Assert-PathIsNotReparsePoint `
        -Path $markerPath `
        -Description 'staged ZIP owner marker'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Staged ZIP owner marker is not a regular file: $markerPath"
    }

    $lease = [System.IO.File]::Open(
        $markerPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        Assert-PathIsNotReparsePoint `
            -Path $markerPath `
            -Description 'leased staged ZIP owner marker'
        return $lease
    }
    catch {
        $lease.Dispose()
        throw
    }
}

function Get-PackageStagedZipOwnerMarker {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageName,
        [Parameter(Mandatory = $true)][string]$MarkerName,
        [System.IO.FileStream]$MarkerStream,
        [string]$ExpectedMarkerHash
    )

    $null = Assert-SingleFileName `
        -Name 'staged ZIP owner marker name' `
        -Value $MarkerName
    if ($MarkerName -notmatch
        '^\.georaeplan-package-staged-owner\.[0-9a-f]{32}\.json$') {
        throw "Staged ZIP owner marker filename is invalid: $MarkerName"
    }

    $markerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $MarkerName `
        -Description 'staged ZIP owner marker'
    Assert-PathIsNotReparsePoint `
        -Path $markerPath `
        -Description 'staged ZIP owner marker'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Staged ZIP owner marker is not a regular file: $markerPath"
    }
    try {
        if ($null -ne $MarkerStream) {
            $streamPath = Get-NormalizedPathForComparison `
                -Path $MarkerStream.Name
            if (-not [string]::Equals(
                    $streamPath,
                    $markerPath,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                -not $MarkerStream.CanRead -or
                -not $MarkerStream.CanSeek -or
                $MarkerStream.Length -le 0 -or
                $MarkerStream.Length -gt 4096) {
                throw 'Staged ZIP owner marker lease is invalid.'
            }
            $MarkerStream.Position = 0
            $markerBytes = New-Object byte[] ([int]$MarkerStream.Length)
            $offset = 0
            while ($offset -lt $markerBytes.Length) {
                $readCount = $MarkerStream.Read(
                    $markerBytes,
                    $offset,
                    $markerBytes.Length - $offset)
                if ($readCount -le 0) {
                    throw 'Staged ZIP owner marker ended unexpectedly.'
                }
                $offset += $readCount
            }
            $MarkerStream.Position = 0
        }
        else {
            $markerInfo = Get-Item `
                -LiteralPath $markerPath `
                -Force `
                -ErrorAction Stop
            if ($markerInfo.PSIsContainer -or
                $markerInfo.Length -le 0 -or
                $markerInfo.Length -gt 4096) {
                throw "Staged ZIP owner marker size or type is invalid: $markerPath"
            }
            $markerBytes = [System.IO.File]::ReadAllBytes($markerPath)
            if ($markerBytes.Length -ne $markerInfo.Length) {
                throw 'Staged ZIP owner marker changed while it was read.'
            }
        }
        $strictUtf8 = [System.Text.UTF8Encoding]::new(
            $false,
            $true)
        $markerJson = $strictUtf8.GetString($markerBytes)
        $marker = $markerJson |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Staged ZIP owner marker is malformed and requires manual inspection: $markerPath. $($_.Exception.Message)"
    }
    Assert-PathIsNotReparsePoint `
        -Path $markerPath `
        -Description 'read staged ZIP owner marker'
    $markerHash = Get-Sha256FromBytes -Bytes $markerBytes
    if (-not [string]::IsNullOrWhiteSpace($ExpectedMarkerHash)) {
        if ($ExpectedMarkerHash -notmatch '^[0-9A-F]{64}$' -or
            -not [string]::Equals(
                $markerHash,
                $ExpectedMarkerHash,
                [System.StringComparison]::Ordinal)) {
            throw "Staged ZIP owner marker hash changed: $markerPath"
        }
    }

    $requiredProperties = @(
        'SchemaVersion',
        'TransactionId',
        'PackageName',
        'StagedZipName'
    )
    $actualProperties = @($marker.PSObject.Properties.Name)
    $propertyDifference = @(
        Compare-Object `
            -ReferenceObject $requiredProperties `
            -DifferenceObject $actualProperties `
            -CaseSensitive
    )
    if ($propertyDifference.Count -ne 0) {
        throw "Staged ZIP owner marker has an invalid schema: $markerPath"
    }
    foreach ($stringProperty in @(
        'TransactionId',
        'PackageName',
        'StagedZipName')) {
        if (-not ($marker.$stringProperty -is [string])) {
            throw "Staged ZIP owner marker field must be a string: $stringProperty"
        }
    }
    if ((-not ($marker.SchemaVersion -is [int]) -and
            -not ($marker.SchemaVersion -is [long])) -or
        [int64]$marker.SchemaVersion -ne 1) {
        throw "Staged ZIP owner marker schema version is invalid: $markerPath"
    }

    $transactionId = [string]$marker.TransactionId
    $expectedMarkerName =
        Get-PackageStagedZipOwnerMarkerName `
            -TransactionId $transactionId
    if (-not [string]::Equals(
            $MarkerName,
            $expectedMarkerName,
            [System.StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$marker.PackageName,
            $ExpectedPackageName,
            [System.StringComparison]::Ordinal)) {
        throw "Staged ZIP owner marker identity is invalid: $markerPath"
    }

    $expectedStagedZipName = '.{0}.{1}.staged.zip' -f
        $ExpectedPackageName,
        $transactionId
    $null = Assert-SingleFileName `
        -Name 'staged ZIP owner artifact name' `
        -Value ([string]$marker.StagedZipName)
    if (-not [string]::Equals(
            [string]$marker.StagedZipName,
            $expectedStagedZipName,
            [System.StringComparison]::Ordinal)) {
        throw "Staged ZIP owner marker artifact name is invalid: $markerPath"
    }
    $null = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName ([string]$marker.StagedZipName) `
        -Description 'owned staged package archive'

    return [PSCustomObject]@{
        Owner = $marker
        MarkerHash = $markerHash
    }
}

function Write-PackageStagedZipOwnerMarker {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][PSCustomObject]$Owner
    )

    $markerName =
        Get-PackageStagedZipOwnerMarkerName `
            -TransactionId ([string]$Owner.TransactionId)
    $markerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $markerName `
        -Description 'staged ZIP owner marker'
    Assert-PathIsNotReparsePoint `
        -Path $markerPath `
        -Description 'staged ZIP owner marker'
    if (Test-Path -LiteralPath $markerPath) {
        throw "A staged ZIP owner marker already exists: $markerPath"
    }

    $json = $Owner | ConvertTo-Json -Compress -Depth 2
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    $markerLease = $null
    $leaseReturned = $false
    try {
        $markerLease = [System.IO.FileStream]::new(
            $markerPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::Read,
            4096,
            [System.IO.FileOptions]::WriteThrough)
        $markerLease.Write($bytes, 0, $bytes.Length)
        $markerLease.Flush($true)
        $markerLease.Position = 0
        Assert-PathIsNotReparsePoint `
            -Path $markerPath `
            -Description 'durable staged ZIP owner marker'
        $expectedMarkerHash = Get-Sha256FromBytes -Bytes $bytes
        $validatedMarker = Get-PackageStagedZipOwnerMarker `
            -ParentPath $ParentPath `
            -ExpectedPackageName ([string]$Owner.PackageName) `
            -MarkerName $markerName `
            -MarkerStream $markerLease `
            -ExpectedMarkerHash $expectedMarkerHash
        $leaseReturned = $true
        return [PSCustomObject]@{
            MarkerName = $markerName
            Lease = $markerLease
            Owner = $validatedMarker.Owner
            MarkerHash = $validatedMarker.MarkerHash
        }
    }
    finally {
        if (-not $leaseReturned -and
            $null -ne $markerLease -and
            $markerLease.CanRead) {
            $markerLease.Dispose()
        }
    }
}

function Remove-PackageStagedZipOwnerMarker {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageName,
        [Parameter(Mandatory = $true)][PSCustomObject]$ValidatedOwner,
        [Parameter(Mandatory = $true)]
        [System.IO.FileStream]$MarkerLease,
        [Parameter(Mandatory = $true)][string]$ExpectedMarkerHash
    )

    $markerName =
        Get-PackageStagedZipOwnerMarkerName `
            -TransactionId ([string]$ValidatedOwner.TransactionId)
    try {
        $currentMarker = Get-PackageStagedZipOwnerMarker `
            -ParentPath $ParentPath `
            -ExpectedPackageName $ExpectedPackageName `
            -MarkerName $markerName `
            -MarkerStream $MarkerLease `
            -ExpectedMarkerHash $ExpectedMarkerHash
        if (-not [string]::Equals(
                [string]$currentMarker.Owner.StagedZipName,
                [string]$ValidatedOwner.StagedZipName,
                [System.StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string]$currentMarker.Owner.TransactionId,
                [string]$ValidatedOwner.TransactionId,
                [System.StringComparison]::Ordinal)) {
            throw "Staged ZIP owner marker changed before release: $markerName"
        }
    }
    finally {
        $MarkerLease.Dispose()
    }

    Assert-FileSha256Equals `
        -Path (Get-ContainedDirectChildPath `
            -ParentPath $ParentPath `
            -ChildName $markerName `
            -Description 'released staged ZIP owner marker') `
        -ExpectedHash $ExpectedMarkerHash `
        -Description 'released staged ZIP owner marker'
    $null = Remove-ContainedOutputItem `
        -ParentPath $ParentPath `
        -ChildName $markerName `
        -Description 'released staged ZIP owner marker'
}

function Invoke-PackageStagedZipOwnerRecovery {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageName,
        [PSCustomObject]$PrevalidatedOwner,
        [System.IO.FileStream]$PreopenedMarkerLease,
        [string]$PreopenedMarkerHash
    )

    Assert-PathIsNotReparsePoint `
        -Path $ParentPath `
        -Description 'staged ZIP owner recovery directory'
    $ownerEntries = @(
        [System.IO.DirectoryInfo]::new(
            $ParentPath).EnumerateFileSystemInfos(
                '.georaeplan-package-staged-owner.*.json',
                [System.IO.SearchOption]::TopDirectoryOnly) |
            Sort-Object -Property Name
    )
    if ($ownerEntries.Count -eq 0) {
        if ($null -ne $PreopenedMarkerLease) {
            if ($PreopenedMarkerLease.CanRead) {
                $PreopenedMarkerLease.Dispose()
            }
            throw 'Preopened staged ZIP owner marker was not discovered in its recovery directory.'
        }
        return 'NONE'
    }

    $validatedOwners = @()
    try {
        $preopenedMarkerName = ''
        if ($null -ne $PreopenedMarkerLease) {
            if ($null -eq $PrevalidatedOwner -or
                [string]::IsNullOrWhiteSpace($PreopenedMarkerHash)) {
                throw 'Preopened staged ZIP owner marker requires validated owner and hash.'
            }
            $preopenedMarkerName =
                Get-PackageStagedZipOwnerMarkerName `
                    -TransactionId ([string]$PrevalidatedOwner.TransactionId)
        }

        foreach ($ownerEntry in $ownerEntries) {
            $ownerEntryPath = Get-ContainedDirectChildPath `
                -ParentPath $ParentPath `
                -ChildName $ownerEntry.Name `
                -Description 'discovered staged ZIP owner marker'
            Assert-PathIsNotReparsePoint `
                -Path $ownerEntryPath `
                -Description 'discovered staged ZIP owner marker'
            if ($ownerEntry.PSIsContainer -or
                -not (Test-Path -LiteralPath $ownerEntryPath -PathType Leaf)) {
                throw "Staged ZIP owner marker is not a regular file: $ownerEntryPath"
            }

            $markerLease = $null
            if ($null -ne $PreopenedMarkerLease -and
                [string]::Equals(
                    $ownerEntry.Name,
                    $preopenedMarkerName,
                    [System.StringComparison]::Ordinal)) {
                $markerLease = $PreopenedMarkerLease
                $expectedMarkerHash = $PreopenedMarkerHash
            }
            else {
                $markerLease = Open-PackageStagedZipOwnerMarkerLease `
                    -ParentPath $ParentPath `
                    -MarkerName $ownerEntry.Name
                $expectedMarkerHash = ''
            }
            try {
                $validatedMarker = Get-PackageStagedZipOwnerMarker `
                    -ParentPath $ParentPath `
                    -ExpectedPackageName $ExpectedPackageName `
                    -MarkerName $ownerEntry.Name `
                    -MarkerStream $markerLease `
                    -ExpectedMarkerHash $expectedMarkerHash
                if ($null -ne $PrevalidatedOwner -and
                    [string]::Equals(
                        $ownerEntry.Name,
                        $preopenedMarkerName,
                        [System.StringComparison]::Ordinal) -and
                    (-not [string]::Equals(
                            [string]$validatedMarker.Owner.StagedZipName,
                            [string]$PrevalidatedOwner.StagedZipName,
                            [System.StringComparison]::Ordinal) -or
                        -not [string]::Equals(
                            [string]$validatedMarker.Owner.TransactionId,
                            [string]$PrevalidatedOwner.TransactionId,
                            [System.StringComparison]::Ordinal))) {
                    throw 'Preopened staged ZIP owner marker changed before recovery.'
                }
                $stagedZipPath = Get-ContainedDirectChildPath `
                    -ParentPath $ParentPath `
                    -ChildName ([string]$validatedMarker.Owner.StagedZipName) `
                    -Description 'owned staged package archive'
                if (Test-Path -LiteralPath $stagedZipPath) {
                    Assert-PathIsNotReparsePoint `
                        -Path $stagedZipPath `
                        -Description 'owned staged package archive'
                    if (-not (Test-Path `
                            -LiteralPath $stagedZipPath `
                            -PathType Leaf)) {
                        throw "Owned staged package archive is not a regular file: $stagedZipPath"
                    }
                }
                $validatedOwners += [PSCustomObject]@{
                    MarkerName = $ownerEntry.Name
                    Owner = $validatedMarker.Owner
                    MarkerHash = $validatedMarker.MarkerHash
                    MarkerLease = $markerLease
                    StagedZipPath = $stagedZipPath
                }
                $markerLease = $null
            }
            finally {
                if ($null -ne $markerLease -and
                    $markerLease.CanRead) {
                    $markerLease.Dispose()
                }
            }
        }

        if ($null -ne $PreopenedMarkerLease -and
            -not ($validatedOwners |
                Where-Object {
                    [object]::ReferenceEquals(
                        $_.MarkerLease,
                        $PreopenedMarkerLease)
                })) {
            throw 'Preopened staged ZIP owner marker was not discovered in its recovery directory.'
        }

        $recoveredCount = 0
        foreach ($validatedOwner in $validatedOwners) {
            if (Test-Path -LiteralPath $validatedOwner.StagedZipPath) {
                $null = Remove-ContainedOutputItem `
                    -ParentPath $ParentPath `
                    -ChildName ([string]$validatedOwner.Owner.StagedZipName) `
                    -Description 'owned staged package archive'
            }
        }
        foreach ($validatedOwner in $validatedOwners) {
            Remove-PackageStagedZipOwnerMarker `
                -ParentPath $ParentPath `
                -ExpectedPackageName $ExpectedPackageName `
                -ValidatedOwner $validatedOwner.Owner `
                -MarkerLease $validatedOwner.MarkerLease `
                -ExpectedMarkerHash $validatedOwner.MarkerHash
            $validatedOwner.MarkerLease = $null
            $recoveredCount++
            Write-Host (
                "package_staged_zip_owner_recovery=RECOVERED marker={0}" -f
                    $validatedOwner.MarkerName)
        }
        return 'RECOVERED:{0}' -f $recoveredCount
    }
    finally {
        foreach ($validatedOwner in $validatedOwners) {
            if ($null -ne $validatedOwner.MarkerLease -and
                $validatedOwner.MarkerLease.CanRead) {
                $validatedOwner.MarkerLease.Dispose()
                $validatedOwner.MarkerLease = $null
            }
        }
        if ($null -ne $PreopenedMarkerLease -and
            $PreopenedMarkerLease.CanRead) {
            $PreopenedMarkerLease.Dispose()
        }
    }
}

function Get-PackagePublishTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageName,
        [System.IO.FileStream]$MarkerStream
    )

    $markerName = '.georaeplan-package-publish-transaction.json'
    $markerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $markerName `
        -Description 'package publish transaction marker'
    Assert-PathIsNotReparsePoint `
        -Path $markerPath `
        -Description 'package publish transaction marker'
    if ($null -eq $MarkerStream -and
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return $null
    }

    try {
        if ($null -ne $MarkerStream) {
            $streamPath = Get-NormalizedPathForComparison `
                -Path $MarkerStream.Name
            if (-not [string]::Equals(
                    $streamPath,
                    $markerPath,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                -not $MarkerStream.CanRead -or
                -not $MarkerStream.CanSeek) {
                throw 'Package publish transaction marker lease is invalid.'
            }
            if ($MarkerStream.Length -gt 16384) {
                throw 'Package publish transaction marker is unexpectedly large.'
            }
            $MarkerStream.Position = 0
            $markerBytes = New-Object byte[] ([int]$MarkerStream.Length)
            $offset = 0
            while ($offset -lt $markerBytes.Length) {
                $readCount = $MarkerStream.Read(
                    $markerBytes,
                    $offset,
                    $markerBytes.Length - $offset)
                if ($readCount -le 0) {
                    throw 'Package publish transaction marker ended unexpectedly.'
                }
                $offset += $readCount
            }
            $MarkerStream.Position = 0
            $strictUtf8 = [System.Text.UTF8Encoding]::new(
                $false,
                $true)
            $markerJson = $strictUtf8.GetString($markerBytes)
        }
        else {
            $markerInfo = Get-Item `
                -LiteralPath $markerPath `
                -Force `
                -ErrorAction Stop
            if ($markerInfo.Length -gt 16384) {
                throw 'Package publish transaction marker is unexpectedly large.'
            }
            $markerJson = Get-Content `
                -LiteralPath $markerPath `
                -Raw `
                -Encoding UTF8
        }
        $marker = $markerJson |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Package publish transaction marker is malformed and requires manual inspection: $markerPath. $($_.Exception.Message)"
    }

    $requiredProperties = @(
        'SchemaVersion',
        'TransactionId',
        'PackageName',
        'ZipName',
        'SidecarName',
        'StagedZipName',
        'BackupZipName',
        'BackupSidecarName',
        'NewZipHash',
        'HadExistingZip',
        'PreviousZipHash',
        'HadExistingSidecar',
        'PreviousSidecarContentBase64',
        'PreviousSidecarHash'
    )
    $actualProperties = @($marker.PSObject.Properties.Name)
    $propertyDifference = @(
        Compare-Object `
            -ReferenceObject $requiredProperties `
            -DifferenceObject $actualProperties
    )
    if ($propertyDifference.Count -ne 0) {
        throw "Package publish transaction marker has an invalid schema: $markerPath"
    }
    $stringProperties = @(
        'TransactionId',
        'PackageName',
        'ZipName',
        'SidecarName',
        'StagedZipName',
        'BackupZipName',
        'BackupSidecarName',
        'NewZipHash',
        'PreviousZipHash',
        'PreviousSidecarContentBase64',
        'PreviousSidecarHash'
    )
    foreach ($stringProperty in $stringProperties) {
        if (-not ($marker.$stringProperty -is [string])) {
            throw "Package publish transaction marker field must be a string: $stringProperty"
        }
    }
    if (-not ($marker.SchemaVersion -is [int]) -and
        -not ($marker.SchemaVersion -is [long])) {
        throw "Package publish transaction marker schema version type is invalid: $markerPath"
    }
    if ([int64]$marker.SchemaVersion -ne 1 -or
        [string]$marker.TransactionId -notmatch '^[0-9a-f]{32}$' -or
        -not [string]::Equals(
            [string]$marker.PackageName,
            $ExpectedPackageName,
            [System.StringComparison]::Ordinal)) {
        throw "Package publish transaction marker identity is invalid: $markerPath"
    }
    if (-not ($marker.HadExistingZip -is [bool]) -or
        -not ($marker.HadExistingSidecar -is [bool])) {
        throw "Package publish transaction marker state types are invalid: $markerPath"
    }

    $transactionId = [string]$marker.TransactionId
    $expectedZipName = $ExpectedPackageName + '.zip'
    $expectedSidecarName = $expectedZipName + '.sha256.txt'
    $expectedStagedZipName = '.{0}.{1}.staged.zip' -f
        $ExpectedPackageName,
        $transactionId
    $expectedBackupZipName = '.{0}.{1}.previous.zip' -f
        $ExpectedPackageName,
        $transactionId
    $expectedBackupSidecarName =
        '.{0}.{1}.previous.sha256.txt' -f
            $expectedZipName,
            $transactionId
    $validatedNames = @(
        @('ZipName', [string]$marker.ZipName, $expectedZipName),
        @('SidecarName', [string]$marker.SidecarName, $expectedSidecarName),
        @('StagedZipName', [string]$marker.StagedZipName, $expectedStagedZipName),
        @('BackupZipName', [string]$marker.BackupZipName, $expectedBackupZipName),
        @('BackupSidecarName', [string]$marker.BackupSidecarName, $expectedBackupSidecarName)
    )
    foreach ($validatedName in $validatedNames) {
        $null = Assert-SingleFileName `
            -Name ([string]$validatedName[0]) `
            -Value ([string]$validatedName[1])
        if (-not [string]::Equals(
                [string]$validatedName[1],
                [string]$validatedName[2],
                [System.StringComparison]::Ordinal)) {
            throw "Package publish transaction marker filename is invalid: $($validatedName[0])"
        }
        $null = Get-ContainedDirectChildPath `
            -ParentPath $ParentPath `
            -ChildName ([string]$validatedName[1]) `
            -Description ([string]$validatedName[0])
    }

    if ([string]$marker.NewZipHash -notmatch '^[0-9A-F]{64}$') {
        throw "Package publish transaction marker new hash is invalid: $markerPath"
    }
    if ([bool]$marker.HadExistingZip) {
        if ([string]$marker.PreviousZipHash -notmatch '^[0-9A-F]{64}$') {
            throw "Package publish transaction marker previous ZIP hash is invalid: $markerPath"
        }
    }
    elseif (-not [string]::IsNullOrEmpty(
            [string]$marker.PreviousZipHash)) {
        throw "Package publish transaction marker unexpectedly contains a previous ZIP hash: $markerPath"
    }

    if ([bool]$marker.HadExistingSidecar) {
        try {
            $previousSidecarBytes = [Convert]::FromBase64String(
                [string]$marker.PreviousSidecarContentBase64)
        }
        catch {
            throw "Package publish transaction marker sidecar backup is invalid: $markerPath"
        }
        if ($previousSidecarBytes.Length -gt 4096 -or
            [string]$marker.PreviousSidecarHash -notmatch '^[0-9A-F]{64}$' -or
            -not [string]::Equals(
                (Get-Sha256FromBytes -Bytes $previousSidecarBytes),
                [string]$marker.PreviousSidecarHash,
                [System.StringComparison]::Ordinal)) {
            throw "Package publish transaction marker sidecar backup hash is invalid: $markerPath"
        }
    }
    elseif (-not [string]::IsNullOrEmpty(
                [string]$marker.PreviousSidecarContentBase64) -or
            -not [string]::IsNullOrEmpty(
                [string]$marker.PreviousSidecarHash)) {
        throw "Package publish transaction marker unexpectedly contains a previous sidecar backup: $markerPath"
    }

    return $marker
}

function Open-PackagePublishTransactionMarkerLease {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $markerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName '.georaeplan-package-publish-transaction.json' `
        -Description 'package publish transaction marker'
    Assert-PathIsNotReparsePoint `
        -Path $markerPath `
        -Description 'package publish transaction marker'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return $null
    }

    $lease = [System.IO.File]::Open(
        $markerPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        Assert-PathIsNotReparsePoint `
            -Path $markerPath `
            -Description 'leased package publish transaction marker'
        return $lease
    }
    catch {
        $lease.Dispose()
        throw
    }
}

function Write-PackagePublishTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][PSCustomObject]$Transaction
    )

    $markerName = '.georaeplan-package-publish-transaction.json'
    $stagedMarkerName =
        '.georaeplan-package-publish-transaction.staged.json'
    $markerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $markerName `
        -Description 'package publish transaction marker'
    $stagedMarkerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $stagedMarkerName `
        -Description 'staged package publish transaction marker'
    if ((Test-Path -LiteralPath $markerPath) -or
        (Test-Path -LiteralPath $stagedMarkerPath)) {
        throw "A package publish transaction marker already exists: $markerPath"
    }

    $json = $Transaction | ConvertTo-Json -Compress -Depth 3
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    $markerLease = $null
    $leaseReturned = $false
    try {
        Write-DurableBytes -Path $stagedMarkerPath -Bytes $bytes
        [System.IO.File]::Move($stagedMarkerPath, $markerPath)
        $markerLease =
            Open-PackagePublishTransactionMarkerLease `
                -ParentPath $ParentPath
        if ($null -eq $markerLease) {
            throw 'Durable package publish transaction marker disappeared before it could be leased.'
        }
        $validatedTransaction = Get-PackagePublishTransaction `
            -ParentPath $ParentPath `
            -ExpectedPackageName ([string]$Transaction.PackageName) `
            -MarkerStream $markerLease
        $markerHash = Get-Sha256FromStream -Stream $markerLease
        $expectedMarkerHash = Get-Sha256FromBytes -Bytes $bytes
        if (-not [string]::Equals(
                $markerHash,
                $expectedMarkerHash,
                [System.StringComparison]::Ordinal)) {
            throw 'Durable package publish transaction marker does not match the transaction that was written.'
        }
        $leaseReturned = $true
        return [PSCustomObject]@{
            Lease = $markerLease
            Transaction = $validatedTransaction
        }
    }
    finally {
        if (-not $leaseReturned -and $null -ne $markerLease) {
            $markerLease.Dispose()
        }
        if (Test-Path -LiteralPath $stagedMarkerPath) {
            $null = Remove-ContainedOutputItem `
                -ParentPath $ParentPath `
                -ChildName $stagedMarkerName `
                -Description 'staged package publish transaction marker'
        }
    }
}

function Invoke-PackagePublishTransactionRecovery {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageName,
        [PSCustomObject]$ValidatedTransaction,
        [System.IO.FileStream]$TransactionMarkerLease
    )

    $markerName = '.georaeplan-package-publish-transaction.json'
    $stagedMarkerName =
        '.georaeplan-package-publish-transaction.staged.json'
    $markerLease = $TransactionMarkerLease
    try {
    if ($null -eq $markerLease) {
        $markerLease =
            Open-PackagePublishTransactionMarkerLease `
                -ParentPath $ParentPath
    }
    if ($null -eq $markerLease) {
        if (Test-Path -LiteralPath (
                Get-ContainedDirectChildPath `
                    -ParentPath $ParentPath `
                    -ChildName $stagedMarkerName `
                    -Description 'staged package publish transaction marker')) {
            throw "An orphaned staged package publish transaction marker requires manual inspection; no artifact was deleted."
        }
        return 'NONE'
    }
    if ($null -ne $ValidatedTransaction) {
        $marker = $ValidatedTransaction
    }
    else {
        $marker = Get-PackagePublishTransaction `
            -ParentPath $ParentPath `
            -ExpectedPackageName $ExpectedPackageName `
            -MarkerStream $markerLease
    }

    $zipPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName ([string]$marker.ZipName) `
        -Description 'transaction package archive'
    $sidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName ([string]$marker.SidecarName) `
        -Description 'transaction SHA-256 sidecar'
    $backupZipPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName ([string]$marker.BackupZipName) `
        -Description 'transaction package archive backup'
    $backupSidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName ([string]$marker.BackupSidecarName) `
        -Description 'transaction SHA-256 sidecar backup'
    $failedZipName = '.{0}.{1}.failed-publish.zip' -f
        $ExpectedPackageName,
        [string]$marker.TransactionId
    $failedZipPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $failedZipName `
        -Description 'failed transaction package archive'
    $stagedSidecarName = '.{0}.{1}.staged.sha256.txt' -f
        [string]$marker.ZipName,
        [string]$marker.TransactionId
    $failedSidecarName = '.{0}.{1}.failed-publish.sha256.txt' -f
        [string]$marker.ZipName,
        [string]$marker.TransactionId
    $failedSidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $failedSidecarName `
        -Description 'failed transaction SHA-256 sidecar'

    $stagedZipPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName ([string]$marker.StagedZipName) `
        -Description 'transaction staged package archive'
    $stagedSidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $stagedSidecarName `
        -Description 'transaction staged SHA-256 sidecar'
    $stagedMarkerPath = Get-ContainedDirectChildPath `
        -ParentPath $ParentPath `
        -ChildName $stagedMarkerName `
        -Description 'staged package publish transaction marker'

    if (Test-Path -LiteralPath $stagedMarkerPath) {
        throw "A staged transaction marker exists beside the durable marker and requires manual inspection: $stagedMarkerPath"
    }

    $currentZipHash = ''
    if (Test-Path -LiteralPath $zipPath) {
        Assert-PathIsNotReparsePoint `
            -Path $zipPath `
            -Description 'transaction package archive'
        if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
            throw "Transaction package archive is not a regular file: $zipPath"
        }
        $currentZipHash = (
            Get-FileHash `
                -LiteralPath $zipPath `
                -Algorithm SHA256 `
                -ErrorAction Stop).Hash
        $currentZipIsJournalOwned =
            [string]::Equals(
                $currentZipHash,
                [string]$marker.NewZipHash,
                [System.StringComparison]::Ordinal) -or
            ([bool]$marker.HadExistingZip -and
                [string]::Equals(
                    $currentZipHash,
                    [string]$marker.PreviousZipHash,
                    [System.StringComparison]::Ordinal))
        if (-not $currentZipIsJournalOwned) {
            throw "Transaction package archive hash is not journal-owned; recovery stopped without deleting evidence: $zipPath"
        }
    }

    if (Test-Path -LiteralPath $backupZipPath) {
        Assert-PathIsNotReparsePoint `
            -Path $backupZipPath `
            -Description 'transaction package archive backup'
        if (-not [bool]$marker.HadExistingZip -or
            -not (Test-Path -LiteralPath $backupZipPath -PathType Leaf) -or
            -not [string]::Equals(
                (Get-FileHash `
                    -LiteralPath $backupZipPath `
                    -Algorithm SHA256 `
                    -ErrorAction Stop).Hash,
                [string]$marker.PreviousZipHash,
                [System.StringComparison]::Ordinal)) {
            throw "Transaction package archive backup is not journal-owned; recovery stopped without deleting evidence: $backupZipPath"
        }
    }

    foreach ($candidate in @(
        @($stagedZipPath, [string]$marker.NewZipHash),
        @($failedZipPath, [string]$marker.NewZipHash))) {
        if (Test-Path -LiteralPath ([string]$candidate[0])) {
            Assert-PathIsNotReparsePoint `
                -Path ([string]$candidate[0]) `
                -Description 'transaction package archive residue'
            if (-not (Test-Path `
                    -LiteralPath ([string]$candidate[0]) `
                    -PathType Leaf) -or
                -not [string]::Equals(
                    (Get-FileHash `
                        -LiteralPath ([string]$candidate[0]) `
                        -Algorithm SHA256 `
                        -ErrorAction Stop).Hash,
                    [string]$candidate[1],
                    [System.StringComparison]::Ordinal)) {
                throw "Transaction package archive residue is not journal-owned; recovery stopped without deleting evidence: $($candidate[0])"
            }
        }
    }

    $currentSidecarIsNew = $false
    $currentSidecarIsPrevious = $false
    if (Test-Path -LiteralPath $sidecarPath) {
        Assert-PathIsNotReparsePoint `
            -Path $sidecarPath `
            -Description 'transaction SHA-256 sidecar'
        if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) {
            throw "Transaction SHA-256 sidecar is not a regular file: $sidecarPath"
        }
        $currentSidecarHash = (
            Get-FileHash `
                -LiteralPath $sidecarPath `
                -Algorithm SHA256 `
                -ErrorAction Stop).Hash
        $currentSidecarIsPrevious =
            [bool]$marker.HadExistingSidecar -and
            [string]::Equals(
                $currentSidecarHash,
                [string]$marker.PreviousSidecarHash,
                [System.StringComparison]::Ordinal)
        try {
            Assert-Sha256Sidecar `
                -SidecarPath $sidecarPath `
                -ArtifactPath $zipPath `
                -ExpectedHash ([string]$marker.NewZipHash)
            $currentSidecarIsNew = $true
        }
        catch {
            $currentSidecarIsNew = $false
        }
        if (-not $currentSidecarIsNew -and
            -not $currentSidecarIsPrevious) {
            throw "Transaction SHA-256 sidecar is not journal-owned; recovery stopped without deleting evidence: $sidecarPath"
        }
    }
    if (Test-Path -LiteralPath $stagedSidecarPath) {
        Assert-Sha256Sidecar `
            -SidecarPath $stagedSidecarPath `
            -ArtifactPath $zipPath `
            -ExpectedHash ([string]$marker.NewZipHash)
    }
    if (Test-Path -LiteralPath $backupSidecarPath) {
        Assert-PathIsNotReparsePoint `
            -Path $backupSidecarPath `
            -Description 'transaction SHA-256 sidecar backup'
        if (-not [bool]$marker.HadExistingSidecar -or
            -not (Test-Path `
                -LiteralPath $backupSidecarPath `
                -PathType Leaf) -or
            -not [string]::Equals(
                (Get-FileHash `
                    -LiteralPath $backupSidecarPath `
                    -Algorithm SHA256 `
                    -ErrorAction Stop).Hash,
                [string]$marker.PreviousSidecarHash,
                [System.StringComparison]::Ordinal)) {
            throw "Transaction SHA-256 sidecar backup is not journal-owned; recovery stopped without deleting evidence: $backupSidecarPath"
        }
    }
    if (Test-Path -LiteralPath $failedSidecarPath) {
        Assert-Sha256Sidecar `
            -SidecarPath $failedSidecarPath `
            -ArtifactPath $zipPath `
            -ExpectedHash ([string]$marker.NewZipHash)
    }

    $commitForward =
        [string]::Equals(
            $currentZipHash,
            [string]$marker.NewZipHash,
            [System.StringComparison]::Ordinal) -and
        $currentSidecarIsNew
    $commitForwardZipLease = $null
    $commitForwardSidecarLease = $null
    try {
        if ($commitForward) {
            $commitForwardZipLease = [System.IO.File]::Open(
                $zipPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            $commitForwardSidecarLease = [System.IO.File]::Open(
                $sidecarPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            if (-not [string]::Equals(
                    (Get-Sha256FromStream `
                        -Stream $commitForwardZipLease),
                    [string]$marker.NewZipHash,
                    [System.StringComparison]::Ordinal)) {
                throw "Transaction package archive changed while acquiring the commit-forward lease: $zipPath"
            }
            Assert-Sha256Sidecar `
                -SidecarPath $sidecarPath `
                -ArtifactPath $zipPath `
                -ExpectedHash ([string]$marker.NewZipHash)
        }
        else {
            if ([bool]$marker.HadExistingZip) {
                $zipAlreadyRestored = [string]::Equals(
                    $currentZipHash,
                    [string]$marker.PreviousZipHash,
                    [System.StringComparison]::Ordinal)
                if (-not $zipAlreadyRestored) {
                    if (-not (Test-Path `
                            -LiteralPath $backupZipPath `
                            -PathType Leaf)) {
                        throw "Package publish recovery cannot find the previous archive: $backupZipPath"
                    }
                    Assert-FileSha256Equals `
                        -Path $backupZipPath `
                        -ExpectedHash ([string]$marker.PreviousZipHash) `
                        -Description 'previous package archive immediately before rollback'
                    if (Test-Path -LiteralPath $zipPath) {
                        Assert-FileSha256Equals `
                            -Path $zipPath `
                            -ExpectedHash ([string]$marker.NewZipHash) `
                            -Description 'new package archive immediately before rollback'
                        [System.IO.File]::Replace(
                            $backupZipPath,
                            $zipPath,
                            $failedZipPath,
                            $true)
                    }
                    else {
                        [System.IO.File]::Move(
                            $backupZipPath,
                            $zipPath)
                    }
                }
                $restoredZipHash = (
                    Get-FileHash `
                        -LiteralPath $zipPath `
                        -Algorithm SHA256 `
                        -ErrorAction Stop).Hash
                if (-not [string]::Equals(
                        $restoredZipHash,
                        [string]$marker.PreviousZipHash,
                        [System.StringComparison]::Ordinal)) {
                    throw "Restored package archive hash mismatch: $zipPath"
                }
            }
            elseif (Test-Path -LiteralPath $zipPath) {
                Assert-FileSha256Equals `
                    -Path $zipPath `
                    -ExpectedHash ([string]$marker.NewZipHash) `
                    -Description 'uncommitted first package archive immediately before rollback'
                $null = Remove-ContainedOutputItem `
                    -ParentPath $ParentPath `
                    -ChildName ([string]$marker.ZipName) `
                    -Description 'uncommitted first package archive'
            }

            if ([bool]$marker.HadExistingSidecar) {
                $previousSidecarBytes = [Convert]::FromBase64String(
                    [string]$marker.PreviousSidecarContentBase64)
                if (-not $currentSidecarIsPrevious) {
                    if (Test-Path -LiteralPath $stagedSidecarPath) {
                        $null = Remove-ContainedOutputItem `
                            -ParentPath $ParentPath `
                            -ChildName $stagedSidecarName `
                            -Description 'journal-owned staged SHA-256 sidecar'
                    }
                    Write-DurableBytes `
                        -Path $stagedSidecarPath `
                        -Bytes $previousSidecarBytes
                    if (Test-Path -LiteralPath $sidecarPath) {
                        Assert-Sha256Sidecar `
                            -SidecarPath $sidecarPath `
                            -ArtifactPath $zipPath `
                            -ExpectedHash ([string]$marker.NewZipHash)
                        [System.IO.File]::Replace(
                            $stagedSidecarPath,
                            $sidecarPath,
                            $failedSidecarPath,
                            $true)
                    }
                    else {
                        [System.IO.File]::Move(
                            $stagedSidecarPath,
                            $sidecarPath)
                    }
                }
                $restoredSidecarHash = (
                    Get-FileHash `
                        -LiteralPath $sidecarPath `
                        -Algorithm SHA256 `
                        -ErrorAction Stop).Hash
                if (-not [string]::Equals(
                        $restoredSidecarHash,
                        [string]$marker.PreviousSidecarHash,
                        [System.StringComparison]::Ordinal)) {
                    throw "Restored package archive sidecar hash mismatch: $sidecarPath"
                }
            }
            elseif (Test-Path -LiteralPath $sidecarPath) {
                Assert-Sha256Sidecar `
                    -SidecarPath $sidecarPath `
                    -ArtifactPath $zipPath `
                    -ExpectedHash ([string]$marker.NewZipHash)
                $null = Remove-ContainedOutputItem `
                    -ParentPath $ParentPath `
                    -ChildName ([string]$marker.SidecarName) `
                    -Description 'uncommitted first SHA-256 sidecar'
            }
            Write-Host "package_zip_restore=SUCCESS path=$zipPath"
        }

        $cleanupNames = @(
            [string]$marker.StagedZipName,
            [string]$marker.BackupZipName,
            [string]$marker.BackupSidecarName,
            $stagedSidecarName,
            $failedZipName,
            $failedSidecarName)
        foreach ($cleanupName in $cleanupNames) {
            $cleanupPath = Get-ContainedDirectChildPath `
                -ParentPath $ParentPath `
                -ChildName $cleanupName `
                -Description 'package publish transaction residue'
            if (Test-Path -LiteralPath $cleanupPath) {
                if ($cleanupName -eq [string]$marker.StagedZipName -or
                    $cleanupName -eq $failedZipName) {
                    $expectedCleanupHash = [string]$marker.NewZipHash
                    $actualCleanupHash = (
                        Get-FileHash `
                            -LiteralPath $cleanupPath `
                            -Algorithm SHA256 `
                            -ErrorAction Stop).Hash
                    if (-not [string]::Equals(
                            $actualCleanupHash,
                            $expectedCleanupHash,
                            [System.StringComparison]::Ordinal)) {
                        throw "Package publish transaction residue changed before cleanup; evidence was preserved: $cleanupPath"
                    }
                }
                elseif ($cleanupName -eq [string]$marker.BackupZipName) {
                    if (-not [bool]$marker.HadExistingZip -or
                        -not [string]::Equals(
                            (Get-FileHash `
                                -LiteralPath $cleanupPath `
                                -Algorithm SHA256 `
                                -ErrorAction Stop).Hash,
                            [string]$marker.PreviousZipHash,
                            [System.StringComparison]::Ordinal)) {
                        throw "Package publish transaction ZIP backup changed before cleanup; evidence was preserved: $cleanupPath"
                    }
                }
                elseif ($cleanupName -eq
                    [string]$marker.BackupSidecarName) {
                    if (-not [bool]$marker.HadExistingSidecar -or
                        -not [string]::Equals(
                            (Get-FileHash `
                                -LiteralPath $cleanupPath `
                                -Algorithm SHA256 `
                                -ErrorAction Stop).Hash,
                            [string]$marker.PreviousSidecarHash,
                            [System.StringComparison]::Ordinal)) {
                        throw "Package publish transaction sidecar backup changed before cleanup; evidence was preserved: $cleanupPath"
                    }
                }
                elseif ($cleanupName -eq $stagedSidecarName -or
                    $cleanupName -eq $failedSidecarName) {
                    Assert-Sha256Sidecar `
                        -SidecarPath $cleanupPath `
                        -ArtifactPath $zipPath `
                        -ExpectedHash ([string]$marker.NewZipHash)
                }
            }
        }
        foreach ($cleanupName in $cleanupNames) {
            $cleanupPath = Get-ContainedDirectChildPath `
                -ParentPath $ParentPath `
                -ChildName $cleanupName `
                -Description 'validated package publish transaction residue'
            if (Test-Path -LiteralPath $cleanupPath) {
                if ($cleanupName -eq [string]$marker.StagedZipName -or
                    $cleanupName -eq $failedZipName) {
                    Assert-FileSha256Equals `
                        -Path $cleanupPath `
                        -ExpectedHash ([string]$marker.NewZipHash) `
                        -Description 'package publish transaction ZIP residue immediately before cleanup'
                }
                elseif ($cleanupName -eq [string]$marker.BackupZipName) {
                    Assert-FileSha256Equals `
                        -Path $cleanupPath `
                        -ExpectedHash ([string]$marker.PreviousZipHash) `
                        -Description 'package publish transaction ZIP backup immediately before cleanup'
                }
                elseif ($cleanupName -eq
                    [string]$marker.BackupSidecarName) {
                    Assert-FileSha256Equals `
                        -Path $cleanupPath `
                        -ExpectedHash ([string]$marker.PreviousSidecarHash) `
                        -Description 'package publish transaction sidecar backup immediately before cleanup'
                }
                else {
                    Assert-Sha256Sidecar `
                        -SidecarPath $cleanupPath `
                        -ArtifactPath $zipPath `
                        -ExpectedHash ([string]$marker.NewZipHash)
                }
                $null = Remove-ContainedOutputItem `
                    -ParentPath $ParentPath `
                    -ChildName $cleanupName `
                    -Description 'validated package publish transaction residue'
            }
        }
        $markerLease.Dispose()
        $markerLease = $null
        $null = Remove-ContainedOutputItem `
            -ParentPath $ParentPath `
            -ChildName $markerName `
            -Description 'completed package publish transaction marker'
        if ($commitForward) {
            Write-Host "package_publish_recovery=COMMIT_FORWARD path=$zipPath"
            return 'COMMIT_FORWARD'
        }
        return 'ROLLBACK'
    }
    finally {
        if ($null -ne $commitForwardSidecarLease) {
            $commitForwardSidecarLease.Dispose()
        }
        if ($null -ne $commitForwardZipLease) {
            $commitForwardZipLease.Dispose()
        }
    }
    }
    finally {
        if ($null -ne $markerLease) {
            $markerLease.Dispose()
        }
    }
}

function Enter-PackageBuilderOutputLock {
    param(
        [Parameter(Mandatory = $true)][string]$AdminOutputRoot
    )

    $lockPath = Get-ContainedDirectChildPath `
        -ParentPath $AdminOutputRoot `
        -ChildName '.georaeplan-package-builder.lock' `
        -Description 'package builder output lock'
    Assert-PathIsNotReparsePoint `
        -Path $lockPath `
        -Description 'package builder output lock'

    $lockStream = $null
    try {
        $lockStream = [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        Assert-PathIsNotReparsePoint `
            -Path $lockPath `
            -Description 'package builder output lock'
        Write-Host "package_builder_lock=ACQUIRED path=$lockPath"
        return $lockStream
    }
    catch {
        if ($null -ne $lockStream) {
            $lockStream.Dispose()
        }
        throw "Another package builder already owns this output root, or its lock file is unsafe: $lockPath. $($_.Exception.Message)"
    }
}

function Resolve-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $candidates = @(
        $env:DOTNET_EXE,
        'D:\.dotnet-sdk\dotnet.exe',
        'C:\Users\beene\AppData\Local\GeoraePlan.Android\dotnet8\dotnet.exe',
        'C:\Program Files\dotnet\dotnet.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        try {
            & $candidate --version *> $null
            if ($LASTEXITCODE -eq 0) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
        catch {
            continue
        }
    }

    throw "Unable to locate a working dotnet executable for packaging under $ProjectRoot."
}

function Get-Utf8String {
    param(
        [Parameter(Mandatory = $true)][string]$Base64
    )

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Base64))
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [int]$RetryCount = 5,
        [int]$RetryDelaySeconds = 2
    )

    Assert-DirectoryTreeHasNoReparsePoints `
        -RootPath $Source `
        -Description 'robocopy source'
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
        Assert-DirectoryTreeHasNoReparsePoints `
            -RootPath $Source `
            -Description 'robocopy source'
        $output = & robocopy $Source $Destination /MIR /XJ /R:2 /W:2 /NFL /NDL /NJH /NJS /NP 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -lt 8) {
            return
        }

        Write-Host ("robocopy attempt {0}/{1} failed ({2}): {3} -> {4}" -f $attempt, $RetryCount, $exitCode, $Source, $Destination)
        foreach ($line in @($output)) {
            if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace($line.ToString())) {
                Write-Host ("  {0}" -f $line.ToString().TrimEnd())
            }
        }

        if ($attempt -lt $RetryCount) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
        else {
            throw "robocopy failed ($exitCode): $Source -> $Destination"
        }
    }
}

function Get-DeploymentRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $candidate = Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Set-ApiBaseUrl.ps1') } |
        Select-Object -First 1 -ExpandProperty FullName

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'Deployment root not found under project root.'
    }

    return $candidate
}

function Get-DefaultClientSourceFolder {
    param(
        [Parameter(Mandatory = $true)][string]$DeploymentRoot
    )

    $candidates = Get-ChildItem -LiteralPath $DeploymentRoot -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'appsettings.json')) -and
            ((Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.cmd' | Measure-Object).Count -ge 1) -and
            ((Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.exe' | Measure-Object).Count -ge 1) -and
            ((Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.db' | Measure-Object).Count -eq 0)
        } |
        Sort-Object FullName

    $preferred = $candidates |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$AppDisplayName.exe") } |
        Select-Object -First 1

    if ($null -eq $preferred) {
        $preferred = $candidates |
        Where-Object { (Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.pdb' | Measure-Object).Count -eq 0 } |
        Select-Object -First 1
    }

    if ($null -eq $preferred) {
        $preferred = $candidates | Select-Object -First 1
    }

    if ($null -eq $preferred) {
        throw 'Desktop client deployment source folder not found.'
    }

    return $preferred.FullName
}

function Get-DefaultOutputRoot {
    param(
        [Parameter(Mandatory = $true)][string]$DeploymentRoot
    )

    return $DeploymentRoot
}

function Resolve-WindowsSigningConfigPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$ConfigPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        if (-not (Test-Path -LiteralPath $ConfigPath)) {
            throw "Windows signing config not found: $ConfigPath"
        }

        return (Resolve-Path -LiteralPath $ConfigPath).Path
    }

    $defaultPath = Join-Path $ProjectRoot 'tools\release\windows-signing.local.json'
    if (Test-Path -LiteralPath $defaultPath) {
        return (Resolve-Path -LiteralPath $defaultPath).Path
    }

    return ''
}

function Invoke-WindowsArtifactSigning {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$WindowsSigningConfigPath,
        [string]$PackageRoot,
        [string[]]$Paths = @(),
        [switch]$RequireSigning
    )

    if ([string]::IsNullOrWhiteSpace($WindowsSigningConfigPath) -and -not $RequireSigning) {
        Write-Host 'windows_authenticode_signing=SKIPPED_NO_CONFIG'
        return
    }

    $signingScript = Join-Path $ProjectRoot 'tools\release\Sign-GeoraePlanWindowsArtifacts.ps1'
    if (-not (Test-Path -LiteralPath $signingScript)) {
        throw "Windows Authenticode signing script not found: $signingScript"
    }

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $signingScript,
        '-ProjectRoot', $ProjectRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($WindowsSigningConfigPath)) {
        $arguments += @('-WindowsSigningConfigPath', $WindowsSigningConfigPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
        $arguments += @('-PackageRoot', $PackageRoot)
    }
    if ($Paths.Count -gt 0) {
        $arguments += '-Paths'
        $arguments += $Paths
    }
    if ($RequireSigning) {
        $arguments += '-RequireSigning'
    }

    & powershell @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Windows Authenticode signing failed.'
    }
}

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $projectPath = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Desktop project not found: $projectPath"
    }

    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "Desktop project version not found: $projectPath"
    }

    return [string]$versionNode
}

function Clear-DesktopReleaseArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $projectDir = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App'
    foreach ($relativePath in @('bin\Release', 'obj\Release')) {
        $targetPath = Join-Path $projectDir $relativePath
        if (Test-Path -LiteralPath $targetPath) {
            Remove-Item -LiteralPath $targetPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Publish-DesktopApplication {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    $desktopProject = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
    if (-not (Test-Path -LiteralPath $desktopProject)) {
        throw "Desktop project not found: $desktopProject"
    }

    Clear-DesktopReleaseArtifacts -ProjectRoot $ProjectRoot
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null

    & $DotnetExe publish $desktopProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $PublishRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to publish desktop application for packaging.'
    }

    return $PublishRoot
}

function Ensure-DesktopLaunchCommand {
    param(
        [Parameter(Mandatory = $true)][string]$SourceFolder,
        [Parameter(Mandatory = $true)][string]$AppDisplayName
    )

    $launchScriptPath = Join-Path $SourceFolder '앱실행.cmd'
    $launchScript = @"
@echo off
setlocal EnableExtensions
set "APP_EXE="
for %%I in ("%~dp0*.Desktop.App.exe") do if exist "%%~fI" if not defined APP_EXE set "APP_EXE=%%~fI"
if not defined APP_EXE (
  echo [GeoraePlan] Desktop application executable was not found.
  exit /b 1
)
start "" "%APP_EXE%"
endlocal
"@
    $launchScript | Set-Content -LiteralPath $launchScriptPath -Encoding ASCII
}

function Ensure-DesktopPackageLaunchFiles {
    param(
        [Parameter(Mandatory = $true)][string]$SourceFolder,
        [Parameter(Mandatory = $true)][string]$AppDisplayName
    )

    $canonicalDesktopExePath =
        Join-Path $SourceFolder '거래플랜.Desktop.App.exe'
    $publishedExeCandidates = @(
        $canonicalDesktopExePath,
        (Join-Path $SourceFolder "$AppDisplayName.exe")
    )
    $publishedExe = $publishedExeCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($publishedExe)) {
        throw "Published desktop executable not found under $SourceFolder"
    }

    if (-not [string]::Equals(
            $publishedExe,
            $canonicalDesktopExePath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item `
            -LiteralPath $publishedExe `
            -Destination $canonicalDesktopExePath `
            -Force
    }

    $displayExePath = Join-Path $SourceFolder "$AppDisplayName.exe"
    if (-not [string]::Equals(
            $canonicalDesktopExePath,
            $displayExePath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item `
            -LiteralPath $canonicalDesktopExePath `
            -Destination $displayExePath `
            -Force
    }

    Ensure-DesktopLaunchCommand -SourceFolder $SourceFolder -AppDisplayName $AppDisplayName
}

function Assert-DesktopPackageRequiredFiles {
    param(
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [Parameter(Mandatory = $true)][string]$AppDisplayName
    )

    $requiredFiles = @(
        (Join-Path $AppRoot '거래플랜.Desktop.App.exe'),
        (Join-Path $AppRoot "$AppDisplayName.exe"),
        (Join-Path $AppRoot 'appsettings.json'),
        (Join-Path $AppRoot '앱실행.cmd'),
        (Join-Path $AppRoot 'Updater\거래플랜.Updater.exe'),
        (Join-Path $AppRoot "Updater\$AppDisplayName.Updater.exe")
    )

    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Required desktop package file not found: $requiredFile"
        }
    }

    $desktopAppExecutables = @(
        Get-ChildItem `
            -LiteralPath $AppRoot `
            -File `
            -Filter '*.Desktop.App.exe' `
            -ErrorAction Stop
    )
    $canonicalDesktopAppPath =
        Join-Path $AppRoot '거래플랜.Desktop.App.exe'
    if ($desktopAppExecutables.Count -ne 1 -or
        -not [string]::Equals(
            $desktopAppExecutables[0].FullName,
            $canonicalDesktopAppPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $candidateNames = @($desktopAppExecutables |
            ForEach-Object { $_.Name }) -join ', '
        throw "Desktop package must contain exactly one canonical *.Desktop.App.exe launcher target. Found: $candidateNames"
    }

    $launchScriptPath = Join-Path $AppRoot '앱실행.cmd'
    $launchScript = Get-Content `
        -LiteralPath $launchScriptPath `
        -Raw `
        -Encoding ASCII
    $requiredLaunchFragments = @(
        'for %%I in ("%~dp0*.Desktop.App.exe") do if exist "%%~fI"',
        'start "" "%APP_EXE%"'
    )
    foreach ($requiredLaunchFragment in $requiredLaunchFragments) {
        if ($launchScript.IndexOf(
                $requiredLaunchFragment,
                [System.StringComparison]::Ordinal) -lt 0) {
            throw "Generated desktop launch command is missing its executable wildcard contract: $requiredLaunchFragment"
        }
    }
    if ($launchScript.IndexOf(
            '"%~dp0*.exe"',
            [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Generated desktop launch command must not use a generic root-level executable fallback.'
    }
}

function Ensure-DesktopPackageUpdaterFile {
    param(
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [Parameter(Mandatory = $true)][string]$AppDisplayName
    )

    $updaterRoot = Join-Path $AppRoot 'Updater'
    $updaterCandidates = @(
        (Join-Path $updaterRoot '거래플랜.Updater.exe'),
        (Join-Path $updaterRoot "$AppDisplayName.Updater.exe")
    )
    $publishedUpdater = $updaterCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($publishedUpdater)) {
        throw "Published updater executable not found under $updaterRoot"
    }

    $canonicalUpdaterPath = Join-Path $updaterRoot '거래플랜.Updater.exe'
    if (-not [string]::Equals(
            $publishedUpdater,
            $canonicalUpdaterPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $publishedUpdater -Destination $canonicalUpdaterPath -Force
    }

    $displayUpdaterPath = Join-Path $updaterRoot "$AppDisplayName.Updater.exe"
    if (-not [string]::Equals(
            $canonicalUpdaterPath,
            $displayUpdaterPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $canonicalUpdaterPath -Destination $displayUpdaterPath -Force
    }
}

function ConvertTo-SafeDesktopArchiveRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    if ([string]::IsNullOrEmpty($EntryName)) {
        throw 'Desktop package archive contains an empty entry name.'
    }

    $normalized = $EntryName.Replace('\', '/')
    if ($normalized.StartsWith('/') -or
        [IO.Path]::IsPathRooted($EntryName)) {
        throw "Desktop package archive contains a rooted entry: $EntryName"
    }
    if ($normalized.Contains('//')) {
        throw "Desktop package archive contains an empty path segment: $EntryName"
    }

    $isDirectory = $normalized.EndsWith('/')
    $relativePath = if ($isDirectory) {
        $normalized.Substring(0, $normalized.Length - 1)
    }
    else {
        $normalized
    }
    if ([string]::IsNullOrEmpty($relativePath)) {
        throw "Desktop package archive contains an empty relative path: $EntryName"
    }

    $segments = $relativePath.Split('/')
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or
            $segment -eq '.' -or
            $segment -eq '..') {
            throw "Desktop package archive contains an unsafe path segment: $EntryName"
        }
        if ($segment.Length -gt 255) {
            throw "Desktop package archive path segment is too long: $EntryName"
        }
        if ($segment -match '[\x00-\x1F\x7F<>:"|?*]') {
            throw "Desktop package archive path contains invalid characters: $EntryName"
        }
        if ($segment.EndsWith('.') -or $segment.EndsWith(' ')) {
            throw "Desktop package archive path has a trailing dot or space: $EntryName"
        }
        if ($segment -match '^(?i:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?$') {
            throw "Desktop package archive uses a reserved Windows name: $EntryName"
        }
    }
    if ($relativePath.Length -gt 1024) {
        throw "Desktop package archive relative path is too long: $EntryName"
    }

    return [pscustomobject]@{
        Path = [string]::Join('/', $segments)
        IsDirectory = $isDirectory
    }
}

function Get-ValidatedDesktopArchiveEntryMap {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][long]$PackageFileSize
    )

    $maximumPackageFileSize = 512MB
    $maximumEntryCount = 10000
    $maximumEntryUncompressedSize = 512MB
    $maximumTotalUncompressedSize = 2GB
    if ($PackageFileSize -le 0 -or
        $PackageFileSize -gt $maximumPackageFileSize) {
        throw "Desktop package archive file size is outside the allowed range: $PackageFileSize"
    }
    if ($Archive.Entries.Count -gt $maximumEntryCount) {
        throw "Desktop package archive contains too many entries: $($Archive.Entries.Count)"
    }

    $entries = @{}
    $canonicalPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $filePaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $directoryPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    [long]$totalUncompressedSize = 0

    foreach ($entry in $Archive.Entries) {
        $externalAttributes = [int]$entry.ExternalAttributes
        $unixMode = ($externalAttributes -shr 16) -band 0xFFFF
        if (
            ($externalAttributes -band
                [int][IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($unixMode -band 0xF000) -eq 0xA000
        ) {
            throw "Desktop package archive contains a link or reparse entry: $($entry.FullName)"
        }
        $validated = ConvertTo-SafeDesktopArchiveRelativePath `
            -EntryName $entry.FullName
        $entryPath = [string]$validated.Path
        if (-not $canonicalPaths.Add($entryPath)) {
            throw "Desktop package archive contains a canonical duplicate entry: $entryPath"
        }
        if ($entry.Length -lt 0 -or
            $entry.Length -gt $maximumEntryUncompressedSize) {
            throw "Desktop package archive entry is too large: $entryPath"
        }
        $actualEntryLength =
            Read-DesktopArchiveEntryActualLengthBounded `
                -Entry $entry `
                -MaximumEntryBytes $maximumEntryUncompressedSize `
                -RemainingTotalBytes (
                    $maximumTotalUncompressedSize -
                    $totalUncompressedSize) `
                -Description $entryPath
        if ($validated.IsDirectory -and $actualEntryLength -ne 0) {
            throw "Desktop package archive directory entry contains data: $entryPath"
        }
        $totalUncompressedSize += $actualEntryLength

        $segments = $entryPath.Split('/')
        for ($index = 1; $index -lt $segments.Count; $index++) {
            $ancestor = [string]::Join('/', $segments[0..($index - 1)])
            if ($filePaths.Contains($ancestor)) {
                throw "Desktop package archive file is an ancestor of another entry: $ancestor"
            }
            [void]$directoryPaths.Add($ancestor)
        }
        if ($validated.IsDirectory) {
            if ($filePaths.Contains($entryPath)) {
                throw "Desktop package archive has a file-directory conflict: $entryPath"
            }
            [void]$directoryPaths.Add($entryPath)
        }
        else {
            if ($directoryPaths.Contains($entryPath)) {
                throw "Desktop package archive has a file-directory conflict: $entryPath"
            }
            [void]$filePaths.Add($entryPath)
        }
        $entries[$entryPath] = $entry
    }

    return $entries
}

function Read-DesktopArchiveEntryActualLengthBounded {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][long]$MaximumEntryBytes,
        [Parameter(Mandatory = $true)][long]$RemainingTotalBytes,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($RemainingTotalBytes -lt 0) {
        throw 'Desktop package archive total uncompressed size is too large.'
    }

    $source = $null
    try {
        $source = $Entry.Open()
        $buffer = New-Object byte[] 81920
        [long]$actualLength = 0
        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($read -gt ($MaximumEntryBytes - $actualLength)) {
                throw "Desktop package archive entry is too large: $Description"
            }
            if ($read -gt ($RemainingTotalBytes - $actualLength)) {
                throw 'Desktop package archive total uncompressed size is too large.'
            }
            $actualLength += $read
        }
        if ($actualLength -ne $Entry.Length) {
            throw "Desktop package archive entry length does not match ZIP metadata: $Description"
        }
        return $actualLength
    }
    finally {
        if ($null -ne $source) {
            $source.Dispose()
        }
    }
}

function Read-DesktopArchiveEntryTextBounded {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Entry.Length -le 0 -or $Entry.Length -gt $MaximumBytes) {
        throw "$Description size is outside the allowed range."
    }

    $source = $null
    $buffered = $null
    try {
        $source = $Entry.Open()
        $buffered = [IO.MemoryStream]::new()
        $buffer = New-Object byte[] 81920
        [long]$written = 0
        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($read -gt ($MaximumBytes - $written)) {
                throw "$Description exceeded the read size limit."
            }
            $buffered.Write($buffer, 0, $read)
            $written += $read
        }
        if ($written -ne $Entry.Length) {
            throw "$Description length does not match ZIP metadata."
        }
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        return $strictUtf8.GetString($buffered.ToArray()).TrimStart(
            [char]0xFEFF)
    }
    finally {
        if ($null -ne $buffered) {
            $buffered.Dispose()
        }
        if ($null -ne $source) {
            $source.Dispose()
        }
    }
}

function Assert-DesktopArchiveScriptContract {
    param(
        [Parameter(Mandatory = $true)]$Entries,
        [string]$InstallCmdName
    )

    $installScriptName = 'Install-GeoraePlan.ps1'
    if (-not $Entries.ContainsKey($installScriptName)) {
        throw "Desktop package archive is missing installer script: $installScriptName"
    }
    $installScript = Read-DesktopArchiveEntryTextBounded `
        -Entry $Entries[$installScriptName] `
        -MaximumBytes 1MB `
        -Description $installScriptName
    $installTokens = $null
    $installParseErrors = $null
    $installAst = [System.Management.Automation.Language.Parser]::ParseInput(
        $installScript,
        [ref]$installTokens,
        [ref]$installParseErrors)
    if ([string]::IsNullOrWhiteSpace($installScript) -or
        $installParseErrors.Count -ne 0 -or
        $null -eq $installAst.ParamBlock) {
        throw 'Desktop package installer script content is invalid.'
    }
    $installParameterNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($parameter in $installAst.ParamBlock.Parameters) {
        [void]$installParameterNames.Add(
            [string]$parameter.Name.VariablePath.UserPath)
    }
    $requiredInstallParameters = @(
        'InstallRoot',
        'NoLaunch',
        'SuppressUi',
        'WorkerTimeoutSeconds',
        'LogPath',
        'RecoveryOnly',
        'LegacyBridgeCopy',
        'UpdaterOwnsInstallRootGate',
        'InstallRootGateOwnerProcessId',
        'InstallRootGateOwnerProcessPath',
        'InstallRootGateOwnerProcessStartTimeUtcTicks'
    )
    foreach ($requiredInstallParameter in $requiredInstallParameters) {
        if (-not $installParameterNames.Contains($requiredInstallParameter)) {
            throw "Desktop package installer script is missing updater parameter: $requiredInstallParameter"
        }
    }
    foreach ($requiredInstallMarker in @(
        'GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1',
        'GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1'
    )) {
        if ($installScript.IndexOf(
                $requiredInstallMarker,
                [StringComparison]::Ordinal) -lt 0) {
            throw "Desktop package installer script is missing updater contract marker: $requiredInstallMarker"
        }
    }

    $launchScriptName = 'App/앱실행.cmd'
    if (-not $Entries.ContainsKey($launchScriptName)) {
        throw "Desktop package archive is missing launch script: $launchScriptName"
    }
    $launchScript = Read-DesktopArchiveEntryTextBounded `
        -Entry $Entries[$launchScriptName] `
        -MaximumBytes 64KB `
        -Description $launchScriptName
    if ($launchScript.IndexOf(
            '*.Desktop.App.exe',
            [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $launchScript.IndexOf(
            'start "" "%APP_EXE%"',
            [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $launchScript.IndexOf(
            'if not defined APP_EXE for %%I in ("%~dp0*.exe")',
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Desktop package launch script content is invalid.'
    }

    if ([string]::IsNullOrWhiteSpace($InstallCmdName)) {
        $rootCmdEntries = @(
            $Entries.Keys |
                Where-Object {
                    $_ -notmatch '/' -and $_.EndsWith(
                        '.cmd',
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($rootCmdEntries.Count -ne 1) {
            throw 'Desktop package archive must contain exactly one root installer command.'
        }
        $InstallCmdName = $rootCmdEntries[0]
    }
    if (-not $Entries.ContainsKey($InstallCmdName)) {
        throw "Desktop package archive is missing installer command: $InstallCmdName"
    }
    $installCommand = Read-DesktopArchiveEntryTextBounded `
        -Entry $Entries[$InstallCmdName] `
        -MaximumBytes 64KB `
        -Description $InstallCmdName
    if ($installCommand.IndexOf(
            'powershell -ExecutionPolicy Bypass -File "%~dp0Install-GeoraePlan.ps1"',
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'Desktop package installer command content is invalid.'
    }
}

function ConvertTo-DesktopExecutableProductVersionCore {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$ProductVersion,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $normalized = $ProductVersion.Trim()
    if ($normalized.StartsWith(
            'v',
            [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }
    foreach ($separator in @('+', '-')) {
        $separatorIndex = $normalized.IndexOf($separator)
        if ($separatorIndex -ge 0) {
            $normalized = $normalized.Substring(0, $separatorIndex)
        }
    }

    $parsedVersion = $null
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        -not [Version]::TryParse(
            $normalized,
            [ref]$parsedVersion)) {
        throw "$Description has an invalid ProductVersion: $ProductVersion"
    }
    return $parsedVersion.ToString()
}

function Invoke-DesktopArchiveExecutableInspectionTestPausePoint {
    if (-not $script:EnableTestHooks) {
        return
    }
    $readySignal = [string]$env:GEORAEPLAN_PACKAGE_TEST_ARCHIVE_EXE_INSPECTION_READY_SIGNAL
    $continueSignal = [string]$env:GEORAEPLAN_PACKAGE_TEST_ARCHIVE_EXE_INSPECTION_CONTINUE_SIGNAL
    if (
        [string]::IsNullOrWhiteSpace($readySignal) -and
        [string]::IsNullOrWhiteSpace($continueSignal)
    ) {
        return
    }
    if (
        [string]::IsNullOrWhiteSpace($readySignal) -or
        [string]::IsNullOrWhiteSpace($continueSignal)
    ) {
        throw 'Archive EXE inspection test pause signals are incomplete.'
    }
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($readySignal),
        'ready',
        [Text.UTF8Encoding]::new($false))
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $continueSignal -PathType Leaf)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw 'Timed out waiting for archive EXE inspection test signal.'
        }
        Start-Sleep -Milliseconds 25
    }
}

function Get-DesktopArchiveExecutableIdentity {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $inspectionRoot = Join-Path `
        ([IO.Path]::GetTempPath()) `
        ('georaeplan-desktop-archive-identity-{0}' -f
            [Guid]::NewGuid().ToString('N'))
    $inspectionPath = Join-Path $inspectionRoot 'entry.exe'
    [void][IO.Directory]::CreateDirectory($inspectionRoot)
    $inspectionStream = $null
    try {
        $entryStream = $null
        $extractedSha256Hash = $null
        try {
            $entryStream = $Entry.Open()
            $inspectionStream = [IO.FileStream]::new(
                $inspectionPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::Read,
                81920,
                [IO.FileOptions]::WriteThrough)
            $entryStream.CopyTo($inspectionStream)
            $inspectionStream.Flush($true)
            if ($inspectionStream.Length -ne [long]$Entry.Length) {
                throw "$Description extracted length does not match ZIP metadata."
            }
            $extractedSha256Hash =
                Get-Sha256FromStream -Stream $inspectionStream
        }
        finally {
            if ($null -ne $entryStream) {
                $entryStream.Dispose()
            }
            if ($null -ne $inspectionStream) {
                $inspectionStream.Dispose()
                $inspectionStream = $null
            }
        }

        $inspectionStream = [IO.File]::Open(
            $inspectionPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $sha256Hash = Get-Sha256FromStream -Stream $inspectionStream
        if (-not [string]::Equals(
            $extractedSha256Hash,
            $sha256Hash,
            [StringComparison]::Ordinal
        )) {
            throw "$Description immutable inspection identity changed before lease acquisition."
        }
        Invoke-DesktopArchiveExecutableInspectionTestPausePoint
        $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
            $inspectionPath).ProductVersion
        $productVersionCore =
            ConvertTo-DesktopExecutableProductVersionCore `
                -ProductVersion ([string]$productVersion) `
                -Description $Description
        $verifiedSha256Hash = Get-Sha256FromStream -Stream $inspectionStream
        if (-not [string]::Equals(
            $sha256Hash,
            $verifiedSha256Hash,
            [StringComparison]::Ordinal
        )) {
            throw "$Description immutable inspection identity changed."
        }
    }
    finally {
        if ($null -ne $inspectionStream) {
            $inspectionStream.Dispose()
        }
        if (Test-Path -LiteralPath $inspectionPath) {
            [IO.File]::Delete($inspectionPath)
        }
        if (Test-Path -LiteralPath $inspectionRoot) {
            [IO.Directory]::Delete($inspectionRoot, $false)
        }
    }

    return [pscustomobject]@{
        Sha256 = $sha256Hash
        ProductVersionCore = $productVersionCore
    }
}

function Assert-DesktopArchiveExecutableIdentity {
    param(
        [Parameter(Mandatory = $true)]$CanonicalEntry,
        [Parameter(Mandatory = $true)]$DisplayAliasEntry,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $canonicalIdentity = Get-DesktopArchiveExecutableIdentity `
        -Entry $CanonicalEntry `
        -Description 'canonical desktop executable'
    $aliasIdentity = Get-DesktopArchiveExecutableIdentity `
        -Entry $DisplayAliasEntry `
        -Description 'display-name desktop executable alias'
    $expectedVersionCore =
        ConvertTo-DesktopExecutableProductVersionCore `
            -ProductVersion $ExpectedVersion `
            -Description 'desktop package expected version'
    if (-not [string]::Equals(
            $canonicalIdentity.ProductVersionCore,
            $expectedVersionCore,
            [StringComparison]::Ordinal)) {
        throw 'Desktop package executable ProductVersion does not match the package version.'
    }
    if (-not [string]::Equals(
            $canonicalIdentity.ProductVersionCore,
            $aliasIdentity.ProductVersionCore,
            [StringComparison]::Ordinal)) {
        throw 'Desktop package canonical and display-name executables have different ProductVersion core values.'
    }
    if (-not [string]::Equals(
            $canonicalIdentity.Sha256,
            $aliasIdentity.Sha256,
            [StringComparison]::Ordinal)) {
        throw 'Desktop package canonical and display-name executables have different SHA-256 hashes.'
    }
}

function Assert-DesktopPackageArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$AppDisplayName,
        [Parameter(Mandatory = $true)][string]$InstallCmdName,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        throw "Desktop package archive was not created: $ArchivePath"
    }
    $archiveFile = Get-Item -LiteralPath $ArchivePath -Force -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = Get-ValidatedDesktopArchiveEntryMap `
            -Archive $archive `
            -PackageFileSize $archiveFile.Length

        $desktopAppArchiveEntries = @(
            $entries.Keys |
                Where-Object {
                    $_ -match '^App/[^/]+\.Desktop\.App\.exe$'
                }
        )
        if ($desktopAppArchiveEntries.Count -ne 1 -or
            -not [string]::Equals(
                $desktopAppArchiveEntries[0],
                'App/거래플랜.Desktop.App.exe',
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Desktop package archive must contain exactly one canonical *.Desktop.App.exe launcher target."
        }

        $requiredEntries = @(
            'App/거래플랜.Desktop.App.exe',
            "App/$AppDisplayName.exe",
            'App/appsettings.json',
            'App/앱실행.cmd',
            'App/Updater/거래플랜.Updater.exe',
            "App/Updater/$AppDisplayName.Updater.exe",
            'Install-GeoraePlan.ps1',
            $InstallCmdName,
            'README.txt'
        )
        foreach ($requiredEntry in $requiredEntries) {
            if (-not $entries.ContainsKey($requiredEntry)) {
                throw "Desktop package archive is missing a required entry: $requiredEntry"
            }
            if ($entries[$requiredEntry].Length -le 0) {
                throw "Desktop package archive contains an empty required entry: $requiredEntry"
            }
        }
        Assert-DesktopArchiveExecutableIdentity `
            -CanonicalEntry $entries['App/거래플랜.Desktop.App.exe'] `
            -DisplayAliasEntry $entries["App/$AppDisplayName.exe"] `
            -ExpectedVersion $ExpectedVersion
        Assert-DesktopArchiveScriptContract `
            -Entries $entries `
            -InstallCmdName $InstallCmdName
    }
    finally {
        $archive.Dispose()
    }
}

function Set-DesktopPackageApiBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [Parameter(Mandatory = $true)][string]$BaseUrl
    )

    $normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($normalizedBaseUrl)) {
        throw 'ApiBaseUrl is empty.'
    }

    $appSettingsPath = Join-Path $AppRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appSettingsPath)) {
        throw "appsettings.json not found in desktop package: $appSettingsPath"
    }

    $json = Get-Content -LiteralPath $appSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $json.PSObject.Properties['Api']) {
        $json | Add-Member -NotePropertyName Api -NotePropertyValue ([pscustomobject]@{ BaseUrl = $normalizedBaseUrl })
    }
    elseif ($null -eq $json.Api.PSObject.Properties['BaseUrl']) {
        $json.Api | Add-Member -NotePropertyName BaseUrl -NotePropertyValue $normalizedBaseUrl
    }
    else {
        $json.Api.BaseUrl = $normalizedBaseUrl
    }

    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $appSettingsPath -Encoding UTF8
}

function Prepare-DefaultClientSourceFolder {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$AppDisplayName,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    $publishRoot = Join-Path $env:TEMP (
        'georaeplan-desktop-package-publish-{0}' -f
        [System.Guid]::NewGuid().ToString('N'))
    try {
        $sourceFolder = Publish-DesktopApplication -ProjectRoot $ProjectRoot -PublishRoot $publishRoot -DotnetExe $DotnetExe
        Ensure-DesktopPackageLaunchFiles -SourceFolder $sourceFolder -AppDisplayName $AppDisplayName
        return $sourceFolder
    }
    catch {
        if (Test-Path -LiteralPath $publishRoot) {
            $null = Remove-ContainedOutputItem `
                -ParentPath $env:TEMP `
                -ChildName ([System.IO.Path]::GetFileName($publishRoot)) `
                -Description 'failed generated desktop publish directory' `
                -Recurse
        }
        throw
    }
}

if ([string]::IsNullOrWhiteSpace($AppDisplayName)) {
    $AppDisplayName = Get-Utf8String '6rGw656Y7ZSM656c'
}

if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = Get-Utf8String '6rGw656Y7ZSM656cLVBDLeyEpOy5mO2MqO2CpOyngA=='
}

$AppDisplayName = Assert-SingleFileName `
    -Name 'AppDisplayName' `
    -Value $AppDisplayName
$PackageName = Assert-SingleFileName `
    -Name 'PackageName' `
    -Value $PackageName

$installCmdName = Get-Utf8String '6rGw656Y7ZSM656cLeyEpOy5mC5jbWQ='
$removeShortcutSuffix = Get-Utf8String 'IOygnOqxsC5sbms='

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

$hasExplicitSourceFolder =
    -not [string]::IsNullOrWhiteSpace($SourceFolder)
if ($hasExplicitSourceFolder) {
    if (-not (Test-Path -LiteralPath $SourceFolder -PathType Container)) {
        throw "Source folder not found or is not a directory: $SourceFolder"
    }
    Assert-DirectoryTreeHasNoReparsePoints `
        -RootPath $SourceFolder `
        -Description 'SourceFolder'
    $SourceFolder = (
        Get-Item `
            -LiteralPath $SourceFolder `
            -Force `
            -ErrorAction Stop).FullName
}

$tempInitializer = Join-Path $ProjectRoot 'tools\common\Initialize-GeoraePlanTemp.ps1'
if (Test-Path -LiteralPath $tempInitializer) {
    . $tempInitializer -ProjectRoot $ProjectRoot
}

$dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$env:DOTNET_EXE = $dotnetExe

$deploymentRoot = Get-DeploymentRoot -ProjectRoot $ProjectRoot
$desktopVersion = Get-ProjectVersion -ProjectRoot $ProjectRoot
$WindowsSigningConfigPath = Resolve-WindowsSigningConfigPath -ProjectRoot $ProjectRoot -ConfigPath $WindowsSigningConfigPath

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Get-DefaultOutputRoot -DeploymentRoot $deploymentRoot
}

Assert-PathIsNotReparsePoint `
    -Path $OutputRoot `
    -Description 'OutputRoot'
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Get-Item -LiteralPath $OutputRoot -Force -ErrorAction Stop).FullName
Assert-PathIsNotReparsePoint `
    -Path $OutputRoot `
    -Description 'OutputRoot'
$adminOutputRoot = Get-ContainedDirectChildPath `
    -ParentPath $OutputRoot `
    -ChildName '관리자용' `
    -Description 'administrator output directory'
Assert-PathIsNotReparsePoint `
    -Path $adminOutputRoot `
    -Description 'administrator output directory'
New-Item -ItemType Directory -Force -Path $adminOutputRoot | Out-Null
Assert-PathIsNotReparsePoint `
    -Path $adminOutputRoot `
    -Description 'administrator output directory'

$packageBuilderLockStream =
    Enter-PackageBuilderOutputLock -AdminOutputRoot $adminOutputRoot
$generatedSourceFolder = ''
try {
$startupRecoveryResult = Invoke-PackagePublishTransactionRecovery `
    -ParentPath $adminOutputRoot `
    -ExpectedPackageName $PackageName
$startupOwnerRecoveryResult = Invoke-PackageStagedZipOwnerRecovery `
    -ParentPath $adminOutputRoot `
    -ExpectedPackageName $PackageName
if ($EnableTestHooks -and
    $env:GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY -eq '1') {
    Write-Host "startup_recovery_only=$startupRecoveryResult"
    Write-Host (
        "startup_staged_zip_owner_recovery=$startupOwnerRecoveryResult")
    exit 0
}
if ([string]::IsNullOrWhiteSpace($SourceFolder)) {
    $SourceFolder = Prepare-DefaultClientSourceFolder -ProjectRoot $ProjectRoot -AppDisplayName $AppDisplayName -DotnetExe $dotnetExe
    $generatedSourceFolder = $SourceFolder
}

if (-not (Test-Path -LiteralPath $SourceFolder -PathType Container)) {
    throw "Source folder not found or is not a directory: $SourceFolder"
}

Assert-DirectoryTreeHasNoReparsePoints `
    -RootPath $SourceFolder `
    -Description 'SourceFolder'
$SourceFolder = (
    Get-Item `
        -LiteralPath $SourceFolder `
        -Force `
        -ErrorAction Stop).FullName
Write-Host "source_folder=$SourceFolder"

if ($EnableTestHooks -and
    -not [string]::IsNullOrWhiteSpace(
        $env:GEORAEPLAN_PACKAGE_TEST_HOLD_LOCK_MILLISECONDS)) {
    $holdLockMilliseconds = 0
    if (-not [int]::TryParse(
            $env:GEORAEPLAN_PACKAGE_TEST_HOLD_LOCK_MILLISECONDS,
            [ref]$holdLockMilliseconds) -or
        $holdLockMilliseconds -lt 1 -or
        $holdLockMilliseconds -gt 30000) {
        throw 'GEORAEPLAN_PACKAGE_TEST_HOLD_LOCK_MILLISECONDS must be an integer from 1 through 30000.'
    }
    Start-Sleep -Milliseconds $holdLockMilliseconds
}

$legacyRootPackage = Remove-ContainedOutputItem `
    -ParentPath $OutputRoot `
    -ChildName $PackageName `
    -Description 'legacy package directory' `
    -Recurse
$legacyRootZipName = $PackageName + '.zip'
$legacyRootZip = Remove-ContainedOutputItem `
    -ParentPath $OutputRoot `
    -ChildName $legacyRootZipName `
    -Description 'legacy package archive'

$packageRoot = Get-ContainedDirectChildPath `
    -ParentPath $adminOutputRoot `
    -ChildName $PackageName `
    -Description 'package directory'
$appRoot = Join-Path $packageRoot 'App'
$zipName = $PackageName + '.zip'
$zipPath = Get-ContainedDirectChildPath `
    -ParentPath $adminOutputRoot `
    -ChildName $zipName `
    -Description 'package archive'
$testHookCapabilityMarkerName =
    '.georaeplan-installer-test-capability'
$testHookCapability = ''
if ($EnableTestHooks) {
    $testHookRandomBytes = New-Object byte[] 32
    $testHookRandomNumberGenerator =
        [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $testHookRandomNumberGenerator.GetBytes(
            $testHookRandomBytes)
    }
    finally {
        $testHookRandomNumberGenerator.Dispose()
    }
    $testHookCapability =
        -join ($testHookRandomBytes | ForEach-Object {
            $_.ToString('X2')
        })
}

$null = Remove-ContainedOutputItem `
    -ParentPath $adminOutputRoot `
    -ChildName $PackageName `
    -Description 'package directory' `
    -Recurse
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

Invoke-RobocopyMirror -Source $SourceFolder -Destination $appRoot
Ensure-DesktopPackageLaunchFiles -SourceFolder $appRoot -AppDisplayName $AppDisplayName
Set-DesktopPackageApiBaseUrl -AppRoot $appRoot -BaseUrl $ApiBaseUrl
Get-ChildItem -LiteralPath $appRoot -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction Stop

$updaterProject = Join-Path $ProjectRoot 'Updater\거래플랜.Updater\거래플랜.Updater.csproj'
if (Test-Path -LiteralPath $updaterProject) {
    $updaterPublishName =
        'georaeplan-updater-publish-{0}' -f
        [System.Guid]::NewGuid().ToString('N')
    $updaterPublishRoot =
        Join-Path $env:TEMP $updaterPublishName
    try {
        & $dotnetExe publish $updaterProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $updaterPublishRoot | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to publish updater for desktop package.'
        }

        $publishedUpdaterExe = Join-Path $updaterPublishRoot '거래플랜.Updater.exe'
        if (-not (Test-Path -LiteralPath $publishedUpdaterExe)) {
            throw "Published updater executable not found: $publishedUpdaterExe"
        }

        Invoke-RobocopyMirror -Source $updaterPublishRoot -Destination (Join-Path $appRoot 'Updater')
        Get-ChildItem -LiteralPath (Join-Path $appRoot 'Updater') -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $updaterPublishRoot) {
            $null = Remove-ContainedOutputItem `
                -ParentPath $env:TEMP `
                -ChildName $updaterPublishName `
                -Description 'generated updater publish directory' `
                -Recurse
        }
    }
}

Ensure-DesktopPackageUpdaterFile -AppRoot $appRoot -AppDisplayName $AppDisplayName
Assert-DesktopPackageRequiredFiles -AppRoot $appRoot -AppDisplayName $AppDisplayName
Invoke-WindowsArtifactSigning -ProjectRoot $ProjectRoot -WindowsSigningConfigPath $WindowsSigningConfigPath -PackageRoot $packageRoot -RequireSigning:$RequireWindowsAuthenticode

$serverUrl = ''
$appSettingsPath = Join-Path $appRoot 'appsettings.json'
if (Test-Path -LiteralPath $appSettingsPath) {
    try {
        $appSettings = Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json
        if ($null -ne $appSettings.Api -and -not [string]::IsNullOrWhiteSpace($appSettings.Api.BaseUrl)) {
            $serverUrl = [string]$appSettings.Api.BaseUrl
        }
    }
    catch {
        $serverUrl = ''
    }
}

$installPs1Name = 'Install-GeoraePlan.ps1'
$uninstallPs1Name = 'Uninstall-GeoraePlan.ps1'

$uninstallScriptBody = @"
param(
    [string]`$InstallRoot = '',
    [switch]`$NoShortcutCleanup
)

if ([string]::IsNullOrWhiteSpace(`$InstallRoot)) {
    `$programFilesRoot = [Environment]::GetFolderPath('ProgramFilesX86')
    if ([string]::IsNullOrWhiteSpace(`$programFilesRoot)) {
        `$programFilesRoot = [Environment]::GetFolderPath('ProgramFiles')
    }

    `$InstallRoot = Join-Path `$programFilesRoot 'tradeplan'
}

if (-not ('GeoraePlanUninstallPathNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class GeoraePlanUninstallPathNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
'@
}

function ConvertTo-DosDirectoryPath {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    if (`$Path.StartsWith('\\?\UNC\', [System.StringComparison]::OrdinalIgnoreCase)) {
        return '\\' + `$Path.Substring(8)
    }
    if (`$Path.StartsWith('\\?\', [System.StringComparison]::OrdinalIgnoreCase)) {
        return `$Path.Substring(4)
    }

    return `$Path
}

function Resolve-PhysicalDirectoryPath {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$providerPath = ConvertTo-DosDirectoryPath `$Path
    `$item = Get-Item -LiteralPath `$providerPath -Force -ErrorAction Stop
    if (-not `$item.PSIsContainer) {
        throw "디렉터리 경로가 아닙니다: `$Path"
    }

    for (`$current = `$item; `$null -ne `$current; `$current = `$current.Parent) {
        if ((`$current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "재분석 지점을 통과하는 설치 경로는 제거할 수 없습니다: `$Path"
        }
    }

    `$handle = [GeoraePlanUninstallPathNative]::CreateFile(
        `$item.FullName,
        0,
        7,
        [IntPtr]::Zero,
        3,
        0x02000000,
        [IntPtr]::Zero)
    if (`$handle.IsInvalid) {
        `$nativeError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        `$handle.Dispose()
        throw "설치 경로의 실제 위치를 확인하지 못했습니다. Path=`$Path, Win32Error=`$nativeError"
    }

    try {
        `$buffer = New-Object System.Text.StringBuilder 32768
        `$length = [GeoraePlanUninstallPathNative]::GetFinalPathNameByHandle(
            `$handle,
            `$buffer,
            [uint32]`$buffer.Capacity,
            0)
        if (`$length -eq 0 -or `$length -ge `$buffer.Capacity) {
            `$nativeError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "설치 경로의 실제 위치를 정규화하지 못했습니다. Path=`$Path, Win32Error=`$nativeError"
        }

        `$fullPath = ConvertTo-DosDirectoryPath `$buffer.ToString()
    }
    finally {
        `$handle.Dispose()
    }

    `$trimmedPath = `$fullPath.TrimEnd([char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar))
    `$trimmedRoot = [System.IO.Path]::GetPathRoot(`$fullPath).TrimEnd([char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar))
    if ([string]::Equals(
            `$trimmedPath,
            `$trimmedRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return [System.IO.Path]::GetPathRoot(`$fullPath)
    }

    return `$trimmedPath
}

`$scriptRoot = Resolve-PhysicalDirectoryPath (Split-Path -Parent `$PSCommandPath)
`$InstallRoot = Resolve-PhysicalDirectoryPath `$InstallRoot
if (-not [string]::Equals(
        `$InstallRoot,
        `$scriptRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "제거 스크립트가 설치된 폴더와 InstallRoot가 일치하지 않습니다."
}

`$forbiddenRemovalRoots = @(
    [System.IO.Path]::GetPathRoot(`$InstallRoot),
    [Environment]::GetFolderPath('Windows'),
    [Environment]::GetFolderPath('System'),
    [Environment]::GetFolderPath('UserProfile'),
    [Environment]::GetFolderPath('ProgramFiles'),
    [Environment]::GetFolderPath('ProgramFilesX86'),
    [Environment]::GetFolderPath('CommonApplicationData'),
    [Environment]::GetFolderPath('LocalApplicationData')
)
`$installRootComparison = `$InstallRoot.TrimEnd([char[]]@(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar))
foreach (`$forbiddenRootCandidate in `$forbiddenRemovalRoots) {
    if ([string]::IsNullOrWhiteSpace(`$forbiddenRootCandidate)) {
        continue
    }

    `$forbiddenRoot = [System.IO.Path]::GetFullPath(
        (ConvertTo-DosDirectoryPath `$forbiddenRootCandidate)).TrimEnd([char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar))
    if ([string]::Equals(
            `$installRootComparison,
            `$forbiddenRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "볼륨 또는 주요 시스템 폴더는 제거할 수 없습니다: `$InstallRoot"
    }
}

`$localDataRootCandidate = Join-Path (
    [Environment]::GetFolderPath('LocalApplicationData')) '__APP_DISPLAY_NAME__'
`$localDataRoot = if (Test-Path -LiteralPath `$localDataRootCandidate) {
    Resolve-PhysicalDirectoryPath `$localDataRootCandidate
}
else {
    [System.IO.Path]::GetFullPath(`$localDataRootCandidate).TrimEnd([char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar))
}
`$separator = [System.IO.Path]::DirectorySeparatorChar
`$installContainsData = `$localDataRoot.StartsWith(
    `$InstallRoot + `$separator,
    [System.StringComparison]::OrdinalIgnoreCase)
`$dataContainsInstall = `$InstallRoot.StartsWith(
    `$localDataRoot + `$separator,
    [System.StringComparison]::OrdinalIgnoreCase)
if ([string]::Equals(
        `$InstallRoot,
        `$localDataRoot,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    `$installContainsData -or
    `$dataContainsInstall) {
    throw "로컬 데이터 폴더와 겹치는 경로는 제거할 수 없습니다."
}

`$installedExecutable = Join-Path `$InstallRoot '__APP_DISPLAY_NAME__.exe'
if (-not (Test-Path -LiteralPath `$installedExecutable -PathType Leaf)) {
    throw "설치된 거래플랜 실행 파일을 확인하지 못해 제거를 중단합니다."
}

`$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) '__APP_DISPLAY_NAME__.lnk'
`$commonDesktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) '__APP_DISPLAY_NAME__.lnk'
`$startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) '__APP_DISPLAY_NAME__'
`$commonStartMenuDir = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) '__APP_DISPLAY_NAME__'
`$legacyUserRoot = Join-Path `$env:LOCALAPPDATA 'Programs\__APP_DISPLAY_NAME__'

if (-not `$NoShortcutCleanup) {
    Remove-Item -LiteralPath `$desktopShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath `$commonDesktopShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath `$startMenuDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath `$commonStartMenuDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath `$legacyUserRoot -Recurse -Force -ErrorAction SilentlyContinue
}

`$installParent = Split-Path -Parent `$InstallRoot
Set-Location -LiteralPath `$installParent
Remove-Item -LiteralPath `$InstallRoot -Recurse -Force -ErrorAction Stop
if (Test-Path -LiteralPath `$InstallRoot) {
    throw "설치 폴더를 완전히 제거하지 못했습니다: `$InstallRoot"
}

Write-Host '__APP_DISPLAY_NAME__ removed. Local database, attachments, and backups were preserved.'
"@
$appDisplayNamePowerShellLiteralContent =
    ConvertTo-PowerShellSingleQuotedLiteralContent -Value $AppDisplayName
$uninstallScriptBody = $uninstallScriptBody.Replace(
    '__APP_DISPLAY_NAME__',
    $appDisplayNamePowerShellLiteralContent)
Assert-PowerShellScriptSyntax `
    -ScriptContent $uninstallScriptBody `
    -Description 'generated desktop uninstaller'

$uninstallScriptBodyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($uninstallScriptBody))

$installScriptTemplate = @"
param(
    [string]`$InstallRoot = '',
    [switch]`$NoLaunch,
    [switch]`$NoShortcuts,
    [switch]`$SuppressUi,
    [string]`$LogPath = '',
    [switch]`$WorkerMode,
    [switch]`$RecoveryOnly,
    [switch]`$LegacyBridgeCopy,
    [switch]`$UpdaterOwnsInstallRootGate,
    [switch]`$BootstrapperOwnsInstallRootGate,
    [int]`$InstallRootGateOwnerProcessId = 0,
    [string]`$InstallRootGateOwnerProcessPath = '',
    [long]`$InstallRootGateOwnerProcessStartTimeUtcTicks = 0,
    [int]`$OriginUserProcessId = 0,
    [string]`$OriginUserProcessPath = '',
    [long]`$OriginUserProcessStartTimeUtcTicks = 0,
    [string]`$WorkerStartPipeName = '',
    [string]`$WorkerStartToken = '',
    [int]`$WorkerStartServerProcessId = 0,
    [ValidateRange(1, 86400)]
    [int]`$WorkerTimeoutSeconds = 900
)

# GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1
# GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1
`$ErrorActionPreference = 'Stop'
`$script:InstallerTestHooksEnabled =
    __INSTALLER_TEST_HOOKS_ENABLED__
`$script:InstallerTestCapabilityMarkerName =
    '.georaeplan-installer-test-capability'
`$InstallerScriptPath = [System.IO.Path]::GetFullPath(`$MyInvocation.MyCommand.Path)
`$ExpectedVersion = '__EXPECTED_VERSION__'

if (`$null -eq ('GeoraePlanInstaller.NativePathIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlanInstaller
{
    public static class NativePathIdentity
    {
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

        public static string Resolve(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", "path");

            string fullPath = NormalizeLexicalPath(path);
            var missingSegments = new Stack<string>();
            string existingPath = fullPath;
            FileAttributes existingAttributes;
            while (!TryGetAttributes(existingPath, out existingAttributes))
            {
                string leafName = Path.GetFileName(existingPath);
                string parentPath = Path.GetDirectoryName(existingPath);
                if (String.IsNullOrEmpty(leafName) ||
                    String.IsNullOrEmpty(parentPath) ||
                    String.Equals(
                        existingPath,
                        parentPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new DirectoryNotFoundException(
                        "Existing path ancestor was not found: " + fullPath);
                }

                missingSegments.Push(leafName);
                existingPath = NormalizeLexicalPath(parentPath);
            }

            if (missingSegments.Count > 0 &&
                (existingAttributes & FileAttributes.Directory) == 0)
            {
                throw new IOException(
                    "Existing path ancestor is not a directory: " +
                    existingPath);
            }

            AssertNoReparsePoints(existingPath);
            string resolvedExistingPath =
                GetFinalExistingPath(existingPath);
            AssertNoReparsePoints(resolvedExistingPath);

            string resolvedPath = resolvedExistingPath;
            while (missingSegments.Count > 0)
                resolvedPath = Path.Combine(
                    resolvedPath,
                    missingSegments.Pop());

            return NormalizeLexicalPath(resolvedPath);
        }

        private static string NormalizeLexicalPath(string path)
        {
            string fullPath = RemoveExtendedPathPrefix(
                    Path.GetFullPath(path))
                .Replace(
                    Path.AltDirectorySeparatorChar,
                    Path.DirectorySeparatorChar);
            string root = Path.GetPathRoot(fullPath);
            while (fullPath.Length > root.Length &&
                   (fullPath[fullPath.Length - 1] ==
                        Path.DirectorySeparatorChar ||
                    fullPath[fullPath.Length - 1] ==
                        Path.AltDirectorySeparatorChar))
            {
                fullPath = fullPath.Substring(0, fullPath.Length - 1);
            }
            return fullPath;
        }

        private static bool TryGetAttributes(
            string path,
            out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
        }

        private static void AssertNoReparsePoints(
            string existingPath)
        {
            string currentPath = existingPath;
            while (!String.IsNullOrEmpty(currentPath))
            {
                FileAttributes attributes =
                    File.GetAttributes(currentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "Install path traverses a reparse point: " +
                        currentPath);
                }

                string parentPath = Path.GetDirectoryName(currentPath);
                if (String.IsNullOrEmpty(parentPath) ||
                    String.Equals(
                        currentPath,
                        parentPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                currentPath = parentPath;
            }
        }

        private static string GetFinalExistingPath(
            string existingPath)
        {
            using (SafeFileHandle handle = CreateFile(
                existingPath,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Final path handle open failed: " + existingPath);
                }

                var buffer = new StringBuilder(512);
                uint result = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    0);
                if (result == 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Final path resolution failed: " + existingPath);
                }

                if (result >= buffer.Capacity)
                {
                    buffer = new StringBuilder(checked((int)result + 1));
                    result = GetFinalPathNameByHandle(
                        handle,
                        buffer,
                        (uint)buffer.Capacity,
                        0);
                    if (result == 0 || result >= buffer.Capacity)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Final path resolution failed: " +
                            existingPath);
                    }
                }

                return RemoveExtendedPathPrefix(buffer.ToString());
            }
        }

        private static string RemoveExtendedPathPrefix(string path)
        {
            const string extendedUncPrefix = @"\\?\UNC\";
            const string extendedPrefix = @"\\?\";
            if (path.StartsWith(
                    extendedUncPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" +
                    path.Substring(extendedUncPrefix.Length);
            }

            return path.StartsWith(
                extendedPrefix,
                StringComparison.OrdinalIgnoreCase)
                ? path.Substring(extendedPrefix.Length)
                : path;
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);
    }

    public static class ProcessTokenIdentity
    {
        private const uint TokenQuery = 0x0008;

        public static string GetUserSid(Process process)
        {
            if (process == null)
                throw new ArgumentNullException("process");

            IntPtr tokenHandle;
            if (!OpenProcessToken(
                    process.Handle,
                    TokenQuery,
                    out tokenHandle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Process token open failed.");
            }

            using (var safeToken =
                new SafeAccessTokenHandle(tokenHandle))
            using (var identity =
                new System.Security.Principal.WindowsIdentity(
                    safeToken.DangerousGetHandle()))
            {
                if (identity.User == null)
                {
                    throw new InvalidOperationException(
                        "Process token has no user SID.");
                }
                return identity.User.Value;
            }
        }

        [DllImport(
            "advapi32.dll",
            SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);
    }

    public sealed class WorkerJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectBasicProcessIdList = 3;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectQuery = 0x0004;
        private const uint JobObjectTerminate = 0x0008;
        private IntPtr handle;

        public WorkerJob(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Job name is required.", "name");

            handle = CreateJobObject(IntPtr.Zero, name);
            int createError = Marshal.GetLastWin32Error();
            if (handle == IntPtr.Zero)
                throw new Win32Exception(createError);
            if (createError == 183)
            {
                Dispose();
                throw new IOException(
                    "Installer worker job already exists: " + name);
            }

            var information =
                new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            information.BasicLimitInformation.LimitFlags =
                JobObjectLimitKillOnJobClose;
            int length = Marshal.SizeOf(
                typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(information, buffer, false);
                if (!SetInformationJobObject(
                        handle,
                        JobObjectExtendedLimitInformation,
                        buffer,
                        (uint)length))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "KILL_ON_JOB_CLOSE configuration failed.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private WorkerJob(IntPtr existingHandle)
        {
            handle = existingHandle;
        }

        public static WorkerJob OpenExisting(string name)
        {
            IntPtr existingHandle = OpenJobObject(
                JobObjectQuery | JobObjectTerminate,
                false,
                name);
            if (existingHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2)
                    return null;
                throw new Win32Exception(
                    error,
                    "Existing installer worker job open failed: " +
                    name);
            }
            return new WorkerJob(existingHandle);
        }

        public void AssignProcess(Process process)
        {
            if (process == null)
                throw new ArgumentNullException("process");
            EnsureNotDisposed();
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Worker process job assignment failed.");
            }

            bool assigned;
            if (!IsProcessInJob(process.Handle, handle, out assigned))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Worker process job membership check failed.");
            }
            if (!assigned)
                throw new IOException(
                    "Worker process is not in the expected job.");
        }

        public uint ActiveProcessCount
        {
            get
            {
                EnsureNotDisposed();
                int length = Marshal.SizeOf(
                    typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    uint returnedLength;
                    if (!QueryInformationJobObject(
                            handle,
                            JobObjectBasicAccountingInformation,
                            buffer,
                            (uint)length,
                            out returnedLength))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Installer worker job query failed.");
                    }
                    var information =
                        (JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)
                        Marshal.PtrToStructure(
                            buffer,
                            typeof(
                                JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
                    return information.ActiveProcesses;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        public bool ContainsProcessId(int processId)
        {
            if (processId <= 0)
                throw new ArgumentOutOfRangeException("processId");
            EnsureNotDisposed();

            int capacity = 16;
            while (capacity <= 65536)
            {
                int length = checked(8 + (capacity * IntPtr.Size));
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    uint returnedLength;
                    if (QueryInformationJobObject(
                            handle,
                            JobObjectBasicProcessIdList,
                            buffer,
                            (uint)length,
                            out returnedLength))
                    {
                        uint count = unchecked(
                            (uint)Marshal.ReadInt32(buffer, 4));
                        for (uint index = 0; index < count; index++)
                        {
                            long candidate = Marshal.ReadIntPtr(
                                    buffer,
                                    checked(
                                        8 +
                                        ((int)index * IntPtr.Size)))
                                .ToInt64();
                            if (candidate == processId)
                                return true;
                        }
                        return false;
                    }

                    int error = Marshal.GetLastWin32Error();
                    if (error != 234)
                    {
                        throw new Win32Exception(
                            error,
                            "Installer worker job process query failed.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                capacity = checked(capacity * 2);
            }

            throw new IOException(
                "Installer worker job process list is too large.");
        }

        public void WaitForEmpty(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(
                    "timeoutMilliseconds");
            var stopwatch = Stopwatch.StartNew();
            while (ActiveProcessCount != 0)
            {
                if (stopwatch.ElapsedMilliseconds >=
                    timeoutMilliseconds)
                {
                    throw new TimeoutException(
                        "Installer worker job did not become empty.");
                }
                Thread.Sleep(25);
            }
        }

        public void TerminateAndWait(
            uint exitCode,
            int timeoutMilliseconds)
        {
            EnsureNotDisposed();
            if (!TerminateJobObject(handle, exitCode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Installer worker job termination failed.");
            }
            WaitForEmpty(timeoutMilliseconds);
        }

        public static uint GetPipeClientProcessId(
            SafePipeHandle pipeHandle)
        {
            uint processId;
            if (!GetNamedPipeClientProcessId(
                    pipeHandle,
                    out processId))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Worker start pipe client PID query failed.");
            }
            return processId;
        }

        public static uint GetPipeServerProcessId(
            SafePipeHandle pipeHandle)
        {
            uint processId;
            if (!GetNamedPipeServerProcessId(
                    pipeHandle,
                    out processId))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Worker start pipe server PID query failed.");
            }
            return processId;
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
                return;
            IntPtr closingHandle = handle;
            handle = IntPtr.Zero;
            CloseHandle(closingHandle);
        }

        private void EnsureNotDisposed()
        {
            if (handle == IntPtr.Zero)
                throw new ObjectDisposedException("WorkerJob");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION
                BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateJobObjectW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr CreateJobObject(
            IntPtr jobAttributes,
            string name);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "OpenJobObjectW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr OpenJobObject(
            uint desiredAccess,
            bool inheritHandle,
            string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(
            IntPtr job,
            IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(
            IntPtr process,
            IntPtr job,
            out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(
            IntPtr job,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(
            SafePipeHandle pipe,
            out uint serverProcessId);
    }
}
'@ | Out-Null
}

function Resolve-InstallerPathIdentity {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    return [GeoraePlanInstaller.NativePathIdentity]::Resolve(`$Path)
}

function New-WorkerStartPipeServer {
    param([Parameter(Mandatory = `$true)][string]`$Name)

    `$pipeSecurity = [System.IO.Pipes.PipeSecurity]::new()
    `$currentUserSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    if (`$null -eq `$currentUserSid) {
        throw 'worker start pipe 현재 사용자 SID를 확인하지 못했습니다.'
    }
    `$pipeRule = [System.IO.Pipes.PipeAccessRule]::new(
        `$currentUserSid,
        [System.IO.Pipes.PipeAccessRights]::ReadWrite,
        [Security.AccessControl.AccessControlType]::Allow)
    `$pipeSecurity.SetAccessRuleProtection(`$true, `$false)
    [void]`$pipeSecurity.AddAccessRule(`$pipeRule)
    return [System.IO.Pipes.NamedPipeServerStream]::new(
        `$Name,
        [System.IO.Pipes.PipeDirection]::InOut,
        1,
        [System.IO.Pipes.PipeTransmissionMode]::Byte,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        4096,
        4096,
        `$pipeSecurity)
}

function Wait-AndAuthorizeWorkerStart {
    param(
        [Parameter(Mandatory = `$true)]
        [System.IO.Pipes.NamedPipeServerStream]`$Pipe,
        [Parameter(Mandatory = `$true)]
        [System.Diagnostics.Process]`$Worker,
        [Parameter(Mandatory = `$true)][string]`$Token
    )

    `$connectTask = `$Pipe.WaitForConnectionAsync()
    if (-not `$connectTask.Wait([TimeSpan]::FromSeconds(30))) {
        throw 'worker start pipe 연결 시간이 초과되었습니다.'
    }
    `$connectTask.GetAwaiter().GetResult()
    `$clientProcessId =
        [GeoraePlanInstaller.WorkerJob]::GetPipeClientProcessId(
            `$Pipe.SafePipeHandle)
    if (`$clientProcessId -ne [uint32]`$Worker.Id) {
        throw (
            'worker start pipe client PID가 시작한 worker와 다릅니다. ' +
            "Expected=`$(`$Worker.Id), Actual=`$clientProcessId")
    }

    `$reader = [System.IO.StreamReader]::new(
        `$Pipe,
        [System.Text.Encoding]::UTF8,
        `$false,
        1024,
        `$true)
    `$writer = [System.IO.StreamWriter]::new(
        `$Pipe,
        [System.Text.UTF8Encoding]::new(`$false),
        1024,
        `$true)
    try {
        `$receivedToken = `$reader.ReadLine()
        if (-not [string]::Equals(
                `$receivedToken,
                `$Token,
                [System.StringComparison]::Ordinal)) {
            throw 'worker start pipe token이 일치하지 않습니다.'
        }
        `$writer.WriteLine('GO:' + `$Token)
        `$writer.Flush()
    }
    finally {
        try {
            `$reader.Dispose()
        }
        catch {
            # Preserve the handshake failure that triggered cleanup.
        }
        try {
            `$writer.Dispose()
        }
        catch {
            # Explicit Flush above is authoritative for GO delivery.
        }
    }
}

function Confirm-WorkerStartAuthorization {
    if ([string]::IsNullOrWhiteSpace(`$WorkerStartPipeName) -or
        [string]::IsNullOrWhiteSpace(`$WorkerStartToken) -or
        `$WorkerStartServerProcessId -le 0) {
        throw 'WorkerMode start pipe identity가 완전하지 않습니다.'
    }

    `$client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        `$WorkerStartPipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None)
    try {
        `$client.Connect(30000)
        `$serverProcessId =
            [GeoraePlanInstaller.WorkerJob]::GetPipeServerProcessId(
                `$client.SafePipeHandle)
        if (`$serverProcessId -ne
            [uint32]`$WorkerStartServerProcessId) {
            throw (
                'worker start pipe server PID가 예상 supervisor와 다릅니다. ' +
                "Expected=`$WorkerStartServerProcessId, Actual=`$serverProcessId")
        }
        `$writer = [System.IO.StreamWriter]::new(
            `$client,
            [System.Text.UTF8Encoding]::new(`$false),
            1024,
            `$true)
        `$reader = [System.IO.StreamReader]::new(
            `$client,
            [System.Text.Encoding]::UTF8,
            `$false,
            1024,
            `$true)
        try {
            `$writer.WriteLine(`$WorkerStartToken)
            `$writer.Flush()
            `$response = `$reader.ReadLine()
            if (-not [string]::Equals(
                    `$response,
                    'GO:' + `$WorkerStartToken,
                    [System.StringComparison]::Ordinal)) {
                throw 'worker start authorization 응답이 올바르지 않습니다.'
            }
        }
        finally {
            try {
                `$writer.Dispose()
            }
            catch {
                # Preserve the authorization failure that triggered cleanup.
            }
            try {
                `$reader.Dispose()
            }
            catch {
                # Pipe teardown must not mask the authorization result.
            }
        }
    }
    finally {
        `$client.Dispose()
    }
}

`$InstallerScriptPath =
    Resolve-InstallerPathIdentity -Path `$InstallerScriptPath

if (`$WorkerMode -and `$RecoveryOnly) {
    throw 'RecoveryOnly 모드는 supervisor에서만 사용할 수 있습니다.'
}
if (`$WorkerMode -and
    `$UpdaterOwnsInstallRootGate -and
    `$BootstrapperOwnsInstallRootGate) {
    throw 'install-root gate 외부 소유자 모드는 동시에 사용할 수 없습니다.'
}
if (`$WorkerMode -and
    -not `$UpdaterOwnsInstallRootGate -and
    -not `$BootstrapperOwnsInstallRootGate) {
    throw 'WorkerMode에는 검증 가능한 install-root gate 소유자 identity가 필요합니다.'
}
if (`$WorkerMode -and
    ([string]::IsNullOrWhiteSpace(`$WorkerStartPipeName) -or
     [string]::IsNullOrWhiteSpace(`$WorkerStartToken) -or
     `$WorkerStartServerProcessId -le 0)) {
    throw 'WorkerMode에는 supervisor-bound start pipe identity가 필요합니다.'
}

function Get-VerifiedOriginProcessUserSid {
    param(
        [Parameter(Mandatory = `$true)][int]`$ProcessId,
        [Parameter(Mandatory = `$true)][string]`$ProcessPath,
        [Parameter(Mandatory = `$true)][long]`$ProcessStartTimeUtcTicks
    )

    `$originProcess =
        Get-Process -Id `$ProcessId -ErrorAction Stop
    try {
        `$actualProcessPath =
            Resolve-InstallerPathIdentity -Path (
                `$originProcess.MainModule.FileName)
        `$expectedProcessPath =
            Resolve-InstallerPathIdentity -Path `$ProcessPath
        `$actualStartTimeUtcTicks =
            `$originProcess.StartTime.ToUniversalTime().Ticks
        if (-not [string]::Equals(
                `$actualProcessPath,
                `$expectedProcessPath,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            `$actualStartTimeUtcTicks -ne
                `$ProcessStartTimeUtcTicks) {
            throw 'origin user process identity가 현재 프로세스와 일치하지 않습니다.'
        }
        return [GeoraePlanInstaller.ProcessTokenIdentity]::GetUserSid(
            `$originProcess)
    }
    finally {
        if (`$originProcess -is [System.IDisposable]) {
            `$originProcess.Dispose()
        }
    }
}

`$hasExplicitOriginIdentity =
    `$OriginUserProcessId -gt 0 -or
    -not [string]::IsNullOrWhiteSpace(`$OriginUserProcessPath) -or
    `$OriginUserProcessStartTimeUtcTicks -gt 0
if (`$hasExplicitOriginIdentity -and
    (`$OriginUserProcessId -le 0 -or
     [string]::IsNullOrWhiteSpace(`$OriginUserProcessPath) -or
     `$OriginUserProcessStartTimeUtcTicks -le 0)) {
    throw 'origin user process identity가 완전하지 않습니다.'
}
if (-not `$hasExplicitOriginIdentity) {
    if (`$UpdaterOwnsInstallRootGate -or
        `$BootstrapperOwnsInstallRootGate) {
        `$OriginUserProcessId =
            `$InstallRootGateOwnerProcessId
        `$OriginUserProcessPath =
            `$InstallRootGateOwnerProcessPath
        `$OriginUserProcessStartTimeUtcTicks =
            `$InstallRootGateOwnerProcessStartTimeUtcTicks
    }
    else {
        `$originCurrentProcess =
            Get-Process -Id `$PID -ErrorAction Stop
        try {
            `$OriginUserProcessId = `$PID
            `$OriginUserProcessPath =
                `$originCurrentProcess.MainModule.FileName
            `$OriginUserProcessStartTimeUtcTicks =
                `$originCurrentProcess.StartTime.ToUniversalTime().Ticks
        }
        finally {
            if (`$originCurrentProcess -is [System.IDisposable]) {
                `$originCurrentProcess.Dispose()
            }
        }
    }
}
if (-not `$WorkerMode -and
    (`$UpdaterOwnsInstallRootGate -or
     `$BootstrapperOwnsInstallRootGate) -and
    (`$OriginUserProcessId -ne `$InstallRootGateOwnerProcessId -or
     -not [string]::Equals(
        (Resolve-InstallerPathIdentity -Path `$OriginUserProcessPath),
        (Resolve-InstallerPathIdentity -Path `$InstallRootGateOwnerProcessPath),
        [System.StringComparison]::OrdinalIgnoreCase) -or
     `$OriginUserProcessStartTimeUtcTicks -ne
        `$InstallRootGateOwnerProcessStartTimeUtcTicks)) {
    throw 'origin user identity는 검증 대상 install-root gate owner와 일치해야 합니다.'
}
`$script:OriginUserSid =
    Get-VerifiedOriginProcessUserSid -ProcessId `$OriginUserProcessId -ProcessPath `$OriginUserProcessPath -ProcessStartTimeUtcTicks `$OriginUserProcessStartTimeUtcTicks
`$currentUserIdentity =
    [Security.Principal.WindowsIdentity]::GetCurrent()
if (`$null -eq `$currentUserIdentity.User) {
    throw '현재 installer token의 user SID를 확인하지 못했습니다.'
}
`$script:CurrentUserSid =
    `$currentUserIdentity.User.Value
function Test-SameInstallerUserSid {
    param(
        [Parameter(Mandatory = `$true)][string]`$OriginSid,
        [Parameter(Mandatory = `$true)][string]`$CurrentSid
    )

    return [string]::Equals(
        `$OriginSid,
        `$CurrentSid,
        [System.StringComparison]::Ordinal)
}
if (-not (Test-SameInstallerUserSid -OriginSid `$script:OriginUserSid -CurrentSid `$script:CurrentUserSid)) {
    throw (
        '설치를 시작한 Windows 사용자와 현재 권한 사용자가 다릅니다. ' +
        '다른 관리자 자격 증명으로는 설치/복구를 진행할 수 없습니다.')
}

`$programFilesRoot = [Environment]::GetFolderPath('ProgramFilesX86')
if ([string]::IsNullOrWhiteSpace(`$programFilesRoot)) {
    `$programFilesRoot = [Environment]::GetFolderPath('ProgramFiles')
}

`$CanonicalInstallRoot = Join-Path `$programFilesRoot 'tradeplan'
`$LegacyUserRoot = Join-Path `$env:LOCALAPPDATA 'Programs\__APP_DISPLAY_NAME__'
`$CanonicalInstallRoot =
    Resolve-InstallerPathIdentity -Path `$CanonicalInstallRoot
`$LegacyUserRoot =
    Resolve-InstallerPathIdentity -Path `$LegacyUserRoot
if ([string]::IsNullOrWhiteSpace(`$InstallRoot)) {
    `$InstallRoot = `$CanonicalInstallRoot
}

`$requestedInstallRoot =
    Resolve-InstallerPathIdentity -Path `$InstallRoot
`$useLegacyBridgeCopy = `$LegacyBridgeCopy.IsPresent
if (-not [string]::IsNullOrWhiteSpace(`$requestedInstallRoot)) {
    if ([string]::Equals(`$requestedInstallRoot, `$LegacyUserRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        `$useLegacyBridgeCopy = `$true
        `$InstallRoot = `$CanonicalInstallRoot
    }
    else {
        `$InstallRoot = `$requestedInstallRoot
    }
}
if (`$useLegacyBridgeCopy) {
    `$effectiveInstallRoot =
        Resolve-InstallerPathIdentity -Path `$InstallRoot
    if (-not [string]::Equals(`$effectiveInstallRoot, `$CanonicalInstallRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "LegacyBridgeCopy는 canonical 설치 경로에만 사용할 수 있습니다: `$effectiveInstallRoot"
    }
    `$InstallRoot = `$CanonicalInstallRoot
}
function Test-InstallerPathsOverlap {
    param(
        [Parameter(Mandatory = `$true)][string]`$Left,
        [Parameter(Mandatory = `$true)][string]`$Right
    )

    `$leftPath = Resolve-InstallerPathIdentity -Path `$Left
    `$rightPath = Resolve-InstallerPathIdentity -Path `$Right
    if ([string]::Equals(
            `$leftPath,
            `$rightPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return `$true
    }

    `$leftPrefix = `$leftPath + [System.IO.Path]::DirectorySeparatorChar
    `$rightPrefix = `$rightPath + [System.IO.Path]::DirectorySeparatorChar
    return `$leftPath.StartsWith(
            `$rightPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        `$rightPath.StartsWith(
            `$leftPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)
}
`$managesCanonicalAndLegacyRoots = [string]::Equals(
    (Resolve-InstallerPathIdentity -Path `$InstallRoot),
    `$CanonicalInstallRoot,
    [System.StringComparison]::OrdinalIgnoreCase)
if (-not `$managesCanonicalAndLegacyRoots -and
    ((Test-InstallerPathsOverlap -Left `$InstallRoot -Right `$CanonicalInstallRoot) -or
     (Test-InstallerPathsOverlap -Left `$InstallRoot -Right `$LegacyUserRoot))) {
    throw 'custom 설치 경로는 canonical/legacy 설치 경로와 조상 또는 자손 관계일 수 없습니다.'
}
`$script:ActiveSupervisorInstallRoot = `$InstallRoot

function Assert-NoReparsePoints {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    # Resolution rejects reparse points on every existing ancestor even when
    # the requested leaf does not exist yet.
    `$fullPath = Resolve-InstallerPathIdentity -Path `$Path
    if (-not (Test-SupervisorPathExistsFailClosed -Path `$fullPath)) {
        return
    }

    for (`$current = Get-Item -LiteralPath `$fullPath -Force; `$null -ne `$current; `$current = `$current.Parent) {
        if ((`$current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "rollback source 경로가 재분석 지점을 통과합니다: `$fullPath"
        }
    }

    `$rootItem = Get-Item -LiteralPath `$fullPath -Force -ErrorAction Stop
    if (-not `$rootItem.PSIsContainer) {
        return
    }

    `$directories = [System.Collections.Generic.Stack[string]]::new()
    `$directories.Push(`$fullPath)
    while (`$directories.Count -gt 0) {
        `$directoryPath = `$directories.Pop()
        foreach (`$entry in Get-ChildItem -LiteralPath `$directoryPath -Force -ErrorAction Stop) {
            if ((`$entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "rollback source에 재분석 지점이 포함되어 있습니다: `$(`$entry.FullName)"
            }
            if (`$entry.PSIsContainer) {
                `$directories.Push(`$entry.FullName)
            }
        }
    }
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = `$true)][string]`$Source,
        [Parameter(Mandatory = `$true)][string]`$Destination,
        [int]`$RetryCount = 5,
        [int]`$RetryDelaySeconds = 2
    )

    `$sourcePath = Resolve-InstallerPathIdentity -Path `$Source
    `$destinationPath =
        Resolve-InstallerPathIdentity -Path `$Destination
    Assert-NoReparsePoints -Path `$sourcePath
    if (Test-SupervisorPathExistsFailClosed -Path `$destinationPath) {
        Assert-NoReparsePoints -Path `$destinationPath
    }
    New-Item -ItemType Directory -Force -Path `$destinationPath | Out-Null

    # Re-resolve after creation and immediately before the external mutator.
    `$sourcePath = Resolve-InstallerPathIdentity -Path `$sourcePath
    `$destinationPath =
        Resolve-InstallerPathIdentity -Path `$destinationPath
    Assert-NoReparsePoints -Path `$sourcePath
    Assert-NoReparsePoints -Path `$destinationPath

    for (`$attempt = 1; `$attempt -le `$RetryCount; `$attempt++) {
        Assert-NoReparsePoints -Path `$sourcePath
        Assert-NoReparsePoints -Path `$destinationPath
        `$output = & robocopy `$sourcePath `$destinationPath /MIR /COPY:DAT /DCOPY:DAT /XJ /R:2 /W:1 /NFL /NDL /NJH /NJS /NP 2>&1
        `$exitCode = `$LASTEXITCODE

        if (`$exitCode -lt 8) {
            return
        }

        Write-InstallLog ("robocopy {0}/{1} 실패 ({2}): {3} -> {4}" -f `$attempt, `$RetryCount, `$exitCode, `$sourcePath, `$destinationPath)
        foreach (`$line in @(`$output)) {
            if (`$null -ne `$line -and -not [string]::IsNullOrWhiteSpace(`$line.ToString())) {
                Write-InstallLog ("robocopy> {0}" -f `$line.ToString().TrimEnd())
            }
        }

        if (`$attempt -lt `$RetryCount) {
            Write-InstallLog ("파일 잠금 해제를 기다린 뒤 {0}초 후 재시도합니다." -f `$RetryDelaySeconds)
            Start-Sleep -Seconds `$RetryDelaySeconds
        }
        else {
            throw "robocopy failed (`$exitCode): `$sourcePath -> `$destinationPath"
        }
    }
}

function Get-DirectorySizeBytes {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    if (-not (Test-SupervisorPathExistsFailClosed -Path `$Path)) {
        return 0L
    }

    return (Get-ChildItem -LiteralPath `$Path -File -Recurse | Measure-Object -Property Length -Sum).Sum
}

function Get-AvailableFreeBytes {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$root = [System.IO.Path]::GetPathRoot(
        (Resolve-InstallerPathIdentity -Path `$Path))
    if ([string]::IsNullOrWhiteSpace(`$root)) {
        throw "Drive root not found: `$Path"
    }

    return ([System.IO.DriveInfo]::new(`$root)).AvailableFreeSpace
}

function Format-Size {
    param([long]`$Bytes)

    `$units = @('B','KB','MB','GB','TB')
    `$value = [double]`$Bytes
    `$unitIndex = 0
    while (`$value -ge 1024 -and `$unitIndex -lt (`$units.Length - 1)) {
        `$value /= 1024
        `$unitIndex++
    }

    return ('{0:0.##} {1}' -f `$value, `$units[`$unitIndex])
}

function Normalize-VersionText {
    param([string]`$Value)

    if (`$null -eq `$Value) {
        `$normalized = ''
    }
    else {
        `$normalized = `$Value.Trim()
    }

    if (`$normalized.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        `$normalized = `$normalized.Substring(1)
    }

    `$plusIndex = `$normalized.IndexOf('+')
    if (`$plusIndex -ge 0) {
        `$normalized = `$normalized.Substring(0, `$plusIndex)
    }

    return `$normalized
}

function Compare-Version {
    param(
        [string]`$Left,
        [string]`$Right
    )

    `$leftVersion = [Version]'0.0.0'
    `$rightVersion = [Version]'0.0.0'

    [Version]::TryParse((Normalize-VersionText `$Left), [ref]`$leftVersion) | Out-Null
    [Version]::TryParse((Normalize-VersionText `$Right), [ref]`$rightVersion) | Out-Null

    return `$leftVersion.CompareTo(`$rightVersion)
}

function Show-InstallError {
    param([Parameter(Mandatory = `$true)][string]`$Message)

    if (`$SuppressUi) {
        Write-InstallLog ("설치 오류 UI 생략: {0}" -f `$Message)
        return
    }

    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(`$Message, '__APP_DISPLAY_NAME__ 설치', 'OK', 'Error') | Out-Null
}

function Write-InstallLog {
    param([Parameter(Mandatory = `$true)][string]`$Message)

    `$line = ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), `$Message)
    Write-Host `$line

    if (-not [string]::IsNullOrWhiteSpace(`$LogPath)) {
        try {
            Add-Content -LiteralPath `$LogPath -Value `$line -Encoding UTF8
        }
        catch {
            # ignore logging failures
        }
    }
}

function Test-ProtectedInstallRoot {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$fullPath = Resolve-InstallerPathIdentity -Path `$Path
    `$protectedRoots = @(
        [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86'),
        [Environment]::GetFolderPath('Windows')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace(`$_) } | ForEach-Object {
        Resolve-InstallerPathIdentity -Path `$_
    }

    foreach (`$root in `$protectedRoots) {
        if ([string]::Equals(
                `$fullPath,
                `$root,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            `$fullPath.StartsWith(
                `$root + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return `$true
        }
    }

    return `$false
}

function Get-ActiveInstallerTestHooks {
    `$environmentVariables =
        [Environment]::GetEnvironmentVariables()
    foreach (`$nameObject in `$environmentVariables.Keys) {
        `$name = [string]`$nameObject
        if (`$name.StartsWith(
                'GEORAEPLAN_INSTALLER_TEST_',
                [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals(
                `$name,
                'GEORAEPLAN_INSTALLER_TEST_CAPABILITY',
                [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::IsNullOrWhiteSpace(
                [string]`$environmentVariables[`$nameObject])) {
            `$name
        }
    }
}

function Assert-InstallerTestHooksAllowed {
    `$activeHooks = @(
        Get-ActiveInstallerTestHooks
    )
    if (`$activeHooks.Count -eq 0) {
        return
    }

    if (Test-ProtectedInstallRoot -Path `$InstallRoot) {
        throw (
            '보호된 설치 경로에서는 installer test hook을 사용할 수 없습니다: ' +
            (`$activeHooks -join ', '))
    }
    if (-not `$script:InstallerTestHooksEnabled) {
        throw (
            'production installer package에서는 test hook이 비활성화됩니다: ' +
            (`$activeHooks -join ', '))
    }

    `$packageRoot = Split-Path -Parent `$InstallerScriptPath
    `$capabilityMarkerPath = Join-Path `$packageRoot `$script:InstallerTestCapabilityMarkerName
    Assert-SupervisorChildPath -Candidate `$capabilityMarkerPath -Parent `$packageRoot
    if (-not (Test-SupervisorPathExistsFailClosed -Path `$capabilityMarkerPath)) {
        throw 'installer test capability marker가 없습니다.'
    }
    Assert-NoReparsePoints -Path `$capabilityMarkerPath
    `$markerAttributes =
        [System.IO.File]::GetAttributes(`$capabilityMarkerPath)
    if ((`$markerAttributes -band
            [System.IO.FileAttributes]::Directory) -ne 0 -or
        (`$markerAttributes -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'installer test capability marker 형식이 올바르지 않습니다.'
    }

    `$expectedCapability =
        [System.IO.File]::ReadAllText(
            `$capabilityMarkerPath,
            [System.Text.Encoding]::UTF8)
    if (`$expectedCapability -notmatch '^[A-F0-9]{64}$' -or
        -not [string]::Equals(
            `$expectedCapability,
            [string]`$env:GEORAEPLAN_INSTALLER_TEST_CAPABILITY,
            [System.StringComparison]::Ordinal)) {
        throw 'installer test capability nonce가 일치하지 않습니다.'
    }
}

function Get-InstallRootGateMutexName {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$normalizedRoot =
        Resolve-InstallerPathIdentity -Path `$Path
    `$normalizedRoot = `$normalizedRoot.Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar).ToUpperInvariant()
    `$sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        `$hashBytes = `$sha.ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes(`$normalizedRoot))
    }
    finally {
        `$sha.Dispose()
    }
    `$hash = -join (`$hashBytes | ForEach-Object { `$_.ToString('X2') })
    return 'Global\GeoraePlan.Updater.InstallRoot.' + `$hash
}

function Get-InstallOperationLeaseMutexName {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$rootGateName = Get-InstallRootGateMutexName -Path `$Path
    return `$rootGateName.Replace(
        'Global\GeoraePlan.Updater.InstallRoot.',
        'Global\GeoraePlan.Updater.InstallRootLease.')
}

function Get-InstallWorkerLeaseMutexName {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$rootGateName = Get-InstallRootGateMutexName -Path `$Path
    return `$rootGateName.Replace(
        'Global\GeoraePlan.Updater.InstallRoot.',
        'Global\GeoraePlan.Updater.InstallRootWorkerLease.')
}

function Get-InstallRootGateEntries {
    `$uniqueRoots =
        [System.Collections.Generic.Dictionary[string,string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
    `$candidateRoots = @(`$InstallRoot)
    if (`$managesCanonicalAndLegacyRoots) {
        `$candidateRoots += `$LegacyUserRoot
    }
    foreach (`$candidateRoot in `$candidateRoots) {
        `$normalizedRoot =
            Resolve-InstallerPathIdentity -Path `$candidateRoot
        if (-not `$uniqueRoots.ContainsKey(`$normalizedRoot)) {
            `$uniqueRoots.Add(`$normalizedRoot, `$normalizedRoot)
        }
    }

    return @(
        `$uniqueRoots.Values |
            ForEach-Object {
                [pscustomobject]@{
                    InstallRoot = `$_
                    MutexName = Get-InstallRootGateMutexName -Path `$_
                    OperationLeaseMutexName =
                        Get-InstallOperationLeaseMutexName -Path `$_
                    WorkerLeaseMutexName =
                        Get-InstallWorkerLeaseMutexName -Path `$_
                }
            } |
            Sort-Object -Property MutexName
    )
}

function Assert-InstallRootGateOwnerIdentity {
    if (`$UpdaterOwnsInstallRootGate -and `$BootstrapperOwnsInstallRootGate) {
        throw 'install-root gate 외부 소유자 모드는 동시에 사용할 수 없습니다.'
    }
    if (-not `$UpdaterOwnsInstallRootGate -and
        -not `$BootstrapperOwnsInstallRootGate) {
        return
    }
    if (`$InstallRootGateOwnerProcessId -le 0 -or
        [string]::IsNullOrWhiteSpace(`$InstallRootGateOwnerProcessPath) -or
        `$InstallRootGateOwnerProcessStartTimeUtcTicks -le 0) {
        throw 'install-root gate 외부 소유자 identity가 완전하지 않습니다.'
    }

    `$ownerProcess = Get-Process -Id `$InstallRootGateOwnerProcessId -ErrorAction Stop
    try {
        `$actualProcessPath =
            Resolve-InstallerPathIdentity -Path (
                `$ownerProcess.MainModule.FileName)
        `$expectedProcessPath =
            Resolve-InstallerPathIdentity -Path (
                `$InstallRootGateOwnerProcessPath)
        `$actualStartTimeUtcTicks =
            `$ownerProcess.StartTime.ToUniversalTime().Ticks
        if (-not [string]::Equals(
                `$actualProcessPath,
                `$expectedProcessPath,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            `$actualStartTimeUtcTicks -ne
                `$InstallRootGateOwnerProcessStartTimeUtcTicks) {
            throw 'install-root gate 외부 소유자 identity가 현재 프로세스와 일치하지 않습니다.'
        }

        if (`$UpdaterOwnsInstallRootGate) {
            `$actualUpdaterFileName =
                [System.IO.Path]::GetFileName(`$actualProcessPath)
            `$expectedUpdaterFileNames = @(
                '__APP_DISPLAY_NAME__.Updater.exe',
                '거래플랜.Updater.exe'
            )
            `$matchesExpectedUpdaterName = `$false
            foreach (`$expectedUpdaterFileName in
                `$expectedUpdaterFileNames) {
                if ([string]::Equals(
                        `$actualUpdaterFileName,
                        `$expectedUpdaterFileName,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    `$matchesExpectedUpdaterName = `$true
                    break
                }
            }
            if (-not `$matchesExpectedUpdaterName) {
                throw "UpdaterOwnsInstallRootGate owner는 검증된 updater 실행 파일이어야 합니다: `$actualProcessPath"
            }

            `$canonicalUpdaterPath = Join-Path (
                Split-Path -Parent `$actualProcessPath
            ) '거래플랜.Updater.exe'
            if (-not (Test-Path -LiteralPath `$canonicalUpdaterPath -PathType Leaf)) {
                throw "UpdaterOwnsInstallRootGate owner 옆에 canonical updater가 없습니다: `$canonicalUpdaterPath"
            }
            `$actualUpdaterHash = (Get-FileHash -LiteralPath `$actualProcessPath -Algorithm SHA256 -ErrorAction Stop).Hash
            `$canonicalUpdaterHash = (Get-FileHash -LiteralPath `$canonicalUpdaterPath -Algorithm SHA256 -ErrorAction Stop).Hash
            if (-not [string]::Equals(
                    `$actualUpdaterHash,
                    `$canonicalUpdaterHash,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'UpdaterOwnsInstallRootGate owner와 canonical updater의 SHA-256이 일치하지 않습니다.'
            }
        }
        else {
            `$bootstrapperFileName =
                [System.IO.Path]::GetFileName(`$actualProcessPath)
            if (-not [string]::Equals(
                    `$bootstrapperFileName,
                    'powershell.exe',
                    [System.StringComparison]::OrdinalIgnoreCase) -and
                -not [string]::Equals(
                    `$bootstrapperFileName,
                    'pwsh.exe',
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "BootstrapperOwnsInstallRootGate owner는 PowerShell installer bootstrapper여야 합니다: `$actualProcessPath"
            }

            `$ownerProcessRecord =
                Get-CimInstance -ClassName Win32_Process -Filter (
                    'ProcessId = {0}' -f
                    `$InstallRootGateOwnerProcessId) -ErrorAction Stop
            `$ownerCommandLine = `$ownerProcessRecord.CommandLine
            if ([string]::IsNullOrWhiteSpace(`$ownerCommandLine) -or
                `$ownerCommandLine.IndexOf(
                    `$InstallerScriptPath,
                    [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw 'BootstrapperOwnsInstallRootGate owner command line이 동일 installer script에 결합되지 않았습니다.'
            }
        }
    }
    finally {
        if (`$ownerProcess -is [System.IDisposable]) {
            `$ownerProcess.Dispose()
        }
    }

    foreach (`$gateEntry in @(Get-InstallRootGateEntries)) {
        `$gateProbe = [System.Threading.Mutex]::new(
            `$false,
            `$gateEntry.MutexName)
        `$probeAcquired = `$false
        try {
            try {
                `$probeAcquired = `$gateProbe.WaitOne(0)
            }
            catch [System.Threading.AbandonedMutexException] {
                `$probeAcquired = `$true
            }
            if (`$probeAcquired) {
                throw (
                    '지정된 외부 프로세스가 install-root gate를 보유하지 않습니다. InstallRoot=' +
                    `$gateEntry.InstallRoot)
            }
        }
        finally {
            if (`$probeAcquired) {
                try { `$gateProbe.ReleaseMutex() } catch {}
            }
            `$gateProbe.Dispose()
        }
    }
}

function Assert-InstallOperationLeasesHeld {
    foreach (`$gateEntry in @(Get-InstallRootGateEntries)) {
        `$leaseProbe = [System.Threading.Mutex]::new(
            `$false,
            `$gateEntry.OperationLeaseMutexName)
        `$probeAcquired = `$false
        try {
            try {
                `$probeAcquired = `$leaseProbe.WaitOne(0)
            }
            catch [System.Threading.AbandonedMutexException] {
                `$probeAcquired = `$true
            }
            if (`$probeAcquired) {
                throw (
                    'WorkerMode supervisor operation lease를 확인하지 못했습니다. InstallRoot=' +
                    `$gateEntry.InstallRoot)
            }
        }
        finally {
            if (`$probeAcquired) {
                try { `$leaseProbe.ReleaseMutex() } catch {}
            }
            `$leaseProbe.Dispose()
        }
    }
}

function Enter-InstallRootGates {
    `$heldGates = [System.Collections.Generic.List[object]]::new()
    try {
        foreach (`$gateEntry in @(Get-InstallRootGateEntries)) {
            `$mutex = [System.Threading.Mutex]::new(
                `$false,
                `$gateEntry.MutexName)
            `$ownsMutex = `$false
            try {
                try {
                    `$ownsMutex = `$mutex.WaitOne(0)
                }
                catch [System.Threading.AbandonedMutexException] {
                    `$ownsMutex = `$true
                }
                if (-not `$ownsMutex) {
                    throw (
                        '같은 설치 위치를 사용하는 거래플랜 앱, updater 또는 다른 installer가 실행 중입니다. ' +
                        '종료된 뒤 다시 시도해 주세요. InstallRoot=' +
                        `$gateEntry.InstallRoot)
                }
                [void]`$heldGates.Add(
                    [pscustomobject]@{
                        InstallRoot = `$gateEntry.InstallRoot
                        Mutex = `$mutex
                    })
            }
            catch {
                if (`$ownsMutex) {
                    try { `$mutex.ReleaseMutex() } catch {}
                }
                `$mutex.Dispose()
                throw
            }
        }

        return `$heldGates.ToArray()
    }
    catch {
        Exit-InstallRootGates -Gates `$heldGates.ToArray()
        throw
    }
}

function Enter-InstallOperationLeases {
    `$heldLeases = [System.Collections.Generic.List[object]]::new()
    try {
        foreach (`$gateEntry in @(
            Get-InstallRootGateEntries |
                Sort-Object -Property OperationLeaseMutexName
        )) {
            `$mutex = [System.Threading.Mutex]::new(
                `$false,
                `$gateEntry.OperationLeaseMutexName)
            `$ownsMutex = `$false
            try {
                try {
                    `$ownsMutex = `$mutex.WaitOne(0)
                }
                catch [System.Threading.AbandonedMutexException] {
                    `$ownsMutex = `$true
                }
                if (-not `$ownsMutex) {
                    throw (
                        '같은 설치 위치의 install operation이 아직 실행 중입니다. ' +
                        '종료된 뒤 다시 시도해 주세요. InstallRoot=' +
                        `$gateEntry.InstallRoot)
                }
                [void]`$heldLeases.Add(
                    [pscustomobject]@{
                        InstallRoot = `$gateEntry.InstallRoot
                        Mutex = `$mutex
                    })
            }
            catch {
                if (`$ownsMutex) {
                    try { `$mutex.ReleaseMutex() } catch {}
                }
                `$mutex.Dispose()
                throw
            }
        }

        return `$heldLeases.ToArray()
    }
    catch {
        Exit-InstallRootGates -Gates `$heldLeases.ToArray()
        throw
    }
}

function Enter-InstallWorkerLeases {
    `$heldLeases = [System.Collections.Generic.List[object]]::new()
    try {
        foreach (`$gateEntry in @(
            Get-InstallRootGateEntries |
                Sort-Object -Property WorkerLeaseMutexName
        )) {
            `$mutex = [System.Threading.Mutex]::new(
                `$false,
                `$gateEntry.WorkerLeaseMutexName)
            `$ownsMutex = `$false
            try {
                try {
                    `$ownsMutex = `$mutex.WaitOne(0)
                }
                catch [System.Threading.AbandonedMutexException] {
                    `$ownsMutex = `$true
                }
                if (-not `$ownsMutex) {
                    throw (
                        '같은 설치 위치의 installer worker가 아직 실행 중입니다. ' +
                        '종료된 뒤 다시 시도해 주세요. InstallRoot=' +
                        `$gateEntry.InstallRoot)
                }
                [void]`$heldLeases.Add(
                    [pscustomobject]@{
                        InstallRoot = `$gateEntry.InstallRoot
                        Mutex = `$mutex
                    })
            }
            catch {
                if (`$ownsMutex) {
                    try { `$mutex.ReleaseMutex() } catch {}
                }
                `$mutex.Dispose()
                throw
            }
        }

        return `$heldLeases.ToArray()
    }
    catch {
        Exit-InstallRootGates -Gates `$heldLeases.ToArray()
        throw
    }
}

function Exit-InstallRootGates {
    param([object[]]`$Gates)

    for (`$index = @(`$Gates).Count - 1; `$index -ge 0; `$index--) {
        `$gate = @(`$Gates)[`$index]
        if (`$null -eq `$gate -or `$null -eq `$gate.Mutex) {
            continue
        }
        `$mutex = `$gate.Mutex
        `$gate.Mutex = `$null
        try {
            `$mutex.ReleaseMutex()
        }
        catch [System.ApplicationException] {
            # The current thread no longer owns this abandoned gate.
        }
        finally {
            `$mutex.Dispose()
        }
    }
}

function Ensure-ElevatedIfNeeded {
    if (-not (Test-ProtectedInstallRoot -Path `$script:ActiveSupervisorInstallRoot)) {
        return [pscustomobject]@{
            Relaunched = `$false
            ExitCode = 0
        }
    }

    `$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    `$principal = [Security.Principal.WindowsPrincipal]::new(`$currentIdentity)
    if (`$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return [pscustomobject]@{
            Relaunched = `$false
            ExitCode = 0
        }
    }

    `$argumentParts = @(
        '-NoProfile',
        '-ExecutionPolicy Bypass',
        ('-File "{0}"' -f `$InstallerScriptPath),
        ('-InstallRoot "{0}"' -f `$InstallRoot),
        ('-WorkerTimeoutSeconds {0}' -f `$WorkerTimeoutSeconds),
        ('-OriginUserProcessId {0}' -f `$OriginUserProcessId),
        ('-OriginUserProcessPath "{0}"' -f `$OriginUserProcessPath),
        ('-OriginUserProcessStartTimeUtcTicks {0}' -f
            `$OriginUserProcessStartTimeUtcTicks),
        '-NoLaunch'
    )

    if (`$WorkerMode) {
        `$argumentParts += '-WorkerMode'
    }

    if (`$RecoveryOnly) {
        `$argumentParts += '-RecoveryOnly'
    }

    if (`$useLegacyBridgeCopy) {
        `$argumentParts += '-LegacyBridgeCopy'
    }

    if (`$UpdaterOwnsInstallRootGate -or
        `$BootstrapperOwnsInstallRootGate) {
        if (`$UpdaterOwnsInstallRootGate) {
            `$argumentParts += '-UpdaterOwnsInstallRootGate'
        }
        else {
            `$argumentParts += '-BootstrapperOwnsInstallRootGate'
        }
        `$argumentParts +=
            ('-InstallRootGateOwnerProcessId {0}' -f
                `$InstallRootGateOwnerProcessId)
        `$argumentParts +=
            ('-InstallRootGateOwnerProcessPath "{0}"' -f
                `$InstallRootGateOwnerProcessPath)
        `$argumentParts +=
            ('-InstallRootGateOwnerProcessStartTimeUtcTicks {0}' -f
                `$InstallRootGateOwnerProcessStartTimeUtcTicks)
    }
    else {
        `$currentProcess = Get-Process -Id `$PID -ErrorAction Stop
        try {
            `$currentProcessPath = `$currentProcess.MainModule.FileName
            `$currentProcessStartTimeUtcTicks =
                `$currentProcess.StartTime.ToUniversalTime().Ticks
        }
        finally {
            if (`$currentProcess -is [System.IDisposable]) {
                `$currentProcess.Dispose()
            }
        }
        `$argumentParts += '-BootstrapperOwnsInstallRootGate'
        `$argumentParts +=
            ('-InstallRootGateOwnerProcessId {0}' -f `$PID)
        `$argumentParts +=
            ('-InstallRootGateOwnerProcessPath "{0}"' -f
                `$currentProcessPath)
        `$argumentParts +=
            ('-InstallRootGateOwnerProcessStartTimeUtcTicks {0}' -f
                `$currentProcessStartTimeUtcTicks)
    }

    if (`$NoShortcuts) {
        `$argumentParts += '-NoShortcuts'
    }

    if (`$SuppressUi) {
        `$argumentParts += '-SuppressUi'
    }

    if (-not [string]::IsNullOrWhiteSpace(`$LogPath)) {
        `$argumentParts += ('-LogPath "{0}"' -f `$LogPath)
    }

    `$arguments = `$argumentParts -join ' '

    try {
        if (@(`$heldInstallWorkerLeases).Count -gt 0) {
            Exit-InstallRootGates -Gates `$heldInstallWorkerLeases
            `$script:heldInstallWorkerLeases = @()
        }
        if (@(`$heldInstallOperationLeases).Count -gt 0) {
            Exit-InstallRootGates -Gates `$heldInstallOperationLeases
            `$script:heldInstallOperationLeases = @()
        }
        Write-InstallLog '관리자 권한으로 설치를 다시 시작합니다.'
        `$elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList `$arguments -Verb RunAs -Wait -PassThru
    }
    catch {
        Show-InstallError '관리자 권한 승인이 취소되어 업데이트를 진행할 수 없습니다.'
        return [pscustomobject]@{
            Relaunched = `$true
            ExitCode = 1
        }
    }

    return [pscustomobject]@{
        Relaunched = `$true
        ExitCode = `$elevated.ExitCode
    }
}

function Ensure-SufficientInstallSpace {
    param([Parameter(Mandatory = `$true)][string]`$SourceRoot)

    `$requiredBytes = [Math]::Max(268435456, (Get-DirectorySizeBytes -Path `$SourceRoot) + 134217728)
    `$availableBytes = Get-AvailableFreeBytes -Path `$InstallRoot

    if (`$availableBytes -lt `$requiredBytes) {
        Show-InstallError ("설치 드라이브 여유 공간이 부족합니다.`r`n필요 공간: {0}`r`n현재 여유 공간: {1}" -f (Format-Size `$requiredBytes), (Format-Size `$availableBytes))
        exit 1
    }
}

function Get-NormalizedSupervisorPath {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    return Resolve-InstallerPathIdentity -Path `$Path
}

function Test-SameSupervisorPath {
    param(
        [Parameter(Mandatory = `$true)][string]`$Left,
        [Parameter(Mandatory = `$true)][string]`$Right
    )

    return [string]::Equals(
        (Get-NormalizedSupervisorPath -Path `$Left),
        (Get-NormalizedSupervisorPath -Path `$Right),
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-SupervisorChildPath {
    param(
        [Parameter(Mandatory = `$true)][string]`$Candidate,
        [Parameter(Mandatory = `$true)][string]`$Parent
    )

    `$candidatePath = Get-NormalizedSupervisorPath -Path `$Candidate
    `$parentPath = Get-NormalizedSupervisorPath -Path `$Parent
    `$prefix = `$parentPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not `$candidatePath.StartsWith(
            `$prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "supervisor 경로가 허용된 부모 밖을 가리킵니다. Candidate=`$candidatePath, Parent=`$parentPath"
    }
}

function Get-WorkerJobNamePrefix {
    param([Parameter(Mandatory = `$true)][string]`$StateRoot)

    `$normalizedStateRoot =
        Get-NormalizedSupervisorPath -Path `$StateRoot
    `$sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        `$hashBytes = `$sha.ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes(
                `$normalizedStateRoot.ToUpperInvariant()))
    }
    finally {
        `$sha.Dispose()
    }
    `$hash =
        -join (`$hashBytes | ForEach-Object {
            `$_.ToString('X2')
        })
    return 'Local\GeoraePlan.InstallerWorker.' + `$hash + '.'
}

function New-WorkerJobName {
    param([Parameter(Mandatory = `$true)][string]`$StateRoot)

    return (Get-WorkerJobNamePrefix -StateRoot `$StateRoot) +
        [Guid]::NewGuid().ToString('N')
}

function Assert-WorkerJobJournalBinding {
    param([Parameter(Mandatory = `$true)]`$Journal)

    if ([int]`$Journal.FormatVersion -lt 2) {
        return
    }

    `$jobName = [string]`$Journal.WorkerJobName
    `$jobPrefix =
        Get-WorkerJobNamePrefix -StateRoot `$Journal.StateRoot
    if ([string]::IsNullOrWhiteSpace(`$jobName) -or
        -not `$jobName.StartsWith(
            `$jobPrefix,
            [System.StringComparison]::Ordinal)) {
        throw 'supervisor journal worker job name binding이 올바르지 않습니다.'
    }
    `$jobNonceText = `$jobName.Substring(`$jobPrefix.Length)
    `$jobNonce = [Guid]::Empty
    if (-not [Guid]::TryParseExact(
            `$jobNonceText,
            'N',
            [ref]`$jobNonce)) {
        throw 'supervisor journal worker job nonce가 올바르지 않습니다.'
    }

    if ([string]`$Journal.Phase -eq 'WorkerRunning') {
        if ([int]`$Journal.WorkerProcessId -le 0 -or
            [string]::IsNullOrWhiteSpace(
                [string]`$Journal.WorkerProcessPath) -or
            [long]`$Journal.WorkerProcessStartTimeUtcTicks -le 0) {
            throw 'WorkerRunning journal의 worker identity가 완전하지 않습니다.'
        }
    }
}

function Complete-WorkerJobBarrierForJournal {
    param([Parameter(Mandatory = `$true)]`$Journal)

    if ([int]`$Journal.FormatVersion -lt 2) {
        Write-InstallLog 'legacy supervisor journal에는 worker job identity가 없어 기존 복구 계약을 적용합니다.'
        return
    }

    Assert-WorkerJobJournalBinding -Journal `$Journal
    `$workerJob =
        [GeoraePlanInstaller.WorkerJob]::OpenExisting(
            [string]`$Journal.WorkerJobName)
    if (`$null -eq `$workerJob) {
        Write-InstallLog 'durable worker job이 이미 소멸해 active process가 없습니다.'
        return
    }

    try {
        `$activeCount = `$workerJob.ActiveProcessCount
        if ([string]`$Journal.Phase -eq 'WorkerRunning') {
            `$journalWorker = Get-Process -Id ([int]`$Journal.WorkerProcessId) -ErrorAction SilentlyContinue
            if (`$null -ne `$journalWorker) {
                try {
                    `$actualWorkerPath =
                        Resolve-InstallerPathIdentity -Path (
                            `$journalWorker.MainModule.FileName)
                    `$expectedWorkerPath =
                        Resolve-InstallerPathIdentity -Path (
                            [string]`$Journal.WorkerProcessPath)
                    `$actualWorkerStartTicks =
                        `$journalWorker.StartTime.ToUniversalTime().Ticks
                    `$workerIdentityMatches = [string]::Equals(
                            `$actualWorkerPath,
                            `$expectedWorkerPath,
                            [System.StringComparison]::OrdinalIgnoreCase) -and
                        `$actualWorkerStartTicks -eq
                            [long]`$Journal.WorkerProcessStartTimeUtcTicks
                    `$jobContainsRecordedPid =
                        `$activeCount -gt 0 -and
                        `$workerJob.ContainsProcessId(
                            [int]`$Journal.WorkerProcessId)
                    if (`$workerIdentityMatches -ne
                        `$jobContainsRecordedPid) {
                        throw 'durable worker process identity가 journal과 일치하지 않습니다.'
                    }
                }
                finally {
                    `$journalWorker.Dispose()
                }
            }
        }

        `$workerJob.TerminateAndWait(254, 120000)
        if (`$workerJob.ActiveProcessCount -ne 0) {
            throw 'durable worker job active process barrier가 비어 있지 않습니다.'
        }
        Write-InstallLog 'durable worker job active process=0 barrier를 확인했습니다.'
    }
    finally {
        `$workerJob.Dispose()
    }
}

function Get-SupervisorStateRoot {
    param([Parameter(Mandatory = `$true)][string]`$InstallPath)

    `$fullInstallPath = Get-NormalizedSupervisorPath -Path `$InstallPath
    `$installParent = Split-Path -Parent `$fullInstallPath
    if ([string]::IsNullOrWhiteSpace(`$installParent)) {
        throw "설치 경로의 부모를 확인하지 못했습니다: `$fullInstallPath"
    }

    `$sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        `$hashBytes = `$sha.ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes(
                `$fullInstallPath.ToUpperInvariant()))
    }
    finally {
        `$sha.Dispose()
    }
    `$hash = -join (`$hashBytes | ForEach-Object { `$_.ToString('X2') })
    return Join-Path `$installParent ('.tradeplan-update-supervisor-state-' + `$hash)
}

function Get-SupervisorRelativePath {
    param(
        [Parameter(Mandatory = `$true)][string]`$Root,
        [Parameter(Mandatory = `$true)][string]`$Path
    )

    `$rootPath = Get-NormalizedSupervisorPath -Path `$Root
    `$fullPath = Resolve-InstallerPathIdentity -Path `$Path
    `$prefix = `$rootPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not `$fullPath.StartsWith(
            `$prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "manifest 경로가 root 밖을 가리킵니다: `$fullPath"
    }

    `$relativePath = `$fullPath.Substring(`$prefix.Length)
    if ([string]::IsNullOrWhiteSpace(`$relativePath) -or
        `$relativePath.Split([char[]]@('\', '/')) -contains '..') {
        throw "manifest 상대 경로가 안전하지 않습니다: `$relativePath"
    }

    return `$relativePath
}

function Get-FileSha256 {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$stream = [System.IO.FileStream]::new(
        `$Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    `$sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        `$hashBytes = `$sha.ComputeHash(`$stream)
        return -join (`$hashBytes | ForEach-Object { `$_.ToString('X2') })
    }
    finally {
        `$sha.Dispose()
        `$stream.Dispose()
    }
}

function Get-InstallTreeManifest {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$rootPath = Get-NormalizedSupervisorPath -Path `$Path
    if (-not (Test-SupervisorPathExistsFailClosed -Path `$rootPath)) {
        throw "manifest 대상 설치 경로를 찾지 못했습니다: `$rootPath"
    }
    `$rootAttributes = [System.IO.File]::GetAttributes(`$rootPath)
    if ((`$rootAttributes -band
        [System.IO.FileAttributes]::Directory) -eq 0) {
        throw "manifest 대상 경로가 디렉터리가 아닙니다: `$rootPath"
    }

    Assert-NoReparsePoints -Path `$rootPath
    `$files = New-Object System.Collections.Generic.List[object]
    `$directories = New-Object System.Collections.Generic.List[object]
    `$pending = New-Object System.Collections.Generic.Stack[string]
    `$pending.Push(`$rootPath)
    while (`$pending.Count -gt 0) {
        `$currentPath = `$pending.Pop()
        foreach (`$entry in Get-ChildItem -LiteralPath `$currentPath -Force) {
            if ((`$entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "manifest tree에 재분석 지점이 포함되어 있습니다: `$(`$entry.FullName)"
            }

            `$relativePath = Get-SupervisorRelativePath -Root `$rootPath -Path `$entry.FullName
            if (`$entry.PSIsContainer) {
                `$directories.Add([pscustomobject]@{
                    RelativePath = `$relativePath
                    Attributes = [int]`$entry.Attributes
                    CreationTimeUtcTicks = [long]`$entry.CreationTimeUtc.Ticks
                    LastWriteTimeUtcTicks = [long]`$entry.LastWriteTimeUtc.Ticks
                })
                `$pending.Push(`$entry.FullName)
            }
            else {
                `$files.Add([pscustomobject]@{
                    RelativePath = `$relativePath
                    Length = [long]`$entry.Length
                    Sha256 = Get-FileSha256 -Path `$entry.FullName
                    Attributes = [int]`$entry.Attributes
                    CreationTimeUtcTicks = [long]`$entry.CreationTimeUtc.Ticks
                    LastWriteTimeUtcTicks = [long]`$entry.LastWriteTimeUtc.Ticks
                })
            }
        }
    }

    `$rootItem = Get-Item -LiteralPath `$rootPath -Force
    return [pscustomobject]@{
        RootAttributes = [int]`$rootItem.Attributes
        RootCreationTimeUtcTicks = [long]`$rootItem.CreationTimeUtc.Ticks
        RootLastWriteTimeUtcTicks = [long]`$rootItem.LastWriteTimeUtc.Ticks
        Files = @(`$files.ToArray() | Sort-Object -Property RelativePath)
        Directories = @(`$directories.ToArray() | Sort-Object -Property RelativePath)
    }
}

function Set-InstallTreeManifestMetadata {
    param(
        [Parameter(Mandatory = `$true)][string]`$Path,
        [Parameter(Mandatory = `$true)]`$Manifest
    )

    `$rootPath = Get-NormalizedSupervisorPath -Path `$Path
    foreach (`$file in @(`$Manifest.Files)) {
        `$filePath = Resolve-InstallerPathIdentity -Path (
            (Join-Path `$rootPath ([string]`$file.RelativePath)))
        Assert-SupervisorChildPath -Candidate `$filePath -Parent `$rootPath
        `$currentAttributes = [System.IO.File]::GetAttributes(`$filePath)
        `$writableAttributes =
            `$currentAttributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
        if (`$writableAttributes -ne `$currentAttributes) {
            [System.IO.File]::SetAttributes(
                `$filePath,
                [System.IO.FileAttributes]`$writableAttributes)
        }
        try {
            if ([long]`$file.CreationTimeUtcTicks -gt 0) {
                [System.IO.File]::SetCreationTimeUtc(
                    `$filePath,
                    [DateTime]::new([long]`$file.CreationTimeUtcTicks, [DateTimeKind]::Utc))
            }
            [System.IO.File]::SetLastWriteTimeUtc(
                `$filePath,
                [DateTime]::new([long]`$file.LastWriteTimeUtcTicks, [DateTimeKind]::Utc))
        }
        finally {
            [System.IO.File]::SetAttributes(
                `$filePath,
                [System.IO.FileAttributes][int]`$file.Attributes)
        }
    }

    `$orderedDirectories = @(`$Manifest.Directories) |
        Sort-Object { ([string]`$_.RelativePath).Length } -Descending
    foreach (`$directory in `$orderedDirectories) {
        `$directoryPath = Resolve-InstallerPathIdentity -Path (
            (Join-Path `$rootPath ([string]`$directory.RelativePath)))
        Assert-SupervisorChildPath -Candidate `$directoryPath -Parent `$rootPath
        `$currentAttributes = [System.IO.File]::GetAttributes(`$directoryPath)
        `$writableAttributes =
            `$currentAttributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
        if (`$writableAttributes -ne `$currentAttributes) {
            [System.IO.File]::SetAttributes(
                `$directoryPath,
                [System.IO.FileAttributes]`$writableAttributes)
        }
        try {
            if ([long]`$directory.CreationTimeUtcTicks -gt 0) {
                [System.IO.Directory]::SetCreationTimeUtc(
                    `$directoryPath,
                    [DateTime]::new([long]`$directory.CreationTimeUtcTicks, [DateTimeKind]::Utc))
            }
            [System.IO.Directory]::SetLastWriteTimeUtc(
                `$directoryPath,
                [DateTime]::new([long]`$directory.LastWriteTimeUtcTicks, [DateTimeKind]::Utc))
        }
        finally {
            [System.IO.File]::SetAttributes(
                `$directoryPath,
                [System.IO.FileAttributes][int]`$directory.Attributes)
        }
    }

    `$currentRootAttributes = [System.IO.File]::GetAttributes(`$rootPath)
    `$writableRootAttributes =
        `$currentRootAttributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
    if (`$writableRootAttributes -ne `$currentRootAttributes) {
        [System.IO.File]::SetAttributes(
            `$rootPath,
            [System.IO.FileAttributes]`$writableRootAttributes)
    }
    try {
        if ([long]`$Manifest.RootCreationTimeUtcTicks -gt 0) {
            [System.IO.Directory]::SetCreationTimeUtc(
                `$rootPath,
                [DateTime]::new([long]`$Manifest.RootCreationTimeUtcTicks, [DateTimeKind]::Utc))
        }
        [System.IO.Directory]::SetLastWriteTimeUtc(
            `$rootPath,
            [DateTime]::new([long]`$Manifest.RootLastWriteTimeUtcTicks, [DateTimeKind]::Utc))
    }
    finally {
        [System.IO.File]::SetAttributes(
            `$rootPath,
            [System.IO.FileAttributes][int]`$Manifest.RootAttributes)
    }
}

function Assert-InstallTreeManifest {
    param(
        [Parameter(Mandatory = `$true)][string]`$Path,
        [Parameter(Mandatory = `$true)]`$Expected
    )

    `$actual = Get-InstallTreeManifest -Path `$Path
    if ([int]`$actual.RootAttributes -ne [int]`$Expected.RootAttributes -or
        (([long]`$Expected.RootCreationTimeUtcTicks -gt 0) -and
            [long]`$actual.RootCreationTimeUtcTicks -ne [long]`$Expected.RootCreationTimeUtcTicks) -or
        [long]`$actual.RootLastWriteTimeUtcTicks -ne [long]`$Expected.RootLastWriteTimeUtcTicks) {
        throw "rollback root metadata 검증에 실패했습니다: `$Path"
    }

    `$expectedFiles = @(`$Expected.Files)
    `$actualFiles = @(`$actual.Files)
    `$expectedDirectories = @(`$Expected.Directories)
    `$actualDirectories = @(`$actual.Directories)
    if (`$expectedFiles.Count -ne `$actualFiles.Count -or
        `$expectedDirectories.Count -ne `$actualDirectories.Count) {
        throw "rollback manifest entry 수가 일치하지 않습니다: `$Path"
    }

    for (`$index = 0; `$index -lt `$expectedFiles.Count; `$index++) {
        `$expectedFile = `$expectedFiles[`$index]
        `$actualFile = `$actualFiles[`$index]
        if (-not [string]::Equals(
                [string]`$actualFile.RelativePath,
                [string]`$expectedFile.RelativePath,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [long]`$actualFile.Length -ne [long]`$expectedFile.Length -or
            -not [string]::Equals(
                [string]`$actualFile.Sha256,
                [string]`$expectedFile.Sha256,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]`$actualFile.Attributes -ne [int]`$expectedFile.Attributes -or
            (([long]`$expectedFile.CreationTimeUtcTicks -gt 0) -and
                [long]`$actualFile.CreationTimeUtcTicks -ne [long]`$expectedFile.CreationTimeUtcTicks) -or
            [long]`$actualFile.LastWriteTimeUtcTicks -ne [long]`$expectedFile.LastWriteTimeUtcTicks) {
            throw "rollback file 검증에 실패했습니다: `$(`$expectedFile.RelativePath)"
        }
    }

    for (`$index = 0; `$index -lt `$expectedDirectories.Count; `$index++) {
        `$expectedDirectory = `$expectedDirectories[`$index]
        `$actualDirectory = `$actualDirectories[`$index]
        if (-not [string]::Equals(
                [string]`$actualDirectory.RelativePath,
                [string]`$expectedDirectory.RelativePath,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [int]`$actualDirectory.Attributes -ne [int]`$expectedDirectory.Attributes -or
            (([long]`$expectedDirectory.CreationTimeUtcTicks -gt 0) -and
                [long]`$actualDirectory.CreationTimeUtcTicks -ne [long]`$expectedDirectory.CreationTimeUtcTicks) -or
            [long]`$actualDirectory.LastWriteTimeUtcTicks -ne [long]`$expectedDirectory.LastWriteTimeUtcTicks) {
            throw "rollback directory 검증에 실패했습니다: `$(`$expectedDirectory.RelativePath)"
        }
    }
}

function Get-ProtectedSupervisorAllowedSids {
    return @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
    )
}

function Get-ProtectedSupervisorWriteMask {
    return (
        [long][System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [long][System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [long][System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [long][System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [long][System.Security.AccessControl.FileSystemRights]::Delete -bor
        [long][System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [long][System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [long][System.Security.AccessControl.FileSystemRights]::TakeOwnership -bor
        [long]0x10000000 -bor
        [long]0x40000000)
}

function Resolve-ProtectedSupervisorSid {
    param([Parameter(Mandatory = `$true)]`$Identity)

    if (`$Identity -is [System.Security.Principal.SecurityIdentifier]) {
        return `$Identity.Value
    }

    `$identityText = [string]`$Identity
    if (`$identityText.StartsWith('S-', [System.StringComparison]::OrdinalIgnoreCase)) {
        return ([System.Security.Principal.SecurityIdentifier]::new(`$identityText)).Value
    }

    if (`$Identity -is [System.Security.Principal.IdentityReference]) {
        return `$Identity.Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
    }

    return ([System.Security.Principal.NTAccount]::new(`$identityText)).Translate(
        [System.Security.Principal.SecurityIdentifier]).Value
}

function Assert-ProtectedSupervisorParentSecurity {
    param([Parameter(Mandatory = `$true)][string]`$ParentPath)

    if (-not (Test-ProtectedInstallRoot -Path `$script:ActiveSupervisorInstallRoot)) {
        return
    }

    `$fullParentPath = Get-NormalizedSupervisorPath -Path `$ParentPath
    for (`$current = Get-Item -LiteralPath `$fullParentPath -Force -ErrorAction Stop;
        `$null -ne `$current;
        `$current = `$current.Parent) {
        if ((`$current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "protected supervisor parent 경로가 재분석 지점을 통과합니다: `$fullParentPath"
        }
    }

    `$allowedSids = @(Get-ProtectedSupervisorAllowedSids)
    `$acl = Get-Acl -LiteralPath `$fullParentPath -ErrorAction Stop
    `$ownerSid = Resolve-ProtectedSupervisorSid -Identity `$acl.Owner
    if (`$allowedSids -notcontains `$ownerSid) {
        throw "protected supervisor parent owner가 허용되지 않습니다. Owner=`$ownerSid"
    }

    `$writeMask = Get-ProtectedSupervisorWriteMask
    foreach (`$rule in `$acl.Access) {
        `$ruleRights = [long][int]`$rule.FileSystemRights
        if (`$rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
            ((`$ruleRights -band `$writeMask) -eq 0)) {
            continue
        }

        `$sid = Resolve-ProtectedSupervisorSid -Identity `$rule.IdentityReference
        `$creatorOwnerInheritance =
            `$sid -eq 'S-1-3-0' -and
            ((`$rule.PropagationFlags -band
                [System.Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0)
        if (`$allowedSids -notcontains `$sid -and
            -not `$creatorOwnerInheritance) {
            throw "protected supervisor parent에 비관리자 쓰기 권한이 있습니다. SID=`$sid, Rights=`$(`$rule.FileSystemRights)"
        }
    }
}

function New-ProtectedSupervisorDirectorySecurity {
    `$acl = [System.Security.AccessControl.DirectorySecurity]::new()
    `$administratorSid =
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    `$acl.SetOwner(`$administratorSid)
    `$acl.SetAccessRuleProtection(`$true, `$false)
    `$inheritanceFlags =
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach (`$sidText in Get-ProtectedSupervisorAllowedSids) {
        `$sid = [System.Security.Principal.SecurityIdentifier]::new(`$sidText)
        `$rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            `$sid,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            `$inheritanceFlags,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]`$acl.AddAccessRule(`$rule)
    }

    return `$acl
}

function New-ProtectedSupervisorFileSecurity {
    `$acl = [System.Security.AccessControl.FileSecurity]::new()
    `$administratorSid =
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    `$acl.SetOwner(`$administratorSid)
    `$acl.SetAccessRuleProtection(`$true, `$false)
    foreach (`$sidText in Get-ProtectedSupervisorAllowedSids) {
        `$sid = [System.Security.Principal.SecurityIdentifier]::new(`$sidText)
        `$rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            `$sid,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]::None,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]`$acl.AddAccessRule(`$rule)
    }

    return `$acl
}

function New-SupervisorStateDirectory {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    if (-not (Test-ProtectedInstallRoot -Path `$script:ActiveSupervisorInstallRoot)) {
        New-Item -ItemType Directory -Path `$Path -ErrorAction Stop |
            Out-Null
        return
    }

    `$acl = New-ProtectedSupervisorDirectorySecurity
    [void][System.IO.Directory]::CreateDirectory(`$Path, `$acl)
}

function Set-ProtectedSupervisorObjectSecurity {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    if (-not (Test-ProtectedInstallRoot -Path `$script:ActiveSupervisorInstallRoot)) {
        return
    }

    `$item = Get-Item -LiteralPath `$Path -Force -ErrorAction Stop
    if ((`$item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "protected supervisor 객체는 재분석 지점일 수 없습니다: `$Path"
    }

    if (`$item.PSIsContainer) {
        `$acl = New-ProtectedSupervisorDirectorySecurity
    }
    else {
        `$acl = New-ProtectedSupervisorFileSecurity
    }
    Set-Acl -LiteralPath `$item.FullName -AclObject `$acl -ErrorAction Stop
}

function Set-ProtectedSupervisorTreeSecurity {
    param([Parameter(Mandatory = `$true)][string]`$StateRoot)

    if (-not (Test-ProtectedInstallRoot -Path `$script:ActiveSupervisorInstallRoot)) {
        return
    }

    `$pending = [System.Collections.Generic.Stack[string]]::new()
    `$pending.Push((Get-NormalizedSupervisorPath -Path `$StateRoot))
    while (`$pending.Count -gt 0) {
        `$currentPath = `$pending.Pop()
        Set-ProtectedSupervisorObjectSecurity -Path `$currentPath
        `$currentItem = Get-Item -LiteralPath `$currentPath -Force -ErrorAction Stop
        if (`$currentItem.PSIsContainer) {
            foreach (`$child in Get-ChildItem -LiteralPath `$currentPath -Force -ErrorAction Stop) {
                if ((`$child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "protected supervisor tree에 재분석 지점이 포함되어 있습니다: `$(`$child.FullName)"
                }
                `$pending.Push(`$child.FullName)
            }
        }
    }
}

function Assert-ProtectedSupervisorStateAcl {
    param([Parameter(Mandatory = `$true)][string]`$StateRoot)

    if (-not (Test-ProtectedInstallRoot -Path `$script:ActiveSupervisorInstallRoot)) {
        return
    }

    `$allowedSids = @(Get-ProtectedSupervisorAllowedSids)
    `$fullControlMask =
        [long][int][System.Security.AccessControl.FileSystemRights]::FullControl
    `$pending = [System.Collections.Generic.Stack[string]]::new()
    `$normalizedStateRoot = Get-NormalizedSupervisorPath -Path `$StateRoot
    `$pending.Push(`$normalizedStateRoot)
    while (`$pending.Count -gt 0) {
        `$currentPath = `$pending.Pop()
        `$item = Get-Item -LiteralPath `$currentPath -Force -ErrorAction Stop
        if ((`$item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "protected supervisor state에 재분석 지점이 포함되어 있습니다: `$currentPath"
        }

        `$acl = Get-Acl -LiteralPath `$currentPath -ErrorAction Stop
        if (Test-SameSupervisorPath -Left `$currentPath -Right `$normalizedStateRoot) {
            if (-not `$acl.AreAccessRulesProtected) {
                throw "protected supervisor state root의 ACL 상속이 차단되지 않았습니다: `$currentPath"
            }
        }

        `$ownerSid = Resolve-ProtectedSupervisorSid -Identity `$acl.Owner
        if (`$allowedSids -notcontains `$ownerSid) {
            throw "protected supervisor 객체 owner가 허용되지 않습니다. Path=`$currentPath, Owner=`$ownerSid"
        }

        `$fullControlSids =
            [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase)
        foreach (`$rule in `$acl.Access) {
            `$sid = Resolve-ProtectedSupervisorSid -Identity `$rule.IdentityReference
            if (`$rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
                `$allowedSids -notcontains `$sid) {
                throw "protected supervisor 객체 DACL 경계가 올바르지 않습니다. Path=`$currentPath, SID=`$sid, Type=`$(`$rule.AccessControlType)"
            }

            `$ruleRights = [long][int]`$rule.FileSystemRights
            if ((`$ruleRights -band `$fullControlMask) -eq `$fullControlMask) {
                [void]`$fullControlSids.Add(`$sid)
            }
        }
        foreach (`$allowedSid in `$allowedSids) {
            if (-not `$fullControlSids.Contains(`$allowedSid)) {
                throw "protected supervisor 객체에 필수 FullControl ACE가 없습니다. Path=`$currentPath, SID=`$allowedSid"
            }
        }

        if (`$item.PSIsContainer) {
            foreach (`$child in Get-ChildItem -LiteralPath `$currentPath -Force -ErrorAction Stop) {
                `$pending.Push(`$child.FullName)
            }
        }
    }
}

function Test-SupervisorPathExistsFailClosed {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    try {
        [void][System.IO.File]::GetAttributes(`$Path)
        return `$true
    }
    catch [System.IO.FileNotFoundException] {
        return `$false
    }
    catch [System.IO.DirectoryNotFoundException] {
        return `$false
    }
}

function Remove-OrphanedSupervisorJournalTemporaryFiles {
    param([Parameter(Mandatory = `$true)][string]`$StateRoot)

    `$normalizedStateRoot =
        Get-NormalizedSupervisorPath -Path `$StateRoot
    `$stateParent = Split-Path -Parent `$normalizedStateRoot
    Assert-SupervisorChildPath -Candidate `$normalizedStateRoot -Parent `$stateParent
    `$temporaryPrefix =
        '.' + [System.IO.Path]::GetFileName(`$normalizedStateRoot) +
        '.journal.'
    foreach (`$temporaryFile in @(
        Get-ChildItem -LiteralPath `$stateParent -File -Force -Filter (`$temporaryPrefix + '*.tmp') -ErrorAction Stop
    )) {
        if ((`$temporaryFile.Attributes -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "supervisor journal 임시 파일은 재분석 지점일 수 없습니다: `$(`$temporaryFile.FullName)"
        }
        Remove-Item -LiteralPath `$temporaryFile.FullName -Force -ErrorAction Stop
    }
}

function Assert-AbandonedSupervisorPreparationState {
    param([Parameter(Mandatory = `$true)][string]`$StateRoot)

    `$entries = @(
        Get-ChildItem -LiteralPath `$StateRoot -Force -ErrorAction Stop
    )
    if (`$entries.Count -eq 0) {
        return
    }
    if (`$entries.Count -ne 1 -or
        -not `$entries[0].PSIsContainer -or
        -not [string]::Equals(
            `$entries[0].Name,
            'snapshots',
            [System.StringComparison]::Ordinal) -or
        ((`$entries[0].Attributes -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "journal 없는 supervisor 준비 state에 허용되지 않은 항목이 있습니다: `$StateRoot"
    }

    `$snapshotEntries = @(
        Get-ChildItem -LiteralPath `$entries[0].FullName -Force -ErrorAction Stop
    )
    if (`$snapshotEntries.Count -ne 0) {
        throw "journal 없는 supervisor snapshots scaffold가 비어 있지 않습니다: `$(`$entries[0].FullName)"
    }
}

function Get-PreexistingInstallerRollbackDirectories {
    param([Parameter(Mandatory = `$true)][string]`$InstallParent)

    if (-not (Test-SupervisorPathExistsFailClosed -Path `$InstallParent)) {
        return @()
    }
    `$installParentAttributes =
        [System.IO.File]::GetAttributes(`$InstallParent)
    if ((`$installParentAttributes -band
        [System.IO.FileAttributes]::Directory) -eq 0) {
        throw "설치 부모 경로가 디렉터리가 아닙니다: `$InstallParent"
    }

    return @(
        Get-ChildItem -LiteralPath `$InstallParent -Directory -Force -Filter '.tradeplan-install-rollback-*' |
        ForEach-Object { Get-NormalizedSupervisorPath -Path `$_.FullName } |
        Sort-Object
    )
}

function Write-SupervisorJournal {
    param(
        [Parameter(Mandatory = `$true)]`$Journal,
        [Parameter(Mandatory = `$true)][string]`$JournalPath
    )

    `$stateRoot = Get-NormalizedSupervisorPath -Path `$Journal.StateRoot
    Assert-NoReparsePoints -Path `$stateRoot
    Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot
    `$stateParent = Split-Path -Parent `$stateRoot
    `$temporaryPrefix =
        '.' + [System.IO.Path]::GetFileName(`$stateRoot) + '.journal.'
    `$temporaryPath = Join-Path `$stateParent (`$temporaryPrefix + `$PID + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    `$replaceBackupPath = `$temporaryPath + '.replace.tmp'
    Assert-SupervisorChildPath -Candidate `$temporaryPath -Parent `$stateParent
    Assert-SupervisorChildPath -Candidate `$replaceBackupPath -Parent `$stateParent
    if (-not [string]::Equals(
            [System.IO.Path]::GetPathRoot(`$temporaryPath),
            [System.IO.Path]::GetPathRoot(`$JournalPath),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'supervisor journal 임시 파일은 journal과 같은 볼륨에 있어야 합니다.'
    }
    `$json = `$Journal | ConvertTo-Json -Depth 20
    `$bytes = [System.Text.Encoding]::UTF8.GetBytes(`$json)
    try {
        if (Test-ProtectedInstallRoot -Path `$script:ActiveSupervisorInstallRoot) {
            `$temporaryAcl = New-ProtectedSupervisorFileSecurity
            `$stream = [System.IO.FileStream]::new(
                `$temporaryPath,
                [System.IO.FileMode]::CreateNew,
                [System.Security.AccessControl.FileSystemRights]::Write,
                [System.IO.FileShare]::None,
                16384,
                [System.IO.FileOptions]::WriteThrough,
                `$temporaryAcl)
        }
        else {
            `$stream = [System.IO.FileStream]::new(
                `$temporaryPath,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None,
                16384,
                [System.IO.FileOptions]::WriteThrough)
        }
        try {
            `$stream.Write(`$bytes, 0, `$bytes.Length)
            `$stream.Flush(`$true)
        }
        finally {
            `$stream.Dispose()
        }

        if (`$env:GEORAEPLAN_INSTALLER_TEST_CRASH_AFTER_FIRST_JOURNAL_TEMP_FLUSH -eq '1' -and
            -not (Test-SupervisorPathExistsFailClosed -Path `$JournalPath)) {
            Write-InstallLog 'Injected crash after first supervisor journal temp flush.'
            [System.Diagnostics.Process]::GetCurrentProcess().Kill()
            throw 'Injected journal crash did not terminate the process.'
        }

        Set-ProtectedSupervisorObjectSecurity -Path `$temporaryPath
        if (Test-SupervisorPathExistsFailClosed -Path `$JournalPath) {
            [System.IO.File]::Replace(
                `$temporaryPath,
                `$JournalPath,
                `$replaceBackupPath,
                `$true)
        }
        else {
            [System.IO.File]::Move(`$temporaryPath, `$JournalPath)
        }
        Set-ProtectedSupervisorObjectSecurity -Path `$JournalPath
    }
    finally {
        if (Test-SupervisorPathExistsFailClosed -Path `$temporaryPath) {
            Remove-Item -LiteralPath `$temporaryPath -Force -ErrorAction Stop
        }
        if (Test-SupervisorPathExistsFailClosed -Path `$replaceBackupPath) {
            Remove-Item -LiteralPath `$replaceBackupPath -Force -ErrorAction Stop
        }
    }
    Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot
}

function New-InstallRollbackDescriptor {
    param(
        [Parameter(Mandatory = `$true)][string]`$Path,
        [Parameter(Mandatory = `$true)][string]`$Label,
        [Parameter(Mandatory = `$true)][string]`$BackupRoot
    )

    `$fullPath = Get-NormalizedSupervisorPath -Path `$Path
    `$hadExistingInstall =
        Test-SupervisorPathExistsFailClosed -Path `$fullPath
    if (`$hadExistingInstall) {
        `$targetAttributes =
            [System.IO.File]::GetAttributes(`$fullPath)
        if ((`$targetAttributes -band
            [System.IO.FileAttributes]::Directory) -eq 0) {
            throw "rollback 대상 경로가 디렉터리가 아닙니다: `$fullPath"
        }
    }

    return [pscustomobject]@{
        Path = `$fullPath
        Label = `$Label
        HadExistingInstall = `$hadExistingInstall
        BackupRoot = Get-NormalizedSupervisorPath -Path `$BackupRoot
        Manifest = if (`$hadExistingInstall) {
            Get-InstallTreeManifest -Path `$fullPath
        }
        else {
            `$null
        }
    }
}

function Initialize-InstallRollbackSnapshot {
    param([Parameter(Mandatory = `$true)]`$Snapshot)

    if (-not `$Snapshot.HadExistingInstall) {
        Write-InstallLog ("rollback snapshot: 기존 설치 경로 없음. Path={0}" -f `$Snapshot.Path)
        return
    }

    Write-InstallLog ("rollback snapshot 생성: {0} -> {1}" -f `$Snapshot.Path, `$Snapshot.BackupRoot)
    Invoke-RobocopyMirror -Source `$Snapshot.Path -Destination `$Snapshot.BackupRoot
    Set-ProtectedSupervisorTreeSecurity -StateRoot `$Snapshot.BackupRoot
    Set-InstallTreeManifestMetadata -Path `$Snapshot.BackupRoot -Manifest `$Snapshot.Manifest
    Assert-InstallTreeManifest -Path `$Snapshot.BackupRoot -Expected `$Snapshot.Manifest
    Write-InstallLog ("rollback snapshot manifest 검증 완료: {0}" -f `$Snapshot.BackupRoot)
}

function Restore-InstallRollbackSnapshot {
    param([Parameter(Mandatory = `$true)]`$Snapshot)

    if (`$Snapshot.HadExistingInstall) {
        if ([string]::IsNullOrWhiteSpace(`$Snapshot.BackupRoot) -or
            -not (Test-SupervisorPathExistsFailClosed -Path `$Snapshot.BackupRoot)) {
            throw ("rollback snapshot을 찾지 못했습니다. Label={0}, BackupRoot={1}" -f `$Snapshot.Label, `$Snapshot.BackupRoot)
        }
        `$backupAttributes =
            [System.IO.File]::GetAttributes(`$Snapshot.BackupRoot)
        if ((`$backupAttributes -band
            [System.IO.FileAttributes]::Directory) -eq 0) {
            throw "rollback snapshot 경로가 디렉터리가 아닙니다: `$(`$Snapshot.BackupRoot)"
        }

        Assert-InstallTreeManifest -Path `$Snapshot.BackupRoot -Expected `$Snapshot.Manifest
        if (Test-SupervisorPathExistsFailClosed -Path `$Snapshot.Path) {
            Assert-NoReparsePoints -Path `$Snapshot.Path
        }
        Write-InstallLog ("rollback 복구: {0} -> {1}" -f `$Snapshot.BackupRoot, `$Snapshot.Path)
        Invoke-RobocopyMirror -Source `$Snapshot.BackupRoot -Destination `$Snapshot.Path
        Set-InstallTreeManifestMetadata -Path `$Snapshot.Path -Manifest `$Snapshot.Manifest
        Assert-InstallTreeManifest -Path `$Snapshot.Path -Expected `$Snapshot.Manifest
        Write-InstallLog ("rollback 대상 manifest 검증 완료: {0}" -f `$Snapshot.Path)
        return
    }

    if (Test-SupervisorPathExistsFailClosed -Path `$Snapshot.Path) {
        Assert-NoReparsePoints -Path `$Snapshot.Path
        Write-InstallLog ("rollback 정리: 실패한 신규 설치 경로 제거 {0}" -f `$Snapshot.Path)
        Remove-Item -LiteralPath `$Snapshot.Path -Recurse -Force -ErrorAction Stop
    }
    if (Test-SupervisorPathExistsFailClosed -Path `$Snapshot.Path) {
        throw "실패한 신규 설치 경로를 완전히 제거하지 못했습니다: `$(`$Snapshot.Path)"
    }
}

function Assert-RestoredInstallRollbackSnapshot {
    param([Parameter(Mandatory = `$true)]`$Snapshot)

    if (`$Snapshot.HadExistingInstall) {
        Assert-InstallTreeManifest -Path `$Snapshot.Path -Expected `$Snapshot.Manifest
    }
    elseif (Test-SupervisorPathExistsFailClosed -Path `$Snapshot.Path) {
        throw "기존에 없던 설치 경로가 rollback 후에도 남아 있습니다: `$(`$Snapshot.Path)"
    }
}

function Remove-InstallRollbackSnapshot {
    param([Parameter(Mandatory = `$true)]`$Snapshot)

    if ([string]::IsNullOrWhiteSpace(`$Snapshot.BackupRoot) -or
        -not (Test-SupervisorPathExistsFailClosed -Path `$Snapshot.BackupRoot)) {
        return
    }

    Assert-NoReparsePoints -Path `$Snapshot.BackupRoot
    Remove-Item -LiteralPath `$Snapshot.BackupRoot -Recurse -Force -ErrorAction Stop
    if (Test-SupervisorPathExistsFailClosed -Path `$Snapshot.BackupRoot) {
        throw "rollback snapshot을 완전히 제거하지 못했습니다: `$(`$Snapshot.BackupRoot)"
    }
    Write-InstallLog ("rollback snapshot 제거: {0}" -f `$Snapshot.BackupRoot)
}

function Assert-SupervisorJournalBinding {
    param(
        [Parameter(Mandatory = `$true)]`$Journal,
        [Parameter(Mandatory = `$true)][string]`$ExpectedStateRoot,
        [Parameter(Mandatory = `$true)][string]`$ExpectedInstallRoot
    )

    `$expectedInstallRoot =
        Get-NormalizedSupervisorPath -Path `$ExpectedInstallRoot
    `$expectedState = Get-NormalizedSupervisorPath -Path `$ExpectedStateRoot
    if (@(1, 2) -notcontains [int]`$Journal.FormatVersion -or
        -not (Test-SameSupervisorPath -Left `$Journal.InstallRoot -Right `$expectedInstallRoot) -or
        -not (Test-SameSupervisorPath -Left `$Journal.StateRoot -Right `$expectedState)) {
        throw "supervisor journal 기본 path binding 검증에 실패했습니다."
    }
    if ([int]`$Journal.FormatVersion -ge 2) {
        if (-not (`$Journal.PSObject.Properties.Name -contains
                'OriginUserSid') -or
            [string]::IsNullOrWhiteSpace(
                [string]`$Journal.OriginUserSid)) {
            throw 'supervisor journal origin user SID가 없습니다.'
        }
        try {
            `$journalOriginUserSid =
                [System.Security.Principal.SecurityIdentifier]::new(
                    [string]`$Journal.OriginUserSid).Value
        }
        catch {
            throw 'supervisor journal origin user SID가 올바르지 않습니다.'
        }
        if (-not [string]::Equals(
                `$journalOriginUserSid,
                `$script:OriginUserSid,
                [System.StringComparison]::Ordinal)) {
            throw (New-OriginUserMismatchException -Message (
                'pending supervisor state는 다른 Windows 사용자가 만든 상태입니다.'
            ))
        }
    }

    `$allowedPhases = @(
        'Preparing',
        'Prepared',
        'WorkerRunning',
        'Recovering',
        'RestoredCleanupPending',
        'ShortcutRepairPending',
        'CommittedCleanupPending'
    )
    if (`$allowedPhases -notcontains [string]`$Journal.Phase) {
        throw "supervisor journal phase가 올바르지 않습니다: `$(`$Journal.Phase)"
    }
    Assert-WorkerJobJournalBinding -Journal `$Journal
    Assert-ShortcutRepairJournalBinding -Journal `$Journal

    `$snapshotRoot = Join-Path `$expectedState 'snapshots'
    foreach (`$snapshot in @(`$Journal.Snapshots)) {
        `$allowedTarget =
            Test-SameSupervisorPath -Left `$snapshot.Path -Right `$expectedInstallRoot
        if (`$managesCanonicalAndLegacyRoots -and
            (Test-SameSupervisorPath -Left `$expectedInstallRoot -Right `$CanonicalInstallRoot)) {
            `$allowedTarget =
                `$allowedTarget -or
                (Test-SameSupervisorPath -Left `$snapshot.Path -Right `$LegacyUserRoot)
        }
        if (-not `$allowedTarget) {
            throw "supervisor journal rollback target이 허용되지 않았습니다: `$(`$snapshot.Path)"
        }

        `$safeLabel = ([string]`$snapshot.Label -replace '[^A-Za-z0-9._-]', '-')
        `$expectedBackupRoot = Join-Path `$snapshotRoot `$safeLabel
        if (-not (Test-SameSupervisorPath -Left `$snapshot.BackupRoot -Right `$expectedBackupRoot)) {
            throw "supervisor journal snapshot path binding 검증에 실패했습니다: `$(`$snapshot.BackupRoot)"
        }
    }

    `$installParent = Split-Path -Parent `$expectedInstallRoot
    foreach (`$preexistingPath in @(`$Journal.PreexistingInstallerRollbackDirectories)) {
        Assert-SupervisorChildPath -Candidate `$preexistingPath -Parent `$installParent
        if (-not ([System.IO.Path]::GetFileName([string]`$preexistingPath)).StartsWith(
                '.tradeplan-install-rollback-',
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "preexisting rollback path 형식이 올바르지 않습니다: `$preexistingPath"
        }
    }
}

function Read-SupervisorJournal {
    param(
        [Parameter(Mandatory = `$true)][string]`$JournalPath,
        [Parameter(Mandatory = `$true)][string]`$ExpectedStateRoot,
        [Parameter(Mandatory = `$true)][string]`$ExpectedInstallRoot
    )

    `$journalItem = Get-Item -LiteralPath `$JournalPath -Force -ErrorAction Stop
    if ((`$journalItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "supervisor journal은 재분석 지점일 수 없습니다: `$JournalPath"
    }
    `$journal = Get-Content -LiteralPath `$JournalPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    Assert-SupervisorJournalBinding -Journal `$journal -ExpectedStateRoot `$ExpectedStateRoot -ExpectedInstallRoot `$ExpectedInstallRoot
    return `$journal
}

function Remove-NewInstallerRollbackDirectories {
    param([Parameter(Mandatory = `$true)]`$Journal)

    `$installParent = Split-Path -Parent (Get-NormalizedSupervisorPath -Path `$Journal.InstallRoot)
    `$preexisting = New-Object System.Collections.Generic.HashSet[string](
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach (`$path in @(`$Journal.PreexistingInstallerRollbackDirectories)) {
        [void]`$preexisting.Add((Get-NormalizedSupervisorPath -Path `$path))
    }

    foreach (`$directoryPath in Get-PreexistingInstallerRollbackDirectories -InstallParent `$installParent) {
        if (`$preexisting.Contains(`$directoryPath)) {
            continue
        }

        Assert-SupervisorChildPath -Candidate `$directoryPath -Parent `$installParent
        Assert-NoReparsePoints -Path `$directoryPath
        Remove-Item -LiteralPath `$directoryPath -Recurse -Force -ErrorAction Stop
        if (Test-SupervisorPathExistsFailClosed -Path `$directoryPath) {
            throw "새 rollback residue를 완전히 제거하지 못했습니다: `$directoryPath"
        }
    }
}

function Remove-CompletedSupervisorState {
    param(
        [Parameter(Mandatory = `$true)]`$Journal,
        [Parameter(Mandatory = `$true)][string]`$JournalPath
    )

    foreach (`$snapshot in @(`$Journal.Snapshots)) {
        Remove-InstallRollbackSnapshot -Snapshot `$snapshot
    }
    Remove-NewInstallerRollbackDirectories -Journal `$Journal

    `$stateRoot = Get-NormalizedSupervisorPath -Path `$Journal.StateRoot
    `$snapshotContainer = Join-Path `$stateRoot 'snapshots'
    if (Test-SupervisorPathExistsFailClosed -Path `$snapshotContainer) {
        Remove-Item -LiteralPath `$snapshotContainer -Recurse -Force -ErrorAction Stop
    }
    Get-ChildItem -LiteralPath `$stateRoot -File -Force -Filter '*.tmp' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction Stop
    [System.IO.File]::Delete(`$JournalPath)
    if (Test-SupervisorPathExistsFailClosed -Path `$stateRoot) {
        Remove-Item -LiteralPath `$stateRoot -Recurse -Force -ErrorAction Stop
    }
    if (Test-SupervisorPathExistsFailClosed -Path `$stateRoot) {
        throw "supervisor state를 완전히 제거하지 못했습니다: `$stateRoot"
    }
}

function Invoke-PendingSupervisorRecoveryForRoot {
    param(
        [Parameter(Mandatory = `$true)][string]`$RecoveryInstallRoot,
        [Parameter(Mandatory = `$true)][string]`$RecoveryStateRoot
    )

    `$previousActiveSupervisorInstallRoot =
        `$script:ActiveSupervisorInstallRoot
    `$script:ActiveSupervisorInstallRoot =
        Get-NormalizedSupervisorPath -Path `$RecoveryInstallRoot
    try {
    `$stateRoot =
        Get-NormalizedSupervisorPath -Path `$RecoveryStateRoot
    `$installParent = Split-Path -Parent (Get-NormalizedSupervisorPath -Path `$RecoveryInstallRoot)
    Assert-SupervisorChildPath -Candidate `$stateRoot -Parent `$installParent
    Assert-ProtectedSupervisorParentSecurity -ParentPath `$installParent
    if (-not (Test-SupervisorPathExistsFailClosed -Path `$stateRoot)) {
        return `$false
    }

    Remove-OrphanedSupervisorJournalTemporaryFiles -StateRoot `$stateRoot
    `$stateAttributes = [System.IO.File]::GetAttributes(`$stateRoot)
    if ((`$stateAttributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
        throw "supervisor state 경로가 디렉터리가 아닙니다: `$stateRoot"
    }
    if ((`$stateAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "supervisor state 경로는 재분석 지점일 수 없습니다: `$stateRoot"
    }
    Assert-NoReparsePoints -Path `$stateRoot
    `$journalPath = Join-Path `$stateRoot 'journal.json'
    if (-not (Test-SupervisorPathExistsFailClosed -Path `$journalPath)) {
        Write-InstallLog 'durable journal 전 단계에서 중단된 supervisor state를 제한 복구합니다. worker는 시작되지 않았습니다.'
        Set-ProtectedSupervisorTreeSecurity -StateRoot `$stateRoot
        Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot
        Assert-AbandonedSupervisorPreparationState -StateRoot `$stateRoot
        Remove-Item -LiteralPath `$stateRoot -Recurse -Force -ErrorAction Stop
        if (Test-SupervisorPathExistsFailClosed -Path `$stateRoot) {
            throw "journal 없는 준비 state를 완전히 제거하지 못했습니다: `$stateRoot"
        }
        return `$true
    }

    Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot
    `$journal = Read-SupervisorJournal -JournalPath `$journalPath -ExpectedStateRoot `$stateRoot -ExpectedInstallRoot `$RecoveryInstallRoot
    Complete-WorkerJobBarrierForJournal -Journal `$journal
    switch ([string]`$journal.Phase) {
        'Preparing' {
            Write-InstallLog '미완성 snapshot 준비 state를 정리합니다. worker는 시작되지 않았습니다.'
            Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
        }
        'Prepared' {
            `$journal.Phase = 'Recovering'
            Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
            foreach (`$snapshot in @(`$journal.Snapshots)) {
                Restore-InstallRollbackSnapshot -Snapshot `$snapshot
            }
            `$journal.Phase = 'RestoredCleanupPending'
            Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
            Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
        }
        'WorkerRunning' {
            `$journal.Phase = 'Recovering'
            Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
            foreach (`$snapshot in @(`$journal.Snapshots)) {
                Restore-InstallRollbackSnapshot -Snapshot `$snapshot
            }
            `$journal.Phase = 'RestoredCleanupPending'
            Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
            Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
        }
        'Recovering' {
            foreach (`$snapshot in @(`$journal.Snapshots)) {
                Restore-InstallRollbackSnapshot -Snapshot `$snapshot
            }
            `$journal.Phase = 'RestoredCleanupPending'
            Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
            Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
        }
        'RestoredCleanupPending' {
            foreach (`$snapshot in @(`$journal.Snapshots)) {
                Assert-RestoredInstallRollbackSnapshot -Snapshot `$snapshot
            }
            Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
        }
        'ShortcutRepairPending' {
            try {
                Invoke-PendingShortcutRepair -Repair `$journal.ShortcutRepair
            }
            catch {
                throw (New-ShortcutRepairPendingException -Message (
                    'commit된 설치의 shortcut repair가 완료되지 않았습니다.'
                ) -InnerException `$_.Exception)
            }
            `$journal.Phase = 'CommittedCleanupPending'
            Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
            Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
        }
        'CommittedCleanupPending' {
            Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
        }
        default {
            throw "지원하지 않는 supervisor journal phase입니다: `$(`$journal.Phase)"
        }
    }

    Write-InstallLog 'pending supervisor journal 처리를 완료했습니다.'
    return `$true
    }
    finally {
        `$script:ActiveSupervisorInstallRoot =
            `$previousActiveSupervisorInstallRoot
    }
}

function Get-SupervisorRecoveryInstallRoots {
    `$effectiveRoot =
        Get-NormalizedSupervisorPath -Path `$InstallRoot
    `$canonicalRoot =
        Get-NormalizedSupervisorPath -Path `$CanonicalInstallRoot
    `$legacyRoot =
        Get-NormalizedSupervisorPath -Path `$LegacyUserRoot
    if (`$managesCanonicalAndLegacyRoots) {
        `$managedRoots = @(`$canonicalRoot, `$legacyRoot) |
            Select-Object -Unique
        return `$managedRoots
    }

    return @(`$effectiveRoot)
}

function Invoke-PendingSupervisorRecovery {
    `$pendingStates = [System.Collections.Generic.List[object]]::new()
    foreach (`$recoveryInstallRoot in @(
        Get-SupervisorRecoveryInstallRoots
    )) {
        `$candidateStateRoot =
            Get-SupervisorStateRoot -InstallPath `$recoveryInstallRoot
        if (Test-SupervisorPathExistsFailClosed -Path `$candidateStateRoot) {
            [void]`$pendingStates.Add(
                [pscustomobject]@{
                    InstallRoot = `$recoveryInstallRoot
                    StateRoot = `$candidateStateRoot
                })
        }
    }

    if (`$pendingStates.Count -gt 1) {
        throw 'canonical과 legacy 설치 경로에 supervisor state가 동시에 남아 있어 자동 복구 대상을 안전하게 결정할 수 없습니다.'
    }
    if (`$pendingStates.Count -eq 0) {
        return `$false
    }

    `$pendingState = `$pendingStates[0]
    return Invoke-PendingSupervisorRecoveryForRoot -RecoveryInstallRoot `$pendingState.InstallRoot -RecoveryStateRoot `$pendingState.StateRoot
}

function New-SupervisorJournal {
    `$installRootFullPath = Get-NormalizedSupervisorPath -Path `$InstallRoot
    `$installParent = Split-Path -Parent `$installRootFullPath
    `$stateRoot = Get-SupervisorStateRoot -InstallPath `$installRootFullPath
    Assert-SupervisorChildPath -Candidate `$stateRoot -Parent `$installParent
    Assert-ProtectedSupervisorParentSecurity -ParentPath `$installParent
    if (Test-SupervisorPathExistsFailClosed -Path `$stateRoot) {
        throw "새 snapshot 전에 처리되지 않은 supervisor state가 남아 있습니다: `$stateRoot"
    }

    New-SupervisorStateDirectory -Path `$stateRoot
    if (`$env:GEORAEPLAN_INSTALLER_TEST_CRASH_AFTER_STATE_ROOT_CREATE -eq '1') {
        Write-InstallLog 'Injected crash after supervisor state root creation.'
        [System.Diagnostics.Process]::GetCurrentProcess().Kill()
        throw 'Injected crash did not terminate the process.'
    }
    Set-ProtectedSupervisorObjectSecurity -Path `$stateRoot
    `$snapshotRoot = Join-Path `$stateRoot 'snapshots'
    New-Item -ItemType Directory -Path `$snapshotRoot -ErrorAction Stop | Out-Null
    Set-ProtectedSupervisorTreeSecurity -StateRoot `$stateRoot
    Assert-NoReparsePoints -Path `$stateRoot
    Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot

    `$snapshots = @(
        New-InstallRollbackDescriptor -Path `$installRootFullPath -Label 'primary' -BackupRoot (Join-Path `$snapshotRoot 'primary')
    )
    `$legacySnapshotPath =
        Get-NormalizedSupervisorPath -Path `$LegacyUserRoot
    if (`$managesCanonicalAndLegacyRoots -and
        -not (Test-SameSupervisorPath -Left `$installRootFullPath -Right `$legacySnapshotPath)) {
        `$snapshots += New-InstallRollbackDescriptor -Path `$LegacyUserRoot -Label 'legacy' -BackupRoot (Join-Path `$snapshotRoot 'legacy')
    }

    `$journalPath = Join-Path `$stateRoot 'journal.json'
    `$journal = [pscustomobject]@{
        FormatVersion = 2
        Phase = 'Preparing'
        InstallRoot = `$installRootFullPath
        StateRoot = `$stateRoot
        OriginUserSid = `$script:OriginUserSid
        WorkerJobName =
            New-WorkerJobName -StateRoot `$stateRoot
        WorkerProcessId = 0
        WorkerProcessPath = ''
        WorkerProcessStartTimeUtcTicks = 0
        PreexistingInstallerRollbackDirectories =
            @(Get-PreexistingInstallerRollbackDirectories -InstallParent `$installParent)
        ShortcutRepair = Get-ShortcutRepairContract
        Snapshots = @(`$snapshots)
    }
    Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
    foreach (`$snapshot in @(`$journal.Snapshots)) {
        Initialize-InstallRollbackSnapshot -Snapshot `$snapshot
    }
    `$journal.Phase = 'Prepared'
    Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
    return `$journal
}

function Set-AtomicShellShortcut {
    param(
        [Parameter(Mandatory = `$true)]`$Shell,
        [Parameter(Mandatory = `$true)][string]`$ShortcutPath,
        [Parameter(Mandatory = `$true)][string]`$TargetPath,
        [Parameter(Mandatory = `$true)][string]`$WorkingDirectory,
        [string]`$Arguments = ''
    )

    `$shortcutDirectory = Split-Path -Parent `$ShortcutPath
    if (-not (Test-SupervisorPathExistsFailClosed -Path `$shortcutDirectory)) {
        New-Item -ItemType Directory -Path `$shortcutDirectory -Force -ErrorAction Stop |
            Out-Null
    }
    `$temporaryPath =
        `$ShortcutPath + '.new.' + [Guid]::NewGuid().ToString('N') + '.lnk'
    `$backupPath =
        `$ShortcutPath + '.previous.' + [Guid]::NewGuid().ToString('N') + '.lnk'
    try {
        `$shortcut = `$Shell.CreateShortcut(`$temporaryPath)
        `$shortcut.TargetPath = `$TargetPath
        `$shortcut.WorkingDirectory = `$WorkingDirectory
        if (-not [string]::IsNullOrWhiteSpace(`$Arguments)) {
            `$shortcut.Arguments = `$Arguments
        }
        `$shortcut.Save()
        `$temporaryAttributes =
            [System.IO.File]::GetAttributes(`$temporaryPath)
        if ((`$temporaryAttributes -band
            [System.IO.FileAttributes]::Directory) -ne 0) {
            throw "shortcut 임시 경로가 파일이 아닙니다: `$temporaryPath"
        }

        if (Test-SupervisorPathExistsFailClosed -Path `$ShortcutPath) {
            [System.IO.File]::Replace(
                `$temporaryPath,
                `$ShortcutPath,
                `$backupPath,
                `$true)
            if (Test-SupervisorPathExistsFailClosed -Path `$backupPath) {
                Remove-Item -LiteralPath `$backupPath -Force -ErrorAction Stop
            }
        }
        else {
            [System.IO.File]::Move(`$temporaryPath, `$ShortcutPath)
        }
    }
    finally {
        foreach (`$residuePath in @(`$temporaryPath, `$backupPath)) {
            if (Test-SupervisorPathExistsFailClosed -Path `$residuePath) {
                Remove-Item -LiteralPath `$residuePath -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Get-ShortcutRepairContract {
    `$useIsolatedTestRoots =
        `$env:GEORAEPLAN_INSTALLER_TEST_USE_ISOLATED_SHORTCUT_ROOTS -eq
        '1'
    `$forceProtectedTestScope =
        `$env:GEORAEPLAN_INSTALLER_TEST_FORCE_PROTECTED_SHORTCUT_SCOPE -eq
        '1'
    if (`$forceProtectedTestScope -and -not `$useIsolatedTestRoots) {
        throw 'protected shortcut test scope에는 package-bound isolated roots가 필요합니다.'
    }

    `$protectedShortcutScope =
        `$forceProtectedTestScope -or
        (Test-ProtectedInstallRoot -Path `$InstallRoot)
    if (`$useIsolatedTestRoots) {
        `$testShortcutRoot = Join-Path (
            Split-Path -Parent `$InstallerScriptPath
        ) '.georaeplan-installer-test-shortcuts'
        `$userDesktopDirectory =
            Join-Path `$testShortcutRoot 'user-desktop'
        `$userProgramsDirectory =
            Join-Path `$testShortcutRoot 'user-programs'
        `$commonDesktopDirectory =
            Join-Path `$testShortcutRoot 'common-desktop'
        `$commonProgramsDirectory =
            Join-Path `$testShortcutRoot 'common-programs'
    }
    else {
        `$userDesktopDirectory =
            [Environment]::GetFolderPath('Desktop')
        `$userProgramsDirectory =
            [Environment]::GetFolderPath('Programs')
        `$commonDesktopDirectory =
            [Environment]::GetFolderPath('CommonDesktopDirectory')
        `$commonProgramsDirectory =
            [Environment]::GetFolderPath('CommonPrograms')
    }

    `$primaryDesktopDirectory = if (`$protectedShortcutScope) {
        `$commonDesktopDirectory
    }
    else {
        `$userDesktopDirectory
    }
    `$primaryProgramsDirectory = if (`$protectedShortcutScope) {
        `$commonProgramsDirectory
    }
    else {
        `$userProgramsDirectory
    }
    `$alternateDesktopDirectory = if (`$protectedShortcutScope) {
        `$userDesktopDirectory
    }
    else {
        `$commonDesktopDirectory
    }
    `$alternateProgramsDirectory = if (`$protectedShortcutScope) {
        `$userProgramsDirectory
    }
    else {
        `$commonProgramsDirectory
    }
    `$primaryStartMenuDirectory =
        Join-Path `$primaryProgramsDirectory '__APP_DISPLAY_NAME__'
    `$alternateStartMenuDirectory =
        Join-Path `$alternateProgramsDirectory '__APP_DISPLAY_NAME__'

    return [pscustomobject]@{
        Enabled = -not `$NoShortcuts
        OriginUserSid = `$script:OriginUserSid
        ProtectedShortcutScope = `$protectedShortcutScope
        LegacyBridgeCopy = `$useLegacyBridgeCopy
        ManagesLegacyUserShortcuts =
            `$protectedShortcutScope -and
            `$managesCanonicalAndLegacyRoots
        RemoveLegacyApplicationShortcuts =
            `$protectedShortcutScope -and
            `$managesCanonicalAndLegacyRoots -and
            -not `$useLegacyBridgeCopy
        InstallRoot =
            Get-NormalizedSupervisorPath -Path `$InstallRoot
        LegacyInstallRoot =
            Get-NormalizedSupervisorPath -Path `$LegacyUserRoot
        PrimaryDesktopShortcutPath =
            Join-Path `$primaryDesktopDirectory '__APP_DISPLAY_NAME__.lnk'
        PrimaryStartMenuDirectory = `$primaryStartMenuDirectory
        PrimaryApplicationShortcutPath =
            Join-Path `$primaryStartMenuDirectory '__APP_DISPLAY_NAME__.lnk'
        PrimaryRemoveShortcutPath =
            Join-Path `$primaryStartMenuDirectory '__APP_DISPLAY_NAME____REMOVE_SHORTCUT_SUFFIX__'
        AlternateDesktopShortcutPath =
            Join-Path `$alternateDesktopDirectory '__APP_DISPLAY_NAME__.lnk'
        AlternateStartMenuDirectory = `$alternateStartMenuDirectory
        AlternateApplicationShortcutPath =
            Join-Path `$alternateStartMenuDirectory '__APP_DISPLAY_NAME__.lnk'
        AlternateRemoveShortcutPath =
            Join-Path `$alternateStartMenuDirectory '__APP_DISPLAY_NAME____REMOVE_SHORTCUT_SUFFIX__'
    }
}

function Assert-ShortcutRepairJournalBinding {
    param([Parameter(Mandatory = `$true)]`$Journal)

    `$hasRepairContract =
        `$Journal.PSObject.Properties.Name -contains 'ShortcutRepair' -and
        `$null -ne `$Journal.ShortcutRepair
    if (-not `$hasRepairContract) {
        if ([string]`$Journal.Phase -eq 'ShortcutRepairPending') {
            throw 'ShortcutRepairPending journal에 shortcut binding이 없습니다.'
        }
        return
    }

    `$repair = `$Journal.ShortcutRepair
    if (-not (`$repair.PSObject.Properties.Name -contains
            'OriginUserSid') -or
        [string]::IsNullOrWhiteSpace(
            [string]`$repair.OriginUserSid)) {
        throw 'shortcut repair origin user SID schema가 올바르지 않습니다.'
    }
    try {
        `$normalizedOriginSid =
            [System.Security.Principal.SecurityIdentifier]::new(
                [string]`$repair.OriginUserSid).Value
    }
    catch {
        throw 'shortcut repair origin user SID가 올바르지 않습니다.'
    }
    if (`$Journal.PSObject.Properties.Name -contains
            'OriginUserSid' -and
        -not [string]::Equals(
            `$normalizedOriginSid,
            [string]`$Journal.OriginUserSid,
            [System.StringComparison]::Ordinal)) {
        throw 'shortcut repair origin SID journal binding 검증에 실패했습니다.'
    }
    foreach (`$booleanProperty in @(
        'Enabled',
        'ProtectedShortcutScope',
        'LegacyBridgeCopy',
        'ManagesLegacyUserShortcuts',
        'RemoveLegacyApplicationShortcuts'
    )) {
        if (-not (`$repair.PSObject.Properties.Name -contains
                `$booleanProperty) -or
            `$repair.`$booleanProperty -isnot [bool]) {
            throw "shortcut repair boolean schema가 올바르지 않습니다: `$booleanProperty"
        }
    }
    foreach (`$pathProperty in @(
        'InstallRoot',
        'LegacyInstallRoot',
        'PrimaryDesktopShortcutPath',
        'PrimaryStartMenuDirectory',
        'PrimaryApplicationShortcutPath',
        'PrimaryRemoveShortcutPath',
        'AlternateDesktopShortcutPath',
        'AlternateStartMenuDirectory',
        'AlternateApplicationShortcutPath',
        'AlternateRemoveShortcutPath'
    )) {
        `$pathValue = [string]`$repair.`$pathProperty
        if ([string]::IsNullOrWhiteSpace(`$pathValue) -or
            -not [System.IO.Path]::IsPathRooted(`$pathValue)) {
            throw "shortcut repair path schema가 올바르지 않습니다: `$pathProperty"
        }
    }
    if (-not (Test-SameSupervisorPath -Left (
                [string]`$repair.InstallRoot
            ) -Right ([string]`$Journal.InstallRoot))) {
        throw 'shortcut repair install-root journal binding 검증에 실패했습니다.'
    }

    `$legacySnapshots = @(
        `$Journal.Snapshots |
        Where-Object {
            [string]::Equals(
                [string]`$_.Label,
                'legacy',
                [System.StringComparison]::Ordinal)
        }
    )
    if (`$legacySnapshots.Count -gt 1) {
        throw 'shortcut repair journal에 legacy snapshot이 중복되었습니다.'
    }
    if (`$legacySnapshots.Count -eq 1 -and
        -not (Test-SameSupervisorPath -Left (
                [string]`$repair.LegacyInstallRoot
            ) -Right ([string]`$legacySnapshots[0].Path))) {
        throw 'shortcut repair legacy snapshot binding 검증에 실패했습니다.'
    }
    `$expectedLegacyManagement =
        [bool]`$repair.ProtectedShortcutScope -and
        `$legacySnapshots.Count -eq 1
    if ([bool]`$repair.ManagesLegacyUserShortcuts -ne
        `$expectedLegacyManagement) {
        throw 'shortcut repair legacy 관리 flag binding 검증에 실패했습니다.'
    }
    `$expectedLegacyApplicationCleanup =
        `$expectedLegacyManagement -and
        -not [bool]`$repair.LegacyBridgeCopy
    if ([bool]`$repair.RemoveLegacyApplicationShortcuts -ne
        `$expectedLegacyApplicationCleanup) {
        throw 'shortcut repair legacy app cleanup flag binding 검증에 실패했습니다.'
    }
    if ([string]`$Journal.Phase -eq 'ShortcutRepairPending' -and
        -not [bool]`$repair.Enabled) {
        throw 'ShortcutRepairPending journal에서 shortcut repair가 비활성화되었습니다.'
    }
    if ([string]`$Journal.Phase -eq 'ShortcutRepairPending') {
        `$expected = Get-ShortcutRepairContract
        if (-not [string]::Equals(
                `$normalizedOriginSid,
                [string]`$expected.OriginUserSid,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'shortcut repair origin user token binding 검증에 실패했습니다.'
        }
        foreach (`$pathProperty in @(
            'InstallRoot',
            'LegacyInstallRoot',
            'PrimaryDesktopShortcutPath',
            'PrimaryStartMenuDirectory',
            'PrimaryApplicationShortcutPath',
            'PrimaryRemoveShortcutPath',
            'AlternateDesktopShortcutPath',
            'AlternateStartMenuDirectory',
            'AlternateApplicationShortcutPath',
            'AlternateRemoveShortcutPath'
        )) {
            if (-not (Test-SameSupervisorPath -Left (
                        [string]`$repair.`$pathProperty
                    ) -Right ([string]`$expected.`$pathProperty))) {
                throw "shortcut repair current path binding 검증에 실패했습니다: `$pathProperty"
            }
        }
        if ([bool]`$repair.ProtectedShortcutScope -ne
            [bool]`$expected.ProtectedShortcutScope) {
            throw 'shortcut repair current scope binding 검증에 실패했습니다.'
        }
    }
    if ([string]`$Journal.Phase -eq 'ShortcutRepairPending' -and
        `$legacySnapshots.Count -eq 1) {
        `$legacyRootExists =
            Test-SupervisorPathExistsFailClosed -Path (
                [string]`$repair.LegacyInstallRoot)
        if ([bool]`$repair.LegacyBridgeCopy -ne `$legacyRootExists) {
            throw 'shortcut repair legacy disposition binding 검증에 실패했습니다.'
        }
    }
}

function Test-ExactShortcutPath {
    param(
        [Parameter(Mandatory = `$true)][string]`$Left,
        [Parameter(Mandatory = `$true)][string]`$Right
    )

    if ([string]::IsNullOrWhiteSpace(`$Left) -or
        [string]::IsNullOrWhiteSpace(`$Right)) {
        return `$false
    }
    return Test-SameSupervisorPath -Left `$Left -Right `$Right
}

function Remove-LegacyManagedShortcut {
    param(
        [Parameter(Mandatory = `$true)]`$Shell,
        [Parameter(Mandatory = `$true)][string]`$ShortcutPath,
        [Parameter(Mandatory = `$true)]
        [ValidateSet('Application', 'Remove')]
        [string]`$Kind,
        [Parameter(Mandatory = `$true)]`$Repair
    )

    if (-not (Test-SupervisorPathExistsFailClosed -Path `$ShortcutPath)) {
        return
    }
    `$shortcutAttributes =
        [System.IO.File]::GetAttributes(`$ShortcutPath)
    if ((`$shortcutAttributes -band
            [System.IO.FileAttributes]::Directory) -ne 0 -or
        (`$shortcutAttributes -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "alternate-scope shortcut이 일반 파일이 아닙니다: `$ShortcutPath"
    }

    `$shortcut = `$Shell.CreateShortcut(`$ShortcutPath)
    `$legacyRoot =
        Get-NormalizedSupervisorPath -Path `$Repair.LegacyInstallRoot
    `$workingDirectoryMatches =
        Test-ExactShortcutPath -Left (
            [string]`$shortcut.WorkingDirectory
        ) -Right `$legacyRoot
    `$ownedByLegacyInstall = `$false
    if (`$Kind -eq 'Application') {
        `$legacyExecutable =
            Join-Path `$legacyRoot '__APP_DISPLAY_NAME__.exe'
        `$ownedByLegacyInstall =
            `$workingDirectoryMatches -and
            (Test-ExactShortcutPath -Left (
                [string]`$shortcut.TargetPath
            ) -Right `$legacyExecutable)
    }
    else {
        `$legacyUninstallScript =
            Join-Path `$legacyRoot '__UNINSTALL_PS1_NAME__'
        `$currentExpectedArguments =
            "-ExecutionPolicy Bypass -File ``"`$legacyUninstallScript``" -InstallRoot ``"`$legacyRoot``""
        `$historicalExpectedArguments =
            "-ExecutionPolicy Bypass -File ``"`$legacyUninstallScript``""
        `$targetLeaf =
            [System.IO.Path]::GetFileName(
                [string]`$shortcut.TargetPath)
        `$argumentsMatch =
            [string]::Equals(
                [string]`$shortcut.Arguments,
                `$currentExpectedArguments,
                [System.StringComparison]::Ordinal) -or
            [string]::Equals(
                [string]`$shortcut.Arguments,
                `$historicalExpectedArguments,
                [System.StringComparison]::Ordinal)
        `$ownedByLegacyInstall =
            `$workingDirectoryMatches -and
            [string]::Equals(
                `$targetLeaf,
                'powershell.exe',
                [System.StringComparison]::OrdinalIgnoreCase) -and
            `$argumentsMatch
    }

    if (-not `$ownedByLegacyInstall) {
        Write-InstallLog (
            '동일명 alternate-scope shortcut은 legacy 설치 소유가 아니므로 보존합니다. Path={0}' -f
            `$ShortcutPath)
        return
    }

    Remove-Item -LiteralPath `$ShortcutPath -Force -ErrorAction Stop
    if (Test-SupervisorPathExistsFailClosed -Path `$ShortcutPath) {
        throw "legacy shortcut을 완전히 제거하지 못했습니다: `$ShortcutPath"
    }
    Write-InstallLog (
        '삭제된 legacy 설치를 가리키는 shortcut을 정리했습니다. Path={0}' -f
        `$ShortcutPath)
}

function Remove-EmptyLegacyShortcutDirectory {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    if (-not (Test-SupervisorPathExistsFailClosed -Path `$Path)) {
        return
    }
    `$attributes = [System.IO.File]::GetAttributes(`$Path)
    if ((`$attributes -band
            [System.IO.FileAttributes]::Directory) -eq 0 -or
        (`$attributes -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "alternate-scope Start Menu 경로가 안전한 디렉터리가 아닙니다: `$Path"
    }
    if (@(Get-ChildItem -LiteralPath `$Path -Force -ErrorAction Stop).
        Count -eq 0) {
        [System.IO.Directory]::Delete(`$Path, `$false)
    }
}

function Invoke-PendingShortcutRepair {
    param([Parameter(Mandatory = `$true)]`$Repair)

    if (`$NoShortcuts) {
        throw 'shortcut repair pending state는 -NoShortcuts로 해제할 수 없습니다.'
    }
    if (-not [bool]`$Repair.Enabled) {
        throw 'shortcut repair pending state가 비활성화되어 있습니다.'
    }
    if (`$env:GEORAEPLAN_INSTALLER_TEST_FAIL_SHORTCUTS -eq '1') {
        throw 'Injected post-commit shortcut failure.'
    }

    `$installedExecutable =
        Join-Path `$Repair.InstallRoot '__APP_DISPLAY_NAME__.exe'
    `$uninstallScript =
        Join-Path `$Repair.InstallRoot '__UNINSTALL_PS1_NAME__'
    foreach (`$requiredShortcutTarget in @(
        `$installedExecutable,
        `$uninstallScript
    )) {
        `$targetAttributes =
            [System.IO.File]::GetAttributes(`$requiredShortcutTarget)
        if ((`$targetAttributes -band
                [System.IO.FileAttributes]::Directory) -ne 0 -or
            (`$targetAttributes -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "shortcut target이 안전한 파일이 아닙니다: `$requiredShortcutTarget"
        }
    }
    `$shell = New-Object -ComObject WScript.Shell
    Set-AtomicShellShortcut -Shell `$shell -ShortcutPath `$Repair.PrimaryDesktopShortcutPath -TargetPath `$installedExecutable -WorkingDirectory `$Repair.InstallRoot
    Set-AtomicShellShortcut -Shell `$shell -ShortcutPath `$Repair.PrimaryApplicationShortcutPath -TargetPath `$installedExecutable -WorkingDirectory `$Repair.InstallRoot
    `$removeArguments =
        "-ExecutionPolicy Bypass -File ``"`$uninstallScript``" -InstallRoot ``"`$(`$Repair.InstallRoot)``""
    Set-AtomicShellShortcut -Shell `$shell -ShortcutPath `$Repair.PrimaryRemoveShortcutPath -TargetPath 'powershell.exe' -WorkingDirectory `$Repair.InstallRoot -Arguments `$removeArguments

    if (`$env:GEORAEPLAN_INSTALLER_TEST_FAIL_SHORTCUTS_AFTER_COMMON -eq
        '1') {
        throw 'Injected post-commit shortcut failure after common set.'
    }

    if ([bool]`$Repair.ManagesLegacyUserShortcuts) {
        # The complete common set is durable before alternate-scope cleanup.
        if ([bool]`$Repair.RemoveLegacyApplicationShortcuts) {
            Remove-LegacyManagedShortcut -Shell `$shell -ShortcutPath `$Repair.AlternateDesktopShortcutPath -Kind Application -Repair `$Repair
            Remove-LegacyManagedShortcut -Shell `$shell -ShortcutPath `$Repair.AlternateApplicationShortcutPath -Kind Application -Repair `$Repair
        }
        Remove-LegacyManagedShortcut -Shell `$shell -ShortcutPath `$Repair.AlternateRemoveShortcutPath -Kind Remove -Repair `$Repair
        Remove-EmptyLegacyShortcutDirectory -Path `$Repair.AlternateStartMenuDirectory
    }
    Write-InstallLog 'commit 후 shortcut repair를 완료했습니다.'
}

function New-ShortcutRepairPendingException {
    param(
        [Parameter(Mandatory = `$true)][string]`$Message,
        [System.Exception]`$InnerException
    )

    `$exception =
        [System.InvalidOperationException]::new(
            `$Message,
            `$InnerException)
    `$exception.Data['GeoraePlanShortcutRepairPending'] = `$true
    return `$exception
}

function Test-ShortcutRepairPendingException {
    param([Parameter(Mandatory = `$true)][System.Exception]`$Exception)

    return `$Exception.Data.Contains(
        'GeoraePlanShortcutRepairPending')
}

function New-OriginUserMismatchException {
    param([Parameter(Mandatory = `$true)][string]`$Message)

    `$exception =
        [System.InvalidOperationException]::new(`$Message)
    `$exception.Data['GeoraePlanOriginUserMismatch'] = `$true
    return `$exception
}

function Test-OriginUserMismatchException {
    param([Parameter(Mandatory = `$true)][System.Exception]`$Exception)

    return `$Exception.Data.Contains(
        'GeoraePlanOriginUserMismatch')
}

function Invoke-WorkerUnderRollbackSupervisor {
    `$elevationResult = Ensure-ElevatedIfNeeded
    if (`$elevationResult.Relaunched) {
        `$script:SupervisorRelaunchedByChild = `$true
        return [int]`$elevationResult.ExitCode
    }

    while (`$true) {
        try {
            [void](Invoke-PendingSupervisorRecovery)
            break
        }
        catch {
            if (Test-OriginUserMismatchException -Exception `$_.Exception) {
                Write-InstallLog (
                    '다른 Windows 사용자의 pending state를 수정하지 않고 실패로 종료합니다. Error={0}' -f
                    `$_.Exception)
                Show-InstallError (
                    '중단된 설치 상태를 만든 Windows 사용자 계정으로 다시 실행해 주세요.')
                return 3
            }
            if (Test-ShortcutRepairPendingException -Exception `$_.Exception) {
                Write-InstallLog (
                    'shortcut repair pending state를 유지하고 설치를 실패로 종료합니다. Error={0}' -f
                    `$_.Exception)
                Show-InstallError (
                    '설치 파일은 반영되었지만 바로가기 복구가 완료되지 않았습니다. 동일한 검증된 설치 패키지를 다시 실행해 주세요.')
                return 2
            }
            Write-InstallLog ("pending supervisor 복구/정리 실패; 설치 gate를 유지하고 재시도합니다. Error={0}" -f `$_.Exception)
            Start-Sleep -Seconds 5
        }
    }

    if (`$RecoveryOnly -or
        `$env:GEORAEPLAN_INSTALLER_TEST_RECOVERY_ONLY -eq '1') {
        Write-InstallLog 'recovery-only 검증을 완료했습니다. 새 worker는 시작하지 않습니다.'
        return 0
    }

    `$packageRoot = Split-Path -Parent `$InstallerScriptPath
    `$sourceRoot = Join-Path `$packageRoot 'App'
    if (-not (Test-SupervisorPathExistsFailClosed -Path `$sourceRoot)) {
        throw "App source not found: `$sourceRoot"
    }

    Write-InstallLog 'supervisor가 설치 공간과 rollback 대상을 확인합니다.'
    Ensure-SufficientInstallSpace -SourceRoot `$sourceRoot
    `$journal = `$null
    `$journalPath = `$null
    `$worker = `$null
    `$workerJob = `$null
    `$workerStartPipe = `$null
    `$workerStarted = `$false
    try {
        `$journal = New-SupervisorJournal
        `$journalPath = Join-Path `$journal.StateRoot 'journal.json'
        `$workerJob =
            [GeoraePlanInstaller.WorkerJob]::new(
                [string]`$journal.WorkerJobName)
        `$workerStartPipeName =
            'GeoraePlanInstaller.WorkerStart.' +
            [Guid]::NewGuid().ToString('N')
        `$workerStartToken = [Guid]::NewGuid().ToString('N')
        `$workerStartPipe =
            New-WorkerStartPipeServer -Name `$workerStartPipeName
        `$workerStartServerProcessId = `$PID
        if (-not [string]::IsNullOrWhiteSpace(
                `$env:GEORAEPLAN_INSTALLER_TEST_WORKER_START_SERVER_PID)) {
            `$workerStartServerProcessId = [int](
                `$env:GEORAEPLAN_INSTALLER_TEST_WORKER_START_SERVER_PID)
        }

        `$workerArgumentParts = @(
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy Bypass',
            ('-File "{0}"' -f `$InstallerScriptPath),
            ('-InstallRoot "{0}"' -f `$InstallRoot),
            '-WorkerMode',
            '-NoLaunch',
            ('-WorkerStartPipeName "{0}"' -f `$workerStartPipeName),
            ('-WorkerStartToken "{0}"' -f `$workerStartToken),
            ('-WorkerStartServerProcessId {0}' -f
                `$workerStartServerProcessId),
            ('-OriginUserProcessId {0}' -f
                `$OriginUserProcessId),
            ('-OriginUserProcessPath "{0}"' -f
                `$OriginUserProcessPath),
            ('-OriginUserProcessStartTimeUtcTicks {0}' -f
                `$OriginUserProcessStartTimeUtcTicks),
            ('-WorkerTimeoutSeconds {0}' -f `$WorkerTimeoutSeconds)
        )
        if (`$NoShortcuts) {
            `$workerArgumentParts += '-NoShortcuts'
        }
        if (`$SuppressUi) {
            `$workerArgumentParts += '-SuppressUi'
        }
        if (`$useLegacyBridgeCopy) {
            `$workerArgumentParts += '-LegacyBridgeCopy'
        }
        if (`$UpdaterOwnsInstallRootGate) {
            `$workerArgumentParts += '-UpdaterOwnsInstallRootGate'
        }
        elseif (`$BootstrapperOwnsInstallRootGate) {
            `$workerArgumentParts += '-BootstrapperOwnsInstallRootGate'
        }
        if (`$UpdaterOwnsInstallRootGate -or
            `$BootstrapperOwnsInstallRootGate) {
            `$workerArgumentParts +=
                ('-InstallRootGateOwnerProcessId {0}' -f
                    `$InstallRootGateOwnerProcessId)
            `$workerArgumentParts +=
                ('-InstallRootGateOwnerProcessPath "{0}"' -f
                    `$InstallRootGateOwnerProcessPath)
            `$workerArgumentParts +=
                ('-InstallRootGateOwnerProcessStartTimeUtcTicks {0}' -f
                    `$InstallRootGateOwnerProcessStartTimeUtcTicks)
        }
        else {
            `$supervisorProcess =
                Get-Process -Id `$PID -ErrorAction Stop
            try {
                `$supervisorProcessPath =
                    `$supervisorProcess.MainModule.FileName
                `$supervisorProcessStartTimeUtcTicks =
                    `$supervisorProcess.StartTime.ToUniversalTime().Ticks
            }
            finally {
                if (`$supervisorProcess -is [System.IDisposable]) {
                    `$supervisorProcess.Dispose()
                }
            }
            `$workerArgumentParts += '-BootstrapperOwnsInstallRootGate'
            `$workerArgumentParts +=
                ('-InstallRootGateOwnerProcessId {0}' -f `$PID)
            `$workerArgumentParts +=
                ('-InstallRootGateOwnerProcessPath "{0}"' -f
                    `$supervisorProcessPath)
            `$workerArgumentParts +=
                ('-InstallRootGateOwnerProcessStartTimeUtcTicks {0}' -f
                    `$supervisorProcessStartTimeUtcTicks)
        }
        if (-not [string]::IsNullOrWhiteSpace(`$LogPath)) {
            `$workerArgumentParts += ('-LogPath "{0}"' -f `$LogPath)
        }

        if (@(`$heldInstallWorkerLeases).Count -gt 0) {
            Exit-InstallRootGates -Gates `$heldInstallWorkerLeases
            `$script:heldInstallWorkerLeases = @()
        }
        Write-InstallLog ("worker 설치 프로세스를 시작합니다. TimeoutSeconds={0}" -f `$WorkerTimeoutSeconds)
        `$workerStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        `$workerStartInfo.FileName = 'powershell.exe'
        `$workerStartInfo.Arguments = (`$workerArgumentParts -join ' ')
        `$workerStartInfo.WorkingDirectory = `$packageRoot
        `$workerStartInfo.UseShellExecute = `$false
        `$workerStartInfo.CreateNoWindow = `$true
        `$workerStartInfo.RedirectStandardOutput = `$true
        `$workerStartInfo.RedirectStandardError = `$true
        `$workerStartInfo.StandardOutputEncoding =
            [System.Text.Encoding]::Default
        `$workerStartInfo.StandardErrorEncoding =
            [System.Text.Encoding]::Default
        `$worker = [System.Diagnostics.Process]::new()
        `$worker.StartInfo = `$workerStartInfo
        if (-not `$worker.Start()) {
            throw 'worker 설치 프로세스를 시작하지 못했습니다.'
        }
        `$workerStarted = `$true
        `$workerJob.AssignProcess(`$worker)
        `$worker.Refresh()
        `$workerProcessPath =
            Resolve-InstallerPathIdentity -Path (
                `$worker.MainModule.FileName)
        `$workerProcessStartTimeUtcTicks =
            `$worker.StartTime.ToUniversalTime().Ticks
        `$journal.WorkerProcessId = `$worker.Id
        `$journal.WorkerProcessPath = `$workerProcessPath
        `$journal.WorkerProcessStartTimeUtcTicks =
            `$workerProcessStartTimeUtcTicks
        `$journal.Phase = 'WorkerRunning'
        Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
        Wait-AndAuthorizeWorkerStart -Pipe `$workerStartPipe -Worker `$worker -Token `$workerStartToken
        `$workerStartPipe.Dispose()
        `$workerStartPipe = `$null
        `$workerStdoutTask = `$worker.StandardOutput.ReadToEndAsync()
        `$workerStderrTask = `$worker.StandardError.ReadToEndAsync()
        `$workerCompleted = `$worker.WaitForExit(`$WorkerTimeoutSeconds * 1000)
        if (-not `$workerCompleted) {
            Write-InstallLog ("worker timeout. PID={0}" -f `$worker.Id)
            `$workerJob.TerminateAndWait(253, 120000)
            `$worker.WaitForExit()
        }

        `$worker.WaitForExit()
        `$workerJob.WaitForEmpty(30000)
        `$workerStdout = `$workerStdoutTask.GetAwaiter().GetResult()
        `$workerStderr = `$workerStderrTask.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrWhiteSpace(`$workerStdout)) {
            Write-Host `$workerStdout.TrimEnd()
        }
        if (-not [string]::IsNullOrWhiteSpace(`$workerStderr)) {
            [Console]::Error.WriteLine(`$workerStderr.TrimEnd())
        }
        if (-not `$workerCompleted) {
            throw ("worker timeout 후 프로세스 트리 종료를 확인했습니다. PID={0}" -f `$worker.Id)
        }
        if (`$worker.ExitCode -ne 0) {
            throw ("worker 설치가 실패했습니다. ExitCode={0}" -f `$worker.ExitCode)
        }

        if ([bool]`$journal.ShortcutRepair.Enabled) {
            `$journal.Phase = 'ShortcutRepairPending'
            Assert-ShortcutRepairJournalBinding -Journal `$journal
            Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
            if (`$env:GEORAEPLAN_INSTALLER_TEST_CRASH_AFTER_SHORTCUT_REPAIR_PENDING -eq
                '1') {
                Write-InstallLog 'Injected crash after ShortcutRepairPending journal flush.'
                [System.Diagnostics.Process]::GetCurrentProcess().Kill()
                throw 'Injected crash did not terminate the process.'
            }
            try {
                Invoke-PendingShortcutRepair -Repair `$journal.ShortcutRepair
            }
            catch {
                Write-InstallLog (
                    'shortcut repair pending state를 유지하고 설치를 실패로 종료합니다. Error={0}' -f
                    `$_.Exception)
                Show-InstallError (
                    '설치 파일은 반영되었지만 바로가기 복구가 완료되지 않았습니다. 동일한 검증된 설치 패키지를 다시 실행해 주세요.')
                return 2
            }
        }

        `$journal.Phase = 'CommittedCleanupPending'
        Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath
        `$cleanupComplete = `$false
        while (-not `$cleanupComplete) {
            try {
                Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath
                `$cleanupComplete = `$true
            }
            catch {
                Write-InstallLog ("commit cleanup-pending; supervisor와 설치 gate를 유지하고 재시도합니다. Error={0}" -f `$_.Exception)
                Start-Sleep -Seconds 5
            }
        }

        Write-InstallLog 'supervisor가 worker 성공과 durable rollback state 정리를 확인했습니다.'
    }
    catch {
        `$supervisorFailure = `$_.Exception
        Write-InstallLog ("supervisor가 독립 rollback을 시작합니다. Error={0}" -f `$supervisorFailure)

        `$workerBarrierComplete = `$false
        while (-not `$workerBarrierComplete) {
            try {
                if (`$null -ne `$workerJob) {
                    `$workerJob.TerminateAndWait(252, 120000)
                    if (`$workerJob.ActiveProcessCount -ne 0) {
                        throw 'rollback 전 worker job active process가 남아 있습니다.'
                    }
                }
                if (`$null -ne `$worker -and
                    -not `$worker.HasExited) {
                    # This branch is reachable only before successful job
                    # assignment/GO, so the worker cannot have mutated.
                    `$worker.Kill()
                    `$worker.WaitForExit()
                }
                `$workerBarrierComplete = `$true
            }
            catch {
                Write-InstallLog ("rollback 전 worker job barrier 실패; supervisor와 설치 gate를 유지하고 재시도합니다. Error={0}" -f `$_.Exception)
                Start-Sleep -Seconds 5
            }
        }

        `$rollbackComplete = `$false
        while (-not `$rollbackComplete) {
            try {
                [void](Invoke-PendingSupervisorRecovery)
                `$rollbackComplete = `$true
            }
            catch {
                Write-InstallLog ("rollback/cleanup-pending; supervisor와 설치 gate를 유지하고 재시도합니다. Error={0}" -f `$_.Exception)
                Start-Sleep -Seconds 5
            }
        }

        if (`$workerStarted) {
            Write-InstallLog 'supervisor가 기존 설치본의 검증된 rollback과 durable state 정리를 완료했습니다.'
        }
        else {
            Write-InstallLog 'worker 시작 전 supervisor 실패 state 정리를 완료했습니다.'
        }

        Show-InstallError (`$supervisorFailure.Message)
        return 1
    }
    finally {
        if (`$null -ne `$workerStartPipe) {
            `$workerStartPipe.Dispose()
        }
        if (`$null -ne `$workerJob) {
            `$workerJob.Dispose()
        }
        if (`$null -ne `$worker) {
            `$worker.Dispose()
        }
    }

    return 0
}

`$ErrorActionPreference = 'Stop'
`$heldInstallOperationLeases = @()
`$heldInstallWorkerLeases = @()
`$script:SupervisorRelaunchedByChild = `$false
Assert-InstallerTestHooksAllowed
if (`$WorkerMode) {
    `$NoLaunch = `$true
}
if (`$UpdaterOwnsInstallRootGate -or
    `$BootstrapperOwnsInstallRootGate) {
    Assert-InstallRootGateOwnerIdentity
}
if (`$WorkerMode) {
    Assert-InstallOperationLeasesHeld
    if (Test-ProtectedInstallRoot -Path `$InstallRoot) {
        `$workerIdentity =
            [Security.Principal.WindowsIdentity]::GetCurrent()
        `$workerPrincipal =
            [Security.Principal.WindowsPrincipal]::new(`$workerIdentity)
        if (-not `$workerPrincipal.IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw '보호된 설치 경로의 WorkerMode는 supervisor가 높은 권한으로 시작해야 합니다.'
        }
    }
    `$heldInstallWorkerLeases = @(
        Enter-InstallWorkerLeases
    )
    Confirm-WorkerStartAuthorization
}
if (-not `$WorkerMode) {
    `$heldInstallRootGates = @()
    try {
        if (-not `$UpdaterOwnsInstallRootGate -and
            -not `$BootstrapperOwnsInstallRootGate) {
            `$heldInstallRootGates = @(
                Enter-InstallRootGates
            )
        }
        `$heldInstallOperationLeases = @(
            Enter-InstallOperationLeases
        )
        `$heldInstallWorkerLeases = @(
            Enter-InstallWorkerLeases
        )
        `$supervisorExitValues = @(
            Invoke-WorkerUnderRollbackSupervisor
        )
        if (`$supervisorExitValues.Count -eq 0) {
            throw 'supervisor가 명시적인 exit code를 반환하지 않았습니다.'
        }
        `$supervisorExitCode =
            [int]`$supervisorExitValues[`$supervisorExitValues.Count - 1]
        if (`$supervisorExitCode -lt 0 -or
            `$supervisorExitCode -gt 255) {
            throw "supervisor exit code 범위가 올바르지 않습니다: `$supervisorExitCode"
        }
    }
    finally {
        if (@(`$heldInstallRootGates).Count -gt 0) {
            if (@(`$heldInstallWorkerLeases).Count -gt 0) {
                Exit-InstallRootGates -Gates `$heldInstallWorkerLeases
            }
            if (@(`$heldInstallOperationLeases).Count -gt 0) {
                Exit-InstallRootGates -Gates `$heldInstallOperationLeases
            }
            Exit-InstallRootGates -Gates `$heldInstallRootGates
        }
        elseif (@(`$heldInstallOperationLeases).Count -gt 0) {
            if (@(`$heldInstallWorkerLeases).Count -gt 0) {
                Exit-InstallRootGates -Gates `$heldInstallWorkerLeases
            }
            Exit-InstallRootGates -Gates `$heldInstallOperationLeases
        }
        elseif (@(`$heldInstallWorkerLeases).Count -gt 0) {
            Exit-InstallRootGates -Gates `$heldInstallWorkerLeases
        }
    }

    if (`$supervisorExitCode -eq 0 -and -not `$NoLaunch) {
        `$launchFromCurrentToken = `$true
        if (Test-ProtectedInstallRoot -Path `$InstallRoot) {
            `$launchIdentity =
                [Security.Principal.WindowsIdentity]::GetCurrent()
            `$launchPrincipal =
                [Security.Principal.WindowsPrincipal]::new(`$launchIdentity)
            if (`$launchPrincipal.IsInRole(
                    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
                `$launchFromCurrentToken = `$false
                Write-InstallLog '높은 권한 프로세스에서는 앱 자동 실행을 생략합니다.'
            }
        }

        if (`$launchFromCurrentToken) {
            foreach (`$launchProbeRoot in @(
                Get-SupervisorRecoveryInstallRoots
            )) {
                `$pendingStateRoot =
                    Get-SupervisorStateRoot -InstallPath `$launchProbeRoot
                if (Test-SupervisorPathExistsFailClosed -Path `$pendingStateRoot) {
                    throw "commit 후 복구 state가 남아 있어 앱을 실행하지 않습니다: `$pendingStateRoot"
                }
            }
            `$launchExecutable = Join-Path `$InstallRoot '__APP_DISPLAY_NAME__.exe'
            if (-not (Test-SupervisorPathExistsFailClosed -Path `$launchExecutable)) {
                throw "commit 후 실행할 거래플랜 파일을 찾지 못했습니다: `$launchExecutable"
            }
            Write-InstallLog 'install-root gate 해제 후 앱을 실행합니다.'
            Start-Process -FilePath `$launchExecutable -WorkingDirectory `$InstallRoot
        }
    }
    exit `$supervisorExitCode
}

`$installRollbackSnapshots = @()

try {
    Write-InstallLog ("설치 시작. InstallRoot={0}" -f `$InstallRoot)
    Ensure-ElevatedIfNeeded

    if (`$env:GEORAEPLAN_INSTALLER_TEST_SPAWN_DELAYED_DESCENDANT -eq
        '1') {
        `$descendantMutationPath = Join-Path `$InstallRoot '.georaeplan-installer-delayed-descendant-mutation'
        `$descendantPidPath = Join-Path (Split-Path -Parent `$InstallerScriptPath) '.georaeplan-installer-delayed-descendant.pid'
        `$descendantDelayMilliseconds = 5000
        if (-not [string]::IsNullOrWhiteSpace(
                `$env:GEORAEPLAN_INSTALLER_TEST_DELAYED_DESCENDANT_DELAY_MS)) {
            `$descendantDelayMilliseconds = [int](
                `$env:GEORAEPLAN_INSTALLER_TEST_DELAYED_DESCENDANT_DELAY_MS)
        }
        `$escapedDescendantMutationPath =
            `$descendantMutationPath.Replace("'", "''")
        `$descendantScript =
            "Start-Sleep -Milliseconds `$descendantDelayMilliseconds; " +
            "[System.IO.File]::WriteAllText(" +
            "'`$escapedDescendantMutationPath'," +
            "'job descendant escaped termination')"
        `$encodedDescendantScript =
            [Convert]::ToBase64String(
                [System.Text.Encoding]::Unicode.GetBytes(
                    `$descendantScript))
        `$descendantArguments =
            '-NoProfile -NonInteractive -EncodedCommand ' +
            `$encodedDescendantScript
        `$descendantProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList `$descendantArguments -WindowStyle Hidden -PassThru
        try {
            [System.IO.File]::WriteAllText(
                `$descendantPidPath,
                `$descendantProcess.Id.ToString(),
                [System.Text.Encoding]::UTF8)
        }
        finally {
            `$descendantProcess.Dispose()
        }
        if (`$env:GEORAEPLAN_INSTALLER_TEST_EXIT_AFTER_DESCENDANT_SPAWN -eq
            '1') {
            Write-InstallLog 'Injected worker exit after delayed descendant spawn.'
            exit 0
        }
    }

    `$packageRoot = Split-Path -Parent `$MyInvocation.MyCommand.Path
    `$sourceRoot = Join-Path `$packageRoot 'App'
    `$protectedInstall = Test-ProtectedInstallRoot -Path `$InstallRoot
    `$desktopDir = if (`$protectedInstall) { [Environment]::GetFolderPath('CommonDesktopDirectory') } else { [Environment]::GetFolderPath('Desktop') }
    `$startMenuRoot = if (`$protectedInstall) { [Environment]::GetFolderPath('CommonPrograms') } else { [Environment]::GetFolderPath('Programs') }
    `$startMenuDir = Join-Path `$startMenuRoot '__APP_DISPLAY_NAME__'
    `$legacyUserRoot = `$LegacyUserRoot
    `$exePath = Join-Path `$InstallRoot '__APP_DISPLAY_NAME__.exe'
    `$uninstallScriptPath = Join-Path `$InstallRoot '__UNINSTALL_PS1_NAME__'

    if (-not (Test-SupervisorPathExistsFailClosed -Path `$sourceRoot)) {
        throw "App source not found: `$sourceRoot"
    }

    Write-InstallLog '설치 공간을 확인합니다.'
    Ensure-SufficientInstallSpace -SourceRoot `$sourceRoot

    if (`$env:GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT -eq '1') {
        `$pidPath = Join-Path `$packageRoot '.georaeplan-installer-hang-worker.pid'
        `$pidBytes = [System.Text.Encoding]::UTF8.GetBytes(`$PID.ToString())
        `$pidStream = [System.IO.FileStream]::new(
            `$pidPath,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::Read,
            4096,
            [System.IO.FileOptions]::WriteThrough)
        try {
            `$pidStream.Write(`$pidBytes, 0, `$pidBytes.Length)
            `$pidStream.Flush(`$true)
        }
        finally {
            `$pidStream.Dispose()
        }

        `$mutationPath = Join-Path `$InstallRoot '.georaeplan-installer-timeout-mutation'
        [System.IO.File]::WriteAllText(
            `$mutationPath,
            'rollback must remove this deterministic worker mutation',
            [System.Text.Encoding]::UTF8)
        Write-InstallLog ("deterministic test hang after supervisor rollback snapshot and worker mutation. WorkerPID={0}" -f `$PID)
        while (`$true) {
            Start-Sleep -Milliseconds 250
        }
    }

    Write-InstallLog '파일 복사를 시작합니다.'
    Invoke-RobocopyMirror -Source `$sourceRoot -Destination `$InstallRoot
    if (`$useLegacyBridgeCopy) {
        Write-InstallLog ("기존 사용자 설치 경로도 함께 갱신합니다. LegacyRoot={0}" -f `$legacyUserRoot)
        Invoke-RobocopyMirror -Source `$sourceRoot -Destination `$legacyUserRoot
    }
    Write-InstallLog '파일 복사가 완료되었습니다.'

    # Keep this list aligned with Updater Program.ValidateInstalledApplication.
    # All checks must pass before rollback snapshots are discarded.
    `$validationRoots = @(`$InstallRoot)
    if (`$useLegacyBridgeCopy) {
        `$validationRoots += `$legacyUserRoot
    }
    foreach (`$validationRoot in `$validationRoots) {
        `$validationExecutable =
            Join-Path `$validationRoot '__APP_DISPLAY_NAME__.exe'
        `$requiredInstalledFiles = @(
            `$validationExecutable,
            (Join-Path `$validationRoot 'appsettings.json'),
            (Join-Path `$validationRoot 'Updater\__APP_DISPLAY_NAME__.Updater.exe'),
            (Join-Path `$validationRoot 'Updater\거래플랜.Updater.exe')
        )
        foreach (`$requiredInstalledFile in `$requiredInstalledFiles) {
            if (-not (Test-SupervisorPathExistsFailClosed -Path `$requiredInstalledFile)) {
                throw "설치 후 필수 파일이 누락되었습니다: `$requiredInstalledFile"
            }
            `$requiredAttributes =
                [System.IO.File]::GetAttributes(`$requiredInstalledFile)
            if ((`$requiredAttributes -band
                [System.IO.FileAttributes]::Directory) -ne 0) {
                throw "설치 후 필수 파일 경로가 디렉터리입니다: `$requiredInstalledFile"
            }
        }

        `$installedVersion =
            (Get-Item -LiteralPath `$validationExecutable -Force -ErrorAction Stop).
                VersionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace(`$installedVersion)) {
            throw "설치된 실행 파일 버전을 확인하지 못했습니다: `$validationExecutable"
        }

        if (Compare-Version `$installedVersion `$ExpectedVersion -lt 0) {
            throw ("설치된 실행 파일 버전이 예상보다 낮습니다. 경로: {0}, 기대 버전: {1}, 실제 버전: {2}" -f `$validationExecutable, `$ExpectedVersion, (Normalize-VersionText `$installedVersion))
        }
    }

    `$uninstallScriptContent = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__UNINSTALL_SCRIPT_B64__'))
    `$uninstallScriptContent | Set-Content -LiteralPath `$uninstallScriptPath -Encoding UTF8

    # Shortcut mutation is intentionally deferred until the durable install
    # transaction has committed and its rollback state has been removed.

    if (-not `$NoLaunch) {
        Write-InstallLog '설치 후 앱을 다시 실행합니다.'
        Start-Process -FilePath `$exePath -WorkingDirectory `$InstallRoot
    }

    Write-Host "Install complete: `$InstallRoot"
    Write-Host "Executable: `$exePath"
    Write-InstallLog ("설치 완료. Executable={0}" -f `$exePath)
    foreach (`$snapshot in @(`$installRollbackSnapshots)) {
        Remove-InstallRollbackSnapshot -Snapshot `$snapshot
    }

    # The legacy install is intentionally removed last, after every fallible
    # post-install action has succeeded. Until here it is its own rollback copy.
    `$installRootFullPath =
        Resolve-InstallerPathIdentity -Path `$InstallRoot
    `$legacyUserRootFullPath =
        Resolve-InstallerPathIdentity -Path `$legacyUserRoot
    if (`$managesCanonicalAndLegacyRoots -and
        -not `$useLegacyBridgeCopy -and
        -not [string]::Equals(`$installRootFullPath, `$legacyUserRootFullPath, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-SupervisorPathExistsFailClosed -Path `$legacyUserRoot)) {
        Remove-Item -LiteralPath `$legacyUserRoot -Recurse -Force -ErrorAction Stop
        if (Test-SupervisorPathExistsFailClosed -Path `$legacyUserRoot) {
            throw "legacy 설치 경로를 완전히 제거하지 못했습니다: `$legacyUserRoot"
        }
    }
}
catch {
    `$installFailure = `$_.Exception
    Write-InstallLog ("설치 실패: {0}" -f `$installFailure)
    foreach (`$snapshot in @(`$installRollbackSnapshots)) {
        `$rollbackRestored = `$false
        try {
            Restore-InstallRollbackSnapshot -Snapshot `$snapshot
            `$rollbackRestored = `$true
        }
        catch {
            Write-InstallLog ("rollback 복구 실패; 복구 snapshot을 보존합니다. Snapshot={0}, Error={1}" -f `$snapshot.BackupRoot, `$_.Exception)
        }
        if (`$rollbackRestored) {
            Remove-InstallRollbackSnapshot -Snapshot `$snapshot
        }
    }
    Show-InstallError (`$installFailure.Message)
    exit 1
}
"@

$installScript = $installScriptTemplate
$installScript = $installScript.Replace('__EXPECTED_VERSION__', $desktopVersion)
$installScript = $installScript.Replace(
    '__INSTALLER_TEST_HOOKS_ENABLED__',
    $(if ($EnableTestHooks) { '$true' } else { '$false' }))
$installScript = $installScript.Replace('__UNINSTALL_PS1_NAME__', $uninstallPs1Name)
$installScript = $installScript.Replace('__UNINSTALL_SCRIPT_B64__', $uninstallScriptBodyBase64)
$installScript = $installScript.Replace('__REMOVE_SHORTCUT_SUFFIX__', $removeShortcutSuffix)
$installScript = $installScript.Replace(
    '__APP_DISPLAY_NAME__',
    $appDisplayNamePowerShellLiteralContent)
Assert-PowerShellScriptSyntax `
    -ScriptContent $installScript `
    -Description 'generated desktop installer'
$cmdScript = @"
@echo off
setlocal
powershell -ExecutionPolicy Bypass -File "%~dp0$installPs1Name"
endlocal
"@

$readme = @(
    "$AppDisplayName PC install package",
    '',
    '1. Extract the zip file.',
    "2. Run '$installCmdName'.",
    "3. After install, launch '$AppDisplayName' from the desktop or Start Menu.",
    '',
    'Default install path:',
    "C:\Program Files (x86)\tradeplan",
    '',
    'Default server URL:',
    $(if ([string]::IsNullOrWhiteSpace($serverUrl)) { 'Check appsettings.json' } else { $serverUrl })
) -join [Environment]::NewLine

$installScript | Set-Content -LiteralPath (Join-Path $packageRoot $installPs1Name) -Encoding UTF8
$cmdScript | Set-Content -LiteralPath (Join-Path $packageRoot $installCmdName) -Encoding ASCII
$readme | Set-Content -LiteralPath (Join-Path $packageRoot 'README.txt') -Encoding UTF8
if ($EnableTestHooks) {
    [System.IO.File]::WriteAllText(
        (Join-Path $packageRoot $testHookCapabilityMarkerName),
        $testHookCapability,
        [System.Text.UTF8Encoding]::new($false))
}

$transactionId = [System.Guid]::NewGuid().ToString('N')
$stagedZipName = '.{0}.{1}.staged.zip' -f
    $PackageName,
    $transactionId
$stagedZipPath = Get-ContainedDirectChildPath `
    -ParentPath $adminOutputRoot `
    -ChildName $stagedZipName `
    -Description 'staged package archive'
$replacementBackupZipName = '.{0}.{1}.previous.zip' -f
    $PackageName,
    $transactionId
$replacementBackupZipPath = Get-ContainedDirectChildPath `
    -ParentPath $adminOutputRoot `
    -ChildName $replacementBackupZipName `
    -Description 'package archive replacement backup'
$replacementBackupSidecarName =
    '.{0}.{1}.previous.sha256.txt' -f
        $zipName,
        $transactionId
$transactionReservedNames = @(
    $stagedZipName,
    $replacementBackupZipName,
    $replacementBackupSidecarName,
    ('.{0}.{1}.staged.sha256.txt' -f
        $zipName,
        $transactionId),
    ('.{0}.{1}.failed-publish.zip' -f
        $PackageName,
        $transactionId),
    ('.{0}.{1}.failed-publish.sha256.txt' -f
        $zipName,
        $transactionId)
)
foreach ($transactionReservedName in $transactionReservedNames) {
    $transactionReservedPath = Get-ContainedDirectChildPath `
        -ParentPath $adminOutputRoot `
        -ChildName $transactionReservedName `
        -Description 'reserved package publish transaction path'
    Assert-PathIsNotReparsePoint `
        -Path $transactionReservedPath `
        -Description 'reserved package publish transaction path'
    if (Test-Path -LiteralPath $transactionReservedPath) {
        throw "A reserved package publish transaction path already exists: $transactionReservedPath"
    }
}
$zipHashSidecarPath = ''
$transactionMarkerLease = $null
$validatedTransaction = $null
$validatedStagedZipOwner = $null
$stagedZipOwnerMarkerLease = $null
$stagedZipOwnerMarkerHash = ''
try {
    $stagedZipOwner = [PSCustomObject]@{
        SchemaVersion = 1
        TransactionId = $transactionId
        PackageName = $PackageName
        StagedZipName = $stagedZipName
    }
    $stagedZipOwnerResult = Write-PackageStagedZipOwnerMarker `
        -ParentPath $adminOutputRoot `
        -Owner $stagedZipOwner
    $validatedStagedZipOwner =
        [PSCustomObject]$stagedZipOwnerResult.Owner
    $stagedZipOwnerMarkerLease =
        [System.IO.FileStream]$stagedZipOwnerResult.Lease
    $stagedZipOwnerMarkerHash =
        [string]$stagedZipOwnerResult.MarkerHash

    Compress-Archive `
        -Path (Join-Path $packageRoot '*') `
        -DestinationPath $stagedZipPath `
        -CompressionLevel Optimal `
        -ErrorAction Stop
    if ($EnableTestHooks -and
        -not [string]::IsNullOrWhiteSpace(
            $env:GEORAEPLAN_PACKAGE_TEST_STAGED_ZIP_KILL_SIGNAL)) {
        $killSignalPath =
            $env:GEORAEPLAN_PACKAGE_TEST_STAGED_ZIP_KILL_SIGNAL
        $killSignalDeadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not (Test-Path `
                -LiteralPath $killSignalPath `
                -PathType Leaf)) {
            if ([DateTime]::UtcNow -ge $killSignalDeadline) {
                throw 'Timed out waiting for the staged ZIP hard-kill test signal.'
            }
            Start-Sleep -Milliseconds 50
        }
    }
    if ($EnableTestHooks -and
        $env:GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_STAGED_ZIP_CREATE -eq '1') {
        [System.Diagnostics.Process]::GetCurrentProcess().Kill()
    }
    if ($EnableTestHooks -and
        $env:GEORAEPLAN_PACKAGE_TEST_FAIL_AFTER_STAGED_ZIP_CREATE -eq '1') {
        throw 'Injected staged package archive failure before durable publish transaction.'
    }
    Assert-DesktopPackageArchive `
        -ArchivePath $stagedZipPath `
        -AppDisplayName $AppDisplayName `
        -InstallCmdName $installCmdName `
        -ExpectedVersion $desktopVersion
    $stagedZipHash = (
        Get-FileHash `
            -LiteralPath $stagedZipPath `
            -Algorithm SHA256 `
            -ErrorAction Stop).Hash

    $hadExistingZip = Test-Path -LiteralPath $zipPath -PathType Leaf
    $previousZipHash = ''
    if ($hadExistingZip) {
        Assert-PathIsNotReparsePoint `
            -Path $zipPath `
            -Description 'existing package archive'
        $previousZipHash = (
            Get-FileHash `
                -LiteralPath $zipPath `
                    -Algorithm SHA256 `
                    -ErrorAction Stop).Hash
    }

    $sidecarName = $zipName + '.sha256.txt'
    $sidecarPath = Get-ContainedDirectChildPath `
        -ParentPath $adminOutputRoot `
        -ChildName $sidecarName `
        -Description 'package archive SHA-256 sidecar'
    $hadExistingSidecar =
        Test-Path -LiteralPath $sidecarPath -PathType Leaf
    $previousSidecarContentBase64 = ''
    $previousSidecarHash = ''
    if ($hadExistingSidecar) {
        Assert-PathIsNotReparsePoint `
            -Path $sidecarPath `
            -Description 'existing package archive SHA-256 sidecar'
        $previousSidecarBytes =
            [System.IO.File]::ReadAllBytes($sidecarPath)
        if ($previousSidecarBytes.Length -gt 4096) {
            throw "Existing package archive SHA-256 sidecar is unexpectedly large: $sidecarPath"
        }
        $previousSidecarContentBase64 =
            [Convert]::ToBase64String($previousSidecarBytes)
        $previousSidecarHash =
            Get-Sha256FromBytes -Bytes $previousSidecarBytes
    }

    $transaction = [PSCustomObject]@{
        SchemaVersion = 1
        TransactionId = $transactionId
        PackageName = $PackageName
        ZipName = $zipName
        SidecarName = $sidecarName
        StagedZipName = $stagedZipName
        BackupZipName = $replacementBackupZipName
        BackupSidecarName = $replacementBackupSidecarName
        NewZipHash = $stagedZipHash
        HadExistingZip = [bool]$hadExistingZip
        PreviousZipHash = $previousZipHash
        HadExistingSidecar = [bool]$hadExistingSidecar
        PreviousSidecarContentBase64 =
            $previousSidecarContentBase64
        PreviousSidecarHash = $previousSidecarHash
    }
    $transactionLeaseResult = Write-PackagePublishTransaction `
        -ParentPath $adminOutputRoot `
        -Transaction $transaction
    $transactionMarkerLease =
        [System.IO.FileStream]$transactionLeaseResult.Lease
    $validatedTransaction =
        [PSCustomObject]$transactionLeaseResult.Transaction
    if ($EnableTestHooks -and
        $env:GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_DURABLE_TRANSACTION_WRITE -eq '1') {
        [System.Diagnostics.Process]::GetCurrentProcess().Kill()
    }
    try {
        Remove-PackageStagedZipOwnerMarker `
            -ParentPath $adminOutputRoot `
            -ExpectedPackageName $PackageName `
            -ValidatedOwner $validatedStagedZipOwner `
            -MarkerLease $stagedZipOwnerMarkerLease `
            -ExpectedMarkerHash $stagedZipOwnerMarkerHash
    }
    finally {
        $stagedZipOwnerMarkerLease = $null
    }
    $validatedStagedZipOwner = $null
    $stagedZipOwnerMarkerHash = ''

    if ($hadExistingZip) {
        [System.IO.File]::Replace(
            $stagedZipPath,
            $zipPath,
            $replacementBackupZipPath,
            $true)
    }
    else {
        [System.IO.File]::Move($stagedZipPath, $zipPath)
    }

    if ($EnableTestHooks -and
        $env:GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_ZIP_REPLACE -eq '1') {
        [System.Diagnostics.Process]::GetCurrentProcess().Kill()
    }
    if ($EnableTestHooks -and
        $env:GEORAEPLAN_PACKAGE_TEST_FAIL_AFTER_ZIP_REPLACE -eq '1') {
        throw 'Injected package archive post-replacement verification failure.'
    }

    Assert-DesktopPackageArchive `
        -ArchivePath $zipPath `
        -AppDisplayName $AppDisplayName `
        -InstallCmdName $installCmdName `
        -ExpectedVersion $desktopVersion

    $publishedZipLease = [System.IO.File]::Open(
        $zipPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $sidecarPublishResult = Publish-Sha256Sidecar `
            -ParentPath $adminOutputRoot `
            -ArtifactPath $zipPath `
            -ArtifactStream $publishedZipLease `
            -ExpectedArtifactHash $stagedZipHash `
            -TransactionId $transactionId `
            -BackupSidecarName $replacementBackupSidecarName
        $zipHashSidecarPath =
            [string]$sidecarPublishResult.Path
        $publishedZipHash =
            [string]$sidecarPublishResult.Hash
        if ($EnableTestHooks -and
            $env:GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_SIDECAR_PUBLISH -eq '1') {
            [System.Diagnostics.Process]::GetCurrentProcess().Kill()
        }
    }
    finally {
        $publishedZipLease.Dispose()
    }

    try {
        $normalCompletionResult =
            Invoke-PackagePublishTransactionRecovery `
                -ParentPath $adminOutputRoot `
                -ExpectedPackageName $PackageName `
                -ValidatedTransaction $validatedTransaction `
                -TransactionMarkerLease $transactionMarkerLease
    }
    finally {
        $transactionMarkerLease = $null
    }
}
catch {
    $archivePublishFailure = $_.Exception
    try {
        if ($null -ne $transactionMarkerLease) {
            try {
                $failureRecoveryResult =
                    Invoke-PackagePublishTransactionRecovery `
                        -ParentPath $adminOutputRoot `
                        -ExpectedPackageName $PackageName `
                        -ValidatedTransaction $validatedTransaction `
                        -TransactionMarkerLease $transactionMarkerLease
            }
            finally {
                $transactionMarkerLease = $null
            }
        }
        else {
            $failureRecoveryResult =
                Invoke-PackagePublishTransactionRecovery `
                    -ParentPath $adminOutputRoot `
                    -ExpectedPackageName $PackageName
        }
    }
    catch {
        throw [System.InvalidOperationException]::new(
            'Package archive publish failed and its durable transaction could not be recovered.',
            [System.AggregateException]::new(
                @($archivePublishFailure, $_.Exception)))
    }
    throw $archivePublishFailure
}
finally {
    try {
        if ($null -ne $stagedZipOwnerMarkerLease) {
            $null = Invoke-PackageStagedZipOwnerRecovery `
                -ParentPath $adminOutputRoot `
                -ExpectedPackageName $PackageName `
                -PrevalidatedOwner $validatedStagedZipOwner `
                -PreopenedMarkerLease $stagedZipOwnerMarkerLease `
                -PreopenedMarkerHash $stagedZipOwnerMarkerHash
        }
        else {
            $null = Invoke-PackageStagedZipOwnerRecovery `
                -ParentPath $adminOutputRoot `
                -ExpectedPackageName $PackageName
        }
    }
    finally {
        if ($null -ne $stagedZipOwnerMarkerLease -and
            $stagedZipOwnerMarkerLease.CanRead) {
            $stagedZipOwnerMarkerLease.Dispose()
            $stagedZipOwnerMarkerLease = $null
        }
    }
}

Write-Host "package_ready root=$packageRoot"
Write-Host "package_zip=$zipPath"
Write-Host "package_zip_sha256=$zipHashSidecarPath"

if (-not $SkipNativeInstallers) {
    $nativeInstallerScript = Join-Path $scriptRoot 'Build-GeoraePlanDesktopNativeInstallers.ps1'
    if (Test-Path -LiteralPath $nativeInstallerScript) {
        $nativeInstallerArguments = @(
            '-ExecutionPolicy', 'Bypass',
            '-File', $nativeInstallerScript,
            '-ProjectRoot', $ProjectRoot,
            '-SourceFolder', $appRoot,
            '-OutputRoot', $OutputRoot,
            '-PackageName', $PackageName,
            '-AppDisplayName', $AppDisplayName
        )
        if (-not [string]::IsNullOrWhiteSpace($WindowsSigningConfigPath)) {
            $nativeInstallerArguments += @('-WindowsSigningConfigPath', $WindowsSigningConfigPath)
        }
        if ($RequireWindowsAuthenticode) {
            $nativeInstallerArguments += '-RequireWindowsAuthenticode'
        }

        & powershell @nativeInstallerArguments
        if ($LASTEXITCODE -ne 0) {
            throw 'Native installer generation failed.'
        }
    }
}
}
finally {
    try {
        if (-not [string]::IsNullOrWhiteSpace($generatedSourceFolder) -and
            (Test-Path -LiteralPath $generatedSourceFolder)) {
            $generatedSourceFullPath =
                Get-NormalizedPathForComparison -Path $generatedSourceFolder
            $generatedSourceParent =
                Get-NormalizedPathForComparison -Path (
                    [System.IO.Path]::GetDirectoryName(
                        $generatedSourceFullPath))
            $tempFullPath =
                Get-NormalizedPathForComparison -Path $env:TEMP
            $generatedSourceName =
                [System.IO.Path]::GetFileName($generatedSourceFullPath)
            if (-not [string]::Equals(
                    $generatedSourceParent,
                    $tempFullPath,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                -not $generatedSourceName.StartsWith(
                    'georaeplan-desktop-package-publish-',
                    [System.StringComparison]::Ordinal)) {
                throw "Generated desktop publish cleanup target is unsafe: $generatedSourceFolder"
            }
            $null = Remove-ContainedOutputItem `
                -ParentPath $tempFullPath `
                -ChildName $generatedSourceName `
                -Description 'generated desktop publish directory' `
                -Recurse
        }
    }
    finally {
        if ($null -ne $packageBuilderLockStream) {
            $packageBuilderLockStream.Dispose()
            Write-Host "package_builder_lock=RELEASED path=$adminOutputRoot"
        }
    }
}
