[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputRoot,
    [string]$Channel = 'stable',
    [int]$KeepDesktopPackageCount = 2,
    [int]$KeepAndroidPackageCount = 2,
    [switch]$SkipPackagePrune,
    [string]$DesktopPackagePath,
    [string]$DesktopExeInstallerPath,
    [string]$DesktopMsiInstallerPath,
    [string]$AndroidPackagePath,
    [switch]$SkipAndroid,
    [switch]$PreserveExistingAndroid,
    [string]$DesktopVersion,
    [string]$AndroidVersion,
    [string]$ApkAnalyzerPath,
    [string]$JavaSdkDirectory,
    [string]$DesktopMinimumSupportedVersion,
    [string]$AndroidMinimumSupportedVersion,
    [string]$DesktopNotes,
    [string]$AndroidNotes,
    [switch]$MandatoryDesktop,
    [switch]$MandatoryAndroid,
    [switch]$AllowDowngrade
)

function Resolve-ProjectRoot {
    param([string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Invoke-GeoraePlanReleaseTestKillPoint {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not [string]::Equals(
        [string]$env:GEORAEPLAN_RELEASE_TEST_KILL_POINT,
        $Name,
        [StringComparison]::Ordinal
    )) {
        return
    }

    [Diagnostics.Process]::GetCurrentProcess().Kill()
    throw "Release test kill point did not terminate the process: $Name"
}

function Invoke-GeoraePlanReleaseTestFailurePoint {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not [string]::Equals(
        [string]$env:GEORAEPLAN_RELEASE_TEST_FAILURE_POINT,
        $Name,
        [StringComparison]::Ordinal
    )) {
        return
    }

    throw "Injected release test failure: $Name"
}

function Invoke-GeoraePlanReleaseTestPausePoint {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not [string]::Equals(
        [string]$env:GEORAEPLAN_RELEASE_TEST_PAUSE_POINT,
        $Name,
        [StringComparison]::Ordinal
    )) {
        return
    }

    $readyEventName =
        [string]$env:GEORAEPLAN_RELEASE_TEST_PAUSE_READY_EVENT
    $continueEventName =
        [string]$env:GEORAEPLAN_RELEASE_TEST_PAUSE_CONTINUE_EVENT
    if (
        $readyEventName -notmatch
            '^GeoraePlanReleaseTest_[0-9a-f]{32}_Ready$' -or
        $continueEventName -notmatch
            '^GeoraePlanReleaseTest_[0-9a-f]{32}_Continue$'
    ) {
        throw "Release test pause event names are invalid: $Name"
    }

    $readyEvent = $null
    $continueEvent = $null
    try {
        $readyEvent =
            [Threading.EventWaitHandle]::OpenExisting($readyEventName)
        $continueEvent =
            [Threading.EventWaitHandle]::OpenExisting($continueEventName)
        [void]$readyEvent.Set()
        if (-not $continueEvent.WaitOne([TimeSpan]::FromSeconds(30))) {
            throw "Release test pause timed out: $Name"
        }
    }
    finally {
        if ($null -ne $continueEvent) {
            $continueEvent.Dispose()
        }
        if ($null -ne $readyEvent) {
            $readyEvent.Dispose()
        }
    }
}

function Get-CsprojPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectFile,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    [xml]$xml = Get-Content -LiteralPath $ProjectFile -Raw
    foreach ($group in $xml.Project.PropertyGroup) {
        $property = $group.$PropertyName
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property)) {
            return ([string]$property).Trim()
        }
    }

    return $null
}

function Assert-AndroidCanonicalDestinationLease {
    param(
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][IO.FileStream]$Lease,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][long]$ExpectedFileSize,
        [string]$ExpectedFileIdentity
    )

    $destinationDirectoryItem =
        Get-Item `
            -LiteralPath $DestinationDirectory `
            -Force `
            -ErrorAction Stop
    if (
        -not $destinationDirectoryItem.PSIsContainer -or
        ($destinationDirectoryItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw (
            'Canonical Android package parent is not a regular owned ' +
            "directory: $DestinationDirectory")
    }

    $canonicalEntries = @(
        [IO.Directory]::EnumerateFileSystemEntries(
            $DestinationDirectory,
            '*',
            [IO.SearchOption]::TopDirectoryOnly) |
            Where-Object {
                [string]::Equals(
                    [IO.Path]::GetFullPath([string]$_),
                    [IO.Path]::GetFullPath($DestinationPath),
                    [StringComparison]::OrdinalIgnoreCase)
            })
    if ($canonicalEntries.Count -ne 1) {
        throw 'Canonical Android package path changed while acquiring its lease.'
    }

    $destinationItem =
        Get-Item -LiteralPath $DestinationPath -Force -ErrorAction Stop
    if (
        $destinationItem.PSIsContainer -or
        ($destinationItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($destinationItem.FullName),
            [IO.Path]::GetFullPath($DestinationPath),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($destinationItem.DirectoryName),
            [IO.Path]::GetFullPath($DestinationDirectory),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($Lease.Name),
            [IO.Path]::GetFullPath($DestinationPath),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw (
            'Canonical Android package is not a regular direct child of ' +
            'its owned destination.')
    }

    $pathLease = [IO.File]::Open(
        $DestinationPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $leaseIdentity =
            Get-GeoraePlanAndroidApkLeaseFileIdentity -Lease $Lease
        $pathIdentity =
            Get-GeoraePlanAndroidApkLeaseFileIdentity -Lease $pathLease
        $leaseHash = Get-GeoraePlanAndroidApkLeaseSha256 -Lease $Lease
        if (
            $Lease.Length -ne $ExpectedFileSize -or
            $pathLease.Length -ne $ExpectedFileSize -or
            -not [string]::Equals(
                $leaseIdentity,
                $pathIdentity,
                [StringComparison]::Ordinal) -or
            (
                -not [string]::IsNullOrWhiteSpace($ExpectedFileIdentity) -and
                -not [string]::Equals(
                    $leaseIdentity,
                    $ExpectedFileIdentity,
                    [StringComparison]::Ordinal)
            ) -or
            -not [string]::Equals(
                $leaseHash,
                $ExpectedSha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Canonical Android package identity changed.'
        }
    }
    finally {
        $pathLease.Dispose()
    }
}

function Remove-StrictOwnedPackageTemporaryFile {
    param(
        [Parameter(Mandatory = $true)][string]$TemporaryPath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $TemporaryPath)) {
        return
    }

    $resolvedDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
    $resolvedTemporaryPath = [IO.Path]::GetFullPath($TemporaryPath)
    if (-not [string]::Equals(
        [IO.Path]::GetDirectoryName($resolvedTemporaryPath),
        $resolvedDirectory,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Package temporary cleanup path escaped its owned directory.'
    }

    $temporaryItem =
        Get-Item -LiteralPath $TemporaryPath -Force -ErrorAction Stop
    if (
        $temporaryItem.PSIsContainer -or
        ($temporaryItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($temporaryItem.FullName),
            $resolvedTemporaryPath,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Package temporary cleanup target is not a strict-owned regular file.'
    }

    Remove-Item -LiteralPath $TemporaryPath -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $TemporaryPath) {
        throw 'Package temporary cleanup did not remove its owned file.'
    }
}

function Assert-GeoraePlanReleaseRegularDirectoryChain {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$PathMayBeMissing,
        [switch]$LeafMayBeFile
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $currentPath = $resolvedPath
    $isLeaf = $true
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
            if (
                ($item.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'Release path chain contains a reparse point: ' +
                    $currentPath)
            }
            if (-not $item.PSIsContainer -and (-not $isLeaf -or -not $LeafMayBeFile)) {
                throw (
                    'Release path parent is not a regular directory: ' +
                    $currentPath)
            }
        }
        elseif ($isLeaf -and -not $PathMayBeMissing) {
            throw "Release path does not exist: $currentPath"
        }

        $parentPath = [IO.Path]::GetDirectoryName($currentPath)
        if (
            [string]::IsNullOrWhiteSpace($parentPath) -or
            [string]::Equals(
                $parentPath,
                $currentPath,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            break
        }
        $currentPath = $parentPath
        $isLeaf = $false
    }
}

function New-GeoraePlanReleaseOwnedDirectory {
    param([Parameter(Mandatory = $true)][string]$DirectoryPath)

    $resolvedDirectory = [IO.Path]::GetFullPath($DirectoryPath)
    if (-not [IO.Directory]::Exists($resolvedDirectory)) {
        $parentPath = [IO.Path]::GetDirectoryName($resolvedDirectory)
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $parentPath
        [void][IO.Directory]::CreateDirectory($resolvedDirectory)
    }
    Assert-GeoraePlanReleaseRegularDirectoryChain -Path $resolvedDirectory
    return $resolvedDirectory
}

function Get-GeoraePlanReleaseLeaseSha256 {
    param([Parameter(Mandatory = $true)][IO.FileStream]$Lease)

    if (-not $Lease.CanRead -or -not $Lease.CanSeek) {
        throw 'Release file lease is not readable and seekable.'
    }
    $position = $Lease.Position
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $Lease.Position = 0
        return (
            [BitConverter]::ToString(
                $sha256.ComputeHash($Lease))).Replace('-', '')
    }
    finally {
        $Lease.Position = $position
        $sha256.Dispose()
    }
}

function New-GeoraePlanReleaseFileSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    $resolvedSourcePath = [IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $SourcePath -ErrorAction Stop).Path)
    Assert-GeoraePlanReleaseRegularDirectoryChain `
        -Path $resolvedSourcePath `
        -LeafMayBeFile
    $sourceItem =
        Get-Item -LiteralPath $resolvedSourcePath -Force -ErrorAction Stop
    if (
        $sourceItem.PSIsContainer -or
        ($sourceItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "$SourceName is not a regular release source file."
    }

    $snapshotParent =
        New-GeoraePlanReleaseOwnedDirectory `
            -DirectoryPath (Resolve-GeoraePlanScriptTempDirectory)
    $snapshotRoot = Join-Path $snapshotParent (
        'georaeplan-release-file-' + [Guid]::NewGuid().ToString('N'))
    $snapshotPath = Join-Path $snapshotRoot 'candidate.bin'
    $sourceLease = $null
    $snapshotLease = $null
    $snapshotError = $null
    try {
        $sourceLease = [IO.File]::Open(
            $resolvedSourcePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $resolvedSourcePath `
            -LeafMayBeFile
        [void](New-GeoraePlanReleaseOwnedDirectory -DirectoryPath $snapshotRoot)
        $writer = [IO.FileStream]::new(
            $snapshotPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            81920,
            [IO.FileOptions]::WriteThrough)
        try {
            $sourceLease.Position = 0
            $sourceLease.CopyTo($writer)
            $writer.Flush($true)
        }
        finally {
            $writer.Dispose()
        }
        $snapshotLease = [IO.File]::Open(
            $snapshotPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $sourceHash = Get-GeoraePlanReleaseLeaseSha256 -Lease $sourceLease
        $snapshotHash =
            Get-GeoraePlanReleaseLeaseSha256 -Lease $snapshotLease
        if (
            $sourceLease.Length -ne $snapshotLease.Length -or
            -not [string]::Equals(
                $sourceHash,
                $snapshotHash,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw "$SourceName changed while its immutable snapshot was created."
        }
        $snapshot = [pscustomobject]@{
            Owner = 'georaeplan-release-file-snapshot'
            SourceName = $SourceName
            SourcePath = $resolvedSourcePath
            SnapshotRoot = [IO.Path]::GetFullPath($snapshotRoot)
            SnapshotPath = [IO.Path]::GetFullPath($snapshotPath)
            SourceLease = $sourceLease
            Lease = $snapshotLease
            Sha256 = $snapshotHash
            FileSize = [long]$snapshotLease.Length
        }
        $sourceLease = $null
        $snapshotLease = $null
        Assert-GeoraePlanReleaseFileSnapshot `
            -Snapshot $snapshot `
            -SourceName $SourceName
        return $snapshot
    }
    catch {
        $snapshotError = $_.Exception
    }
    finally {
        if ($null -ne $snapshotLease) {
            $snapshotLease.Dispose()
        }
        if ($null -ne $sourceLease) {
            $sourceLease.Dispose()
        }
    }

    try {
        Remove-GeoraePlanReleaseFileSnapshot -Snapshot ([pscustomobject]@{
            Owner = 'georaeplan-release-file-snapshot'
            SnapshotRoot = $snapshotRoot
            SnapshotPath = $snapshotPath
        })
    }
    catch {
        throw [AggregateException]::new(
            "$SourceName snapshot creation and cleanup both failed.",
            [Exception[]]@($snapshotError, $_.Exception))
    }
    throw $snapshotError
}

function Assert-GeoraePlanReleaseFileSnapshot {
    param(
        [Parameter(Mandatory = $true)]$Snapshot,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    if (
        $null -eq $Snapshot -or
        -not [string]::Equals(
            [string]$Snapshot.Owner,
            'georaeplan-release-file-snapshot',
            [StringComparison]::Ordinal) -or
        $null -eq $Snapshot.Lease -or
        $null -eq $Snapshot.SourceLease
    ) {
        throw "$SourceName immutable snapshot ownership is invalid."
    }
    $snapshotRoot = [IO.Path]::GetFullPath([string]$Snapshot.SnapshotRoot)
    $snapshotPath = [IO.Path]::GetFullPath([string]$Snapshot.SnapshotPath)
    if (
        (Split-Path -Leaf $snapshotRoot) -notmatch
            '^georaeplan-release-file-[0-9a-fA-F]{32}$' -or
        -not [string]::Equals(
            [IO.Path]::GetDirectoryName($snapshotPath),
            $snapshotRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($Snapshot.Lease.Name),
            $snapshotPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($Snapshot.SourceLease.Name),
            [IO.Path]::GetFullPath([string]$Snapshot.SourcePath),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$SourceName immutable snapshot path binding is invalid."
    }
    Assert-GeoraePlanReleaseRegularDirectoryChain -Path $snapshotRoot
    Assert-GeoraePlanReleaseRegularDirectoryChain `
        -Path $snapshotPath `
        -LeafMayBeFile
    Assert-GeoraePlanReleaseRegularDirectoryChain `
        -Path ([string]$Snapshot.SourcePath) `
        -LeafMayBeFile
    $snapshotHash =
        Get-GeoraePlanReleaseLeaseSha256 -Lease $Snapshot.Lease
    $sourceHash =
        Get-GeoraePlanReleaseLeaseSha256 -Lease $Snapshot.SourceLease
    if (
        $Snapshot.Lease.Length -ne [long]$Snapshot.FileSize -or
        $Snapshot.SourceLease.Length -ne [long]$Snapshot.FileSize -or
        -not [string]::Equals(
            $snapshotHash,
            [string]$Snapshot.Sha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $sourceHash,
            [string]$Snapshot.Sha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$SourceName immutable snapshot identity changed."
    }
}

function Remove-GeoraePlanReleaseFileSnapshot {
    param($Snapshot)

    if ($null -eq $Snapshot) {
        return
    }
    foreach ($leaseName in @('Lease', 'SourceLease')) {
        $leaseProperty = $Snapshot.PSObject.Properties[$leaseName]
        if (
            $null -ne $leaseProperty -and
            $leaseProperty.Value -is [IDisposable]
        ) {
            $leaseProperty.Value.Dispose()
        }
    }
    $rootProperty = $Snapshot.PSObject.Properties['SnapshotRoot']
    if ($null -eq $rootProperty) {
        return
    }
    $snapshotRoot = [IO.Path]::GetFullPath([string]$rootProperty.Value)
    if (
        (Split-Path -Leaf $snapshotRoot) -notmatch
            '^georaeplan-release-file-[0-9a-fA-F]{32}$'
    ) {
        throw 'Release snapshot cleanup ownership is invalid.'
    }
    if (Test-Path -LiteralPath $snapshotRoot) {
        Assert-GeoraePlanReleaseRegularDirectoryChain -Path $snapshotRoot
        foreach ($entry in @(
            [IO.Directory]::EnumerateFileSystemEntries(
                $snapshotRoot,
                '*',
                [IO.SearchOption]::TopDirectoryOnly)
        )) {
            $entryItem =
                Get-Item -LiteralPath $entry -Force -ErrorAction Stop
            if (
                $entryItem.PSIsContainer -or
                ($entryItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                -not [string]::Equals(
                    $entryItem.Name,
                    'candidate.bin',
                    [StringComparison]::Ordinal)
            ) {
                throw 'Release snapshot cleanup found an unowned entry.'
            }
        }
        Remove-Item -LiteralPath $snapshotRoot -Recurse -Force -ErrorAction Stop
    }
}

function Copy-GeoraePlanReleaseLeaseToDurableFile {
    param(
        [Parameter(Mandatory = $true)][IO.FileStream]$SourceLease,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $target = [IO.FileStream]::new(
        $TargetPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        81920,
        [IO.FileOptions]::WriteThrough)
    try {
        $sourcePosition = $SourceLease.Position
        try {
            $SourceLease.Position = 0
            $SourceLease.CopyTo($target)
        }
        finally {
            $SourceLease.Position = $sourcePosition
        }
        $target.Flush($true)
    }
    finally {
        $target.Dispose()
    }
}

function Get-GeoraePlanReleaseTransactionStageLeaseStore {
    if ($null -eq $script:releaseTransactionStageLeases) {
        $script:releaseTransactionStageLeases =
            [Collections.Generic.Dictionary[string, IO.FileStream]]::new(
                [StringComparer]::OrdinalIgnoreCase)
    }
    return $script:releaseTransactionStageLeases
}

function Register-GeoraePlanReleaseTransactionStageLease {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][IO.FileStream]$Lease
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($Lease.Name),
        $resolvedPath,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Release transaction stage lease path binding is invalid.'
    }
    $store = Get-GeoraePlanReleaseTransactionStageLeaseStore
    if ($store.ContainsKey($resolvedPath)) {
        throw 'Release transaction stage lease is already registered.'
    }
    $store.Add($resolvedPath, $Lease)
}

function Get-GeoraePlanReleaseTransactionStageLease {
    param([Parameter(Mandatory = $true)][string]$Path)

    $store = Get-GeoraePlanReleaseTransactionStageLeaseStore
    $lease = $null
    if ($store.TryGetValue([IO.Path]::GetFullPath($Path), [ref]$lease)) {
        return $lease
    }
    return $null
}

function Close-GeoraePlanReleaseTransactionStageLeases {
    $store = Get-GeoraePlanReleaseTransactionStageLeaseStore
    foreach ($lease in @($store.Values)) {
        $lease.Dispose()
    }
    $store.Clear()
}

function Sync-GeoraePlanReleaseCommittedFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-GeoraePlanReleaseRegularDirectoryChain `
        -Path $Path `
        -LeafMayBeFile
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::Read)
    try {
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Copy-PackageWithMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$OutputFileName,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][bool]$Mandatory,
        [string]$MinimumSupportedVersion,
        [string]$ExpectedSha256,
        [long]$ExpectedFileSize = -1,
        [switch]$RejectDifferentExistingFile,
        [object]$SourceSnapshot,
        [ref]$DestinationLeaseReference
    )

    if ($Platform -eq 'desktop') {
        Test-DesktopUpdatePackage `
            -PackagePath $SourcePath `
            -ExpectedVersion $Version `
            -SourceSnapshot $SourceSnapshot
    }

    [void](New-GeoraePlanReleaseOwnedDirectory `
        -DirectoryPath $DestinationDirectory)
    $destinationPath = Join-Path $DestinationDirectory $OutputFileName
    $temporaryPath = Join-Path $DestinationDirectory (
        ".{0}.{1}.tmp" -f
        $OutputFileName,
        [Guid]::NewGuid().ToString('N'))
    $isAndroidLeaseCommit =
        $Platform -eq 'android' -and
        $null -ne $DestinationLeaseReference
    $skipDestinationWrite = $false
    $backupPath = $null
    $copyError = $null
    try {
        $isGenericSnapshot =
            $null -ne $SourceSnapshot -and
            [string]::Equals(
                [string]$SourceSnapshot.Owner,
                'georaeplan-release-file-snapshot',
                [StringComparison]::Ordinal)
        if ($null -ne $SourceSnapshot) {
            if ($isGenericSnapshot) {
                Assert-GeoraePlanReleaseFileSnapshot `
                    -Snapshot $SourceSnapshot `
                    -SourceName "$Platform publish source"
            }
            else {
                Assert-GeoraePlanAndroidApkSnapshot `
                    -Snapshot $SourceSnapshot `
                    -SourceName "$Platform publish source"
            }
            if (-not [string]::Equals(
                [IO.Path]::GetFullPath($SourcePath),
                [IO.Path]::GetFullPath(
                    [string]$SourceSnapshot.SnapshotPath),
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw "$Platform package source does not match its snapshot lease."
            }
        }

        if (
            $isAndroidLeaseCommit -and
            $null -ne $DestinationLeaseReference.Value
        ) {
            Assert-AndroidCanonicalDestinationLease `
                -DestinationDirectory $DestinationDirectory `
                -DestinationPath $destinationPath `
                -Lease $DestinationLeaseReference.Value `
                -ExpectedSha256 $ExpectedSha256 `
                -ExpectedFileSize $ExpectedFileSize
            $hash = [pscustomobject]@{ Hash = $ExpectedSha256 }
            $fileInfo =
                [pscustomobject]@{ Length = [long]$ExpectedFileSize }
            $skipDestinationWrite = $true
        }

        if (-not $skipDestinationWrite) {
            Assert-GeoraePlanReleaseRegularDirectoryChain `
                -Path $DestinationDirectory
            if ($null -ne $SourceSnapshot) {
                Copy-GeoraePlanReleaseLeaseToDurableFile `
                    -SourceLease $SourceSnapshot.Lease `
                    -TargetPath $temporaryPath
            }
            else {
                Copy-DurableGeoraePlanReleaseOwnedFile `
                    -SourcePath $SourcePath `
                    -TargetPath $temporaryPath
            }
            if ($null -ne $SourceSnapshot) {
                if ($isGenericSnapshot) {
                    Assert-GeoraePlanReleaseFileSnapshot `
                        -Snapshot $SourceSnapshot `
                        -SourceName "$Platform publish source after copy"
                }
                else {
                    Assert-GeoraePlanAndroidApkSnapshot `
                        -Snapshot $SourceSnapshot `
                        -SourceName "$Platform publish source after copy"
                }
            }
            $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $temporaryPath
            $fileInfo = Get-Item -LiteralPath $temporaryPath
            if (
                -not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
                -not [string]::Equals(
                    $hash.Hash,
                    $ExpectedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw "$Platform package changed after validation: SHA-256 mismatch."
            }
            if ($ExpectedFileSize -ge 0 -and $fileInfo.Length -ne $ExpectedFileSize) {
                throw "$Platform package changed after validation: file size mismatch."
            }

            if ($isAndroidLeaseCommit) {
                Assert-GeoraePlanReleaseRegularDirectoryChain `
                    -Path $DestinationDirectory
                $destinationDirectoryItem =
                    Get-Item `
                        -LiteralPath $DestinationDirectory `
                        -Force `
                        -ErrorAction Stop
                if (
                    -not $destinationDirectoryItem.PSIsContainer -or
                    ($destinationDirectoryItem.Attributes -band
                        [IO.FileAttributes]::ReparsePoint) -ne 0
                ) {
                    throw (
                        'Canonical Android package parent is not a regular ' +
                        'owned directory at commit.')
                }
                if (Test-Path -LiteralPath $destinationPath) {
                    throw (
                        'Canonical Android package appeared before its ' +
                        'authenticated commit lease was acquired.')
                }

                $transitionLease = $null
                $destinationLease = $null
                try {
                    $transitionLease = [IO.File]::Open(
                        $temporaryPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read -bor [IO.FileShare]::Delete)
                    $temporaryIdentity =
                        Get-GeoraePlanAndroidApkLeaseFileIdentity `
                            -Lease $transitionLease
                    [IO.File]::Move($temporaryPath, $destinationPath)
                    $destinationLease = [IO.File]::Open(
                        $destinationPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)
                    Assert-AndroidCanonicalDestinationLease `
                        -DestinationDirectory $DestinationDirectory `
                        -DestinationPath $destinationPath `
                        -Lease $destinationLease `
                        -ExpectedSha256 $ExpectedSha256 `
                        -ExpectedFileSize $ExpectedFileSize `
                        -ExpectedFileIdentity $temporaryIdentity
                    $DestinationLeaseReference.Value = $destinationLease
                    $destinationLease = $null
                }
                finally {
                    if ($null -ne $destinationLease) {
                        $destinationLease.Dispose()
                    }
                    if ($null -ne $transitionLease) {
                        $transitionLease.Dispose()
                    }
                }
            }
            else {
                Assert-GeoraePlanReleaseRegularDirectoryChain `
                    -Path $DestinationDirectory
                if (
                    $RejectDifferentExistingFile -and
                    (Test-Path -LiteralPath $destinationPath -PathType Leaf)
                ) {
                    $existingHash = (
                        Get-FileHash `
                            -LiteralPath $destinationPath `
                            -Algorithm SHA256).Hash
                    if (-not [string]::Equals(
                        $existingHash,
                        $hash.Hash,
                        [StringComparison]::OrdinalIgnoreCase
                    )) {
                        throw (
                            "$Platform package version already exists with different " +
                            "bytes: $destinationPath")
                    }
                }

                $backupPath = Join-Path $DestinationDirectory (
                    ".{0}.{1}.bak" -f
                    $OutputFileName,
                    [Guid]::NewGuid().ToString('N'))
                if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
                    [IO.File]::Replace(
                        $temporaryPath,
                        $destinationPath,
                        $backupPath,
                        $true)
                }
                else {
                    [IO.File]::Move($temporaryPath, $destinationPath)
                }
                Sync-GeoraePlanReleaseCommittedFile -Path $destinationPath
            }
        }
    }
    catch {
        $copyError = $_.Exception
    }

    $cleanupErrors = @()
    foreach ($ownedPath in @($temporaryPath, $backupPath)) {
        if ([string]::IsNullOrWhiteSpace($ownedPath)) {
            continue
        }
        try {
            Remove-StrictOwnedPackageTemporaryFile `
                -TemporaryPath $ownedPath `
                -DestinationDirectory $DestinationDirectory
        }
        catch {
            $cleanupErrors += $_.Exception
        }
    }
    if ($null -ne $copyError) {
        if ($cleanupErrors.Count -gt 0) {
            throw [AggregateException]::new(
                "$Platform package operation and strict temporary cleanup failed.",
                [Exception[]](@($copyError) + $cleanupErrors))
        }
        throw $copyError
    }
    if ($cleanupErrors.Count -gt 0) {
        throw [AggregateException]::new(
            "$Platform package strict temporary cleanup failed.",
            [Exception[]]$cleanupErrors)
    }

    $resolvedMinimumSupportedVersion = if ([string]::IsNullOrWhiteSpace($MinimumSupportedVersion)) {
        if ($Mandatory) { $Version } else { '' }
    }
    else {
        $MinimumSupportedVersion.Trim()
    }

    return [ordered]@{
        platform = $Platform
        version = $Version
        mandatory = $Mandatory
        minimumSupportedVersion = $resolvedMinimumSupportedVersion
        packageUrl = "/updates/download/$Platform/$([Uri]::EscapeDataString($OutputFileName))"
        fileName = $OutputFileName
        sha256 = $hash.Hash
        fileSize = [int64]$fileInfo.Length
        notes = $Notes
        releasedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
}

function Resolve-DesktopNativeInstallerPath {
    param(
        [string]$ExplicitPath,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet('exe', 'msi')][string]$Format
    )

    $candidate = $ExplicitPath
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = Join-Path $Root ("배포\관리자용\버전보관\거래플랜-PC-설치패키지-v{0}.{1}" -f $Version, $Format)
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "데스크톱 $Format 설치패키지가 없습니다. 버전 $Version 설치패키지를 먼저 생성해야 합니다: $candidate"
    }

    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    if (-not [string]::Equals([System.IO.Path]::GetExtension($resolved), ".$Format", [StringComparison]::OrdinalIgnoreCase)) {
        throw "데스크톱 설치패키지 확장자가 올바르지 않습니다. 기대 형식: .$Format / 실제 파일: $resolved"
    }

    return $resolved
}

function Copy-DesktopNativeInstallerWithMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$OutputFileName,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet('user', 'administrator')][string]$Audience,
        [Parameter(Mandatory = $true)][ValidateSet('exe', 'msi')][string]$Format,
        [Parameter(Mandatory = $true)]$SourceSnapshot
    )

    Assert-GeoraePlanReleaseFileSnapshot `
        -Snapshot $SourceSnapshot `
        -SourceName "desktop $Format installer"
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($SourcePath),
        [IO.Path]::GetFullPath([string]$SourceSnapshot.SnapshotPath),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Desktop $Format installer source is not its immutable snapshot."
    }
    [void](New-GeoraePlanReleaseOwnedDirectory `
        -DirectoryPath $DestinationDirectory)
    $destinationPath = Join-Path $DestinationDirectory $OutputFileName
    Copy-GeoraePlanReleaseLeaseToDurableFile `
        -SourceLease $SourceSnapshot.Lease `
        -TargetPath $destinationPath
    Assert-GeoraePlanReleaseFileSnapshot `
        -Snapshot $SourceSnapshot `
        -SourceName "desktop $Format installer after copy"

    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $destinationPath
    $fileInfo = Get-Item -LiteralPath $destinationPath
    if (
        $fileInfo.Length -ne [long]$SourceSnapshot.FileSize -or
        -not [string]::Equals(
            $hash.Hash,
            [string]$SourceSnapshot.Sha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Desktop $Format installer staged snapshot binding failed."
    }
    return [ordered]@{
        audience = $Audience
        format = $Format
        version = $Version
        packageUrl = "/updates/download/desktop/$([Uri]::EscapeDataString($OutputFileName))"
        fileName = $OutputFileName
        sha256 = $hash.Hash
        fileSize = [int64]$fileInfo.Length
    }
}

function Assert-DesktopNativeInstallerProductVersion {
    param(
        [Parameter(Mandatory = $true)]$SourceSnapshot,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)]
        [ValidateSet('exe', 'msi')]
        [string]$Format
    )

    Assert-GeoraePlanReleaseFileSnapshot `
        -Snapshot $SourceSnapshot `
        -SourceName "desktop $Format installer version source"
    $snapshotPath = [IO.Path]::GetFullPath(
        [string]$SourceSnapshot.SnapshotPath)
    $actualVersion = $null
    $actualFileVersion = $null
    if ($Format -eq 'exe') {
        try {
            $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo(
                $snapshotPath)
            $actualVersion = $versionInfo.ProductVersion
            $actualFileVersion = $versionInfo.FileVersion
        }
        catch {
            throw (
                'Desktop EXE installer version metadata could not be read: ' +
                $_.Exception.Message)
        }
    }
    else {
        $installer = $null
        $database = $null
        $view = $null
        $record = $null
        try {
            $installer = New-Object -ComObject WindowsInstaller.Installer
            $database = $installer.OpenDatabase($snapshotPath, 0)
            $view = $database.OpenView(
                'SELECT `Value` FROM `Property` ' +
                'WHERE `Property`=''ProductVersion''')
            $view.Execute()
            $record = $view.Fetch()
            if ($null -eq $record) {
                throw 'MSI Property.ProductVersion is missing.'
            }
            $actualVersion = [string]$record.StringData(1)
        }
        catch {
            throw (
                'Desktop MSI installer ProductVersion could not be read: ' +
                $_.Exception.Message)
        }
        finally {
            foreach ($comObject in @(
                $record,
                $view,
                $database,
                $installer
            )) {
                if ($null -ne $comObject -and
                    [Runtime.InteropServices.Marshal]::IsComObject(
                        $comObject)
                ) {
                    [void][Runtime.InteropServices.Marshal]::
                        FinalReleaseComObject($comObject)
                }
            }
        }
    }

    if (
        [string]::IsNullOrWhiteSpace([string]$actualVersion) -or
        -not [string]::Equals(
            [string]$actualVersion,
            $ExpectedVersion,
            [StringComparison]::Ordinal)
    ) {
        throw (
            "Desktop $Format installer ProductVersion does not exactly " +
            "match DesktopVersion. expected=$ExpectedVersion " +
            "actual=$actualVersion")
    }
    if ($Format -eq 'exe') {
        $expectedFileVersion = $ExpectedVersion + '.0'
        if (
            [string]::IsNullOrWhiteSpace([string]$actualFileVersion) -or
            -not [string]::Equals(
                [string]$actualFileVersion,
                $expectedFileVersion,
                [StringComparison]::Ordinal)
        ) {
            throw (
                'Desktop EXE installer FileVersion does not exactly match ' +
                "DesktopVersion plus .0. expected=$expectedFileVersion " +
                "actual=$actualFileVersion")
        }
    }
    Assert-GeoraePlanReleaseFileSnapshot `
        -Snapshot $SourceSnapshot `
        -SourceName "desktop $Format installer version source after inspection"
}

function Resolve-GeoraePlanScriptTempDirectory {
    foreach ($candidate in @($env:GEORAEPLAN_TEMP_ROOT, $env:TEMP, [System.IO.Path]::GetTempPath())) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        try {
            $resolved = [System.IO.Path]::GetFullPath($candidate)
            New-Item -ItemType Directory -Force -Path $resolved | Out-Null
            return $resolved
        }
        catch {
            continue
        }
    }

    throw 'Unable to resolve a writable temp directory for update asset publishing.'
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

function Copy-DesktopArchiveEntryToBoundedFile {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][long]$MaximumLength
    )

    if ($Entry.Length -lt 0 -or $Entry.Length -gt $MaximumLength) {
        throw 'Desktop package executable entry is outside the extraction size limit.'
    }

    $source = $null
    $destination = $null
    $inspectionLease = $null
    $copySucceeded = $false
    try {
        $source = $Entry.Open()
        $destination = [IO.FileStream]::new(
            $DestinationPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read,
            81920,
            [IO.FileOptions]::WriteThrough)
        $buffer = New-Object byte[] 81920
        [long]$written = 0
        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($read -gt ($MaximumLength - $written)) {
                throw 'Desktop package executable exceeded the extraction size limit.'
            }
            $destination.Write($buffer, 0, $read)
            $written += $read
        }
        if ($written -ne $Entry.Length) {
            throw 'Desktop package executable extracted length does not match ZIP metadata.'
        }
        $destination.Flush($true)
        $extractedSha256 =
            Get-GeoraePlanReleaseLeaseSha256 -Lease $destination
        $extractedLength = [long]$destination.Length
        $destination.Dispose()
        $destination = $null
        $inspectionLease = [IO.File]::Open(
            $DestinationPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $leasedSha256 =
            Get-GeoraePlanReleaseLeaseSha256 -Lease $inspectionLease
        if (
            $inspectionLease.Length -ne $extractedLength -or
            -not [string]::Equals(
                $leasedSha256,
                $extractedSha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Desktop package executable changed before its immutable inspection lease was acquired.'
        }
        $identity = [pscustomobject]@{
            Lease = $inspectionLease
            Sha256 = $leasedSha256
            FileSize = $extractedLength
        }
        $copySucceeded = $true
        $inspectionLease = $null
        return $identity
    }
    finally {
        if ($null -ne $destination) {
            $destination.Dispose()
        }
        if (-not $copySucceeded -and $null -ne $inspectionLease) {
            $inspectionLease.Dispose()
        }
        if ($null -ne $source) {
            $source.Dispose()
        }
    }
}

function Test-DesktopUpdatePackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        $SourceSnapshot
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $requiredEntries = @(
        'Install-GeoraePlan.ps1',
        'App/거래플랜.Desktop.App.exe',
        'App/거래플랜.exe',
        'App/appsettings.json',
        'App/앱실행.cmd',
        'App/Updater/거래플랜.Updater.exe'
    )

    $archive = $null
    [long]$packageFileSize = 0
    if ($null -ne $SourceSnapshot) {
        Assert-GeoraePlanReleaseFileSnapshot `
            -Snapshot $SourceSnapshot `
            -SourceName 'desktop ZIP validation source'
        if (-not [string]::Equals(
            [IO.Path]::GetFullPath($PackagePath),
            [IO.Path]::GetFullPath([string]$SourceSnapshot.SnapshotPath),
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw 'Desktop ZIP validation source is not its immutable snapshot.'
        }
        $packageFileSize = [long]$SourceSnapshot.FileSize
        $SourceSnapshot.Lease.Position = 0
        $archive = [IO.Compression.ZipArchive]::new(
            $SourceSnapshot.Lease,
            [IO.Compression.ZipArchiveMode]::Read,
            $true)
    }
    else {
        $packageFileSize = (
            Get-Item -LiteralPath $PackagePath -Force -ErrorAction Stop).Length
        $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    }
    $tempDirectory = Join-Path (Resolve-GeoraePlanScriptTempDirectory) ("georaeplan-desktop-package-version-" + [Guid]::NewGuid().ToString('N'))
    $extractedAppPath = $null
    $extractedAliasPath = $null
    $extractedAppIdentity = $null
    $extractedAliasIdentity = $null
    try {
        New-Item -ItemType Directory -Path $tempDirectory -ErrorAction Stop |
            Out-Null
        $entries = Get-ValidatedDesktopArchiveEntryMap `
            -Archive $archive `
            -PackageFileSize $packageFileSize

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
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Desktop package archive must contain exactly one canonical *.Desktop.App.exe launcher target.'
        }

        foreach ($requiredEntry in $requiredEntries) {
            if (-not $entries.ContainsKey($requiredEntry)) {
                throw "데스크톱 업데이트 패키지 필수 항목이 누락되었습니다: $requiredEntry"
            }
        }
        Assert-DesktopArchiveScriptContract -Entries $entries

        $extractedAppPath = Join-Path `
            $tempDirectory `
            '거래플랜.Desktop.App.exe'
        $extractedAliasPath = Join-Path `
            $tempDirectory `
            '거래플랜.exe'
        $extractedAppIdentity = Copy-DesktopArchiveEntryToBoundedFile `
            -Entry $entries['App/거래플랜.Desktop.App.exe'] `
            -DestinationPath $extractedAppPath `
            -MaximumLength 512MB
        $extractedAliasIdentity = Copy-DesktopArchiveEntryToBoundedFile `
            -Entry $entries['App/거래플랜.exe'] `
            -DestinationPath $extractedAliasPath `
            -MaximumLength 512MB

        Invoke-GeoraePlanReleaseTestPausePoint `
            -Name 'DuringDesktopArchiveExecutableInspection'
        $canonicalHash = [string]$extractedAppIdentity.Sha256
        $aliasHash = [string]$extractedAliasIdentity.Sha256
        if (-not [string]::Equals(
            $canonicalHash,
            $aliasHash,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Desktop package canonical and display-name executables do not have identical SHA-256 hashes.'
        }

        foreach ($executablePath in @(
            $extractedAppPath,
            $extractedAliasPath
        )) {
            $actualVersion = (
                Get-Item -LiteralPath $executablePath).
                    VersionInfo.ProductVersion
            $actualVersionText = if ($null -eq $actualVersion) {
                ''
            }
            else {
                [string]$actualVersion
            }
            $actualVersionPrefix = $actualVersionText.Split('+')[0].Trim()
            if (-not [string]::Equals(
                $actualVersionPrefix,
                $ExpectedVersion,
                [StringComparison]::Ordinal)) {
                throw "데스크톱 업데이트 패키지 버전이 manifest 버전과 일치하지 않습니다. 기대 버전: $ExpectedVersion, 실제 버전: $actualVersion, 경로: $executablePath"
            }
        }
        foreach ($identity in @(
            $extractedAppIdentity,
            $extractedAliasIdentity
        )) {
            $verifiedHash =
                Get-GeoraePlanReleaseLeaseSha256 -Lease $identity.Lease
            if (
                $identity.Lease.Length -ne [long]$identity.FileSize -or
                -not [string]::Equals(
                    $verifiedHash,
                    [string]$identity.Sha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw 'Desktop package executable immutable inspection identity changed.'
            }
        }
    }
    finally {
        $archive.Dispose()
        if ($null -ne $SourceSnapshot) {
            $SourceSnapshot.Lease.Position = 0
            Assert-GeoraePlanReleaseFileSnapshot `
                -Snapshot $SourceSnapshot `
                -SourceName 'desktop ZIP validation source after inspection'
        }
        foreach ($identity in @(
            $extractedAppIdentity,
            $extractedAliasIdentity
        )) {
            if ($null -ne $identity -and $null -ne $identity.Lease) {
                $identity.Lease.Dispose()
            }
        }
        if ($null -ne $extractedAppPath -and
            (Test-Path -LiteralPath $extractedAppPath)) {
            Remove-Item -LiteralPath $extractedAppPath -Force -ErrorAction Stop
        }
        if ($null -ne $extractedAliasPath -and
            (Test-Path -LiteralPath $extractedAliasPath)) {
            Remove-Item -LiteralPath $extractedAliasPath -Force -ErrorAction Stop
        }
        if (Test-Path -LiteralPath $tempDirectory) {
            Remove-Item -LiteralPath $tempDirectory -Force -ErrorAction Stop
        }
    }
}

function Get-ManifestReferencedFileNames {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$Platform
    )

    $fileNames = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not (Test-Path -LiteralPath $ManifestRoot)) {
        return $fileNames
    }
    Assert-GeoraePlanReleaseRegularDirectoryChain -Path $ManifestRoot

    $manifestFiles =
        Get-ChildItem `
            -LiteralPath $ManifestRoot `
            -File `
            -Filter '*.json' `
            -Force `
            -ErrorAction Stop
    foreach ($manifestFile in $manifestFiles) {
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $manifestFile.FullName `
            -LeafMayBeFile
        try {
            $manifestJson =
                Get-Content `
                    -LiteralPath $manifestFile.FullName `
                    -Raw `
                    -Encoding UTF8 `
                    -ErrorAction Stop
            if ([string]::IsNullOrWhiteSpace($manifestJson)) {
                throw 'Manifest is empty.'
            }
            $manifest = $manifestJson | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw (
                'Package prune manifest cannot be parsed safely; refusing ' +
                "prune: $($manifestFile.FullName). $($_.Exception.Message)")
        }
        $platformNode = $manifest.$Platform
        $fileName = [string]$platformNode.fileName
        if (-not [string]::IsNullOrWhiteSpace($fileName)) {
            [void]$fileNames.Add($fileName.Trim())
        }

        foreach ($installer in @($platformNode.installers)) {
            $installerFileName = [string]$installer.fileName
            if (-not [string]::IsNullOrWhiteSpace($installerFileName)) {
                [void]$fileNames.Add($installerFileName.Trim())
            }
        }
    }

    return $fileNames
}

function Remove-OldPackages {
    param(
        [Parameter(Mandatory = $true)][string]$DirectoryPath,
        [Parameter(Mandatory = $true)][int]$KeepCount,
        $PreserveFileNames
    )

    if ($KeepCount -lt 1 -or -not (Test-Path -LiteralPath $DirectoryPath)) {
        return @()
    }
    Assert-GeoraePlanReleaseRegularDirectoryChain -Path $DirectoryPath

    if ($null -eq $PreserveFileNames) {
        $PreserveFileNames = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    }

    $removed = New-Object System.Collections.Generic.List[string]
    $keptNonPreserved = 0
    $files = Get-ChildItem -LiteralPath $DirectoryPath -File -Force -ErrorAction Stop |
        Sort-Object -Property @(
            @{ Expression = 'LastWriteTimeUtc'; Descending = $true },
            @{ Expression = 'Name'; Descending = $false }
        )

    foreach ($file in $files) {
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $file.FullName `
            -LeafMayBeFile
        if (
            $file.Name.EndsWith(
                '.georaeplan-release-tmp',
                [StringComparison]::OrdinalIgnoreCase) -or
            $file.Name.EndsWith(
                '.georaeplan-release-bak',
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            continue
        }
        if ($PreserveFileNames.Contains($file.Name)) {
            continue
        }

        if ($keptNonPreserved -lt $KeepCount) {
            $keptNonPreserved++
            continue
        }

        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
        $removed.Add($file.Name) | Out-Null
    }

    return $removed
}

function Write-JsonFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)]$InputObject
    )

    $directory = Split-Path -Parent $TargetPath
    [void](New-GeoraePlanReleaseOwnedDirectory -DirectoryPath $directory)

    $fileName = Split-Path -Leaf $TargetPath
    $tempPath = Join-Path $directory ($fileName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $directory ($fileName + '.' + [Guid]::NewGuid().ToString('N') + '.bak')
    $json = $InputObject | ConvertTo-Json -Depth 10
    $json = $json.Replace("`r`n", "`n").Replace("`r", "`n")
    $jsonBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)

    $operationError = $null
    try {
        $stream = [IO.FileStream]::new(
            $tempPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($jsonBytes, 0, $jsonBytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        Assert-GeoraePlanReleaseRegularDirectoryChain -Path $directory
        if (Test-Path -LiteralPath $TargetPath) {
            [System.IO.File]::Replace($tempPath, $TargetPath, $backupPath, $true)
        }
        else {
            [System.IO.File]::Move($tempPath, $TargetPath)
        }
        Sync-GeoraePlanReleaseCommittedFile -Path $TargetPath
    }
    catch {
        $operationError = $_.Exception
    }
    $cleanupErrors = @()
    foreach ($ownedPath in @($tempPath, $backupPath)) {
        try {
            Remove-StrictOwnedPackageTemporaryFile `
                -TemporaryPath $ownedPath `
                -DestinationDirectory $directory
        }
        catch {
            $cleanupErrors += $_.Exception
        }
    }
    if ($null -ne $operationError) {
        if ($cleanupErrors.Count -gt 0) {
            throw [AggregateException]::new(
                'Atomic JSON write and strict cleanup both failed.',
                [Exception[]](@($operationError) + $cleanupErrors))
        }
        throw $operationError
    }
    if ($cleanupErrors.Count -gt 0) {
        throw [AggregateException]::new(
            'Atomic JSON strict cleanup failed.',
            [Exception[]]$cleanupErrors)
    }
}

function Get-GeoraePlanReleaseRequestFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][bool]$HasDesktopPackage,
        [Parameter(Mandatory = $true)][bool]$HasAndroidPackage,
        $DesktopPackageSnapshot,
        $DesktopExeInstallerSnapshot,
        $DesktopMsiInstallerSnapshot,
        $AndroidPackageSnapshot,
        [string]$DesktopVersion = '',
        [string]$AndroidVersion = '',
        [string]$DesktopNotes = '',
        [string]$AndroidNotes = '',
        [bool]$MandatoryDesktop,
        [bool]$MandatoryAndroid,
        [string]$DesktopMinimumSupportedVersion = '',
        [string]$AndroidMinimumSupportedVersion = '',
        [bool]$SkipAndroid,
        [bool]$PreserveExistingAndroid,
        [bool]$SkipPackagePrune,
        [int]$KeepDesktopPackageCount,
        [int]$KeepAndroidPackageCount,
        [bool]$AllowDowngrade
    )

    function Get-EffectiveMinimumVersion {
        param(
            [string]$Value,
            [bool]$Mandatory,
            [string]$Version
        )

        if ([string]::IsNullOrWhiteSpace($Value)) {
            if ($Mandatory) {
                return $Version
            }
            return ''
        }
        return $Value.Trim()
    }

    function New-RequestAssetFingerprintNode {
        param(
            [string]$Role,
            $Snapshot
        )

        if ($null -eq $Snapshot) {
            return [ordered]@{
                present = $false
                role = $Role
                sha256 = ''
                fileSize = '0'
                artifactIdentity = $Role + ':absent'
            }
        }
        $assetSha256 =
            ([string]$Snapshot.Sha256).ToUpperInvariant()
        $assetFileSize =
            ([long]$Snapshot.FileSize).ToString(
                [Globalization.CultureInfo]::InvariantCulture)
        return [ordered]@{
            present = $true
            role = $Role
            sha256 = $assetSha256
            fileSize = $assetFileSize
            artifactIdentity =
                '{0}:{1}:{2}' -f (
                    $Role,
                    $assetSha256,
                    $assetFileSize)
        }
    }

    $desktopNode = if ($HasDesktopPackage) {
        $desktopMinimum = Get-EffectiveMinimumVersion `
            -Value $DesktopMinimumSupportedVersion `
            -Mandatory $MandatoryDesktop `
            -Version $DesktopVersion
        [ordered]@{
            present = $true
            version = $DesktopVersion
            build = ''
            protocolVersion = ''
            policyVersion = ''
            compatibilityPolicy = ''
            requiresUserAction = ''
            notes = $DesktopNotes
            mandatory = $MandatoryDesktop
            minimumSupportedVersion = $desktopMinimum
            package = New-RequestAssetFingerprintNode `
                -Role 'desktop-package' `
                -Snapshot $DesktopPackageSnapshot
            exeInstaller = New-RequestAssetFingerprintNode `
                -Role 'desktop-exe-installer' `
                -Snapshot $DesktopExeInstallerSnapshot
            msiInstaller = New-RequestAssetFingerprintNode `
                -Role 'desktop-msi-installer' `
                -Snapshot $DesktopMsiInstallerSnapshot
        }
    }
    else {
        [ordered]@{
            present = $false
            version = ''
            build = ''
            protocolVersion = ''
            policyVersion = ''
            compatibilityPolicy = ''
            requiresUserAction = ''
            notes = ''
            mandatory = $false
            minimumSupportedVersion = ''
            package = New-RequestAssetFingerprintNode `
                -Role 'desktop-package' `
                -Snapshot $null
            exeInstaller = New-RequestAssetFingerprintNode `
                -Role 'desktop-exe-installer' `
                -Snapshot $null
            msiInstaller = New-RequestAssetFingerprintNode `
                -Role 'desktop-msi-installer' `
                -Snapshot $null
        }
    }
    $androidNode = if ($HasAndroidPackage) {
        $androidMinimum = Get-EffectiveMinimumVersion `
            -Value $AndroidMinimumSupportedVersion `
            -Mandatory $MandatoryAndroid `
            -Version $AndroidVersion
        [ordered]@{
            present = $true
            skipAndroid = $false
            preserveExistingAndroid = $false
            applicationId = [string]$AndroidPackageSnapshot.ApplicationId
            version = $AndroidVersion
            build =
                ([long]$AndroidPackageSnapshot.VersionCode).ToString(
                    [Globalization.CultureInfo]::InvariantCulture)
            protocolVersion = ''
            policyVersion = ''
            compatibilityPolicy = ''
            requiresUserAction = ''
            notes = $AndroidNotes
            mandatory = $MandatoryAndroid
            minimumSupportedVersion = $androidMinimum
            package = New-RequestAssetFingerprintNode `
                -Role 'android-package' `
                -Snapshot $AndroidPackageSnapshot
        }
    }
    else {
        [ordered]@{
            present = $false
            skipAndroid = $SkipAndroid
            preserveExistingAndroid = $PreserveExistingAndroid
            applicationId = ''
            version = ''
            build = ''
            protocolVersion = ''
            policyVersion = ''
            compatibilityPolicy = ''
            requiresUserAction = ''
            notes = ''
            mandatory = $false
            minimumSupportedVersion = ''
            package = New-RequestAssetFingerprintNode `
                -Role 'android-package' `
                -Snapshot $null
        }
    }
    $payload = [ordered]@{
        schemaVersion = '1'
        channel = $Channel
        packagePolicy = [ordered]@{
            skipPackagePrune = $SkipPackagePrune
            keepDesktopPackageCount = $KeepDesktopPackageCount
            keepAndroidPackageCount = $KeepAndroidPackageCount
            allowDowngrade = $AllowDowngrade
        }
        desktop = $desktopNode
        android = $androidNode
    }
    $canonicalJson =
        $payload | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(
        $canonicalJson)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return (
            [BitConverter]::ToString(
                $sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Initialize-GeoraePlanReleaseJournalTypes {
    if ($null -ne ('GeoraePlan.ReleaseTransaction.Journal' -as [type])) {
        return
    }
    $releaseJournalTypeDefinition = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;

namespace GeoraePlan.ReleaseTransaction
{
    [DataContract]
    public sealed class Journal
    {
        [DataMember(Name = "schemaVersion", IsRequired = true, Order = 0)]
        public int SchemaVersion { get; set; }
        [DataMember(Name = "owner", IsRequired = true, Order = 1)]
        public string Owner { get; set; }
        [DataMember(Name = "channel", IsRequired = true, Order = 2)]
        public string Channel { get; set; }
        [DataMember(Name = "phase", IsRequired = true, Order = 3)]
        public string Phase { get; set; }
        [DataMember(Name = "projectRoot", IsRequired = true, Order = 4)]
        public string ProjectRoot { get; set; }
        [DataMember(Name = "outputRoot", IsRequired = true, Order = 5)]
        public string OutputRoot { get; set; }
        [DataMember(Name = "transactionRoot", IsRequired = true, Order = 6)]
        public string TransactionRoot { get; set; }
        [DataMember(Name = "requestFingerprint", IsRequired = false, Order = 7)]
        public string RequestFingerprint { get; set; }
        [DataMember(Name = "entries", IsRequired = true, Order = 8)]
        public Entry[] Entries { get; set; }
    }

    [DataContract]
    public sealed class Entry
    {
        [DataMember(Name = "targetPath", IsRequired = true, Order = 0)]
        public string TargetPath { get; set; }
        [DataMember(Name = "stagedPath", IsRequired = true, Order = 1)]
        public string StagedPath { get; set; }
        [DataMember(Name = "backupPath", IsRequired = true, Order = 2)]
        public string BackupPath { get; set; }
        [DataMember(Name = "targetExisted", IsRequired = true, Order = 3)]
        public bool TargetExisted { get; set; }
        [DataMember(Name = "stagedSha256", IsRequired = true, Order = 4)]
        public string StagedSha256 { get; set; }
        [DataMember(Name = "backupSha256", IsRequired = true, Order = 5)]
        public string BackupSha256 { get; set; }
        [DataMember(Name = "stagedFileSize", IsRequired = true, Order = 6)]
        public long StagedFileSize { get; set; }
        [DataMember(Name = "backupFileSize", IsRequired = true, Order = 7)]
        public long? BackupFileSize { get; set; }
        [DataMember(Name = "commitTemporaryPath", IsRequired = true, Order = 8)]
        public string CommitTemporaryPath { get; set; }
    }

    public sealed class StrictDirectoryChainLease : IDisposable
    {
        private const uint FileListDirectory = 0x0001;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint InvalidAttributes = 0xFFFFFFFF;
        private const uint ReparsePoint = 0x00000400;
        private const uint DirectoryAttribute = 0x00000010;

        private sealed class Entry
        {
            public string Path;
            public SafeFileHandle Handle;
            public uint Volume;
            public ulong Index;
        }

        private readonly List<Entry> entries;
        private bool disposed;

        private StrictDirectoryChainLease(List<Entry> entries)
        {
            this.entries = entries;
        }

        public static StrictDirectoryChainLease Open(string[] directoryPaths)
        {
            if (directoryPaths == null)
            {
                throw new ArgumentNullException("directoryPaths");
            }
            SortedSet<string> paths =
                new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in directoryPaths)
            {
                if (String.IsNullOrWhiteSpace(input))
                {
                    throw new ArgumentException(
                        "Release directory lease path is empty.");
                }
                string current = System.IO.Path.GetFullPath(input);
                while (!String.IsNullOrWhiteSpace(current))
                {
                    paths.Add(current);
                    string parent = System.IO.Path.GetDirectoryName(current);
                    if (
                        String.IsNullOrWhiteSpace(parent) ||
                        String.Equals(
                            parent,
                            current,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    current = parent;
                }
            }

            List<Entry> opened = new List<Entry>();
            try
            {
                foreach (string path in paths)
                {
                    opened.Add(OpenEntry(path));
                }
                StrictDirectoryChainLease lease =
                    new StrictDirectoryChainLease(opened);
                lease.Validate();
                return lease;
            }
            catch
            {
                foreach (Entry entry in opened)
                {
                    entry.Handle.Dispose();
                }
                throw;
            }
        }

        public void Validate()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    "StrictDirectoryChainLease");
            }
            foreach (Entry expected in entries)
            {
                Entry current = OpenEntry(expected.Path);
                try
                {
                    if (
                        current.Volume != expected.Volume ||
                        current.Index != expected.Index)
                    {
                        throw new IOException(
                            "Release directory path identity changed: " +
                            expected.Path);
                    }
                }
                finally
                {
                    current.Handle.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (Entry entry in entries)
            {
                entry.Handle.Dispose();
            }
        }

        private static Entry OpenEntry(string path)
        {
            uint attributes = GetFileAttributesW(path);
            if (attributes == InvalidAttributes)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Release directory path cannot be inspected: " + path);
            }
            if (
                (attributes & DirectoryAttribute) == 0 ||
                (attributes & ReparsePoint) != 0)
            {
                throw new IOException(
                    "Release directory path is not a regular directory: " +
                    path);
            }
            SafeFileHandle handle = CreateFileW(
                path,
                FileListDirectory,
                ShareRead | ShareWrite,
                IntPtr.Zero,
                OpenExisting,
                BackupSemantics | OpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    "Release directory lease cannot be acquired: " + path);
            }
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    "Release directory identity cannot be read: " + path);
            }
            return new Entry
            {
                Path = path,
                Handle = handle,
                Volume = information.VolumeSerialNumber,
                Index =
                    ((ulong)information.FileIndexHigh << 32) |
                    information.FileIndexLow
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFileAttributesW(string fileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
    }

    public static class StrictJournalJsonValidator
    {
        public static void Validate(byte[] utf8Json)
        {
            if (utf8Json == null)
            {
                throw new ArgumentNullException("utf8Json");
            }

            string json = new UTF8Encoding(false, true).GetString(utf8Json);
            new Parser(json).ValidateDocument();
        }

        public static void ValidateExactStringObject(
            byte[] utf8Json,
            string[] expectedProperties)
        {
            if (utf8Json == null)
            {
                throw new ArgumentNullException("utf8Json");
            }
            if (expectedProperties == null)
            {
                throw new ArgumentNullException("expectedProperties");
            }

            string json = new UTF8Encoding(false, true).GetString(utf8Json);
            new Parser(json).ValidateExactStringObjectDocument(
                expectedProperties);
        }

        private sealed class Parser
        {
            private static readonly string[] RootPropertiesV3 = new[]
            {
                "schemaVersion",
                "owner",
                "channel",
                "phase",
                "projectRoot",
                "outputRoot",
                "transactionRoot",
                "entries"
            };
            private static readonly string[] RootPropertiesV4 = new[]
            {
                "schemaVersion",
                "owner",
                "channel",
                "phase",
                "projectRoot",
                "outputRoot",
                "transactionRoot",
                "requestFingerprint",
                "entries"
            };

            private static readonly string[] EntryProperties = new[]
            {
                "targetPath",
                "stagedPath",
                "backupPath",
                "targetExisted",
                "stagedSha256",
                "backupSha256",
                "stagedFileSize",
                "backupFileSize",
                "commitTemporaryPath"
            };

            private readonly string json;
            private int position;

            public Parser(string json)
            {
                this.json = json;
            }

            public void ValidateDocument()
            {
                SkipWhitespace();
                ValidateRoot();
                SkipWhitespace();
                if (position != json.Length)
                {
                    Fail("Release transaction journal has trailing JSON.");
                }
            }

            public void ValidateExactStringObjectDocument(
                string[] expectedProperties)
            {
                HashSet<string> expected =
                    new HashSet<string>(
                        expectedProperties,
                        StringComparer.Ordinal);
                if (expected.Count != expectedProperties.Length)
                {
                    Fail("Strict metadata schema contains duplicate properties.");
                }

                HashSet<string> seen =
                    new HashSet<string>(StringComparer.Ordinal);
                SkipWhitespace();
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    Fail("Strict metadata object is missing properties.");
                }
                while (true)
                {
                    string property = ReadString();
                    if (!expected.Contains(property) || !seen.Add(property))
                    {
                        Fail(
                            "Strict metadata object has an unknown or " +
                            "duplicate property.");
                    }
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    ReadRequiredString();
                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        break;
                    }
                    Expect(',');
                    SkipWhitespace();
                }
                AssertExactProperties(
                    seen,
                    expectedProperties,
                    "Strict metadata object");
                SkipWhitespace();
                if (position != json.Length)
                {
                    Fail("Strict metadata object has trailing JSON.");
                }
            }

            private void ValidateRoot()
            {
                HashSet<string> seen =
                    new HashSet<string>(StringComparer.Ordinal);
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    Fail("Release transaction journal is missing properties.");
                }

                while (true)
                {
                    string property = ReadString();
                    if (!seen.Add(property))
                    {
                        Fail(
                            "Release transaction journal has a duplicate " +
                            "property.");
                    }
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    switch (property)
                    {
                        case "schemaVersion":
                            ReadNumber();
                            break;
                        case "owner":
                        case "channel":
                        case "phase":
                        case "projectRoot":
                        case "outputRoot":
                        case "transactionRoot":
                        case "requestFingerprint":
                            ReadRequiredString();
                            break;
                        case "entries":
                            ValidateEntries();
                            break;
                        default:
                            Fail(
                                "Release transaction journal has an unknown " +
                                "property.");
                            break;
                    }

                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        break;
                    }
                    Expect(',');
                    SkipWhitespace();
                }

                AssertExactProperties(
                    seen,
                    seen.Contains("requestFingerprint")
                        ? RootPropertiesV4
                        : RootPropertiesV3,
                    "Release transaction journal");
            }

            private void ValidateEntries()
            {
                Expect('[');
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    return;
                }

                while (true)
                {
                    ValidateEntry();
                    SkipWhitespace();
                    if (TryConsume(']'))
                    {
                        return;
                    }
                    Expect(',');
                    SkipWhitespace();
                }
            }

            private void ValidateEntry()
            {
                HashSet<string> seen =
                    new HashSet<string>(StringComparer.Ordinal);
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    Fail(
                        "Release transaction entry is missing properties.");
                }

                while (true)
                {
                    string property = ReadString();
                    if (!seen.Add(property))
                    {
                        Fail(
                            "Release transaction entry has a duplicate " +
                            "property.");
                    }
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    switch (property)
                    {
                        case "targetPath":
                        case "stagedPath":
                        case "stagedSha256":
                        case "commitTemporaryPath":
                            ReadRequiredString();
                            break;
                        case "backupPath":
                        case "backupSha256":
                            ReadNullableString();
                            break;
                        case "targetExisted":
                            ReadBoolean();
                            break;
                        case "stagedFileSize":
                            ReadNumber();
                            break;
                        case "backupFileSize":
                            ReadNullableNumber();
                            break;
                        default:
                            Fail(
                                "Release transaction entry has an unknown " +
                                "property.");
                            break;
                    }

                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        break;
                    }
                    Expect(',');
                    SkipWhitespace();
                }

                AssertExactProperties(
                    seen,
                    EntryProperties,
                    "Release transaction entry");
            }

            private static void AssertExactProperties(
                HashSet<string> seen,
                string[] expected,
                string description)
            {
                if (seen.Count != expected.Length)
                {
                    Fail(description + " does not have the exact schema.");
                }
                foreach (string property in expected)
                {
                    if (!seen.Contains(property))
                    {
                        Fail(
                            description +
                            " does not have the exact schema.");
                    }
                }
            }

            private void ReadRequiredString()
            {
                ReadString();
            }

            private void ReadNullableString()
            {
                if (!TryReadLiteral("null"))
                {
                    ReadString();
                }
            }

            private void ReadNullableNumber()
            {
                if (!TryReadLiteral("null"))
                {
                    ReadNumber();
                }
            }

            private void ReadBoolean()
            {
                if (!TryReadLiteral("true") && !TryReadLiteral("false"))
                {
                    Fail(
                        "Release transaction journal expected a boolean.");
                }
            }

            private void ReadNumber()
            {
                int start = position;
                TryConsume('-');
                if (TryConsume('0'))
                {
                    if (position < json.Length &&
                        IsDigit(json[position]))
                    {
                        Fail(
                            "Release transaction journal has an invalid " +
                            "number.");
                    }
                }
                else
                {
                    if (position >= json.Length ||
                        json[position] < '1' ||
                        json[position] > '9')
                    {
                        Fail(
                            "Release transaction journal expected a number.");
                    }
                    position++;
                    while (position < json.Length &&
                        IsDigit(json[position]))
                    {
                        position++;
                    }
                }

                if (TryConsume('.'))
                {
                    ReadDigits();
                }
                if (TryConsume('e') || TryConsume('E'))
                {
                    if (!TryConsume('+'))
                    {
                        TryConsume('-');
                    }
                    ReadDigits();
                }
                if (position == start)
                {
                    Fail(
                        "Release transaction journal expected a number.");
                }
            }

            private void ReadDigits()
            {
                int start = position;
                while (position < json.Length && IsDigit(json[position]))
                {
                    position++;
                }
                if (position == start)
                {
                    Fail(
                        "Release transaction journal has an invalid number.");
                }
            }

            private string ReadString()
            {
                Expect('"');
                StringBuilder value = new StringBuilder();
                while (position < json.Length)
                {
                    char current = json[position++];
                    if (current == '"')
                    {
                        string result = value.ToString();
                        ValidateUnicodeScalars(result);
                        return result;
                    }
                    if (current < 0x20)
                    {
                        Fail(
                            "Release transaction journal has an invalid " +
                            "string.");
                    }
                    if (current != '\\')
                    {
                        value.Append(current);
                        continue;
                    }
                    if (position >= json.Length)
                    {
                        Fail(
                            "Release transaction journal has an invalid " +
                            "escape.");
                    }

                    char escaped = json[position++];
                    switch (escaped)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            value.Append(escaped);
                            break;
                        case 'b':
                            value.Append('\b');
                            break;
                        case 'f':
                            value.Append('\f');
                            break;
                        case 'n':
                            value.Append('\n');
                            break;
                        case 'r':
                            value.Append('\r');
                            break;
                        case 't':
                            value.Append('\t');
                            break;
                        case 'u':
                            value.Append(ReadUnicodeEscape());
                            break;
                        default:
                            Fail(
                                "Release transaction journal has an invalid " +
                                "escape.");
                            break;
                    }
                }

                Fail("Release transaction journal has an unterminated string.");
                return null;
            }

            private char ReadUnicodeEscape()
            {
                if (position + 4 > json.Length)
                {
                    Fail(
                        "Release transaction journal has an invalid unicode " +
                        "escape.");
                }
                int value = 0;
                for (int index = 0; index < 4; index++)
                {
                    int digit = HexValue(json[position++]);
                    if (digit < 0)
                    {
                        Fail(
                            "Release transaction journal has an invalid " +
                            "unicode escape.");
                    }
                    value = (value << 4) | digit;
                }
                return (char)value;
            }

            private static void ValidateUnicodeScalars(string value)
            {
                for (int index = 0; index < value.Length; index++)
                {
                    char current = value[index];
                    if (char.IsHighSurrogate(current))
                    {
                        if (index + 1 >= value.Length ||
                            !char.IsLowSurrogate(value[index + 1]))
                        {
                            Fail(
                                "Release transaction journal has an invalid " +
                                "unicode scalar.");
                        }
                        index++;
                    }
                    else if (char.IsLowSurrogate(current))
                    {
                        Fail(
                            "Release transaction journal has an invalid " +
                            "unicode scalar.");
                    }
                }
            }

            private bool TryReadLiteral(string literal)
            {
                if (position + literal.Length > json.Length)
                {
                    return false;
                }
                for (int index = 0; index < literal.Length; index++)
                {
                    if (json[position + index] != literal[index])
                    {
                        return false;
                    }
                }
                position += literal.Length;
                return true;
            }

            private void SkipWhitespace()
            {
                while (position < json.Length)
                {
                    char current = json[position];
                    if (current != ' ' &&
                        current != '\t' &&
                        current != '\r' &&
                        current != '\n')
                    {
                        return;
                    }
                    position++;
                }
            }

            private void Expect(char expected)
            {
                if (!TryConsume(expected))
                {
                    Fail(
                        "Release transaction journal has invalid JSON syntax.");
                }
            }

            private bool TryConsume(char expected)
            {
                if (position >= json.Length ||
                    json[position] != expected)
                {
                    return false;
                }
                position++;
                return true;
            }

            private static bool IsDigit(char value)
            {
                return value >= '0' && value <= '9';
            }

            private static int HexValue(char value)
            {
                if (value >= '0' && value <= '9')
                {
                    return value - '0';
                }
                if (value >= 'a' && value <= 'f')
                {
                    return value - 'a' + 10;
                }
                if (value >= 'A' && value <= 'F')
                {
                    return value - 'A' + 10;
                }
                return -1;
            }

            private static void Fail(string message)
            {
                throw new SerializationException(message);
            }
        }
    }
}
'@
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -TypeDefinition $releaseJournalTypeDefinition
    }
    else {
        Add-Type `
            -ReferencedAssemblies System.Runtime.Serialization `
            -TypeDefinition $releaseJournalTypeDefinition
    }
}

function Open-GeoraePlanReleaseDirectoryChainLease {
    param(
        [Parameter(Mandatory = $true)][string[]]$DirectoryPaths
    )

    foreach ($directoryPath in $DirectoryPaths) {
        Assert-GeoraePlanReleaseRegularDirectoryChain -Path $directoryPath
    }
    Initialize-GeoraePlanReleaseJournalTypes
    return [GeoraePlan.ReleaseTransaction.StrictDirectoryChainLease]::Open(
        $DirectoryPaths)
}

function Assert-GeoraePlanReleaseDirectoryChainLease {
    param([Parameter(Mandatory = $true)]$Lease)

    if ($null -eq $Lease) {
        throw 'Release directory chain lease is missing.'
    }
    $Lease.Validate()
}

function Read-StrictGeoraePlanReleaseJournal {
    param([Parameter(Mandatory = $true)][string]$JournalPath)

    $journalItem =
        Get-Item -LiteralPath $JournalPath -Force -ErrorAction Stop
    if (
        $journalItem.PSIsContainer -or
        ($journalItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Release transaction journal is not a regular file.'
    }
    $journalBytes = [IO.File]::ReadAllBytes($JournalPath)
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $null = $strictUtf8.GetString($journalBytes)
    Initialize-GeoraePlanReleaseJournalTypes
    [GeoraePlan.ReleaseTransaction.StrictJournalJsonValidator]::Validate(
        $journalBytes)
    $serializer =
        [Runtime.Serialization.Json.DataContractJsonSerializer]::new(
            [GeoraePlan.ReleaseTransaction.Journal])
    $stream = [IO.MemoryStream]::new($journalBytes, $false)
    try {
        $journal = $serializer.ReadObject($stream)
    }
    finally {
        $stream.Dispose()
    }
    if ($null -eq $journal -or $null -eq $journal.Entries) {
        throw 'Release transaction journal schema is not exact.'
    }
    foreach ($entry in $journal.Entries) {
        if ($null -eq $entry) {
            throw 'Release transaction entry schema is not exact.'
        }
    }
    return $journal
}

function Write-DurableGeoraePlanReleaseJournal {
    param(
        [Parameter(Mandatory = $true)][string]$JournalPath,
        [Parameter(Mandatory = $true)]$Journal,
        [switch]$CreateNew
    )

    $journalDirectory = Split-Path -Parent $JournalPath
    Assert-GeoraePlanReleaseRegularDirectoryChain -Path $journalDirectory
    $journalEntries = @(
        foreach ($entry in @($Journal.Entries)) {
            [ordered]@{
                targetPath = [string]$entry.TargetPath
                stagedPath = [string]$entry.StagedPath
                backupPath =
                    if ($null -eq $entry.BackupPath) {
                        $null
                    }
                    else {
                        [string]$entry.BackupPath
                    }
                targetExisted = $entry.TargetExisted
                stagedSha256 = [string]$entry.StagedSha256
                backupSha256 =
                    if ($null -eq $entry.BackupSha256) {
                        $null
                    }
                    else {
                        [string]$entry.BackupSha256
                    }
                stagedFileSize = [long]$entry.StagedFileSize
                backupFileSize =
                    if ($null -eq $entry.BackupFileSize) {
                        $null
                    }
                    else {
                        [long]$entry.BackupFileSize
                    }
                commitTemporaryPath = [string]$entry.CommitTemporaryPath
            }
        }
    )
    $journalPayload = if ([int]$Journal.SchemaVersion -eq 4) {
        [ordered]@{
            schemaVersion = [int]$Journal.SchemaVersion
            owner = [string]$Journal.Owner
            channel = [string]$Journal.Channel
            phase = [string]$Journal.Phase
            projectRoot = [string]$Journal.ProjectRoot
            outputRoot = [string]$Journal.OutputRoot
            transactionRoot = [string]$Journal.TransactionRoot
            requestFingerprint = [string]$Journal.RequestFingerprint
            entries = $journalEntries
        }
    }
    else {
        [ordered]@{
            schemaVersion = [int]$Journal.SchemaVersion
            owner = [string]$Journal.Owner
            channel = [string]$Journal.Channel
            phase = [string]$Journal.Phase
            projectRoot = [string]$Journal.ProjectRoot
            outputRoot = [string]$Journal.OutputRoot
            transactionRoot = [string]$Journal.TransactionRoot
            entries = $journalEntries
        }
    }
    $json = $journalPayload | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    if ($CreateNew) {
        $createPendingPath = $JournalPath + '.create-pending'
        try {
            $stream = [IO.FileStream]::new(
                $createPendingPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None,
                4096,
                [IO.FileOptions]::WriteThrough)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
            Invoke-GeoraePlanReleaseTestKillPoint `
                -Name 'AfterInitialJournalTempFlush'
            if (Test-Path -LiteralPath $JournalPath) {
                throw 'Release transaction journal already exists.'
            }
            [IO.File]::Move($createPendingPath, $JournalPath)
            Sync-GeoraePlanReleaseCommittedFile -Path $JournalPath
        }
        catch {
            $createError = $_.Exception
            try {
                Remove-StrictOwnedPackageTemporaryFile `
                    -TemporaryPath $createPendingPath `
                    -DestinationDirectory $journalDirectory
            }
            catch {
                throw [AggregateException]::new(
                    'Initial release journal activation and cleanup both failed.',
                    [Exception[]]@($createError, $_.Exception))
            }
            throw $createError
        }
        return
    }

    $temporaryPath = Join-Path $journalDirectory (
        '.journal.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $journalDirectory (
        '.journal.' + [Guid]::NewGuid().ToString('N') + '.bak')
    $operationError = $null
    try {
        $stream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        [IO.File]::Replace(
            $temporaryPath,
            $JournalPath,
            $backupPath,
            $true)
        Sync-GeoraePlanReleaseCommittedFile -Path $JournalPath
    }
    catch {
        $operationError = $_.Exception
    }
    $cleanupErrors = @()
    foreach ($ownedPath in @($temporaryPath, $backupPath)) {
        try {
            Invoke-GeoraePlanReleaseTestFailurePoint `
                -Name (
                    'DuringJournalSidecarCleanupAfter' +
                    [string]$Journal.Phase)
            Remove-StrictOwnedPackageTemporaryFile `
                -TemporaryPath $ownedPath `
                -DestinationDirectory $journalDirectory
        }
        catch {
            $cleanupErrors += $_.Exception
        }
    }
    if ($null -ne $operationError) {
        if ($cleanupErrors.Count -gt 0) {
            throw [AggregateException]::new(
                'Release journal update and cleanup both failed.',
                [Exception[]](@($operationError) + $cleanupErrors))
        }
        throw $operationError
    }
    if ($cleanupErrors.Count -gt 0) {
        throw [AggregateException]::new(
            'Release journal cleanup failed.',
            [Exception[]]$cleanupErrors)
    }
}

function Write-DurableGeoraePlanReleaseStringMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)]$Payload,
        [string]$KillPoint
    )

    $targetDirectory = Split-Path -Parent $TargetPath
    Assert-GeoraePlanReleaseRegularDirectoryChain -Path $targetDirectory
    if (Test-Path -LiteralPath $TargetPath) {
        throw "Release metadata target already exists: $TargetPath"
    }
    $pendingPath = $TargetPath + '.create-pending'
    if (Test-Path -LiteralPath $pendingPath) {
        throw "Release metadata activation sidecar already exists: $pendingPath"
    }
    $json = $Payload | ConvertTo-Json -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    $operationError = $null
    try {
        $stream = [IO.FileStream]::new(
            $pendingPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        if (-not [string]::IsNullOrWhiteSpace($KillPoint)) {
            Invoke-GeoraePlanReleaseTestKillPoint -Name $KillPoint
        }
        if (Test-Path -LiteralPath $TargetPath) {
            throw "Release metadata target appeared during activation: $TargetPath"
        }
        [IO.File]::Move($pendingPath, $TargetPath)
        Sync-GeoraePlanReleaseCommittedFile -Path $TargetPath
    }
    catch {
        $operationError = $_.Exception
    }
    if ($null -ne $operationError) {
        try {
            Remove-StrictOwnedPackageTemporaryFile `
                -TemporaryPath $pendingPath `
                -DestinationDirectory $targetDirectory
        }
        catch {
            throw [AggregateException]::new(
                'Release metadata activation and cleanup both failed.',
                [Exception[]]@($operationError, $_.Exception))
        }
        throw $operationError
    }
}

function Read-StrictGeoraePlanReleaseStringMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$MetadataPath,
        [Parameter(Mandatory = $true)][string[]]$ExpectedProperties
    )

    Assert-GeoraePlanReleaseRegularDirectoryChain `
        -Path $MetadataPath `
        -LeafMayBeFile
    $metadataItem =
        Get-Item -LiteralPath $MetadataPath -Force -ErrorAction Stop
    if (
        $metadataItem.PSIsContainer -or
        ($metadataItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Release metadata is not a regular owned file.'
    }
    $bytes = [IO.File]::ReadAllBytes($MetadataPath)
    Initialize-GeoraePlanReleaseJournalTypes
    [GeoraePlan.ReleaseTransaction.StrictJournalJsonValidator]::
        ValidateExactStringObject($bytes, $ExpectedProperties)
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    return $json | ConvertFrom-Json -ErrorAction Stop
}

function Get-GeoraePlanReleasePreparationState {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$PreparationId
    )

    if ($PreparationId -notmatch '^[0-9a-f]{32}$') {
        throw 'Release preparation id is invalid.'
    }
    $preparationRoot =
        [IO.Path]::GetFullPath($TransactionRoot + '.prepare-' + $PreparationId)
    return [ordered]@{
        owner = 'georaeplan-release-preparation'
        channel = $Channel
        projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
        outputRoot = [IO.Path]::GetFullPath($OutputRoot)
        transactionRoot = [IO.Path]::GetFullPath($TransactionRoot)
        preparationRoot = $preparationRoot
        preparationId = $PreparationId
    }
}

function Assert-GeoraePlanReleasePreparationMarker {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected
    )

    if (
        -not [string]::Equals(
            [string]$Actual.owner,
            [string]$Expected.owner,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$Actual.channel,
            [string]$Expected.channel,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$Actual.preparationId,
            [string]$Expected.preparationId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$Actual.projectRoot),
            [IO.Path]::GetFullPath([string]$Expected.projectRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$Actual.outputRoot),
            [IO.Path]::GetFullPath([string]$Expected.outputRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$Actual.transactionRoot),
            [IO.Path]::GetFullPath([string]$Expected.transactionRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$Actual.preparationRoot),
            [IO.Path]::GetFullPath([string]$Expected.preparationRoot),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw (
            'Release preparation ownership binding is invalid; preserving ' +
            'preparation evidence.')
    }
}

function Resume-GeoraePlanReleasePreparations {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $transactionLeaf = Split-Path -Leaf $TransactionRoot
    $ownerPattern = $transactionLeaf + '.prepare-*.owner.json'
    $ownerFiles = @(
        Get-ChildItem `
            -LiteralPath $OutputRoot `
            -File `
            -Force `
            -ErrorAction Stop |
            Where-Object {
                $_.Name -like $ownerPattern -or
                $_.Name -like ($ownerPattern + '.create-pending')
            })
    $properties = @(
        'owner',
        'channel',
        'projectRoot',
        'outputRoot',
        'transactionRoot',
        'preparationRoot',
        'preparationId')
    foreach ($ownerFile in $ownerFiles) {
        $isPendingOwner =
            $ownerFile.Name.EndsWith(
                '.create-pending',
                [StringComparison]::OrdinalIgnoreCase)
        $actual =
            Read-StrictGeoraePlanReleaseStringMetadata `
                -MetadataPath $ownerFile.FullName `
                -ExpectedProperties $properties
        $expected =
            Get-GeoraePlanReleasePreparationState `
                -TransactionRoot $TransactionRoot `
                -OutputRoot $OutputRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel `
                -PreparationId ([string]$actual.preparationId)
        $expectedOwnerPath =
            [string]$expected.preparationRoot + '.owner.json'
        $expectedCurrentOwnerPath = if ($isPendingOwner) {
            $expectedOwnerPath + '.create-pending'
        }
        else {
            $expectedOwnerPath
        }
        if (-not [string]::Equals(
            [IO.Path]::GetFullPath($ownerFile.FullName),
            [IO.Path]::GetFullPath($expectedCurrentOwnerPath),
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw 'Release preparation owner file name is not canonical.'
        }
        Assert-GeoraePlanReleasePreparationMarker `
            -Actual $actual `
            -Expected $expected
        $preparationRoot = [string]$expected.preparationRoot
        $hasPreparation = Test-Path -LiteralPath $preparationRoot
        $hasTransaction = Test-Path -LiteralPath $TransactionRoot
        if ($hasPreparation -and $hasTransaction) {
            throw (
                'Release preparation and active transaction roots both exist; ' +
                'preserving both.')
        }
        if ($isPendingOwner -and ($hasPreparation -or $hasTransaction)) {
            if (Test-Path -LiteralPath $expectedOwnerPath) {
                throw (
                    'Release preparation has both pending and activated owner ' +
                    'metadata; preserving both.')
            }
            [IO.File]::Move($ownerFile.FullName, $expectedOwnerPath)
            Sync-GeoraePlanReleaseCommittedFile -Path $expectedOwnerPath
            $ownerFile =
                Get-Item `
                    -LiteralPath $expectedOwnerPath `
                    -Force `
                    -ErrorAction Stop
            $isPendingOwner = $false
        }
        if ($hasTransaction) {
            $activeJournalPath = Join-Path $TransactionRoot 'journal.json'
            if (-not (Test-Path -LiteralPath $activeJournalPath -PathType Leaf)) {
                throw (
                    'Activated release transaction lacks its owner journal; ' +
                    'preserving preparation evidence.')
            }
            $activeJournal =
                Read-StrictGeoraePlanReleaseJournal `
                    -JournalPath $activeJournalPath
            if (
                -not [string]::Equals(
                    [IO.Path]::GetFullPath($activeJournal.TransactionRoot),
                    [IO.Path]::GetFullPath($TransactionRoot),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    $activeJournal.Owner,
                    'georaeplan-release-transaction',
                    [StringComparison]::Ordinal)
            ) {
                throw 'Activated release preparation journal binding is invalid.'
            }
        }
        elseif ($hasPreparation) {
            Remove-GeoraePlanReleaseTransactionDirectory `
                -TransactionRoot $preparationRoot `
                -OutputRoot $OutputRoot
        }
        Remove-StrictOwnedPackageTemporaryFile `
            -TemporaryPath $ownerFile.FullName `
            -DestinationDirectory $OutputRoot
    }
}

function Get-GeoraePlanReleaseTransactionRoot {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    if ($Channel -notmatch '^[A-Za-z0-9._-]+$') {
        throw 'Release channel is not safe for an owned transaction path.'
    }
    return Join-Path $OutputRoot (
        '.georaeplan-release-transaction-' + $Channel)
}

function Open-GeoraePlanReleaseExclusiveFileLease {
    param(
        [Parameter(Mandatory = $true)][string]$LockPath,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            return [IO.FileStream]::new(
                $LockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None,
                4096,
                [IO.FileOptions]::WriteThrough)
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw (
                    'Timed out waiting for the shared release publish lock: ' +
                    $LockPath)
            }
            Start-Sleep -Milliseconds 25
        }
    }
}

function Open-GeoraePlanReleasePublishLock {
    param([Parameter(Mandatory = $true)][string]$OutputRoot)

    [void](New-GeoraePlanReleaseOwnedDirectory -DirectoryPath $OutputRoot)
    $outputItem =
        Get-Item -LiteralPath $OutputRoot -Force -ErrorAction Stop
    if (
        -not $outputItem.PSIsContainer -or
        ($outputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Release publish lock parent is not a regular owned directory.'
    }
    $lockPath = Join-Path $OutputRoot '.georaeplan-release-publish.lock'
    $lease =
        Open-GeoraePlanReleaseExclusiveFileLease -LockPath $lockPath
    try {
        $lockItem =
            Get-Item -LiteralPath $lockPath -Force -ErrorAction Stop
        if (
            $lockItem.PSIsContainer -or
            ($lockItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not [string]::Equals(
                [IO.Path]::GetDirectoryName(
                    [IO.Path]::GetFullPath($lockItem.FullName)),
                [IO.Path]::GetFullPath($OutputRoot),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($lease.Name),
                [IO.Path]::GetFullPath($lockPath),
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Release publish lock binding is invalid.'
        }
        return $lease
    }
    catch {
        $lease.Dispose()
        throw
    }
}

function Open-GeoraePlanReleaseDeliveryPublishLock {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    if ($Channel -notmatch '^[A-Za-z0-9._-]+$') {
        throw 'Release channel is not safe for the shared delivery lock.'
    }
    $deliveryRoot =
        New-GeoraePlanReleaseOwnedDirectory `
            -DirectoryPath (Join-Path $ProjectRoot '배포')
    $lockPath = Join-Path $deliveryRoot (
        '.georaeplan-release-publish-' + $Channel + '.lock')
    $lease =
        Open-GeoraePlanReleaseExclusiveFileLease -LockPath $lockPath
    try {
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $lockPath `
            -LeafMayBeFile
        if (-not [string]::Equals(
            [IO.Path]::GetFullPath($lease.Name),
            [IO.Path]::GetFullPath($lockPath),
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw 'Shared release delivery lock binding is invalid.'
        }
        return $lease
    }
    catch {
        $lease.Dispose()
        throw
    }
}

function Test-GeoraePlanReleaseTransactionTarget {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $resolvedTarget = [IO.Path]::GetFullPath($TargetPath)
    $allowedExactTargets = @(
        (Join-Path $OutputRoot ("manifest\" + $Channel + '.json')),
        (Join-Path $OutputRoot ("manifest\" + $Channel + '.previous.json')),
        (Join-Path $OutputRoot (
            "manifest\" + $Channel + '.current.json')),
        (Join-Path $OutputRoot (
            "manifest\" + $Channel + '.request-receipt.json')),
        (Join-Path $ProjectRoot ("배포\" + $Channel + '.json'))
    ) | ForEach-Object { [IO.Path]::GetFullPath($_) }
    foreach ($allowedTarget in $allowedExactTargets) {
        if ([string]::Equals(
            $resolvedTarget,
            $allowedTarget,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            return $true
        }
    }

    $generationDirectories = @(
        (Join-Path $OutputRoot (
            'manifest\generations\' + $Channel)),
        (Join-Path $ProjectRoot (
            '배포\.georaeplan-release-generations\' + $Channel))
    ) | ForEach-Object { [IO.Path]::GetFullPath($_) }
    $allowedDirectories = @(
        (Join-Path $OutputRoot 'downloads\android'),
        (Join-Path $OutputRoot 'downloads\desktop')
    ) + @($generationDirectories)
    foreach ($allowedDirectory in $allowedDirectories) {
        $resolvedDirectory = [IO.Path]::GetFullPath($allowedDirectory)
        if ([string]::Equals(
            [IO.Path]::GetDirectoryName($resolvedTarget),
            $resolvedDirectory,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            if (
                $generationDirectories -contains $resolvedDirectory -and
                $resolvedTarget -notmatch
                    '[\\/][0-9a-f]{32}\.json$'
            ) {
                continue
            }
            return $true
        }
    }
    return $false
}

function Assert-GeoraePlanReleaseTransactionTargetBoundary {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    if (-not (Test-GeoraePlanReleaseTransactionTarget `
        -TargetPath $TargetPath `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
    )) {
        throw "Release transaction target is outside its owned scope: $TargetPath"
    }
    Assert-GeoraePlanReleaseRegularDirectoryChain `
        -Path (Split-Path -Parent $TargetPath)
    if (Test-Path -LiteralPath $TargetPath) {
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $TargetPath `
            -LeafMayBeFile
        $targetItem =
            Get-Item -LiteralPath $TargetPath -Force -ErrorAction Stop
        if ($targetItem.PSIsContainer) {
            throw "Release transaction target is not a regular file: $TargetPath"
        }
    }
    if ($null -ne $script:releaseDirectoryLease) {
        Assert-GeoraePlanReleaseDirectoryChainLease `
            -Lease $script:releaseDirectoryLease
    }
}

function Assert-GeoraePlanReleaseTransactionDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    $resolvedTransactionRoot = [IO.Path]::GetFullPath($TransactionRoot)
    Assert-GeoraePlanReleaseRegularDirectoryChain -Path $OutputRoot
    if ($null -ne $script:releaseDirectoryLease) {
        Assert-GeoraePlanReleaseDirectoryChainLease `
            -Lease $script:releaseDirectoryLease
    }
    if (-not [string]::Equals(
        [IO.Path]::GetDirectoryName($resolvedTransactionRoot),
        [IO.Path]::GetFullPath($OutputRoot),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Release transaction root escaped its owned output root.'
    }
    if (-not [IO.Directory]::Exists($resolvedTransactionRoot)) {
        return
    }
    $rootItem =
        Get-Item -LiteralPath $resolvedTransactionRoot -Force -ErrorAction Stop
    if (
        -not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Release transaction root is not a strict-owned regular directory.'
    }
    $pendingDirectories =
        New-Object System.Collections.Generic.Queue[string]
    $pendingDirectories.Enqueue($resolvedTransactionRoot)
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Dequeue()
        foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries(
            $currentDirectory,
            '*',
            [IO.SearchOption]::TopDirectoryOnly
        )) {
            $entry =
                Get-Item -LiteralPath $entryPath -Force -ErrorAction Stop
            if (
                ($entry.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw 'Release transaction tree contains a reparse-point entry.'
            }
            if ($entry.PSIsContainer) {
                $pendingDirectories.Enqueue($entry.FullName)
            }
        }
    }
}

function Remove-GeoraePlanReleaseTransactionDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    if (-not (Test-Path -LiteralPath $TransactionRoot)) {
        return
    }
    Assert-GeoraePlanReleaseTransactionDirectory `
        -TransactionRoot $TransactionRoot `
        -OutputRoot $OutputRoot
    Remove-Item `
        -LiteralPath $TransactionRoot `
        -Recurse `
        -Force `
        -ErrorAction Stop
    if (Test-Path -LiteralPath $TransactionRoot) {
        throw 'Release transaction cleanup did not remove its owned directory.'
    }
}

function Copy-GeoraePlanReleaseTransactionFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$TemporaryPath,
        [string]$ExpectedSha256,
        [long]$ExpectedFileSize = -1
    )

    $targetDirectory = Split-Path -Parent $TargetPath
    [void](New-GeoraePlanReleaseOwnedDirectory `
        -DirectoryPath $targetDirectory)
    $temporaryPath = [IO.Path]::GetFullPath($TemporaryPath)
    if (-not [string]::Equals(
        [IO.Path]::GetDirectoryName($temporaryPath),
        [IO.Path]::GetFullPath($targetDirectory),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Release transaction temporary commit path escaped its target.'
    }
    if (Test-Path -LiteralPath $temporaryPath) {
        throw (
            'Release transaction temporary commit path already exists; ' +
            'refusing to claim unowned evidence.')
    }
    if (-not $temporaryPath.EndsWith(
        '.georaeplan-release-tmp',
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Release transaction temporary commit suffix is invalid.'
    }
    $backupPath =
        $temporaryPath.Substring(
            0,
            $temporaryPath.Length -
                '.georaeplan-release-tmp'.Length) +
        '.georaeplan-release-bak'
    if (Test-Path -LiteralPath $backupPath) {
        throw (
            'Release transaction temporary backup path already exists; ' +
            'refusing to claim unowned evidence.')
    }
    $operationError = $null
    try {
        if ($null -ne $script:releaseDirectoryLease) {
            Assert-GeoraePlanReleaseDirectoryChainLease `
                -Lease $script:releaseDirectoryLease
        }
        $sourceStageLease =
            Get-GeoraePlanReleaseTransactionStageLease -Path $SourcePath
        if ($null -ne $sourceStageLease) {
            if (
                $sourceStageLease.Length -ne $ExpectedFileSize -or
                -not [string]::Equals(
                    (Get-GeoraePlanReleaseLeaseSha256 `
                        -Lease $sourceStageLease),
                    $ExpectedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw 'Release transaction stage lease identity changed.'
            }
            Copy-GeoraePlanReleaseLeaseToDurableFile `
                -SourceLease $sourceStageLease `
                -TargetPath $temporaryPath
        }
        else {
            Copy-DurableGeoraePlanReleaseOwnedFile `
                -SourcePath $SourcePath `
                -TargetPath $temporaryPath
        }
        $temporaryItem =
            Get-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop
        $temporaryHash = (
            Get-FileHash `
                -LiteralPath $temporaryPath `
                -Algorithm SHA256).Hash
        if (
            (
                -not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
                -not [string]::Equals(
                    $temporaryHash,
                    $ExpectedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) -or
            (
                $ExpectedFileSize -ge 0 -and
                $temporaryItem.Length -ne $ExpectedFileSize
            )
        ) {
            throw 'Release transaction temporary commit evidence does not match.'
        }
        Invoke-GeoraePlanReleaseTestKillPoint `
            -Name 'AfterCommitTemporaryFlushBeforeReplace'
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $targetDirectory
        if ($null -ne $script:releaseDirectoryLease) {
            Assert-GeoraePlanReleaseDirectoryChainLease `
                -Lease $script:releaseDirectoryLease
        }
        if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
            [IO.File]::Replace(
                $temporaryPath,
                $TargetPath,
                $backupPath,
                $true)
        }
        elseif (Test-Path -LiteralPath $TargetPath) {
            throw "Release transaction target is not a regular file: $TargetPath"
        }
        else {
            [IO.File]::Move($temporaryPath, $TargetPath)
        }
        Sync-GeoraePlanReleaseCommittedFile -Path $TargetPath
        $committedItem =
            Get-Item -LiteralPath $TargetPath -Force -ErrorAction Stop
        $committedHash = (
            Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash
        if (
            (
                -not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
                -not [string]::Equals(
                    $committedHash,
                    $ExpectedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) -or
            (
                $ExpectedFileSize -ge 0 -and
                $committedItem.Length -ne $ExpectedFileSize
            )
        ) {
            throw 'Durable release transaction target verification failed.'
        }
    }
    catch {
        $operationError = $_.Exception
    }

    $cleanupErrors = @()
    foreach ($ownedPath in @($temporaryPath, $backupPath)) {
        try {
            Remove-StrictOwnedPackageTemporaryFile `
                -TemporaryPath $ownedPath `
                -DestinationDirectory $targetDirectory
        }
        catch {
            $cleanupErrors += $_.Exception
        }
    }
    if ($null -ne $operationError) {
        if ($cleanupErrors.Count -gt 0) {
            throw [AggregateException]::new(
                'Release transaction file commit and cleanup both failed.',
                [Exception[]](@($operationError) + $cleanupErrors))
        }
        throw $operationError
    }
    if ($cleanupErrors.Count -gt 0) {
        throw [AggregateException]::new(
            'Release transaction file cleanup failed.',
            [Exception[]]$cleanupErrors)
    }
}

function Copy-DurableGeoraePlanReleaseOwnedFile {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $source = [IO.File]::Open(
        $SourcePath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $target = $null
    try {
        $target = [IO.FileStream]::new(
            $TargetPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            81920,
            [IO.FileOptions]::WriteThrough)
        $source.CopyTo($target)
        $target.Flush($true)
    }
    finally {
        if ($null -ne $target) {
            $target.Dispose()
        }
        $source.Dispose()
    }
}

function New-GeoraePlanReleaseTransactionEntry {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$StagedPath,
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$BackupRoot,
        [Parameter(Mandatory = $true)][int]$Index,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel,
        [string]$ExpectedSha256,
        [long]$ExpectedFileSize = -1
    )

    Assert-GeoraePlanReleaseTransactionTargetBoundary `
        -TargetPath $TargetPath `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
    $sourceStagedItem =
        Get-Item -LiteralPath $StagedPath -Force -ErrorAction Stop
    if (
        $sourceStagedItem.PSIsContainer -or
        ($sourceStagedItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "Release transaction staged source is not regular: $StagedPath"
    }
    $boundStagedPath = Join-Path $StagingRoot (
        '{0:D3}-{1}.stage' -f
        $Index,
        (Split-Path -Leaf $TargetPath))
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($StagedPath),
        [IO.Path]::GetFullPath($boundStagedPath),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        Copy-DurableGeoraePlanReleaseOwnedFile `
            -SourcePath $StagedPath `
            -TargetPath $boundStagedPath
    }
    $stageLease = [IO.File]::Open(
        $boundStagedPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $stageLeaseRegistered = $false
    try {
        $stagedSha256 =
            Get-GeoraePlanReleaseLeaseSha256 -Lease $stageLease
        $stagedFileSize = [long]$stageLease.Length
        if (
            (
                -not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
                -not [string]::Equals(
                    $stagedSha256,
                    $ExpectedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) -or
            (
                $ExpectedFileSize -ge 0 -and
                $stagedFileSize -ne $ExpectedFileSize
            )
        ) {
            throw (
                'Release transaction staged file does not match its ' +
                'immutable snapshot identity.')
        }
        Register-GeoraePlanReleaseTransactionStageLease `
            -Path $boundStagedPath `
            -Lease $stageLease
        $stageLeaseRegistered = $true
    }
    finally {
        if (-not $stageLeaseRegistered) {
            $stageLease.Dispose()
        }
    }
    $targetExisted = Test-Path -LiteralPath $TargetPath
    $targetDirectory =
        [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($TargetPath))
    $commitTemporaryPath = Join-Path $targetDirectory (
        '.{0}.{1}.georaeplan-release-tmp' -f
        (Split-Path -Leaf $TargetPath),
        $Channel)
    $commitBackupPath = Join-Path $targetDirectory (
        '.{0}.{1}.georaeplan-release-bak' -f
        (Split-Path -Leaf $TargetPath),
        $Channel)
    if (
        (Test-Path -LiteralPath $commitTemporaryPath) -or
        (Test-Path -LiteralPath $commitBackupPath)
    ) {
        throw (
            'Release transaction commit sidecar already exists before journal ' +
            "ownership was established: $TargetPath")
    }
    $backupPath = $null
    $backupSha256 = $null
    $backupFileSize = $null
    if ($targetExisted) {
        $targetItem =
            Get-Item -LiteralPath $TargetPath -Force -ErrorAction Stop
        if (
            $targetItem.PSIsContainer -or
            ($targetItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Release transaction target is not regular: $TargetPath"
        }
        $targetHash = (
            Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash
        $targetParent = [IO.Path]::GetFullPath(
            [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($TargetPath)))
        $isVersionedPackageTarget = $false
        foreach ($packageParent in @(
            (Join-Path $OutputRoot 'downloads\android'),
            (Join-Path $OutputRoot 'downloads\desktop')
        )) {
            if ([string]::Equals(
                $targetParent,
                [IO.Path]::GetFullPath($packageParent),
                [StringComparison]::OrdinalIgnoreCase
            )) {
                $isVersionedPackageTarget = $true
                break
            }
        }
        if (
            $isVersionedPackageTarget -and
            (
                $targetItem.Length -ne $stagedFileSize -or
                -not [string]::Equals(
                    $targetHash,
                    $stagedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            )
        ) {
            throw (
                'Release package version already exists with different ' +
                "bytes: $TargetPath")
        }
        $backupPath = Join-Path $BackupRoot (
            '{0:D3}-{1}.bak' -f $Index, $targetItem.Name)
        Copy-DurableGeoraePlanReleaseOwnedFile `
            -SourcePath $TargetPath `
            -TargetPath $backupPath
        $backupSha256 = (
            Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
        $backupFileSize = (
            Get-Item -LiteralPath $backupPath -Force -ErrorAction Stop).Length
    }

    return [ordered]@{
        targetPath = [IO.Path]::GetFullPath($TargetPath)
        stagedPath = [IO.Path]::GetFullPath($boundStagedPath)
        backupPath = $backupPath
        targetExisted = $targetExisted
        stagedSha256 = $stagedSha256
        backupSha256 = $backupSha256
        stagedFileSize = [long]$stagedFileSize
        backupFileSize = if ($null -eq $backupFileSize) {
            $null
        }
        else {
            [long]$backupFileSize
        }
        commitTemporaryPath =
            [IO.Path]::GetFullPath($commitTemporaryPath)
    }
}

function Get-GeoraePlanReleaseCleanupMarkerState {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $resolvedTransactionRoot = [IO.Path]::GetFullPath($TransactionRoot)
    return [ordered]@{
        owner = 'georaeplan-release-cleanup-marker'
        channel = $Channel
        projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
        outputRoot = [IO.Path]::GetFullPath($OutputRoot)
        transactionRoot = $resolvedTransactionRoot
        cleanupRoot = $resolvedTransactionRoot + '.cleanup-pending'
    }
}

function Write-DurableGeoraePlanReleaseCleanupMarker {
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)]$MarkerState
    )

    $pendingPath = $MarkerPath + '.create-pending'
    if (Test-Path -LiteralPath $pendingPath) {
        Assert-GeoraePlanReleaseCleanupMarker `
            -MarkerPath $pendingPath `
            -ExpectedState $MarkerState
        if (Test-Path -LiteralPath $MarkerPath) {
            throw (
                'Release cleanup marker and its pending activation both ' +
                'exist; preserving both.')
        }
        [IO.File]::Move($pendingPath, $MarkerPath)
        Sync-GeoraePlanReleaseCommittedFile -Path $MarkerPath
        return
    }
    Write-DurableGeoraePlanReleaseStringMetadata `
        -TargetPath $MarkerPath `
        -Payload $MarkerState `
        -KillPoint 'AfterCleanupMarkerTempFlush'
}

function Assert-GeoraePlanReleaseCleanupMarker {
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)]$ExpectedState
    )

    $properties = @(
        'owner',
        'channel',
        'projectRoot',
        'outputRoot',
        'transactionRoot',
        'cleanupRoot')
    $actual =
        Read-StrictGeoraePlanReleaseStringMetadata `
            -MetadataPath $MarkerPath `
            -ExpectedProperties $properties
    if (
        -not [string]::Equals(
            [string]$actual.owner,
            [string]$ExpectedState.owner,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$actual.channel,
            [string]$ExpectedState.channel,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$actual.projectRoot),
            [IO.Path]::GetFullPath([string]$ExpectedState.projectRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$actual.outputRoot),
            [IO.Path]::GetFullPath([string]$ExpectedState.outputRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$actual.transactionRoot),
            [IO.Path]::GetFullPath([string]$ExpectedState.transactionRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$actual.cleanupRoot),
            [IO.Path]::GetFullPath([string]$ExpectedState.cleanupRoot),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw (
            'Release cleanup marker ownership binding is invalid; ' +
            'preserving cleanup evidence.')
    }
}

function Resume-GeoraePlanReleaseTransactionCleanup {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $markerState =
        Get-GeoraePlanReleaseCleanupMarkerState `
            -TransactionRoot $TransactionRoot `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
    $markerPath = [IO.Path]::GetFullPath($TransactionRoot) +
        '.cleanup-marker.json'
    $cleanupRoot = [string]$markerState.cleanupRoot
    $hasMarker = Test-Path -LiteralPath $markerPath
    $hasCleanupRoot = Test-Path -LiteralPath $cleanupRoot
    if ($hasCleanupRoot -and -not $hasMarker) {
        throw (
            'Release cleanup tombstone exists without its durable marker; ' +
            'preserving it for inspection.')
    }
    if (-not $hasMarker) {
        return
    }

    Assert-GeoraePlanReleaseCleanupMarker `
        -MarkerPath $markerPath `
        -ExpectedState $markerState
    $hasTransactionRoot = Test-Path -LiteralPath $TransactionRoot
    $hasCleanupRoot = Test-Path -LiteralPath $cleanupRoot
    if ($hasTransactionRoot -and $hasCleanupRoot) {
        throw (
            'Release cleanup has both transaction and tombstone roots; ' +
            'preserving both for inspection.')
    }
    if ($hasTransactionRoot) {
        Assert-GeoraePlanReleaseTransactionDirectory `
            -TransactionRoot $TransactionRoot `
            -OutputRoot $OutputRoot
        $journalPath = Join-Path $TransactionRoot 'journal.json'
        if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) {
            throw (
                'Cleanup-marked release transaction lacks its journal; ' +
                'preserving it for inspection.')
        }
        $journal =
            Read-StrictGeoraePlanReleaseJournal -JournalPath $journalPath
        if (
            $journal.SchemaVersion -notin @(3, 4) -or
            (
                $journal.SchemaVersion -eq 4 -and
                [string]$journal.RequestFingerprint -notmatch
                    '^[0-9A-F]{64}$'
            ) -or
            (
                $journal.SchemaVersion -eq 3 -and
                $null -ne $journal.RequestFingerprint
            ) -or
            $journal.Phase -notin @(
                'CleanupPending',
                'RollbackCleanupPending') -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($journal.TransactionRoot),
                [IO.Path]::GetFullPath($TransactionRoot),
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'Cleanup-marked release journal is not terminal; ' +
                'preserving it for inspection.')
        }
        [IO.Directory]::Move(
            [IO.Path]::GetFullPath($TransactionRoot),
            $cleanupRoot)
        $hasCleanupRoot = $true
    }
    if ($hasCleanupRoot) {
        Remove-GeoraePlanReleaseTransactionDirectory `
            -TransactionRoot $cleanupRoot `
            -OutputRoot $OutputRoot
    }
    Remove-StrictOwnedPackageTemporaryFile `
        -TemporaryPath $markerPath `
        -DestinationDirectory $OutputRoot
}

function Complete-GeoraePlanReleaseTransactionCleanup {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $markerPath = [IO.Path]::GetFullPath($TransactionRoot) +
        '.cleanup-marker.json'
    if (-not (Test-Path -LiteralPath $markerPath)) {
        $markerState =
            Get-GeoraePlanReleaseCleanupMarkerState `
                -TransactionRoot $TransactionRoot `
                -OutputRoot $OutputRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
        Write-DurableGeoraePlanReleaseCleanupMarker `
            -MarkerPath $markerPath `
            -MarkerState $markerState
    }
    Resume-GeoraePlanReleaseTransactionCleanup `
        -TransactionRoot $TransactionRoot `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
}

function Restore-GeoraePlanReleaseTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel,
        [switch]$InspectOnly,
        [switch]$PassThruOutcome
    )

    if (-not (Test-Path -LiteralPath $TransactionRoot)) {
        if ($PassThruOutcome) {
            return [pscustomobject]@{
                Outcome = 'None'
                RequestFingerprint = ''
                HasRequestFingerprint = $false
            }
        }
        return
    }
    Assert-GeoraePlanReleaseTransactionDirectory `
        -TransactionRoot $TransactionRoot `
        -OutputRoot $OutputRoot
    $journalPath = Join-Path $TransactionRoot 'journal.json'
    if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) {
        throw (
            'Release transaction root exists without its durable owner ' +
            'journal; preserving it for inspection.')
    }

    $journal =
        Read-StrictGeoraePlanReleaseJournal -JournalPath $journalPath
    if (
        $journal.SchemaVersion -notin @(3, 4) -or
        (
            $journal.SchemaVersion -eq 4 -and
            [string]$journal.RequestFingerprint -notmatch
                '^[0-9A-F]{64}$'
        ) -or
        (
            $journal.SchemaVersion -eq 3 -and
            $null -ne $journal.RequestFingerprint
        ) -or
        -not [string]::Equals(
            $journal.Owner,
            'georaeplan-release-transaction',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $journal.Channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($journal.OutputRoot),
            [IO.Path]::GetFullPath($OutputRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($journal.ProjectRoot),
            [IO.Path]::GetFullPath($ProjectRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($journal.TransactionRoot),
            [IO.Path]::GetFullPath($TransactionRoot),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Release transaction journal ownership binding is invalid.'
    }
    if ($journal.Phase -notin @(
        'Staging',
        'CommitPending',
        'PointerCommitPending',
        'Committed',
        'CleanupPending',
        'RollbackCleanupPending'
    )) {
        throw 'Release transaction journal phase is invalid.'
    }
    if ($journal.Phase -eq 'Staging' -and $journal.Entries.Count -ne 0) {
        throw 'Staging release journal must not contain commit entries.'
    }
    $recoveryOutcome = if ($journal.Phase -in @(
        'PointerCommitPending',
        'Committed',
        'CleanupPending'
    )) {
        'Committed'
    }
    else {
        'RolledBack'
    }

    $stagingRoot = [IO.Path]::GetFullPath(
        (Join-Path $TransactionRoot 'staging'))
    $backupRoot = [IO.Path]::GetFullPath(
        (Join-Path $TransactionRoot 'backup'))
    $seenTargets =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    $recoveryPlan = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -lt $journal.Entries.Count; $index++) {
        $entry = $journal.Entries[$index]
        if (
            [string]::IsNullOrWhiteSpace($entry.TargetPath) -or
            [string]::IsNullOrWhiteSpace($entry.StagedPath) -or
            [string]::IsNullOrWhiteSpace($entry.CommitTemporaryPath) -or
            $entry.StagedSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            $entry.StagedFileSize -lt 0
        ) {
            throw 'Release transaction entry contains invalid required values.'
        }
        $targetPath = [IO.Path]::GetFullPath($entry.TargetPath)
        if (
            -not $seenTargets.Add($targetPath) -or
            -not (Test-GeoraePlanReleaseTransactionTarget `
                -TargetPath $targetPath `
                -OutputRoot $OutputRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel)
        ) {
            throw 'Release transaction recovery target binding is invalid.'
        }
        Assert-GeoraePlanReleaseTransactionTargetBoundary `
            -TargetPath $targetPath `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        $expectedCommitTemporaryPath = Join-Path (
            [IO.Path]::GetDirectoryName($targetPath)
        ) (
            '.{0}.{1}.georaeplan-release-tmp' -f
            (Split-Path -Leaf $targetPath),
            $Channel)
        $commitTemporaryPath =
            [IO.Path]::GetFullPath($entry.CommitTemporaryPath)
        $commitBackupPath =
            $commitTemporaryPath.Substring(
                0,
                $commitTemporaryPath.Length -
                    '.georaeplan-release-tmp'.Length) +
            '.georaeplan-release-bak'
        if (-not [string]::Equals(
            $commitTemporaryPath,
            [IO.Path]::GetFullPath($expectedCommitTemporaryPath),
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw (
                'Release transaction temporary commit ownership binding ' +
                'is invalid.')
        }
        if (Test-Path -LiteralPath $commitTemporaryPath) {
            Assert-GeoraePlanReleaseRegularDirectoryChain `
                -Path $commitTemporaryPath `
                -LeafMayBeFile
            $commitTemporaryItem =
                Get-Item `
                    -LiteralPath $commitTemporaryPath `
                    -Force `
                    -ErrorAction Stop
            if (
                $commitTemporaryItem.PSIsContainer -or
                ($commitTemporaryItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'Release transaction temporary commit evidence is not ' +
                    'a regular journal-owned file.')
            }
        }
        if (Test-Path -LiteralPath $commitBackupPath) {
            Assert-GeoraePlanReleaseRegularDirectoryChain `
                -Path $commitBackupPath `
                -LeafMayBeFile
            $commitBackupItem =
                Get-Item `
                    -LiteralPath $commitBackupPath `
                    -Force `
                    -ErrorAction Stop
            if (
                $commitBackupItem.PSIsContainer -or
                ($commitBackupItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'Release transaction temporary backup evidence is not ' +
                    'a regular journal-owned file.')
            }
        }
        $expectedStagedPath = Join-Path $stagingRoot (
            '{0:D3}-{1}.stage' -f
            $index,
            (Split-Path -Leaf $targetPath))
        $stagedPath = [IO.Path]::GetFullPath($entry.StagedPath)
        if (
            -not [string]::Equals(
                $stagedPath,
                $expectedStagedPath,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $stagedPath -PathType Leaf)
        ) {
            throw 'Release transaction staged path binding is invalid.'
        }
        $stagedItem =
            Get-Item -LiteralPath $stagedPath -Force -ErrorAction Stop
        if (
            $stagedItem.PSIsContainer -or
            ($stagedItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $stagedItem.Length -ne [long]$entry.StagedFileSize -or
            -not [string]::Equals(
                (Get-FileHash `
                    -LiteralPath $stagedPath `
                    -Algorithm SHA256).Hash,
                $entry.StagedSha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Release transaction staged file evidence is invalid.'
        }

        $backupPath = $null
        $backupHash = $null
        if ($entry.TargetExisted) {
            if (
                [string]::IsNullOrWhiteSpace($entry.BackupPath) -or
                $entry.BackupSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
                $null -eq $entry.BackupFileSize -or
                $entry.BackupFileSize -lt 0
            ) {
                throw 'Release transaction backup evidence is incomplete.'
            }
            $expectedBackupPath = Join-Path $backupRoot (
                '{0:D3}-{1}.bak' -f
                $index,
                (Split-Path -Leaf $targetPath))
            $backupPath = [IO.Path]::GetFullPath($entry.BackupPath)
            if (
                -not [string]::Equals(
                    $backupPath,
                    $expectedBackupPath,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $backupPath -PathType Leaf)
            ) {
                throw 'Release transaction backup path binding is invalid.'
            }
            $backupItem =
                Get-Item -LiteralPath $backupPath -Force -ErrorAction Stop
            $backupHash = (
                Get-FileHash `
                    -LiteralPath $backupPath `
                    -Algorithm SHA256).Hash
            if (
                $backupItem.PSIsContainer -or
                ($backupItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $backupItem.Length -ne [long]$entry.BackupFileSize -or
                -not [string]::Equals(
                    $backupHash,
                    $entry.BackupSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw 'Release transaction backup file evidence is invalid.'
            }
        }
        elseif (
            $null -ne $entry.BackupPath -or
            $null -ne $entry.BackupSha256 -or
            $null -ne $entry.BackupFileSize
        ) {
            throw 'New release transaction target must not declare a backup.'
        }

        $isCommittedPhase =
            $journal.Phase -in @(
                'PointerCommitPending',
                'Committed',
                'CleanupPending')
        $action = 'None'
        if (Test-Path -LiteralPath $targetPath) {
            $targetItem =
                Get-Item -LiteralPath $targetPath -Force -ErrorAction Stop
            if (
                $targetItem.PSIsContainer -or
                ($targetItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw 'Release transaction target evidence is not regular.'
            }
            $targetHash = (
                Get-FileHash `
                    -LiteralPath $targetPath `
                    -Algorithm SHA256).Hash
            $targetMatchesStaged =
                $targetItem.Length -eq [long]$entry.StagedFileSize -and
                [string]::Equals(
                    $targetHash,
                    $entry.StagedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            $targetMatchesBackup =
                $entry.TargetExisted -and
                $targetItem.Length -eq [long]$entry.BackupFileSize -and
                [string]::Equals(
                    $targetHash,
                    $backupHash,
                    [StringComparison]::OrdinalIgnoreCase)
            if ($isCommittedPhase) {
                if (-not $targetMatchesStaged) {
                    if ($entry.TargetExisted -and $targetMatchesBackup) {
                        $action = 'Replay'
                    }
                    else {
                        throw (
                            'Committed release target contains unowned ' +
                            "replacement bytes: $targetPath")
                    }
                }
            }
            elseif ($entry.TargetExisted) {
                if (-not $targetMatchesBackup) {
                    if (-not $targetMatchesStaged) {
                        throw (
                            'Release transaction target contains unowned ' +
                            "replacement bytes: $targetPath")
                    }
                    $action = 'Restore'
                }
            }
            else {
                if (-not $targetMatchesStaged) {
                    throw (
                        'Release transaction orphan contains unowned ' +
                        "replacement bytes: $targetPath")
                }
                $action = 'Delete'
            }
        }
        elseif ($isCommittedPhase) {
            $action = 'Replay'
        }
        elseif ($entry.TargetExisted) {
            $action = 'Restore'
        }
        $recoveryPlan.Add([pscustomobject]@{
            Action = $action
            TargetPath = $targetPath
            StagedPath = $stagedPath
            StagedSha256 = [string]$entry.StagedSha256
            StagedFileSize = [long]$entry.StagedFileSize
            BackupPath = $backupPath
            BackupSha256 = $backupHash
            BackupFileSize = if ($null -eq $entry.BackupFileSize) {
                -1L
            }
            else {
                [long]$entry.BackupFileSize
            }
            CommitTemporaryPath = $commitTemporaryPath
            CommitBackupPath = $commitBackupPath
        }) | Out-Null
    }

    if ($InspectOnly) {
        if ($PassThruOutcome) {
            return [pscustomobject]@{
                Outcome = 'Inspected'
                RequestFingerprint =
                    if ($journal.SchemaVersion -eq 4) {
                        [string]$journal.RequestFingerprint
                    }
                    else {
                        ''
                    }
                HasRequestFingerprint =
                    $journal.SchemaVersion -eq 4
            }
        }
        return
    }
    foreach ($plan in $recoveryPlan) {
        foreach ($sidecarPath in @(
            $plan.CommitTemporaryPath,
            $plan.CommitBackupPath
        )) {
            Remove-StrictOwnedPackageTemporaryFile `
                -TemporaryPath $sidecarPath `
                -DestinationDirectory (
                    [IO.Path]::GetDirectoryName($plan.TargetPath))
        }
    }
    if ($journal.Phase -notin @(
        'PointerCommitPending',
        'Committed',
        'CleanupPending'
    )) {
        $plans = $recoveryPlan.ToArray()
        [array]::Reverse($plans)
        foreach ($plan in $plans) {
            if ($plan.Action -eq 'Restore') {
                Copy-GeoraePlanReleaseTransactionFileAtomically `
                    -SourcePath $plan.BackupPath `
                    -TargetPath $plan.TargetPath `
                    -TemporaryPath $plan.CommitTemporaryPath `
                    -ExpectedSha256 $plan.BackupSha256 `
                    -ExpectedFileSize $plan.BackupFileSize
            }
            elseif ($plan.Action -eq 'Delete') {
                Remove-Item `
                    -LiteralPath $plan.TargetPath `
                    -Force `
                    -ErrorAction Stop
            }
        }
    }
    else {
        foreach ($plan in $recoveryPlan) {
            if ($plan.Action -eq 'Replay') {
                Copy-GeoraePlanReleaseTransactionFileAtomically `
                    -SourcePath $plan.StagedPath `
                    -TargetPath $plan.TargetPath `
                    -TemporaryPath $plan.CommitTemporaryPath `
                    -ExpectedSha256 $plan.StagedSha256 `
                    -ExpectedFileSize $plan.StagedFileSize
            }
        }
        foreach ($plan in $recoveryPlan) {
            Assert-GeoraePlanReleaseTransactionTargetBoundary `
                -TargetPath $plan.TargetPath `
                -OutputRoot $OutputRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
            $targetItem =
                Get-Item `
                    -LiteralPath $plan.TargetPath `
                    -Force `
                    -ErrorAction Stop
            $targetHash = (
                Get-FileHash `
                    -LiteralPath $plan.TargetPath `
                    -Algorithm SHA256).Hash
            if (
                $targetItem.Length -ne $plan.StagedFileSize -or
                -not [string]::Equals(
                    $targetHash,
                    $plan.StagedSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw (
                    'Committed release target replay verification failed; ' +
                    'preserving transaction evidence.')
            }
        }
    }

    if ($journal.Phase -in @(
        'PointerCommitPending',
        'Committed',
        'CleanupPending'
    )) {
        $journal.Phase = 'CleanupPending'
    }
    else {
        $journal.Phase = 'RollbackCleanupPending'
    }
    Write-DurableGeoraePlanReleaseJournal `
        -JournalPath $journalPath `
        -Journal $journal
    Close-GeoraePlanReleaseTransactionStageLeases
    Complete-GeoraePlanReleaseTransactionCleanup `
        -TransactionRoot $TransactionRoot `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
    if ($PassThruOutcome) {
        return [pscustomobject]@{
            Outcome = $recoveryOutcome
            RequestFingerprint =
                if ($journal.SchemaVersion -eq 4) {
                    [string]$journal.RequestFingerprint
                }
                else {
                    ''
                }
            HasRequestFingerprint =
                $journal.SchemaVersion -eq 4
        }
    }
}

function Get-ManifestPlatformVersion {
    param(
        $Manifest,
        [Parameter(Mandatory = $true)][string]$Platform
    )

    if ($null -eq $Manifest) {
        return ''
    }

    $platformNode = $Manifest.$Platform
    if ($null -eq $platformNode) {
        return ''
    }

    return ([string]$platformNode.version).Trim()
}

function Compare-GeoraePlanReleaseVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    if (
        $Left -notmatch '^\d+(?:\.\d+){1,3}$' -or
        $Right -notmatch '^\d+(?:\.\d+){1,3}$'
    ) {
        throw "Release version is not a safe numeric dotted version: $Left / $Right"
    }
    $leftParts = @($Left.Split('.') | ForEach-Object { [uint64]$_ })
    $rightParts = @($Right.Split('.') | ForEach-Object { [uint64]$_ })
    for ($index = 0; $index -lt 4; $index++) {
        $leftPart = if ($index -lt $leftParts.Count) {
            $leftParts[$index]
        }
        else {
            0L
        }
        $rightPart = if ($index -lt $rightParts.Count) {
            $rightParts[$index]
        }
        else {
            0L
        }
        if ($leftPart -lt $rightPart) {
            return -1
        }
        if ($leftPart -gt $rightPart) {
            return 1
        }
    }
    return 0
}

function Get-GeoraePlanManifestAssetBindings {
    param($PlatformNode)

    $bindings = New-Object System.Collections.Generic.List[object]
    if ($null -eq $PlatformNode) {
        return $bindings.ToArray()
    }
    $bindings.Add([pscustomobject]@{
        Kind = 'package'
        FileName = ([string]$PlatformNode.fileName).Trim()
        Sha256 = ([string]$PlatformNode.sha256).Trim()
        FileSize = [string]$PlatformNode.fileSize
    }) | Out-Null
    $installers = $null
    if ($PlatformNode -is [Collections.IDictionary]) {
        if ($PlatformNode.Contains('installers')) {
            $installers = $PlatformNode['installers']
        }
    }
    else {
        $installersProperty =
            $PlatformNode.PSObject.Properties['installers']
        if ($null -ne $installersProperty) {
            $installers = $installersProperty.Value
        }
    }
    if ($null -ne $installers) {
        foreach ($installer in @($installers)) {
            if ($null -eq $installer) {
                throw 'Release manifest has a null installer binding.'
            }
            $bindings.Add([pscustomobject]@{
                Kind = (
                    'installer:' +
                    ([string]$installer.audience).Trim() +
                    ':' +
                    ([string]$installer.format).Trim())
                FileName = ([string]$installer.fileName).Trim()
                Sha256 = ([string]$installer.sha256).Trim()
                FileSize = [string]$installer.fileSize
            }) | Out-Null
        }
    }
    foreach ($binding in $bindings) {
        [long]$parsedFileSize = -1
        if (
            [string]::IsNullOrWhiteSpace($binding.FileName) -or
            $binding.Sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not [long]::TryParse(
                $binding.FileSize,
                [ref]$parsedFileSize) -or
            $parsedFileSize -lt 0 -or
            -not [string]::Equals(
                [IO.Path]::GetFileName($binding.FileName),
                $binding.FileName,
                [StringComparison]::Ordinal)
        ) {
            throw 'Release manifest asset binding is invalid.'
        }
        $binding.FileSize = $parsedFileSize
    }
    return $bindings.ToArray()
}

function Assert-GeoraePlanManifestAssetsPresent {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$DownloadsRoot
    )

    foreach ($platform in @('desktop', 'android')) {
        $platformRoot =
            [IO.Path]::GetFullPath((Join-Path $DownloadsRoot $platform))
        foreach ($binding in @(
            Get-GeoraePlanManifestAssetBindings `
                -PlatformNode $Manifest.$platform
        )) {
            $assetPath =
                [IO.Path]::GetFullPath(
                    (Join-Path $platformRoot $binding.FileName))
            if (-not [string]::Equals(
                [IO.Path]::GetDirectoryName($assetPath),
                $platformRoot,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw (
                    'Release manifest asset escaped its platform download ' +
                    "root: $($binding.FileName)")
            }
            Assert-GeoraePlanReleaseRegularDirectoryChain `
                -Path $assetPath `
                -LeafMayBeFile
            if ($null -ne $script:releaseDirectoryLease) {
                Assert-GeoraePlanReleaseDirectoryChainLease `
                    -Lease $script:releaseDirectoryLease
            }
            $assetItem =
                Get-Item `
                    -LiteralPath $assetPath `
                    -Force `
                    -ErrorAction Stop
            if (
                $assetItem.PSIsContainer -or
                ($assetItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'Release manifest asset is not an exact regular file: ' +
                    $assetPath)
            }
            $assetHash = (
                Get-FileHash `
                    -LiteralPath $assetPath `
                    -Algorithm SHA256).Hash
            if (
                $assetItem.Length -ne [long]$binding.FileSize -or
                -not [string]::Equals(
                    $assetHash,
                    [string]$binding.Sha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw (
                    'Release manifest asset hash/size does not match the ' +
                    "active manifest: $assetPath")
            }
        }
    }
}

function Assert-GeoraePlanReleaseVersionPolicy {
    param(
        [Parameter(Mandatory = $true)]$CandidateManifest,
        [Parameter(Mandatory = $true)]$ExistingManifest,
        [Parameter(Mandatory = $true)][string]$ExistingSource,
        [switch]$AllowDowngrade,
        [switch]$EnforceDowngrade
    )

    foreach ($platform in @('desktop', 'android')) {
        $candidateNode = $CandidateManifest.$platform
        $existingNode = $ExistingManifest.$platform
        if ($null -eq $candidateNode -or $null -eq $existingNode) {
            continue
        }
        $candidateVersion = ([string]$candidateNode.version).Trim()
        $existingVersion = ([string]$existingNode.version).Trim()
        $comparison =
            Compare-GeoraePlanReleaseVersion `
                -Left $candidateVersion `
                -Right $existingVersion
        if (
            $EnforceDowngrade -and
            $comparison -lt 0 -and
            -not $AllowDowngrade
        ) {
            throw (
                "Release $platform downgrade requires explicit " +
                "-AllowDowngrade. candidate=$candidateVersion " +
                "existing=$existingVersion source=$ExistingSource")
        }
        if ($comparison -ne 0) {
            continue
        }

        $candidateBindings =
            @(Get-GeoraePlanManifestAssetBindings `
                -PlatformNode $candidateNode)
        $existingBindings =
            @(Get-GeoraePlanManifestAssetBindings `
                -PlatformNode $existingNode)
        if ($candidateBindings.Count -ne $existingBindings.Count) {
            throw (
                "Release $platform version $candidateVersion already exists " +
                'with a different asset set.')
        }
        foreach ($candidateBinding in $candidateBindings) {
            $matches = @(
                $existingBindings |
                    Where-Object {
                        [string]::Equals(
                            $_.Kind,
                            $candidateBinding.Kind,
                            [StringComparison]::Ordinal) -and
                        [string]::Equals(
                            $_.FileName,
                            $candidateBinding.FileName,
                            [StringComparison]::Ordinal)
                    })
            if (
                $matches.Count -ne 1 -or
                $matches[0].FileSize -ne $candidateBinding.FileSize -or
                -not [string]::Equals(
                    $matches[0].Sha256,
                    $candidateBinding.Sha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw (
                    "Release $platform version $candidateVersion already " +
                    'exists with different immutable bytes.')
            }
        }
    }
}

function Read-ValidatedExistingReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        $AndroidPackageSnapshot,
        [string]$AndroidFileName
    )

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        return $null
    }
    Assert-GeoraePlanReleaseRegularDirectoryChain `
        -Path $ManifestPath `
        -LeafMayBeFile
    if ($null -ne $script:releaseDirectoryLease) {
        Assert-GeoraePlanReleaseDirectoryChainLease `
            -Lease $script:releaseDirectoryLease
    }
    $manifestItem =
        Get-Item -LiteralPath $ManifestPath -Force -ErrorAction Stop
    if (
        $manifestItem.PSIsContainer -or
        ($manifestItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($manifestItem.FullName),
            [IO.Path]::GetFullPath($ManifestPath),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw (
            'Existing manifest is not an exact regular file: ' +
            $ManifestPath)
    }
    try {
        $existingManifestJson =
            Get-Content `
                -LiteralPath $ManifestPath `
                -Raw `
                -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($existingManifestJson)) {
            throw 'Existing manifest is empty.'
        }
        $candidateExistingManifest =
            $existingManifestJson | ConvertFrom-Json
        if ($null -eq $candidateExistingManifest) {
            throw 'Existing manifest root is null.'
        }
    }
    catch {
        throw (
            "Existing manifest cannot be read safely: $ManifestPath. " +
            $_.Exception.Message)
    }
    if ($null -ne $script:releaseDirectoryLease) {
        Assert-GeoraePlanReleaseDirectoryChainLease `
            -Lease $script:releaseDirectoryLease
    }

    if ($null -ne $AndroidPackageSnapshot) {
        $existingAndroid = $candidateExistingManifest.android
        $existingAndroidVersion = if ($null -eq $existingAndroid) {
            ''
        }
        else {
            ([string]$existingAndroid.version).Trim()
        }
        if ([string]::Equals(
            $existingAndroidVersion,
            [string]$AndroidPackageSnapshot.VersionName,
            [StringComparison]::Ordinal
        )) {
            $existingAndroidSha256 = ([string]$existingAndroid.sha256).Trim()
            if (
                $existingAndroidSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
                -not [string]::Equals(
                    $existingAndroidSha256,
                    [string]$AndroidPackageSnapshot.Sha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw (
                    'Existing manifest Android hash conflicts with the ' +
                    'authenticated publish candidate.')
            }

            if (
                $null -ne
                    $existingAndroid.PSObject.Properties['fileName'] -and
                -not [string]::Equals(
                    ([string]$existingAndroid.fileName).Trim(),
                    $AndroidFileName,
                    [StringComparison]::Ordinal)
            ) {
                throw (
                    'Existing manifest Android fileName conflicts with the ' +
                    'authenticated publish candidate.')
            }

            if (
                $null -ne
                    $existingAndroid.PSObject.Properties['fileSize']
            ) {
                [long]$existingAndroidFileSize = -1
                if (
                    -not [long]::TryParse(
                        [string]$existingAndroid.fileSize,
                        [ref]$existingAndroidFileSize) -or
                    $existingAndroidFileSize -ne
                        [long]$AndroidPackageSnapshot.FileSize
                ) {
                    throw (
                        'Existing manifest Android fileSize conflicts with ' +
                        'the authenticated publish candidate.')
                }
            }
        }
    }
    return $candidateExistingManifest
}

function Read-GeoraePlanReleaseManifestPointer {
    param(
        [Parameter(Mandatory = $true)][string]$PointerPath,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$DeliveryGenerationRoot,
        [Parameter(Mandatory = $true)][string]$Channel,
        $AndroidPackageSnapshot,
        [string]$AndroidFileName
    )

    $properties = @(
        'owner',
        'schemaVersion',
        'channel',
        'generationId',
        'manifestRelativePath',
        'manifestSha256',
        'manifestFileSize',
        'deliveryManifestPath',
        'deliveryManifestSha256',
        'deliveryManifestFileSize')
    $pointer =
        Read-StrictGeoraePlanReleaseStringMetadata `
            -MetadataPath $PointerPath `
            -ExpectedProperties $properties
    [long]$manifestFileSize = -1
    [long]$deliveryManifestFileSize = -1
    if (
        -not [string]::Equals(
            [string]$pointer.owner,
            'georaeplan-release-manifest-pointer',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$pointer.schemaVersion,
            '1',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$pointer.channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        [string]$pointer.generationId -notmatch '^[0-9a-f]{32}$' -or
        [string]$pointer.manifestSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        [string]$pointer.deliveryManifestSha256 -notmatch
            '^[0-9A-Fa-f]{64}$' -or
        -not [long]::TryParse(
            [string]$pointer.manifestFileSize,
            [ref]$manifestFileSize) -or
        -not [long]::TryParse(
            [string]$pointer.deliveryManifestFileSize,
            [ref]$deliveryManifestFileSize) -or
        $manifestFileSize -lt 0 -or
        $deliveryManifestFileSize -lt 0 -or
        $manifestFileSize -ne $deliveryManifestFileSize -or
        -not [string]::Equals(
            [string]$pointer.manifestSha256,
            [string]$pointer.deliveryManifestSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Release manifest pointer values are invalid.'
    }

    $generationId = [string]$pointer.generationId
    $expectedRelativePath =
        'generations/{0}/{1}.json' -f $Channel, $generationId
    if (-not [string]::Equals(
        [string]$pointer.manifestRelativePath,
        $expectedRelativePath,
        [StringComparison]::Ordinal
    )) {
        throw 'Release manifest pointer relative path is not canonical.'
    }
    $manifestPath = [IO.Path]::GetFullPath(
        (Join-Path $ManifestRoot (
            $expectedRelativePath.Replace(
                '/',
                [IO.Path]::DirectorySeparatorChar))))
    $expectedManifestPath = [IO.Path]::GetFullPath(
        (Join-Path (
            Join-Path $ManifestRoot ('generations\' + $Channel)
        ) ($generationId + '.json')))
    $deliveryManifestPath = [IO.Path]::GetFullPath(
        [string]$pointer.deliveryManifestPath)
    $expectedDeliveryManifestPath = [IO.Path]::GetFullPath(
        (Join-Path $DeliveryGenerationRoot ($generationId + '.json')))
    if (
        -not [string]::Equals(
            $manifestPath,
            $expectedManifestPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $deliveryManifestPath,
            $expectedDeliveryManifestPath,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Release manifest pointer generation path binding is invalid.'
    }

    foreach ($binding in @(
        [pscustomobject]@{
            Path = $manifestPath
            Sha256 = [string]$pointer.manifestSha256
            FileSize = $manifestFileSize
        },
        [pscustomobject]@{
            Path = $deliveryManifestPath
            Sha256 = [string]$pointer.deliveryManifestSha256
            FileSize = $deliveryManifestFileSize
        }
    )) {
        if (-not (Test-Path -LiteralPath $binding.Path -PathType Leaf)) {
            throw (
                'Release manifest pointer generation file is missing: ' +
                $binding.Path)
        }
        Assert-GeoraePlanReleaseRegularDirectoryChain `
            -Path $binding.Path `
            -LeafMayBeFile
        $item =
            Get-Item -LiteralPath $binding.Path -Force -ErrorAction Stop
        $hash = (
            Get-FileHash `
                -LiteralPath $binding.Path `
                -Algorithm SHA256).Hash
        if (
            $item.PSIsContainer -or
            ($item.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $item.Length -ne [long]$binding.FileSize -or
            -not [string]::Equals(
                $hash,
                [string]$binding.Sha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'Release manifest pointer generation hash/size binding ' +
                'is invalid.')
        }
    }

    $manifest =
        Read-ValidatedExistingReleaseManifest `
            -ManifestPath $manifestPath `
            -AndroidPackageSnapshot $AndroidPackageSnapshot `
            -AndroidFileName $AndroidFileName
    if (
        $null -eq $manifest -or
        $null -eq $manifest.PSObject.Properties['generationId'] -or
        $null -eq $manifest.PSObject.Properties['channel'] -or
        -not [string]::Equals(
            [string]$manifest.generationId,
            $generationId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$manifest.channel,
            $Channel,
            [StringComparison]::Ordinal)
    ) {
        throw 'Release manifest generation binding does not match its pointer.'
    }

    return [pscustomobject]@{
        Pointer = $pointer
        Manifest = $manifest
        ManifestPath = $manifestPath
        DeliveryManifestPath = $deliveryManifestPath
    }
}

function Read-GeoraePlanReleaseRequestReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$ReceiptPath,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    if (-not (Test-Path -LiteralPath $ReceiptPath)) {
        return $null
    }
    $receipt =
        Read-StrictGeoraePlanReleaseStringMetadata `
            -MetadataPath $ReceiptPath `
            -ExpectedProperties @(
                'owner',
                'schemaVersion',
                'channel',
                'requestFingerprint',
                'generationId',
                'pointerSha256',
                'pointerFileSize',
                'manifestSha256',
                'manifestFileSize')
    [long]$pointerFileSize = -1
    [long]$manifestFileSize = -1
    if (
        -not [string]::Equals(
            [string]$receipt.owner,
            'georaeplan-release-request-receipt',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$receipt.schemaVersion,
            '1',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$receipt.channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        [string]$receipt.requestFingerprint -notmatch
            '^[0-9A-F]{64}$' -or
        [string]$receipt.generationId -notmatch '^[0-9a-f]{32}$' -or
        [string]$receipt.pointerSha256 -notmatch '^[0-9A-F]{64}$' -or
        [string]$receipt.manifestSha256 -notmatch '^[0-9A-F]{64}$' -or
        -not [long]::TryParse(
            [string]$receipt.pointerFileSize,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$pointerFileSize) -or
        -not [long]::TryParse(
            [string]$receipt.manifestFileSize,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$manifestFileSize) -or
        $pointerFileSize -lt 0 -or
        $manifestFileSize -lt 0
    ) {
        throw 'Release request receipt binding is invalid.'
    }
    return [pscustomobject]@{
        Value = $receipt
        PointerFileSize = $pointerFileSize
        ManifestFileSize = $manifestFileSize
    }
}

function Assert-GeoraePlanReleaseCommittedOutcome {
    param(
        [Parameter(Mandatory = $true)][object[]]$TransactionEntries,
        [Parameter(Mandatory = $true)]$MainManifestTransactionEntry,
        [Parameter(Mandatory = $true)]$DeliveryManifestTransactionEntry,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$DeliveryManifestPath,
        [Parameter(Mandatory = $true)][string]$ManifestPointerPath,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$DeliveryGenerationRoot,
        [Parameter(Mandatory = $true)][string]$ManifestGenerationPath,
        [Parameter(Mandatory = $true)][string]$GenerationId,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    foreach ($entry in $TransactionEntries) {
        Assert-GeoraePlanReleaseTransactionTargetBoundary `
            -TargetPath $entry.targetPath `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        $committedItem =
            Get-Item `
                -LiteralPath $entry.targetPath `
                -Force `
                -ErrorAction Stop
        $committedHash = (
            Get-FileHash `
                -LiteralPath $entry.targetPath `
                -Algorithm SHA256).Hash
        if (
            $committedItem.Length -ne [long]$entry.stagedFileSize -or
            -not [string]::Equals(
                $committedHash,
                [string]$entry.stagedSha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'Committed release transaction target does not match its ' +
                'journal hash and size.')
        }
    }

    $mainManifestHash = (
        Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash
    $deliveryManifestHash = (
        Get-FileHash `
            -LiteralPath $DeliveryManifestPath `
            -Algorithm SHA256).Hash
    if (
        -not [string]::Equals(
            $mainManifestHash,
            [string]$MainManifestTransactionEntry.stagedSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $deliveryManifestHash,
            [string]$DeliveryManifestTransactionEntry.stagedSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $mainManifestHash,
            $deliveryManifestHash,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Committed release manifest pair verification failed.'
    }

    $committedPointer =
        Read-GeoraePlanReleaseManifestPointer `
            -PointerPath $ManifestPointerPath `
            -ManifestRoot $ManifestRoot `
            -DeliveryGenerationRoot $DeliveryGenerationRoot `
            -Channel $Channel
    if (
        -not [string]::Equals(
            [string]$committedPointer.Pointer.generationId,
            $GenerationId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($committedPointer.ManifestPath),
            [IO.Path]::GetFullPath($ManifestGenerationPath),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Committed release manifest pointer verification failed.'
    }
}

function Get-GeoraePlanReleaseRecoveredOutcome {
    param(
        [Parameter(Mandatory = $true)][object[]]$TransactionEntries,
        [Parameter(Mandatory = $true)]$MainManifestTransactionEntry,
        [Parameter(Mandatory = $true)]$DeliveryManifestTransactionEntry,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$DeliveryManifestPath,
        [Parameter(Mandatory = $true)][string]$ManifestPointerPath,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$DeliveryGenerationRoot,
        [Parameter(Mandatory = $true)][string]$ManifestGenerationPath,
        [Parameter(Mandatory = $true)][string]$GenerationId,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    try {
        Assert-GeoraePlanReleaseCommittedOutcome `
            -TransactionEntries $TransactionEntries `
            -MainManifestTransactionEntry $MainManifestTransactionEntry `
            -DeliveryManifestTransactionEntry `
                $DeliveryManifestTransactionEntry `
            -ManifestPath $ManifestPath `
            -DeliveryManifestPath $DeliveryManifestPath `
            -ManifestPointerPath $ManifestPointerPath `
            -ManifestRoot $ManifestRoot `
            -DeliveryGenerationRoot $DeliveryGenerationRoot `
            -ManifestGenerationPath $ManifestGenerationPath `
            -GenerationId $GenerationId `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        return 'Committed'
    }
    catch {
        # The rollback evidence below is authoritative when commit evidence
        # is not exact.
    }

    foreach ($entry in $TransactionEntries) {
        Assert-GeoraePlanReleaseTransactionTargetBoundary `
            -TargetPath $entry.targetPath `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        $targetExistedValue = $entry.targetExisted
        if ($targetExistedValue -isnot [bool]) {
            return 'Unknown'
        }
        if (-not $targetExistedValue) {
            if (Test-Path -LiteralPath $entry.targetPath) {
                return 'Unknown'
            }
            continue
        }
        if (
            [string]$entry.backupSha256 -notmatch
                '^[0-9A-Fa-f]{64}$' -or
            [long]$entry.backupFileSize -lt 0 -or
            -not (Test-Path -LiteralPath $entry.targetPath -PathType Leaf)
        ) {
            return 'Unknown'
        }
        $targetItem =
            Get-Item `
                -LiteralPath $entry.targetPath `
                -Force `
                -ErrorAction Stop
        if (
            $targetItem.PSIsContainer -or
            ($targetItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $targetItem.Length -ne [long]$entry.backupFileSize
        ) {
            return 'Unknown'
        }
        $targetHash = (
            Get-FileHash `
                -LiteralPath $entry.targetPath `
                -Algorithm SHA256).Hash
        if (-not [string]::Equals(
            $targetHash,
            [string]$entry.backupSha256,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            return 'Unknown'
        }
    }
    return 'RolledBack'
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

if ($Channel -cnotin @('stable', 'test', 'beta')) {
    throw '-Channel must be canonical lowercase: stable, test, or beta.'
}

if (
    $SkipAndroid -and
    $PSBoundParameters.ContainsKey('AndroidPackagePath')
) {
    throw '-SkipAndroid cannot be combined with -AndroidPackagePath.'
}

if ($SkipAndroid -and $PreserveExistingAndroid) {
    throw '-SkipAndroid cannot be combined with -PreserveExistingAndroid.'
}
if ($PreserveExistingAndroid) {
    foreach ($androidParameterName in @(
        'AndroidPackagePath',
        'AndroidVersion',
        'AndroidMinimumSupportedVersion',
        'AndroidNotes',
        'MandatoryAndroid',
        'ApkAnalyzerPath',
        'JavaSdkDirectory'
    )) {
        if ($PSBoundParameters.ContainsKey($androidParameterName)) {
            throw (
                '-PreserveExistingAndroid cannot be combined with -' +
                $androidParameterName + '.')
        }
    }
}
$publishNewAndroid = -not $SkipAndroid -and -not $PreserveExistingAndroid

if ($publishNewAndroid) {
    $androidMetadataHelper =
        Join-Path $ProjectRoot 'tools\mobile\AndroidApkMetadata.ps1'
    if (-not (Test-Path -LiteralPath $androidMetadataHelper -PathType Leaf)) {
        throw "Android APK metadata helper not found: $androidMetadataHelper"
    }
    . $androidMetadataHelper
}

$tempInitializer = Join-Path $ProjectRoot 'tools\common\Initialize-GeoraePlanTemp.ps1'
if (Test-Path -LiteralPath $tempInitializer) {
    . $tempInitializer -ProjectRoot $ProjectRoot
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot '배포\업데이트'
}

if ([string]::IsNullOrWhiteSpace($DesktopPackagePath)) {
    $desktopCandidates = @(
        (Join-Path $ProjectRoot '배포\관리자용\거래플랜-PC-설치패키지.zip'),
        (Join-Path $ProjectRoot '배포\설치패키지\관리자용\거래플랜-PC-설치패키지.zip'),
        (Join-Path $ProjectRoot '배포\설치패키지\거래플랜-PC-설치패키지.zip')
    )
    $DesktopPackagePath = $desktopCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ($publishNewAndroid -and [string]::IsNullOrWhiteSpace($AndroidPackagePath)) {
    $androidCandidates = @(
        (Get-ChildItem -Path (Join-Path $ProjectRoot '배포') -File -Filter '거래플랜-안드로이드-v*-signed.apk' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1),
        (Get-ChildItem -Path (Join-Path $ProjectRoot 'Mobile\artifacts\android') -File -Filter '거래플랜-안드로이드-*.apk' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1)
    ) | Where-Object { $null -ne $_ }

    $androidNamed = $androidCandidates | Select-Object -First 1
    if ($null -ne $androidNamed) {
        $AndroidPackagePath = $androidNamed.FullName
    }
}
elseif (
    $publishNewAndroid -and
    -not [IO.Path]::IsPathRooted($AndroidPackagePath)
) {
    $AndroidPackagePath = Join-Path $ProjectRoot $AndroidPackagePath
}

$desktopProject = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
$androidProject =
    Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'

if ([string]::IsNullOrWhiteSpace($DesktopVersion)) {
    $DesktopVersion = Get-CsprojPropertyValue -ProjectFile $desktopProject -PropertyName 'Version'
}
$expectedAndroidApplicationId = ''
$expectedAndroidVersion = ''
$expectedAndroidVersionCode = ''
if ($publishNewAndroid) {
    $expectedAndroidApplicationId =
        Get-CsprojPropertyValue `
            -ProjectFile $androidProject `
            -PropertyName 'ApplicationId'
    $expectedAndroidVersion =
        Get-CsprojPropertyValue `
            -ProjectFile $androidProject `
            -PropertyName 'ApplicationDisplayVersion'
    $expectedAndroidVersionCode =
        Get-CsprojPropertyValue `
            -ProjectFile $androidProject `
            -PropertyName 'ApplicationVersion'
    if (
        [string]::IsNullOrWhiteSpace($expectedAndroidApplicationId) -or
        [string]::IsNullOrWhiteSpace($expectedAndroidVersion) -or
        [string]::IsNullOrWhiteSpace($expectedAndroidVersionCode)
    ) {
        throw 'Android csproj identity properties are incomplete.'
    }
    if ($expectedAndroidVersion -notmatch '^\d+(?:\.\d+)+$') {
        throw (
            'Android csproj ApplicationDisplayVersion is not a safe dotted ' +
            'version.')
    }
    if ([string]::IsNullOrWhiteSpace($AndroidVersion)) {
        $AndroidVersion = $expectedAndroidVersion
    }
    elseif (-not [string]::Equals(
        $AndroidVersion.Trim(),
        $expectedAndroidVersion,
        [StringComparison]::Ordinal
    )) {
        throw (
            'Requested AndroidVersion does not match the current Android csproj. ' +
            "requested=$AndroidVersion expected=$expectedAndroidVersion")
    }
}
if ([string]::IsNullOrWhiteSpace($DesktopNotes)) {
    $DesktopNotes = '내부 업데이트 패키지/동기화 안정화 개선 반영'
}
if ([string]::IsNullOrWhiteSpace($AndroidNotes)) {
    $AndroidNotes = '모바일 상세탭/첨부 보기/내부 업데이트 확인 기능 반영'
}

$androidPackageSnapshot = $null
$desktopPackageSnapshot = $null
$desktopExeInstallerSnapshot = $null
$desktopMsiInstallerSnapshot = $null
$androidDestinationLease = $null
$releasePublishLease = $null
$deliveryPublishLease = $null
$script:releaseDirectoryLease = $null
$script:releaseTransactionStageLeases = $null
$transactionPreparedByThisRun = $false
if ($publishNewAndroid) {
    if ([string]::IsNullOrWhiteSpace($AndroidPackagePath)) {
        throw (
            'Android package was not found. Pass -AndroidPackagePath or use ' +
            '-SkipAndroid for an explicit desktop-only manifest.')
    }
    if (-not (Test-Path -LiteralPath $AndroidPackagePath -PathType Leaf)) {
        throw "Android package not found: $AndroidPackagePath"
    }
    $AndroidPackagePath = (Resolve-Path -LiteralPath $AndroidPackagePath).Path
    try {
        $androidPackageSnapshot = New-GeoraePlanAndroidApkSnapshot `
            -ApkPath $AndroidPackagePath `
            -ProjectRoot $ProjectRoot `
            -ApkAnalyzerPath $ApkAnalyzerPath `
            -JavaSdkDirectory $JavaSdkDirectory `
            -SourceName 'publish candidate'
        Assert-GeoraePlanAndroidApkMetadata `
            -Metadata $androidPackageSnapshot `
            -ExpectedApplicationId $expectedAndroidApplicationId `
            -ExpectedVersionName $expectedAndroidVersion `
            -ExpectedVersionCode $expectedAndroidVersionCode `
            -SourceName 'publish candidate'
    }
    catch {
        Remove-GeoraePlanAndroidApkSnapshot -Snapshot $androidPackageSnapshot
        $androidPackageSnapshot = $null
        throw
    }
}

$hasDesktopPackage =
    -not [string]::IsNullOrWhiteSpace($DesktopPackagePath) -and
    (Test-Path -LiteralPath $DesktopPackagePath -PathType Leaf)
$resolvedDesktopExeInstallerPath = $null
$resolvedDesktopMsiInstallerPath = $null
if ($hasDesktopPackage) {
    try {
        $resolvedDesktopExeInstallerPath =
            Resolve-DesktopNativeInstallerPath `
                -ExplicitPath $DesktopExeInstallerPath `
                -Root $ProjectRoot `
                -Version $DesktopVersion `
                -Format 'exe'
        $resolvedDesktopMsiInstallerPath =
            Resolve-DesktopNativeInstallerPath `
                -ExplicitPath $DesktopMsiInstallerPath `
                -Root $ProjectRoot `
                -Version $DesktopVersion `
                -Format 'msi'
        $desktopPackageSnapshot =
            New-GeoraePlanReleaseFileSnapshot `
                -SourcePath $DesktopPackagePath `
                -SourceName 'desktop ZIP publish candidate'
        Test-DesktopUpdatePackage `
            -PackagePath $desktopPackageSnapshot.SnapshotPath `
            -ExpectedVersion $DesktopVersion `
            -SourceSnapshot $desktopPackageSnapshot
        $desktopExeInstallerSnapshot =
            New-GeoraePlanReleaseFileSnapshot `
                -SourcePath $resolvedDesktopExeInstallerPath `
                -SourceName 'desktop EXE publish candidate'
        $desktopMsiInstallerSnapshot =
            New-GeoraePlanReleaseFileSnapshot `
                -SourcePath $resolvedDesktopMsiInstallerPath `
                -SourceName 'desktop MSI publish candidate'
        Assert-DesktopNativeInstallerProductVersion `
            -SourceSnapshot $desktopExeInstallerSnapshot `
            -ExpectedVersion $DesktopVersion `
            -Format 'exe'
        Assert-DesktopNativeInstallerProductVersion `
            -SourceSnapshot $desktopMsiInstallerSnapshot `
            -ExpectedVersion $DesktopVersion `
            -Format 'msi'
    }
    catch {
        Remove-GeoraePlanReleaseFileSnapshot `
            -Snapshot $desktopPackageSnapshot
        Remove-GeoraePlanReleaseFileSnapshot `
            -Snapshot $desktopExeInstallerSnapshot
        Remove-GeoraePlanReleaseFileSnapshot `
            -Snapshot $desktopMsiInstallerSnapshot
        $desktopPackageSnapshot = $null
        $desktopExeInstallerSnapshot = $null
        $desktopMsiInstallerSnapshot = $null
        if ($null -ne $androidPackageSnapshot) {
            Remove-GeoraePlanAndroidApkSnapshot `
                -Snapshot $androidPackageSnapshot
            $androidPackageSnapshot = $null
        }
        throw
    }
}

try {
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$manifestRoot = Join-Path $OutputRoot 'manifest'
$downloadsRoot = Join-Path $OutputRoot 'downloads'
$manifestPath = Join-Path $manifestRoot ($Channel + '.json')
$manifestPointerPath =
    Join-Path $manifestRoot ($Channel + '.current.json')
$requestReceiptPath =
    Join-Path $manifestRoot ($Channel + '.request-receipt.json')
$manifestGenerationsRoot =
    Join-Path $manifestRoot 'generations'
$manifestGenerationRoot =
    Join-Path $manifestGenerationsRoot $Channel
$deliveryManifestPath = Join-Path $ProjectRoot ("배포\" + $Channel + '.json')
$deliveryManifestGenerationsRoot =
    Join-Path $ProjectRoot '배포\.georaeplan-release-generations'
$deliveryManifestGenerationRoot =
    Join-Path $deliveryManifestGenerationsRoot $Channel
$transactionRoot =
    Get-GeoraePlanReleaseTransactionRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
$androidFileName = if ($null -eq $androidPackageSnapshot) {
    ''
}
else {
    "tradeplan-android-v$($androidPackageSnapshot.VersionName).apk"
}
$requestFingerprint =
    Get-GeoraePlanReleaseRequestFingerprint `
        -Channel $Channel `
        -HasDesktopPackage $hasDesktopPackage `
        -HasAndroidPackage ($null -ne $androidPackageSnapshot) `
        -DesktopPackageSnapshot $desktopPackageSnapshot `
        -DesktopExeInstallerSnapshot $desktopExeInstallerSnapshot `
        -DesktopMsiInstallerSnapshot $desktopMsiInstallerSnapshot `
        -AndroidPackageSnapshot $androidPackageSnapshot `
        -DesktopVersion ([string]$DesktopVersion) `
        -AndroidVersion ([string]$AndroidVersion) `
        -DesktopNotes ([string]$DesktopNotes) `
        -AndroidNotes ([string]$AndroidNotes) `
        -MandatoryDesktop ([bool]$MandatoryDesktop) `
        -MandatoryAndroid ([bool]$MandatoryAndroid) `
        -DesktopMinimumSupportedVersion (
            [string]$DesktopMinimumSupportedVersion) `
        -AndroidMinimumSupportedVersion (
            [string]$AndroidMinimumSupportedVersion) `
        -SkipAndroid ([bool]$SkipAndroid) `
        -PreserveExistingAndroid ([bool]$PreserveExistingAndroid) `
        -SkipPackagePrune ([bool]$SkipPackagePrune) `
        -KeepDesktopPackageCount $KeepDesktopPackageCount `
        -KeepAndroidPackageCount $KeepAndroidPackageCount `
        -AllowDowngrade ([bool]$AllowDowngrade)
if ($requestFingerprint -notmatch '^[0-9A-F]{64}$') {
    throw 'Release request fingerprint calculation failed.'
}
$deliveryPublishLease =
    Open-GeoraePlanReleaseDeliveryPublishLock `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
$releasePublishLease =
    Open-GeoraePlanReleasePublishLock -OutputRoot $OutputRoot
[void](New-GeoraePlanReleaseOwnedDirectory -DirectoryPath $manifestRoot)
[void](New-GeoraePlanReleaseOwnedDirectory -DirectoryPath $downloadsRoot)
[void](New-GeoraePlanReleaseOwnedDirectory `
    -DirectoryPath $manifestGenerationsRoot)
[void](New-GeoraePlanReleaseOwnedDirectory `
    -DirectoryPath $manifestGenerationRoot)
[void](New-GeoraePlanReleaseOwnedDirectory `
    -DirectoryPath $deliveryManifestGenerationsRoot)
[void](New-GeoraePlanReleaseOwnedDirectory `
    -DirectoryPath $deliveryManifestGenerationRoot)
[void](New-GeoraePlanReleaseOwnedDirectory `
    -DirectoryPath (Join-Path $downloadsRoot 'android'))
[void](New-GeoraePlanReleaseOwnedDirectory `
    -DirectoryPath (Join-Path $downloadsRoot 'desktop'))
$script:releaseDirectoryLease =
    Open-GeoraePlanReleaseDirectoryChainLease -DirectoryPaths @(
        $OutputRoot,
        $manifestRoot,
        $manifestGenerationRoot,
        $downloadsRoot,
        (Join-Path $downloadsRoot 'android'),
        (Join-Path $downloadsRoot 'desktop'),
        (Split-Path -Parent $deliveryManifestPath),
        $deliveryManifestGenerationRoot
    )
Resume-GeoraePlanReleasePreparations `
    -TransactionRoot $transactionRoot `
    -OutputRoot $OutputRoot `
    -ProjectRoot $ProjectRoot `
    -Channel $Channel
$startupRecovery =
    Restore-GeoraePlanReleaseTransaction `
        -TransactionRoot $transactionRoot `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel `
        -PassThruOutcome
$startupRecoveryOutcome = [string]$startupRecovery.Outcome
Resume-GeoraePlanReleaseTransactionCleanup `
    -TransactionRoot $transactionRoot `
    -OutputRoot $OutputRoot `
    -ProjectRoot $ProjectRoot `
    -Channel $Channel
if (
    $startupRecoveryOutcome -eq 'Committed' -and
    -not (Test-Path -LiteralPath $manifestPointerPath)
) {
    if ([bool]$startupRecovery.HasRequestFingerprint) {
        throw (
            'Recovered committed release has a request fingerprint but no ' +
            'active manifest pointer.')
    }
    Write-Host (
        'release_startup_recovery=Committed ' +
        'legacy_without_pointer=true')
    $startupRecoveryOutcome = 'LegacyCommittedWithoutPointer'
}
if ($startupRecoveryOutcome -eq 'Committed') {
    $recoveredPointer =
        Read-GeoraePlanReleaseManifestPointer `
            -PointerPath $manifestPointerPath `
            -ManifestRoot $manifestRoot `
            -DeliveryGenerationRoot $deliveryManifestGenerationRoot `
            -Channel $Channel `
            -AndroidPackageSnapshot $androidPackageSnapshot `
            -AndroidFileName $androidFileName
    $recoveredPointerSha256 = (
        Get-FileHash `
            -LiteralPath $manifestPointerPath `
            -Algorithm SHA256).Hash
    Write-Host (
        'release_startup_recovery=Committed ' +
        "generation=$($recoveredPointer.Pointer.generationId) " +
        "pointer_sha256=$recoveredPointerSha256")
    if (
        [bool]$startupRecovery.HasRequestFingerprint -and
        [string]::Equals(
            [string]$startupRecovery.RequestFingerprint,
            $requestFingerprint,
            [StringComparison]::Ordinal)
    ) {
        Assert-GeoraePlanManifestAssetsPresent `
            -Manifest $recoveredPointer.Manifest `
            -DownloadsRoot $downloadsRoot
        $startupRemovedDesktopPackages = @()
        $startupRemovedAndroidPackages = @()
        if (-not $SkipPackagePrune) {
            $startupPreservedDesktopFiles =
                Get-ManifestReferencedFileNames `
                    -ManifestRoot $manifestRoot `
                    -Platform 'desktop'
            $startupPreservedAndroidFiles =
                Get-ManifestReferencedFileNames `
                    -ManifestRoot $manifestRoot `
                    -Platform 'android'
            $startupRemovedDesktopPackages =
                Remove-OldPackages `
                    -DirectoryPath (Join-Path $downloadsRoot 'desktop') `
                    -KeepCount $KeepDesktopPackageCount `
                    -PreserveFileNames $startupPreservedDesktopFiles
            $startupRemovedAndroidPackages =
                Remove-OldPackages `
                    -DirectoryPath (Join-Path $downloadsRoot 'android') `
                    -KeepCount $KeepAndroidPackageCount `
                    -PreserveFileNames $startupPreservedAndroidFiles
        }
        Write-Host (
            'release_request_fingerprint=matched ' +
            "sha256=$requestFingerprint")
        if ($startupRemovedDesktopPackages.Count -gt 0) {
            Write-Host (
                'desktop_packages_pruned=' +
                $startupRemovedDesktopPackages.Count)
        }
        if ($startupRemovedAndroidPackages.Count -gt 0) {
            Write-Host (
                'android_packages_pruned=' +
                $startupRemovedAndroidPackages.Count)
        }
        return
    }
    $recoveredFingerprint = if (
        [bool]$startupRecovery.HasRequestFingerprint
    ) {
        [string]$startupRecovery.RequestFingerprint
    }
    else {
        'legacy-missing'
    }
    throw (
        'Recovered committed release request does not exactly match the ' +
        'current request. The recovered release remains committed; rerun ' +
        'the current request as a fresh invocation. ' +
        "recovered_fingerprint=$recoveredFingerprint " +
        "current_fingerprint=$requestFingerprint")
}
if ($startupRecoveryOutcome -eq 'RolledBack') {
    Write-Host 'release_startup_recovery=RolledBack'
}
$existingManifest = $null
$currentManifestSourcePath = $manifestPath
$existingManifestRecords =
    New-Object System.Collections.Generic.List[object]
$existingManifestPaths =
    [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
$currentPointer = $null
if (Test-Path -LiteralPath $manifestPointerPath) {
    $currentPointer =
        Read-GeoraePlanReleaseManifestPointer `
            -PointerPath $manifestPointerPath `
            -ManifestRoot $manifestRoot `
            -DeliveryGenerationRoot $deliveryManifestGenerationRoot `
            -Channel $Channel `
            -AndroidPackageSnapshot $androidPackageSnapshot `
            -AndroidFileName $androidFileName
    $existingManifest = $currentPointer.Manifest
    $currentManifestSourcePath = $currentPointer.ManifestPath
}
$requestReceiptEvidence =
    Read-GeoraePlanReleaseRequestReceipt `
        -ReceiptPath $requestReceiptPath `
        -Channel $Channel
if ($null -ne $requestReceiptEvidence) {
    if ($null -eq $currentPointer) {
        throw (
            'Release request receipt exists without an active manifest ' +
            'pointer.')
    }
    $requestReceipt = $requestReceiptEvidence.Value
    if ([string]::Equals(
        [string]$requestReceipt.generationId,
        [string]$currentPointer.Pointer.generationId,
        [StringComparison]::Ordinal
    )) {
        $activePointerItem =
            Get-Item `
                -LiteralPath $manifestPointerPath `
                -Force `
                -ErrorAction Stop
        $activePointerSha256 = (
            Get-FileHash `
                -LiteralPath $manifestPointerPath `
                -Algorithm SHA256).Hash
        if (
            $activePointerItem.Length -ne
                [long]$requestReceiptEvidence.PointerFileSize -or
            -not [string]::Equals(
                $activePointerSha256,
                [string]$requestReceipt.pointerSha256,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string]$currentPointer.Pointer.manifestSha256,
                [string]$requestReceipt.manifestSha256,
                [StringComparison]::Ordinal) -or
            [long]$currentPointer.Pointer.manifestFileSize -ne
                [long]$requestReceiptEvidence.ManifestFileSize
        ) {
            throw (
                'Release request receipt does not match its active pointer ' +
                'and manifest evidence.')
        }
        if ([string]::Equals(
            [string]$requestReceipt.requestFingerprint,
            $requestFingerprint,
            [StringComparison]::Ordinal
        )) {
            Assert-GeoraePlanManifestAssetsPresent `
                -Manifest $currentPointer.Manifest `
                -DownloadsRoot $downloadsRoot
            $receiptManifestPaths =
                [Collections.Generic.HashSet[string]]::new(
                    [StringComparer]::OrdinalIgnoreCase)
            foreach ($receiptManifestFile in @(
                Get-ChildItem `
                    -LiteralPath $manifestRoot `
                    -File `
                    -Filter '*.json' `
                    -Force `
                    -ErrorAction Stop |
                    Where-Object {
                        -not $_.Name.EndsWith(
                            '.current.json',
                            [StringComparison]::OrdinalIgnoreCase) -and
                        -not $_.Name.EndsWith(
                            '.request-receipt.json',
                            [StringComparison]::OrdinalIgnoreCase)
                    }
                Get-ChildItem `
                    -LiteralPath (
                        Join-Path $manifestRoot 'generations') `
                    -File `
                    -Filter '*.json' `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
            )) {
                [void]$receiptManifestPaths.Add(
                    [IO.Path]::GetFullPath(
                        $receiptManifestFile.FullName))
            }
            [void]$receiptManifestPaths.Add(
                [IO.Path]::GetFullPath($deliveryManifestPath))
            foreach ($receiptManifestPath in $receiptManifestPaths) {
                $receiptExistingManifest =
                    Read-ValidatedExistingReleaseManifest `
                        -ManifestPath $receiptManifestPath `
                        -AndroidPackageSnapshot $androidPackageSnapshot `
                        -AndroidFileName $androidFileName
                if ($null -eq $receiptExistingManifest) {
                    continue
                }
                Assert-GeoraePlanReleaseVersionPolicy `
                    -CandidateManifest $currentPointer.Manifest `
                    -ExistingManifest $receiptExistingManifest `
                    -ExistingSource $receiptManifestPath `
                    -AllowDowngrade:$AllowDowngrade
            }
            $receiptRemovedDesktopPackages = @()
            $receiptRemovedAndroidPackages = @()
            if (-not $SkipPackagePrune) {
                $receiptPreservedDesktopFiles =
                    Get-ManifestReferencedFileNames `
                        -ManifestRoot $manifestRoot `
                        -Platform 'desktop'
                $receiptPreservedAndroidFiles =
                    Get-ManifestReferencedFileNames `
                        -ManifestRoot $manifestRoot `
                        -Platform 'android'
                $receiptRemovedDesktopPackages =
                    Remove-OldPackages `
                        -DirectoryPath (
                            Join-Path $downloadsRoot 'desktop') `
                        -KeepCount $KeepDesktopPackageCount `
                        -PreserveFileNames $receiptPreservedDesktopFiles
                $receiptRemovedAndroidPackages =
                    Remove-OldPackages `
                        -DirectoryPath (
                            Join-Path $downloadsRoot 'android') `
                        -KeepCount $KeepAndroidPackageCount `
                        -PreserveFileNames $receiptPreservedAndroidFiles
            }
            Write-Host (
                'release_request_receipt=matched ' +
                "generation=$($requestReceipt.generationId) " +
                "sha256=$requestFingerprint")
            if ($receiptRemovedDesktopPackages.Count -gt 0) {
                Write-Host (
                    'desktop_packages_pruned=' +
                    $receiptRemovedDesktopPackages.Count)
            }
            if ($receiptRemovedAndroidPackages.Count -gt 0) {
                Write-Host (
                    'android_packages_pruned=' +
                    $receiptRemovedAndroidPackages.Count)
            }
            return
        }
    }
    else {
        Write-Host (
            'release_request_receipt=stale ' +
            "receipt_generation=$($requestReceipt.generationId) " +
            "active_generation=$($currentPointer.Pointer.generationId)")
    }
}
foreach ($manifestFile in @(
    Get-ChildItem `
        -LiteralPath $manifestRoot `
        -File `
        -Filter '*.json' `
        -Force `
        -ErrorAction Stop |
        Where-Object {
            -not $_.Name.EndsWith(
                '.current.json',
                [StringComparison]::OrdinalIgnoreCase) -and
            -not $_.Name.EndsWith(
                '.request-receipt.json',
                [StringComparison]::OrdinalIgnoreCase)
        }
    Get-ChildItem `
        -LiteralPath (Join-Path $manifestRoot 'generations') `
        -File `
        -Filter '*.json' `
        -Recurse `
        -Force `
        -ErrorAction Stop
)) {
    [void]$existingManifestPaths.Add(
        [IO.Path]::GetFullPath($manifestFile.FullName))
}
[void]$existingManifestPaths.Add(
    [IO.Path]::GetFullPath($deliveryManifestPath))
foreach ($existingManifestPath in $existingManifestPaths) {
    $candidateExistingManifest =
        Read-ValidatedExistingReleaseManifest `
            -ManifestPath $existingManifestPath `
            -AndroidPackageSnapshot $androidPackageSnapshot `
                -AndroidFileName $androidFileName
    if ($null -ne $candidateExistingManifest) {
        $existingManifestRecords.Add([pscustomobject]@{
            Path = $existingManifestPath
            Manifest = $candidateExistingManifest
        }) | Out-Null
    }
    if (
        -not (Test-Path -LiteralPath $manifestPointerPath) -and
        [string]::Equals(
            [IO.Path]::GetFullPath($existingManifestPath),
            [IO.Path]::GetFullPath($manifestPath),
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        $existingManifest = $candidateExistingManifest
    }
}
if (-not $SkipPackagePrune) {
    $null =
        Get-ManifestReferencedFileNames `
            -ManifestRoot $manifestRoot `
            -Platform 'desktop'
    $null =
        Get-ManifestReferencedFileNames `
            -ManifestRoot $manifestRoot `
            -Platform 'android'
}

if ($PreserveExistingAndroid) {
    if ($null -eq $existingManifest -or $null -eq $existingManifest.android) {
        throw (
            '-PreserveExistingAndroid requires an authenticated existing ' +
            "Android manifest and package in OutputRoot: $OutputRoot")
    }
    Assert-GeoraePlanManifestAssetsPresent `
        -Manifest ([pscustomobject]@{
            desktop = $null
            android = $existingManifest.android
        }) `
        -DownloadsRoot $downloadsRoot
    Write-Host (
        'android_update_preserved=existing-manifest ' +
        "version=$($existingManifest.android.version) " +
        "file=$($existingManifest.android.fileName)")
}

if ($null -ne $androidPackageSnapshot) {
    $androidDestinationDirectory = Join-Path $downloadsRoot 'android'
    $canonicalAndroidDestination =
        Join-Path $androidDestinationDirectory $androidFileName
    foreach ($existingDirectoryPath in @(
        $OutputRoot,
        $downloadsRoot,
        $androidDestinationDirectory
    )) {
        if (-not (Test-Path -LiteralPath $existingDirectoryPath)) {
            continue
        }
        $existingDirectoryItem =
            Get-Item -LiteralPath $existingDirectoryPath -Force -ErrorAction Stop
        if (
            -not $existingDirectoryItem.PSIsContainer -or
            ($existingDirectoryItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw (
                'Existing canonical Android package parent path is not a ' +
                "regular owned directory: $existingDirectoryPath")
        }
    }

    if ([IO.Directory]::Exists($androidDestinationDirectory)) {
        $canonicalEntries = @(
            [IO.Directory]::EnumerateFileSystemEntries(
                $androidDestinationDirectory,
                '*',
                [IO.SearchOption]::TopDirectoryOnly) |
                Where-Object {
                    [string]::Equals(
                        [IO.Path]::GetFullPath([string]$_),
                        [IO.Path]::GetFullPath($canonicalAndroidDestination),
                        [StringComparison]::OrdinalIgnoreCase)
                })
        if ($canonicalEntries.Count -gt 1) {
            throw 'Existing canonical Android package path is ambiguous.'
        }
        if ($canonicalEntries.Count -eq 1) {
            $canonicalAndroidItem =
                Get-Item `
                    -LiteralPath $canonicalAndroidDestination `
                    -Force `
                    -ErrorAction Stop
            if (
                $canonicalAndroidItem.PSIsContainer -or
                ($canonicalAndroidItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath($canonicalAndroidItem.FullName),
                    [IO.Path]::GetFullPath($canonicalAndroidDestination),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath(
                        $canonicalAndroidItem.DirectoryName),
                    [IO.Path]::GetFullPath(
                        $androidDestinationDirectory),
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw (
                    'Existing canonical Android package is not a regular ' +
                    'direct child of its owned destination.')
            }

            try {
                $androidDestinationLease = [IO.File]::Open(
                    $canonicalAndroidDestination,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read)
                Assert-AndroidCanonicalDestinationLease `
                    -DestinationDirectory $androidDestinationDirectory `
                    -DestinationPath $canonicalAndroidDestination `
                    -Lease $androidDestinationLease `
                    -ExpectedSha256 $androidPackageSnapshot.Sha256 `
                    -ExpectedFileSize $androidPackageSnapshot.FileSize
            }
            catch {
                if ($null -ne $androidDestinationLease) {
                    $androidDestinationLease.Dispose()
                    $androidDestinationLease = $null
                }
                throw (
                    'Existing canonical Android package conflicts with the ' +
                    'authenticated publish candidate. ' +
                    $_.Exception.Message)
            }
        }
    }
}

$preparationId = [Guid]::NewGuid().ToString('N')
$preparationState =
    Get-GeoraePlanReleasePreparationState `
        -TransactionRoot $transactionRoot `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel `
        -PreparationId $preparationId
$preparationRoot = [string]$preparationState.preparationRoot
$preparationOwnerPath = $preparationRoot + '.owner.json'
Write-DurableGeoraePlanReleaseStringMetadata `
    -TargetPath $preparationOwnerPath `
    -Payload $preparationState `
    -KillPoint 'AfterPreparationOwnerTempFlush'
[void](New-GeoraePlanReleaseOwnedDirectory `
    -DirectoryPath $preparationRoot)
Invoke-GeoraePlanReleaseTestKillPoint `
    -Name 'AfterPreparationRootBeforeJournal'
$preparationJournalPath = Join-Path $preparationRoot 'journal.json'
$journal = [ordered]@{
    schemaVersion = 4
    owner = 'georaeplan-release-transaction'
    channel = $Channel
    phase = 'Staging'
    projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
    outputRoot = [IO.Path]::GetFullPath($OutputRoot)
    transactionRoot = [IO.Path]::GetFullPath($transactionRoot)
    requestFingerprint = $requestFingerprint
    entries = @()
}
Write-DurableGeoraePlanReleaseJournal `
    -JournalPath $preparationJournalPath `
    -Journal $journal `
    -CreateNew
[IO.Directory]::Move(
    [IO.Path]::GetFullPath($preparationRoot),
    [IO.Path]::GetFullPath($transactionRoot))
$transactionPreparedByThisRun = $true
$stagingRoot = Join-Path $transactionRoot 'staging'
$backupRoot = Join-Path $transactionRoot 'backup'
$journalPath = Join-Path $transactionRoot 'journal.json'
Remove-StrictOwnedPackageTemporaryFile `
    -TemporaryPath $preparationOwnerPath `
    -DestinationDirectory $OutputRoot
[void](New-GeoraePlanReleaseOwnedDirectory -DirectoryPath $stagingRoot)
[void](New-GeoraePlanReleaseOwnedDirectory -DirectoryPath $backupRoot)

$generationId = [Guid]::NewGuid().ToString('N')
$manifest = [ordered]@{
    channel = $Channel
    generationId = $generationId
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    desktop = $null
    android = if ($PreserveExistingAndroid) {
        $existingManifest.android
    }
    else {
        $null
    }
}
$androidStagedPath = $null
$desktopStagedPath = $null
$desktopExeStagedPath = $null
$desktopMsiStagedPath = $null

if ($null -ne $androidPackageSnapshot) {
    try {
        $androidStagingDirectory = Join-Path $stagingRoot 'android'
        $manifest.android = Copy-PackageWithMetadata `
            -SourcePath $androidPackageSnapshot.SnapshotPath `
            -DestinationDirectory $androidStagingDirectory `
            -OutputFileName $androidFileName `
            -Platform 'android' `
            -Version $androidPackageSnapshot.VersionName `
            -Notes $AndroidNotes `
            -Mandatory:$MandatoryAndroid `
            -MinimumSupportedVersion $AndroidMinimumSupportedVersion `
            -ExpectedSha256 $androidPackageSnapshot.Sha256 `
            -ExpectedFileSize $androidPackageSnapshot.FileSize `
            -SourceSnapshot $androidPackageSnapshot
        $androidStagedPath =
            Join-Path $androidStagingDirectory $androidFileName
    }
    finally {
        Remove-GeoraePlanAndroidApkSnapshot -Snapshot $androidPackageSnapshot
        $androidPackageSnapshot = $null
    }
}

if ($hasDesktopPackage) {
    $desktopFileName = "tradeplan-pc-installer-v$DesktopVersion.zip"
    $desktopStagingDirectory = Join-Path $stagingRoot 'desktop'
    $manifest.desktop = Copy-PackageWithMetadata `
        -SourcePath $desktopPackageSnapshot.SnapshotPath `
        -DestinationDirectory $desktopStagingDirectory `
        -OutputFileName $desktopFileName `
        -Platform 'desktop' `
        -Version $DesktopVersion `
        -Notes $DesktopNotes `
        -Mandatory:$MandatoryDesktop `
        -MinimumSupportedVersion $DesktopMinimumSupportedVersion `
        -ExpectedSha256 $desktopPackageSnapshot.Sha256 `
        -ExpectedFileSize $desktopPackageSnapshot.FileSize `
        -SourceSnapshot $desktopPackageSnapshot
    $desktopStagedPath =
        Join-Path $desktopStagingDirectory $desktopFileName
    $desktopExeFileName = "tradeplan-pc-setup-v$DesktopVersion.exe"
    $desktopMsiFileName = "tradeplan-pc-admin-v$DesktopVersion.msi"
    $manifest.desktop['installers'] = @(
        (Copy-DesktopNativeInstallerWithMetadata `
            -SourcePath $desktopExeInstallerSnapshot.SnapshotPath `
            -DestinationDirectory $desktopStagingDirectory `
            -OutputFileName $desktopExeFileName `
            -Version $DesktopVersion `
            -Audience 'user' `
            -Format 'exe' `
            -SourceSnapshot $desktopExeInstallerSnapshot),
        (Copy-DesktopNativeInstallerWithMetadata `
            -SourcePath $desktopMsiInstallerSnapshot.SnapshotPath `
            -DestinationDirectory $desktopStagingDirectory `
            -OutputFileName $desktopMsiFileName `
            -Version $DesktopVersion `
            -Audience 'administrator' `
            -Format 'msi' `
            -SourceSnapshot $desktopMsiInstallerSnapshot)
    )
    $desktopExeStagedPath =
        Join-Path $desktopStagingDirectory $desktopExeFileName
    $desktopMsiStagedPath =
        Join-Path $desktopStagingDirectory $desktopMsiFileName
}

foreach ($existingManifestRecord in $existingManifestRecords) {
    $enforceDowngradeForManifest =
        [string]::Equals(
            [IO.Path]::GetFullPath($existingManifestRecord.Path),
            [IO.Path]::GetFullPath($currentManifestSourcePath),
            [StringComparison]::OrdinalIgnoreCase)
    Assert-GeoraePlanReleaseVersionPolicy `
        -CandidateManifest $manifest `
        -ExistingManifest $existingManifestRecord.Manifest `
        -ExistingSource $existingManifestRecord.Path `
        -AllowDowngrade:$AllowDowngrade `
        -EnforceDowngrade:$enforceDowngradeForManifest
}

$stagedManifestPath = Join-Path $stagingRoot 'manifest.json'
$stagedDeliveryManifestPath =
    Join-Path $stagingRoot 'delivery-manifest.json'
$stagedManifestGenerationPath =
    Join-Path $stagingRoot 'runtime-generation-manifest.json'
$stagedDeliveryManifestGenerationPath =
    Join-Path $stagingRoot 'delivery-generation-manifest.json'
Write-JsonFileAtomically `
    -TargetPath $stagedManifestPath `
    -InputObject $manifest
Copy-DurableGeoraePlanReleaseOwnedFile `
    -SourcePath $stagedManifestPath `
    -TargetPath $stagedDeliveryManifestPath
Copy-DurableGeoraePlanReleaseOwnedFile `
    -SourcePath $stagedManifestPath `
    -TargetPath $stagedManifestGenerationPath
Copy-DurableGeoraePlanReleaseOwnedFile `
    -SourcePath $stagedManifestPath `
    -TargetPath $stagedDeliveryManifestGenerationPath
$stagedManifestItem =
    Get-Item -LiteralPath $stagedManifestPath -Force -ErrorAction Stop
$stagedManifestSha256 = (
    Get-FileHash `
        -LiteralPath $stagedManifestPath `
        -Algorithm SHA256).Hash
$manifestGenerationPath =
    Join-Path $manifestGenerationRoot ($generationId + '.json')
$deliveryManifestGenerationPath =
    Join-Path $deliveryManifestGenerationRoot ($generationId + '.json')
$manifestPointer = [ordered]@{
    owner = 'georaeplan-release-manifest-pointer'
    schemaVersion = '1'
    channel = $Channel
    generationId = $generationId
    manifestRelativePath =
        'generations/{0}/{1}.json' -f $Channel, $generationId
    manifestSha256 = $stagedManifestSha256
    manifestFileSize = [string]$stagedManifestItem.Length
    deliveryManifestPath =
        [IO.Path]::GetFullPath($deliveryManifestGenerationPath)
    deliveryManifestSha256 = $stagedManifestSha256
    deliveryManifestFileSize = [string]$stagedManifestItem.Length
}
$stagedManifestPointerPath =
    Join-Path $stagingRoot 'manifest-pointer.json'
Write-JsonFileAtomically `
    -TargetPath $stagedManifestPointerPath `
    -InputObject $manifestPointer
$stagedManifestPointerItem =
    Get-Item `
        -LiteralPath $stagedManifestPointerPath `
        -Force `
        -ErrorAction Stop
$stagedManifestPointerSha256 = (
    Get-FileHash `
        -LiteralPath $stagedManifestPointerPath `
        -Algorithm SHA256).Hash
$requestReceipt = [ordered]@{
    owner = 'georaeplan-release-request-receipt'
    schemaVersion = '1'
    channel = $Channel
    requestFingerprint = $requestFingerprint
    generationId = $generationId
    pointerSha256 = $stagedManifestPointerSha256
    pointerFileSize = [string]$stagedManifestPointerItem.Length
    manifestSha256 = $stagedManifestSha256
    manifestFileSize = [string]$stagedManifestItem.Length
}
$stagedRequestReceiptPath =
    Join-Path $stagingRoot 'request-receipt.json'
Write-JsonFileAtomically `
    -TargetPath $stagedRequestReceiptPath `
    -InputObject $requestReceipt

$previousManifestPath = Join-Path $manifestRoot ($Channel + '.previous.json')
$stagedPreviousManifestPath = $null
if ($null -ne $existingManifest) {
    $previousDesktopVersion =
        Get-ManifestPlatformVersion `
            -Manifest $existingManifest `
            -Platform 'desktop'
    $previousAndroidVersion =
        Get-ManifestPlatformVersion `
            -Manifest $existingManifest `
            -Platform 'android'
    $nextDesktopVersion =
        Get-ManifestPlatformVersion -Manifest $manifest -Platform 'desktop'
    $nextAndroidVersion =
        Get-ManifestPlatformVersion -Manifest $manifest -Platform 'android'
    if (
        -not [string]::Equals(
            $previousDesktopVersion,
            $nextDesktopVersion,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $previousAndroidVersion,
            $nextAndroidVersion,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        $stagedPreviousManifestPath =
            Join-Path $stagingRoot 'previous-manifest.json'
        Write-JsonFileAtomically `
            -TargetPath $stagedPreviousManifestPath `
            -InputObject $existingManifest
    }
}

$transactionEntries = New-Object System.Collections.Generic.List[object]
$androidTransactionEntry = $null
$desktopTransactionEntries = @()
$previousTransactionEntry = $null
$runtimeGenerationTransactionEntry = $null
$deliveryGenerationTransactionEntry = $null
$deliveryManifestTransactionEntry = $null
$mainManifestTransactionEntry = $null
$requestReceiptTransactionEntry = $null
$manifestPointerTransactionEntry = $null
$entryIndex = 0
Invoke-GeoraePlanReleaseTestPausePoint `
    -Name 'BeforeReleaseTransactionStageBinding'
if ($null -ne $androidStagedPath) {
    $androidTransactionEntry =
        New-GeoraePlanReleaseTransactionEntry `
            -TargetPath $canonicalAndroidDestination `
            -StagedPath $androidStagedPath `
            -StagingRoot $stagingRoot `
            -BackupRoot $backupRoot `
            -Index $entryIndex `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel `
            -ExpectedSha256 ([string]$manifest.android.sha256) `
            -ExpectedFileSize ([long]$manifest.android.fileSize)
    $transactionEntries.Add($androidTransactionEntry) | Out-Null
    $entryIndex++
}
if ($null -ne $desktopStagedPath) {
    $desktopDestinationDirectory = Join-Path $downloadsRoot 'desktop'
    foreach ($packageBinding in @(
        [pscustomobject]@{
            SourcePath = $desktopStagedPath
            TargetPath = Join-Path $desktopDestinationDirectory $desktopFileName
            Sha256 = [string]$desktopPackageSnapshot.Sha256
            FileSize = [long]$desktopPackageSnapshot.FileSize
        },
        [pscustomobject]@{
            SourcePath = $desktopExeStagedPath
            TargetPath = Join-Path $desktopDestinationDirectory $desktopExeFileName
            Sha256 = [string]$desktopExeInstallerSnapshot.Sha256
            FileSize = [long]$desktopExeInstallerSnapshot.FileSize
        },
        [pscustomobject]@{
            SourcePath = $desktopMsiStagedPath
            TargetPath = Join-Path $desktopDestinationDirectory $desktopMsiFileName
            Sha256 = [string]$desktopMsiInstallerSnapshot.Sha256
            FileSize = [long]$desktopMsiInstallerSnapshot.FileSize
        }
    )) {
        $desktopTransactionEntry =
            New-GeoraePlanReleaseTransactionEntry `
                -TargetPath $packageBinding.TargetPath `
                -StagedPath $packageBinding.SourcePath `
                -StagingRoot $stagingRoot `
                -BackupRoot $backupRoot `
                -Index $entryIndex `
                -OutputRoot $OutputRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel `
                -ExpectedSha256 $packageBinding.Sha256 `
                -ExpectedFileSize $packageBinding.FileSize
        $transactionEntries.Add($desktopTransactionEntry) | Out-Null
        $desktopTransactionEntries += $desktopTransactionEntry
        $entryIndex++
    }
}
if ($null -ne $stagedPreviousManifestPath) {
    $previousTransactionEntry =
        New-GeoraePlanReleaseTransactionEntry `
            -TargetPath $previousManifestPath `
            -StagedPath $stagedPreviousManifestPath `
            -StagingRoot $stagingRoot `
            -BackupRoot $backupRoot `
            -Index $entryIndex `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
    $transactionEntries.Add($previousTransactionEntry) | Out-Null
    $entryIndex++
}
foreach ($generationPair in @(
    @($stagedManifestGenerationPath, $manifestGenerationPath),
    @(
        $stagedDeliveryManifestGenerationPath,
        $deliveryManifestGenerationPath)
)) {
    $generationTransactionEntry =
        New-GeoraePlanReleaseTransactionEntry `
            -TargetPath $generationPair[1] `
            -StagedPath $generationPair[0] `
            -StagingRoot $stagingRoot `
            -BackupRoot $backupRoot `
            -Index $entryIndex `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
    $transactionEntries.Add($generationTransactionEntry) | Out-Null
    if ([string]::Equals(
        [IO.Path]::GetFullPath($generationPair[1]),
        [IO.Path]::GetFullPath($manifestGenerationPath),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        $runtimeGenerationTransactionEntry = $generationTransactionEntry
    }
    else {
        $deliveryGenerationTransactionEntry = $generationTransactionEntry
    }
    $entryIndex++
}
foreach ($manifestPair in @(
    @($stagedDeliveryManifestPath, $deliveryManifestPath),
    @($stagedManifestPath, $manifestPath)
)) {
    $manifestTransactionEntry =
        New-GeoraePlanReleaseTransactionEntry `
            -TargetPath $manifestPair[1] `
            -StagedPath $manifestPair[0] `
            -StagingRoot $stagingRoot `
            -BackupRoot $backupRoot `
            -Index $entryIndex `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
    $transactionEntries.Add($manifestTransactionEntry) | Out-Null
    if ([string]::Equals(
        [IO.Path]::GetFullPath($manifestPair[1]),
        [IO.Path]::GetFullPath($deliveryManifestPath),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        $deliveryManifestTransactionEntry = $manifestTransactionEntry
    }
    else {
        $mainManifestTransactionEntry = $manifestTransactionEntry
    }
    $entryIndex++
}
$requestReceiptTransactionEntry =
    New-GeoraePlanReleaseTransactionEntry `
        -TargetPath $requestReceiptPath `
        -StagedPath $stagedRequestReceiptPath `
        -StagingRoot $stagingRoot `
        -BackupRoot $backupRoot `
        -Index $entryIndex `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
$transactionEntries.Add($requestReceiptTransactionEntry) | Out-Null
$entryIndex++
$manifestPointerTransactionEntry =
    New-GeoraePlanReleaseTransactionEntry `
        -TargetPath $manifestPointerPath `
        -StagedPath $stagedManifestPointerPath `
        -StagingRoot $stagingRoot `
        -BackupRoot $backupRoot `
        -Index $entryIndex `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
$transactionEntries.Add($manifestPointerTransactionEntry) | Out-Null

$journal.entries = $transactionEntries.ToArray()
$journal.phase = 'CommitPending'
Write-DurableGeoraePlanReleaseJournal `
    -JournalPath $journalPath `
    -Journal $journal
Restore-GeoraePlanReleaseTransaction `
    -TransactionRoot $transactionRoot `
    -OutputRoot $OutputRoot `
    -ProjectRoot $ProjectRoot `
    -Channel $Channel `
    -InspectOnly

$commitError = $null
try {
    if ($null -ne $androidStagedPath) {
        if ($null -eq $androidDestinationLease) {
            Copy-GeoraePlanReleaseTransactionFileAtomically `
                -SourcePath $androidTransactionEntry.stagedPath `
                -TargetPath $androidTransactionEntry.targetPath `
                -TemporaryPath $androidTransactionEntry.commitTemporaryPath `
                -ExpectedSha256 $androidTransactionEntry.stagedSha256 `
                -ExpectedFileSize $androidTransactionEntry.stagedFileSize
            $androidDestinationLease = [IO.File]::Open(
                $canonicalAndroidDestination,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::Read)
        }
        Assert-AndroidCanonicalDestinationLease `
            -DestinationDirectory $androidDestinationDirectory `
            -DestinationPath $canonicalAndroidDestination `
            -Lease $androidDestinationLease `
            -ExpectedSha256 $androidTransactionEntry.stagedSha256 `
            -ExpectedFileSize $androidTransactionEntry.stagedFileSize
    }
    if ($null -ne $desktopStagedPath) {
        foreach ($entry in $desktopTransactionEntries) {
            Copy-GeoraePlanReleaseTransactionFileAtomically `
                -SourcePath $entry.stagedPath `
                -TargetPath $entry.targetPath `
                -TemporaryPath $entry.commitTemporaryPath `
                -ExpectedSha256 $entry.stagedSha256 `
                -ExpectedFileSize $entry.stagedFileSize
        }
    }
    if ($null -ne $stagedPreviousManifestPath) {
        Copy-GeoraePlanReleaseTransactionFileAtomically `
            -SourcePath $previousTransactionEntry.stagedPath `
            -TargetPath $previousTransactionEntry.targetPath `
            -TemporaryPath $previousTransactionEntry.commitTemporaryPath `
            -ExpectedSha256 $previousTransactionEntry.stagedSha256 `
            -ExpectedFileSize $previousTransactionEntry.stagedFileSize
    }
    Copy-GeoraePlanReleaseTransactionFileAtomically `
        -SourcePath $runtimeGenerationTransactionEntry.stagedPath `
        -TargetPath $runtimeGenerationTransactionEntry.targetPath `
        -TemporaryPath $runtimeGenerationTransactionEntry.commitTemporaryPath `
        -ExpectedSha256 $runtimeGenerationTransactionEntry.stagedSha256 `
        -ExpectedFileSize $runtimeGenerationTransactionEntry.stagedFileSize
    Copy-GeoraePlanReleaseTransactionFileAtomically `
        -SourcePath $deliveryGenerationTransactionEntry.stagedPath `
        -TargetPath $deliveryGenerationTransactionEntry.targetPath `
        -TemporaryPath $deliveryGenerationTransactionEntry.commitTemporaryPath `
        -ExpectedSha256 $deliveryGenerationTransactionEntry.stagedSha256 `
        -ExpectedFileSize $deliveryGenerationTransactionEntry.stagedFileSize
    Copy-GeoraePlanReleaseTransactionFileAtomically `
        -SourcePath $deliveryManifestTransactionEntry.stagedPath `
        -TargetPath $deliveryManifestTransactionEntry.targetPath `
        -TemporaryPath $deliveryManifestTransactionEntry.commitTemporaryPath `
        -ExpectedSha256 $deliveryManifestTransactionEntry.stagedSha256 `
        -ExpectedFileSize $deliveryManifestTransactionEntry.stagedFileSize
    Invoke-GeoraePlanReleaseTestKillPoint `
        -Name 'AfterDeliveryManifestCommitBeforePointer'
    Copy-GeoraePlanReleaseTransactionFileAtomically `
        -SourcePath $mainManifestTransactionEntry.stagedPath `
        -TargetPath $mainManifestTransactionEntry.targetPath `
        -TemporaryPath $mainManifestTransactionEntry.commitTemporaryPath `
        -ExpectedSha256 $mainManifestTransactionEntry.stagedSha256 `
        -ExpectedFileSize $mainManifestTransactionEntry.stagedFileSize
    $journal.phase = 'PointerCommitPending'
    Write-DurableGeoraePlanReleaseJournal `
        -JournalPath $journalPath `
        -Journal $journal
    Copy-GeoraePlanReleaseTransactionFileAtomically `
        -SourcePath $requestReceiptTransactionEntry.stagedPath `
        -TargetPath $requestReceiptTransactionEntry.targetPath `
        -TemporaryPath $requestReceiptTransactionEntry.commitTemporaryPath `
        -ExpectedSha256 $requestReceiptTransactionEntry.stagedSha256 `
        -ExpectedFileSize $requestReceiptTransactionEntry.stagedFileSize
    Invoke-GeoraePlanReleaseTestKillPoint `
        -Name 'AfterReceiptReplaceBeforePointer'
    Invoke-GeoraePlanReleaseTestFailurePoint `
        -Name 'BeforePointerReplaceAfterCommitIntent'
    Invoke-GeoraePlanReleaseTestKillPoint `
        -Name 'BeforePointerReplaceAfterCommitIntent'
    Copy-GeoraePlanReleaseTransactionFileAtomically `
        -SourcePath $manifestPointerTransactionEntry.stagedPath `
        -TargetPath $manifestPointerTransactionEntry.targetPath `
        -TemporaryPath $manifestPointerTransactionEntry.commitTemporaryPath `
        -ExpectedSha256 $manifestPointerTransactionEntry.stagedSha256 `
        -ExpectedFileSize $manifestPointerTransactionEntry.stagedFileSize
    Invoke-GeoraePlanReleaseTestFailurePoint `
        -Name 'AfterPointerReplaceBeforeCommitJournal'
    Invoke-GeoraePlanReleaseTestKillPoint `
        -Name 'AfterPointerReplaceBeforeCommitJournal'
    Assert-GeoraePlanReleaseCommittedOutcome `
        -TransactionEntries $transactionEntries.ToArray() `
        -MainManifestTransactionEntry $mainManifestTransactionEntry `
        -DeliveryManifestTransactionEntry $deliveryManifestTransactionEntry `
        -ManifestPath $manifestPath `
        -DeliveryManifestPath $deliveryManifestPath `
        -ManifestPointerPath $manifestPointerPath `
        -ManifestRoot $manifestRoot `
        -DeliveryGenerationRoot $deliveryManifestGenerationRoot `
        -ManifestGenerationPath $manifestGenerationPath `
        -GenerationId $generationId `
        -OutputRoot $OutputRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
    $journal.phase = 'Committed'
    Write-DurableGeoraePlanReleaseJournal `
        -JournalPath $journalPath `
        -Journal $journal
}
catch {
    $commitError = $_.Exception
}
if ($null -ne $commitError) {
    if ($null -ne $androidDestinationLease) {
        $androidDestinationLease.Dispose()
        $androidDestinationLease = $null
    }
    try {
        Restore-GeoraePlanReleaseTransaction `
            -TransactionRoot $transactionRoot `
            -OutputRoot $OutputRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        $recoveredOutcome =
            Get-GeoraePlanReleaseRecoveredOutcome `
                -TransactionEntries $transactionEntries.ToArray() `
                -MainManifestTransactionEntry `
                    $mainManifestTransactionEntry `
                -DeliveryManifestTransactionEntry `
                    $deliveryManifestTransactionEntry `
                -ManifestPath $manifestPath `
                -DeliveryManifestPath $deliveryManifestPath `
                -ManifestPointerPath $manifestPointerPath `
                -ManifestRoot $manifestRoot `
                -DeliveryGenerationRoot `
                    $deliveryManifestGenerationRoot `
                -ManifestGenerationPath $manifestGenerationPath `
                -GenerationId $generationId `
                -OutputRoot $OutputRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
    }
    catch {
        throw [AggregateException]::new(
            'Release transaction commit and recovery both failed.',
            [Exception[]]@($commitError, $_.Exception))
    }
    if ($recoveredOutcome -eq 'RolledBack') {
        throw $commitError
    }
    if ($recoveredOutcome -ne 'Committed') {
        throw [AggregateException]::new(
            'Release transaction recovery outcome is unknown.',
            [Exception[]]@(
                $commitError,
                [InvalidOperationException]::new(
                    'Neither committed nor rolled-back target evidence ' +
                    'matched the durable journal.')))
    }
    Write-Host (
        'release_commit_recovered=committed ' +
        "original_error=$($commitError.Message)")
}
Restore-GeoraePlanReleaseTransaction `
    -TransactionRoot $transactionRoot `
    -OutputRoot $OutputRoot `
    -ProjectRoot $ProjectRoot `
    -Channel $Channel
Invoke-GeoraePlanReleaseTestKillPoint `
    -Name 'AfterTransactionCleanupBeforePrune'

$removedDesktopPackages = @()
$removedAndroidPackages = @()
if (-not $SkipPackagePrune) {
    $preservedDesktopFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'desktop'
    $preservedAndroidFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'android'
    $removedDesktopPackages = Remove-OldPackages -DirectoryPath (Join-Path $downloadsRoot 'desktop') -KeepCount $KeepDesktopPackageCount -PreserveFileNames $preservedDesktopFiles
    $removedAndroidPackages = Remove-OldPackages -DirectoryPath (Join-Path $downloadsRoot 'android') -KeepCount $KeepAndroidPackageCount -PreserveFileNames $preservedAndroidFiles
}

Write-Host "update_manifest=$manifestPath"
Write-Host "delivery_manifest=$deliveryManifestPath"
Write-Host "manifest_generation=$manifestGenerationPath"
Write-Host "manifest_pointer=$manifestPointerPath"
if (Test-Path -LiteralPath $previousManifestPath) { Write-Host "previous_manifest=$previousManifestPath" }
if ($manifest.desktop) { Write-Host "desktop_package=$($manifest.desktop.fileName)" }
if ($manifest.desktop -and $manifest.desktop.installers) {
    $manifest.desktop.installers | ForEach-Object { Write-Host "desktop_native_installer=$($_.fileName)" }
}
if ($manifest.android) { Write-Host "android_package=$($manifest.android.fileName)" }
if ($removedDesktopPackages.Count -gt 0) { Write-Host "desktop_packages_pruned=$($removedDesktopPackages.Count)" }
if ($removedAndroidPackages.Count -gt 0) { Write-Host "android_packages_pruned=$($removedAndroidPackages.Count)" }
Invoke-GeoraePlanReleaseTestPausePoint `
    -Name 'BeforeTerminalSnapshotCleanup'
}
catch {
    $outerError = $_.Exception
    if (
        $transactionPreparedByThisRun -and
        -not [string]::IsNullOrWhiteSpace($transactionRoot) -and
        (Test-Path -LiteralPath $transactionRoot) -and
        -not [string]::IsNullOrWhiteSpace($journalPath) -and
        (Test-Path -LiteralPath $journalPath -PathType Leaf)
    ) {
        if ($null -ne $androidDestinationLease) {
            $androidDestinationLease.Dispose()
            $androidDestinationLease = $null
        }
        try {
            Restore-GeoraePlanReleaseTransaction `
                -TransactionRoot $transactionRoot `
                -OutputRoot $OutputRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
        }
        catch {
            throw [AggregateException]::new(
                'Release operation and owner-journal recovery both failed.',
                [Exception[]]@($outerError, $_.Exception))
        }
    }
    throw $outerError
}
finally {
    Close-GeoraePlanReleaseTransactionStageLeases
    if ($null -ne $androidPackageSnapshot) {
        Remove-GeoraePlanAndroidApkSnapshot -Snapshot $androidPackageSnapshot
        $androidPackageSnapshot = $null
    }
    if ($null -ne $androidDestinationLease) {
        $androidDestinationLease.Dispose()
        $androidDestinationLease = $null
    }
    foreach ($snapshotName in @(
        'desktopPackageSnapshot',
        'desktopExeInstallerSnapshot',
        'desktopMsiInstallerSnapshot'
    )) {
        $snapshot = Get-Variable -Name $snapshotName -ValueOnly
        if ($null -ne $snapshot) {
            Remove-GeoraePlanReleaseFileSnapshot -Snapshot $snapshot
            Set-Variable -Name $snapshotName -Value $null
        }
    }
    if ($null -ne $script:releaseDirectoryLease) {
        $script:releaseDirectoryLease.Dispose()
        $script:releaseDirectoryLease = $null
    }
    if ($null -ne $releasePublishLease) {
        $releasePublishLease.Dispose()
        $releasePublishLease = $null
    }
    if ($null -ne $deliveryPublishLease) {
        $deliveryPublishLease.Dispose()
        $deliveryPublishLease = $null
    }
}
