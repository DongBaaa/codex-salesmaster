[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$Configuration = 'Release',
    [string]$ReleaseId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipBuild,
    [switch]$MirrorToLive,
    [switch]$PreserveLiveUpdateAssets,
    [switch]$PreserveLiveAndroidUpdate,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$LinuxRemoteRoot = '/srv/georaeplan',
    [string]$LinuxRemoteOpsPath = '/srv/georaeplan/ops',
    [int]$KeepReleaseCount = 2,
    [int64]$MinimumLinuxFreeBytes = 2147483648,
    [switch]$SkipConfigSync,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [switch]$SkipPreDeployOperationalGate,
    [switch]$SkipPostDeployOperationalGate,
    [switch]$SkipPlatformHealthChecks,
    [switch]$FailOnOperationalWarnings,
    [switch]$AcceptLegacyAndroidDebugSigningWarning,
    [switch]$AcceptRentalTemplateItemReferenceRisk,
    [switch]$SkipAndroidSigningContinuityCheck,
    [switch]$AcceptAndroidSigningCertificateChange,
    [switch]$AllowMissingLiveUpdateBaseline,
    [string]$LocalCacheAppDataRoot = '',
    [string]$LocalCacheEvidenceDirectory = '',
    [switch]$RequireLocalCacheConsistencyCheck,
    [switch]$FailOnLocalCacheWarning,
    [string]$PreDeployBaseUrl = '',
    [string]$PreDeploySecretPath = '',
    [string]$PreDeployOutputDirectory = '',
    [string[]]$PreDeployAllowedIntegrityWarningCodes = @(),
    [string]$PostDeployBaseUrl = '',
    [string]$PostDeploySecretPath = '',
    [string]$PostDeployOutputDirectory = '',
    [string[]]$PostDeployAllowedIntegrityWarningCodes = @(),
    [string]$ExpectedClientCompatibilityMode = 'AuditOnly',
    [int]$ExpectedClientCompatibilityEnabledPolicyCount = 0,
    [switch]$AllowLegacyPreDeployCompatibilitySummary,
    [string]$DesktopNotes = '',
    [string]$AndroidNotes = ''
)

$ErrorActionPreference = 'Stop'

function Get-GeoraePlanLinuxFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read,
        4096,
        [IO.FileOptions]::SequentialScan)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

if ($ExpectedClientCompatibilityMode -cnotin @('AuditOnly', 'StrictBlock')) {
    throw (
        'ExpectedClientCompatibilityMode must be exactly AuditOnly or ' +
        'StrictBlock.')
}
if (
    $ExpectedClientCompatibilityEnabledPolicyCount -lt 0 -or
    $ExpectedClientCompatibilityEnabledPolicyCount -gt 2
) {
    throw (
        'ExpectedClientCompatibilityEnabledPolicyCount must be between 0 ' +
        'and 2.')
}

function Resolve-ProjectRoot {
    param([string]$ExplicitProjectRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitProjectRoot)) {
        return (Resolve-Path -LiteralPath $ExplicitProjectRoot).Path
    }

    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
}

function Resolve-DotnetCommand {
    param([Parameter(Mandatory = $true)][string]$Root)

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

    throw "Unable to locate a working dotnet executable under $Root."
}

function Resolve-SshExecutable {
    $windowsSsh = 'C:\Windows\System32\OpenSSH\ssh.exe'
    if (Test-Path -LiteralPath $windowsSsh) {
        return $windowsSsh
    }

    $ssh = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -ne $ssh) {
        return $ssh.Source
    }

    throw 'ssh executable was not found.'
}

function Resolve-TarExecutable {
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($null -ne $tar) {
        return $tar.Source
    }

    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if ($null -ne $tar) {
        return $tar.Source
    }

    throw 'tar executable was not found.'
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Argument)

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $escaped = $Argument -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Convert-ToSingleQuotedShellLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Assert-GeoraePlanLinuxRegularDirectoryChain {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Label = 'Linux update path'
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $volumeRoot = [IO.Path]::GetPathRoot($resolvedPath)
    $relativePath = $resolvedPath.Substring($volumeRoot.Length)
    $currentPath = $volumeRoot
    foreach ($segment in $relativePath.Split(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries
    )) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            continue
        }
        $item =
            Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (
            -not $item.PSIsContainer -or
            ($item.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "$Label contains a non-regular directory ancestor: $currentPath"
        }
    }
    return $resolvedPath
}

function Initialize-GeoraePlanLinuxDirectoryLeaseType {
    if ($null -ne (
        'GeoraePlan.LinuxUpdate.StrictDirectoryChainLease' -as [type]
    )) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace GeoraePlan.LinuxUpdate
{
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
                        "Linux update directory lease path is empty.");
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
                            "Linux update directory path identity changed: " +
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
                    "Linux update directory path cannot be inspected: " +
                    path);
            }
            if (
                (attributes & DirectoryAttribute) == 0 ||
                (attributes & ReparsePoint) != 0)
            {
                throw new IOException(
                    "Linux update directory path is not regular: " + path);
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
                    "Linux update directory lease cannot be acquired: " +
                    path);
            }
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    "Linux update directory identity cannot be read: " +
                    path);
            }
            if (
                (information.FileAttributes & DirectoryAttribute) == 0 ||
                (information.FileAttributes & ReparsePoint) != 0)
            {
                handle.Dispose();
                throw new IOException(
                    "Linux update directory lease resolved to a non-regular " +
                    "directory: " + path);
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
}
'@
}

function Open-GeoraePlanLinuxDirectoryChainLease {
    param(
        [Parameter(Mandatory = $true)][string[]]$DirectoryPaths
    )

    foreach ($directoryPath in $DirectoryPaths) {
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $directoryPath `
                -Label 'Linux update directory lease'
    }
    Initialize-GeoraePlanLinuxDirectoryLeaseType
    return [GeoraePlan.LinuxUpdate.StrictDirectoryChainLease]::Open(
        $DirectoryPaths)
}

function Assert-GeoraePlanLinuxDirectoryChainLease {
    param([Parameter(Mandatory = $true)]$Lease)

    if ($null -eq $Lease) {
        throw 'Linux update directory chain lease is missing.'
    }
    $Lease.Validate()
}

function Assert-GeoraePlanDurableUpdateWorkRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot
    )

    $expectedRoot = [IO.Path]::GetFullPath(
        (Join-Path $ProjectRoot 'release-temp\linux-update-assets-stable'))
    $actualRoot = [IO.Path]::GetFullPath($DurableUpdatesRoot)
    if (-not [string]::Equals(
        $actualRoot,
        $expectedRoot,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw (
            'Durable Linux update work root is outside its exact owned ' +
            "boundary. expected=$expectedRoot actual=$actualRoot")
    }
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $actualRoot `
            -Label 'Durable Linux update work root'
    if (Test-Path -LiteralPath $actualRoot) {
        $rootItem =
            Get-Item -LiteralPath $actualRoot -Force -ErrorAction Stop
        if (
            -not $rootItem.PSIsContainer -or
            ($rootItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw 'Durable Linux update work root is not a regular directory.'
        }
    }
    return $actualRoot
}

function Test-GeoraePlanDurableUpdateTransactionEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [string]$Channel = 'stable'
    )

    if (-not (Test-Path -LiteralPath $DurableUpdatesRoot)) {
        return $false
    }
    $transactionPrefix =
        '.georaeplan-release-transaction-' + $Channel
    $evidence = @(
        Get-ChildItem `
            -LiteralPath $DurableUpdatesRoot `
            -Force `
            -ErrorAction Stop |
            Where-Object {
                $_.Name.StartsWith(
                    $transactionPrefix,
                    [StringComparison]::Ordinal)
            })
    return $evidence.Count -gt 0
}

function Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not [string]::Equals(
        [string]$env:GEORAEPLAN_LINUX_UPDATE_WRAPPER_TEST_KILL_POINT,
        $Name,
        [StringComparison]::Ordinal
    )) {
        return
    }
    [Diagnostics.Process]::GetCurrentProcess().Kill()
    throw "Linux update wrapper test kill point did not terminate: $Name"
}

function Get-GeoraePlanDurableUpdateWrapperStatePath {
    param(
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot
    )

    return Join-Path $DurableUpdatesRoot (
        '.georaeplan-linux-update-wrapper-state.json')
}

function Get-GeoraePlanLinuxRegularPointerItem {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Label = 'Update manifest pointer',
        [switch]$AllowMissing
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $parentPath = Split-Path -Parent $resolvedPath
    $leafName = [IO.Path]::GetFileName($resolvedPath)
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) {
        if ($AllowMissing) {
            return $null
        }
        throw "$Label is missing: $resolvedPath"
    }

    $matchingItems = @(
        Get-ChildItem `
            -LiteralPath $parentPath `
            -Force `
            -ErrorAction Stop |
            Where-Object {
                [string]::Equals(
                    $_.Name,
                    $leafName,
                    [StringComparison]::OrdinalIgnoreCase)
            })
    if ($matchingItems.Count -eq 0) {
        if ($AllowMissing) {
            return $null
        }
        throw "$Label is missing: $resolvedPath"
    }
    if ($matchingItems.Count -ne 1) {
        throw "$Label path is ambiguous: $resolvedPath"
    }

    $item = $matchingItems[0]
    if (
        $item.PSIsContainer -or
        ($item.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "$Label is not a regular file: $resolvedPath"
    }
    return $item
}

function Get-GeoraePlanDurableUpdatePointerSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [string]$Channel = 'stable'
    )

    $pointerPath =
        Join-Path `
            (Join-Path $DurableUpdatesRoot 'manifest') `
            ($Channel + '.current.json')
    $pointerItem =
        Get-GeoraePlanLinuxRegularPointerItem `
            -Path $pointerPath `
            -Label 'Durable update manifest pointer' `
            -AllowMissing
    if ($null -eq $pointerItem) {
        return ''
    }
    return Get-GeoraePlanLinuxFileSha256 -Path $pointerPath
}

function Read-GeoraePlanDurableUpdateWrapperStateFile {
    param(
        [Parameter(Mandatory = $true)][string]$StatePath,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [string]$Channel = 'stable'
    )

    $state = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $expectedProperties = @(
        'owner',
        'schemaVersion',
        'channel',
        'projectRoot',
        'durableUpdatesRoot',
        'phase',
        'seedPointerSha256',
        'publishedPointerSha256',
        'copiedPublishRoot')
    $actualProperties = @(
        $state.PSObject.Properties | ForEach-Object { $_.Name })
    if (
        $actualProperties.Count -ne $expectedProperties.Count -or
        @($actualProperties | Where-Object {
            $_ -notin $expectedProperties
        }).Count -ne 0 -or
        -not [string]::Equals(
            [string]$state.owner,
            'georaeplan-linux-update-wrapper-state',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$state.schemaVersion,
            '1',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$state.channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$state.projectRoot),
            [IO.Path]::GetFullPath($ProjectRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$state.durableUpdatesRoot),
            [IO.Path]::GetFullPath($DurableUpdatesRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$state.phase -notin @(
            'Seeded',
            'Published',
            'Copied') -or
        (
            -not [string]::IsNullOrWhiteSpace(
                [string]$state.seedPointerSha256) -and
            [string]$state.seedPointerSha256 -notmatch
                '^[0-9A-Fa-f]{64}$'
        ) -or
        (
            [string]$state.phase -in @('Published', 'Copied') -and
            [string]$state.publishedPointerSha256 -notmatch
                '^[0-9A-Fa-f]{64}$'
        ) -or
        (
            [string]$state.phase -eq 'Copied' -and
            [string]::IsNullOrWhiteSpace(
                [string]$state.copiedPublishRoot)
        )
    ) {
        throw 'Durable Linux update wrapper state binding is invalid.'
    }
    return $state
}

function Resume-GeoraePlanDurableUpdateWrapperState {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [string]$Channel = 'stable'
    )

    $statePath =
        Get-GeoraePlanDurableUpdateWrapperStatePath `
            -DurableUpdatesRoot $DurableUpdatesRoot
    $pendingPath = $statePath + '.pending'
    $backupPath = $statePath + '.backup'
    if (Test-Path -LiteralPath $pendingPath -PathType Leaf) {
        $null =
            Read-GeoraePlanDurableUpdateWrapperStateFile `
                -StatePath $pendingPath `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Channel $Channel
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            Remove-Item `
                -LiteralPath $pendingPath `
                -Force `
                -ErrorAction Stop
        }
        else {
            [IO.File]::Move($pendingPath, $statePath)
        }
    }
    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
        $null =
            Read-GeoraePlanDurableUpdateWrapperStateFile `
                -StatePath $backupPath `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Channel $Channel
        Remove-Item `
            -LiteralPath $backupPath `
            -Force `
            -ErrorAction Stop
    }
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        return $null
    }
    return Read-GeoraePlanDurableUpdateWrapperStateFile `
        -StatePath $statePath `
        -ProjectRoot $ProjectRoot `
        -DurableUpdatesRoot $DurableUpdatesRoot `
        -Channel $Channel
}

function Write-GeoraePlanDurableUpdateWrapperState {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Seeded', 'Published', 'Copied')]
        [string]$Phase,
        [string]$SeedPointerSha256 = '',
        [string]$PublishedPointerSha256 = '',
        [string]$CopiedPublishRoot = '',
        [string]$Channel = 'stable'
    )

    $statePath =
        Get-GeoraePlanDurableUpdateWrapperStatePath `
            -DurableUpdatesRoot $DurableUpdatesRoot
    $pendingPath = $statePath + '.pending'
    $backupPath = $statePath + '.backup'
    $null =
        Resume-GeoraePlanDurableUpdateWrapperState `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $DurableUpdatesRoot `
            -Channel $Channel
    $payload = [ordered]@{
        owner = 'georaeplan-linux-update-wrapper-state'
        schemaVersion = '1'
        channel = $Channel
        projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
        durableUpdatesRoot =
            [IO.Path]::GetFullPath($DurableUpdatesRoot)
        phase = $Phase
        seedPointerSha256 = $SeedPointerSha256
        publishedPointerSha256 = $PublishedPointerSha256
        copiedPublishRoot = if (
            [string]::IsNullOrWhiteSpace($CopiedPublishRoot)
        ) {
            ''
        }
        else {
            [IO.Path]::GetFullPath($CopiedPublishRoot)
        }
    }
    $json = $payload | ConvertTo-Json -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
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
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        [IO.File]::Replace(
            $pendingPath,
            $statePath,
            $backupPath,
            $true)
    }
    else {
        [IO.File]::Move($pendingPath, $statePath)
    }
    $stateStream = [IO.File]::Open(
        $statePath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::Read)
    try {
        $stateStream.Flush($true)
    }
    finally {
        $stateStream.Dispose()
    }
    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
        Remove-Item `
            -LiteralPath $backupPath `
            -Force `
            -ErrorAction Stop
    }
    return Read-GeoraePlanDurableUpdateWrapperStateFile `
        -StatePath $statePath `
        -ProjectRoot $ProjectRoot `
        -DurableUpdatesRoot $DurableUpdatesRoot `
        -Channel $Channel
}

function Get-GeoraePlanLinuxUpdateOwnerMetadataPaths {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Preparation', 'Cleanup')]
        [string]$Kind
    )

    $workRoot =
        [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'release-temp'))
    $suffix = if ($Kind -eq 'Preparation') {
        'preparing'
    }
    else {
        'cleanup'
    }
    $ownedWorkRoot = Join-Path $workRoot (
        'linux-update-assets-stable.' + $suffix)
    $metadataPath = Join-Path $workRoot (
        'linux-update-assets-stable.' + $suffix + '-owner.json')
    return [pscustomobject]@{
        WorkRoot = [IO.Path]::GetFullPath($ownedWorkRoot)
        MetadataPath = [IO.Path]::GetFullPath($metadataPath)
        PendingPath = [IO.Path]::GetFullPath($metadataPath + '.pending')
        BackupPath = [IO.Path]::GetFullPath($metadataPath + '.backup')
    }
}

function Read-GeoraePlanLinuxUpdateOwnerMetadataFile {
    param(
        [Parameter(Mandatory = $true)][string]$MetadataPath,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Preparation', 'Cleanup')]
        [string]$Kind,
        [string]$Channel = 'stable'
    )

    $paths =
        Get-GeoraePlanLinuxUpdateOwnerMetadataPaths `
            -ProjectRoot $ProjectRoot `
            -Kind $Kind
    $state =
        Get-Content -LiteralPath $MetadataPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    $expectedProperties = @(
        'owner',
        'schemaVersion',
        'channel',
        'projectRoot',
        'durableUpdatesRoot',
        'workRoot',
        'phase',
        'seedPointerSha256',
        'publishedPointerSha256',
        'copiedPublishRoot')
    $actualProperties = @(
        $state.PSObject.Properties | ForEach-Object { $_.Name })
    $expectedOwner = if ($Kind -eq 'Preparation') {
        'georaeplan-linux-update-preparation'
    }
    else {
        'georaeplan-linux-update-cleanup'
    }
    $validPhase = if ($Kind -eq 'Preparation') {
        [string]$state.phase -in @('Preparing', 'Ready')
    }
    else {
        [string]$state.phase -eq 'CleanupPending'
    }
    if (
        $actualProperties.Count -ne $expectedProperties.Count -or
        @($actualProperties | Where-Object {
            $_ -notin $expectedProperties
        }).Count -ne 0 -or
        -not [string]::Equals(
            [string]$state.owner,
            $expectedOwner,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$state.schemaVersion,
            '1',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$state.channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$state.projectRoot),
            [IO.Path]::GetFullPath($ProjectRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$state.durableUpdatesRoot),
            [IO.Path]::GetFullPath($DurableUpdatesRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$state.workRoot),
            $paths.WorkRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $validPhase -or
        (
            $Kind -eq 'Preparation' -and
            [string]$state.phase -eq 'Ready' -and
            [string]$state.seedPointerSha256 -notmatch
                '^$|^[0-9A-Fa-f]{64}$'
        ) -or
        (
            $Kind -eq 'Cleanup' -and
            [string]$state.publishedPointerSha256 -notmatch
                '^[0-9A-Fa-f]{64}$'
        ) -or
        (
            $Kind -eq 'Cleanup' -and
            [string]::IsNullOrWhiteSpace(
                [string]$state.copiedPublishRoot)
        )
    ) {
        throw "Durable Linux update $Kind owner binding is invalid."
    }
    return $state
}

function Resume-GeoraePlanLinuxUpdateOwnerMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Preparation', 'Cleanup')]
        [string]$Kind,
        [string]$Channel = 'stable'
    )

    $paths =
        Get-GeoraePlanLinuxUpdateOwnerMetadataPaths `
            -ProjectRoot $ProjectRoot `
            -Kind $Kind
    if (Test-Path -LiteralPath $paths.PendingPath -PathType Leaf) {
        $null =
            Read-GeoraePlanLinuxUpdateOwnerMetadataFile `
                -MetadataPath $paths.PendingPath `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Kind $Kind `
                -Channel $Channel
        if (Test-Path -LiteralPath $paths.MetadataPath -PathType Leaf) {
            Remove-Item `
                -LiteralPath $paths.PendingPath `
                -Force `
                -ErrorAction Stop
        }
        else {
            [IO.File]::Move($paths.PendingPath, $paths.MetadataPath)
        }
    }
    if (Test-Path -LiteralPath $paths.BackupPath -PathType Leaf) {
        $null =
            Read-GeoraePlanLinuxUpdateOwnerMetadataFile `
                -MetadataPath $paths.BackupPath `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Kind $Kind `
                -Channel $Channel
        if (-not (Test-Path `
            -LiteralPath $paths.MetadataPath `
            -PathType Leaf
        )) {
            [IO.File]::Move($paths.BackupPath, $paths.MetadataPath)
        }
        else {
            Remove-Item `
                -LiteralPath $paths.BackupPath `
                -Force `
                -ErrorAction Stop
        }
    }
    if (-not (Test-Path -LiteralPath $paths.MetadataPath -PathType Leaf)) {
        return $null
    }
    return Read-GeoraePlanLinuxUpdateOwnerMetadataFile `
        -MetadataPath $paths.MetadataPath `
        -ProjectRoot $ProjectRoot `
        -DurableUpdatesRoot $DurableUpdatesRoot `
        -Kind $Kind `
        -Channel $Channel
}

function Write-GeoraePlanLinuxUpdateOwnerMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Preparation', 'Cleanup')]
        [string]$Kind,
        [Parameter(Mandatory = $true)][string]$Phase,
        [string]$SeedPointerSha256 = '',
        [string]$PublishedPointerSha256 = '',
        [string]$CopiedPublishRoot = '',
        [string]$Channel = 'stable'
    )

    $paths =
        Get-GeoraePlanLinuxUpdateOwnerMetadataPaths `
            -ProjectRoot $ProjectRoot `
            -Kind $Kind
    New-Item `
        -ItemType Directory `
        -Force `
        -Path (Split-Path -Parent $paths.MetadataPath) | Out-Null
    $null =
        Resume-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $DurableUpdatesRoot `
            -Kind $Kind `
            -Channel $Channel
    $owner = if ($Kind -eq 'Preparation') {
        'georaeplan-linux-update-preparation'
    }
    else {
        'georaeplan-linux-update-cleanup'
    }
    $payload = [ordered]@{
        owner = $owner
        schemaVersion = '1'
        channel = $Channel
        projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
        durableUpdatesRoot =
            [IO.Path]::GetFullPath($DurableUpdatesRoot)
        workRoot = $paths.WorkRoot
        phase = $Phase
        seedPointerSha256 = $SeedPointerSha256
        publishedPointerSha256 = $PublishedPointerSha256
        copiedPublishRoot = if (
            [string]::IsNullOrWhiteSpace($CopiedPublishRoot)
        ) {
            ''
        }
        else {
            [IO.Path]::GetFullPath($CopiedPublishRoot)
        }
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($payload | ConvertTo-Json -Compress))
    $stream = [IO.FileStream]::new(
        $paths.PendingPath,
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
    if (Test-Path -LiteralPath $paths.MetadataPath -PathType Leaf) {
        [IO.File]::Replace(
            $paths.PendingPath,
            $paths.MetadataPath,
            $paths.BackupPath,
            $true)
    }
    else {
        [IO.File]::Move($paths.PendingPath, $paths.MetadataPath)
    }
    $metadataStream = [IO.File]::Open(
        $paths.MetadataPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::Read)
    try {
        $metadataStream.Flush($true)
    }
    finally {
        $metadataStream.Dispose()
    }
    Remove-Item `
        -LiteralPath $paths.BackupPath `
        -Force `
        -ErrorAction SilentlyContinue
    return Read-GeoraePlanLinuxUpdateOwnerMetadataFile `
        -MetadataPath $paths.MetadataPath `
        -ProjectRoot $ProjectRoot `
        -DurableUpdatesRoot $DurableUpdatesRoot `
        -Kind $Kind `
        -Channel $Channel
}

function Remove-GeoraePlanLinuxOwnedDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$DirectoryPath,
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [string]$KillPoint = '',
        $RootLease,
        [switch]$PreserveRoot
    )

    $resolvedPath = [IO.Path]::GetFullPath($DirectoryPath)
    if (-not [string]::Equals(
        $resolvedPath,
        [IO.Path]::GetFullPath($ExpectedPath),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Durable Linux update cleanup escaped its exact owned root.'
    }
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $resolvedPath `
            -Label 'Durable Linux update cleanup root'
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        return
    }
    $rootItem =
        Get-Item -LiteralPath $resolvedPath -Force -ErrorAction Stop
    if (
        -not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Durable Linux update cleanup root is not a regular directory.'
    }
    if ($PreserveRoot -and $null -eq $RootLease) {
        throw 'Preserving a Linux update cleanup root requires its live lease.'
    }
    $script:georaePlanLinuxDeleteKillTriggered = $false
    function Invoke-GeoraePlanLinuxOwnedDeleteKillPoint {
        if (
            -not $script:georaePlanLinuxDeleteKillTriggered -and
            -not [string]::IsNullOrWhiteSpace($KillPoint)
        ) {
            $script:georaePlanLinuxDeleteKillTriggered = $true
            Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
                -Name $KillPoint
        }
    }

    function Clear-GeoraePlanLinuxOwnedDirectory {
        param(
            [Parameter(Mandatory = $true)][string]$CurrentPath,
            [Parameter(Mandatory = $true)]$DirectoryLease
        )

        Assert-GeoraePlanLinuxDirectoryChainLease -Lease $DirectoryLease
        foreach ($child in @(
            Get-ChildItem `
                -LiteralPath $CurrentPath `
                -Force `
                -ErrorAction Stop
        )) {
            Assert-GeoraePlanLinuxDirectoryChainLease -Lease $DirectoryLease
            if (($child.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'Durable Linux update cleanup encountered a reparse ' +
                    "entry and stopped: $($child.FullName)")
            }
            if ($child.PSIsContainer) {
                $childLease = $null
                try {
                    $childLease =
                        Open-GeoraePlanLinuxDirectoryChainLease `
                            -DirectoryPaths @($child.FullName)
                    Clear-GeoraePlanLinuxOwnedDirectory `
                        -CurrentPath $child.FullName `
                        -DirectoryLease $childLease
                }
                finally {
                    if ($null -ne $childLease) {
                        $childLease.Dispose()
                    }
                }
                [IO.Directory]::Delete($child.FullName, $false)
            }
            else {
                [IO.File]::Delete($child.FullName)
            }
            Invoke-GeoraePlanLinuxOwnedDeleteKillPoint
        }
        Assert-GeoraePlanLinuxDirectoryChainLease -Lease $DirectoryLease
        if (@(
            Get-ChildItem `
                -LiteralPath $CurrentPath `
                -Force `
                -ErrorAction Stop
        ).Count -ne 0) {
            throw (
                'Durable Linux update cleanup directory changed while it ' +
                "was being cleared: $CurrentPath")
        }
    }

    $activeRootLease = $RootLease
    $ownsRootLease = $false
    try {
        if ($null -eq $activeRootLease) {
            $activeRootLease =
                Open-GeoraePlanLinuxDirectoryChainLease `
                    -DirectoryPaths @($resolvedPath)
            $ownsRootLease = $true
        }
        Clear-GeoraePlanLinuxOwnedDirectory `
            -CurrentPath $resolvedPath `
            -DirectoryLease $activeRootLease
    }
    finally {
        if ($ownsRootLease -and $null -ne $activeRootLease) {
            $activeRootLease.Dispose()
            $activeRootLease = $null
        }
    }
    if (-not $PreserveRoot) {
        [IO.Directory]::Delete($resolvedPath, $false)
    }
}

function Open-GeoraePlanDurableUpdateWorkLease {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [int]$TimeoutSeconds = 30
    )

    $workRoot = [IO.Path]::GetFullPath(
        (Join-Path $ProjectRoot 'release-temp'))
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $workRoot `
            -Label 'Durable Linux update work lease parent'
    [void][IO.Directory]::CreateDirectory($workRoot)
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $workRoot `
            -Label 'Durable Linux update work lease parent'
    $lockPath =
        Join-Path $workRoot 'linux-update-assets-stable.wrapper.lock'
    if (Test-Path -LiteralPath $lockPath) {
        $lockItem =
            Get-Item -LiteralPath $lockPath -Force -ErrorAction Stop
        if (
            $lockItem.PSIsContainer -or
            ($lockItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw 'Durable Linux update work lease is not a regular file.'
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            return [IO.FileStream]::new(
                $lockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None,
                4096,
                [IO.FileOptions]::WriteThrough)
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw (
                    'Timed out waiting for the durable Linux update work ' +
                    "lease: $lockPath")
            }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Remove-GeoraePlanDurableUpdateWorkRootIfSettled {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [string]$Channel = 'stable'
    )

    $ownedRoot =
        Assert-GeoraePlanDurableUpdateWorkRoot `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $DurableUpdatesRoot
    if (-not (Test-Path -LiteralPath $ownedRoot)) {
        return
    }
    if (Test-GeoraePlanDurableUpdateTransactionEvidence `
        -DurableUpdatesRoot $ownedRoot `
        -Channel $Channel
    ) {
        Write-Warning (
            'Durable Linux update transaction evidence is pending; ' +
            "preserving recovery root: $ownedRoot")
        return
    }
    $state =
        Resume-GeoraePlanDurableUpdateWrapperState `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $ownedRoot `
            -Channel $Channel
    if ($null -eq $state -or [string]$state.phase -ne 'Copied') {
        Write-Warning (
            'Durable Linux update wrapper state is not Copied; preserving ' +
            "recovery root: $ownedRoot")
        return
    }
    $publishedPointerSha256 =
        Get-GeoraePlanDurableUpdatePointerSha256 `
            -DurableUpdatesRoot $ownedRoot `
            -Channel $Channel
    $copiedPointerSha256 =
        Get-GeoraePlanDurableUpdatePointerSha256 `
            -DurableUpdatesRoot (
                Join-Path `
                    ([IO.Path]::GetFullPath(
                        [string]$state.copiedPublishRoot)) `
                    'updates') `
            -Channel $Channel
    if (
        -not [string]::Equals(
            $publishedPointerSha256,
            [string]$state.publishedPointerSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $copiedPointerSha256,
            [string]$state.publishedPointerSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw (
            'Copied Linux update wrapper state does not match both pointer ' +
            'snapshots; preserving recovery root.')
    }
    $null =
        Write-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $ownedRoot `
            -Kind 'Cleanup' `
            -Phase 'CleanupPending' `
            -PublishedPointerSha256 (
                [string]$state.publishedPointerSha256) `
            -CopiedPublishRoot (
                [string]$state.copiedPublishRoot) `
            -Channel $Channel
    Resume-GeoraePlanDurableUpdateCleanup `
        -ProjectRoot $ProjectRoot `
        -DurableUpdatesRoot $ownedRoot `
        -Channel $Channel
}

function Get-GeoraePlanLinuxUpdateCopyDirectoryPaths {
    param(
        [Parameter(Mandatory = $true)][string]$SourceUpdatesRoot
    )

    $resolvedSourceRoot = [IO.Path]::GetFullPath($SourceUpdatesRoot)
    $paths =
        [Collections.Generic.List[string]]::new()
    $paths.Add($resolvedSourceRoot)
    foreach ($directoryName in @('manifest', 'downloads')) {
        $categoryRoot =
            [IO.Path]::GetFullPath(
                (Join-Path $resolvedSourceRoot $directoryName))
        if (-not (Test-Path -LiteralPath $categoryRoot)) {
            continue
        }
        $categoryItem =
            Get-Item -LiteralPath $categoryRoot -Force -ErrorAction Stop
        if (
            -not $categoryItem.PSIsContainer -or
            ($categoryItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw (
                'Linux update copy source category is not a regular ' +
                "directory: $categoryRoot")
        }
        $pending =
            [Collections.Generic.Queue[string]]::new()
        $pending.Enqueue($categoryRoot)
        while ($pending.Count -gt 0) {
            $current = $pending.Dequeue()
            $paths.Add([IO.Path]::GetFullPath($current))
            foreach ($child in @(
                Get-ChildItem `
                    -LiteralPath $current `
                    -Directory `
                    -Force `
                    -ErrorAction Stop
            )) {
                if (($child.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
                ) {
                    throw (
                        'Linux update copy source contains a reparse point: ' +
                        $child.FullName)
                }
                $pending.Enqueue($child.FullName)
            }
        }
    }
    return @(
        $paths |
            Sort-Object -Unique
    )
}

function Assert-GeoraePlanLinuxDirectoryPathSet {
    param(
        [Parameter(Mandatory = $true)][string[]]$ExpectedPaths,
        [Parameter(Mandatory = $true)][string[]]$ActualPaths,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $expected =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $ExpectedPaths) {
        [void]$expected.Add([IO.Path]::GetFullPath($path))
    }
    $actual =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $ActualPaths) {
        [void]$actual.Add([IO.Path]::GetFullPath($path))
    }
    if (
        $expected.Count -ne $actual.Count -or
        -not $expected.SetEquals($actual)
    ) {
        throw "$Label directory membership changed during the operation."
    }
}

function Assert-GeoraePlanLinuxManifestReferencedAssets {
    param(
        [Parameter(Mandatory = $true)][string]$UpdatesRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$Channel = 'stable'
    )

    $resolvedUpdatesRoot = [IO.Path]::GetFullPath($UpdatesRoot)
    $manifestRoot = Join-Path $resolvedUpdatesRoot 'manifest'
    $pointerPath = Join-Path $manifestRoot ($Channel + '.current.json')
    $pointerItem =
        Get-GeoraePlanLinuxRegularPointerItem `
            -Path $pointerPath `
            -Label 'Linux update active manifest pointer' `
            -AllowMissing
    $manifestPath = if ($null -ne $pointerItem) {
        $pointerEvidence =
            Get-GeoraePlanUpdatePointerEvidence `
                -UpdatesRoot $resolvedUpdatesRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
        [string]$pointerEvidence.RuntimePath
    }
    else {
        Join-Path $manifestRoot ($Channel + '.json')
    }
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path (Split-Path -Parent $manifestPath) `
            -Label 'Linux update active manifest parent'
    $manifestItem =
        Get-Item -LiteralPath $manifestPath -Force -ErrorAction Stop
    if (
        $manifestItem.PSIsContainer -or
        ($manifestItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw 'Linux update active manifest is not a regular file.'
    }
    $manifest =
        Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    if (-not [string]::Equals(
        [string]$manifest.channel,
        $Channel,
        [StringComparison]::Ordinal
    )) {
        throw 'Linux update active manifest channel binding is invalid.'
    }

    foreach ($platform in @('desktop', 'android')) {
        $platformNode = $manifest.$platform
        if ($null -eq $platformNode) {
            continue
        }
        $artifacts =
            [Collections.Generic.List[object]]::new()
        if ([string]::IsNullOrWhiteSpace(
            [string]$platformNode.fileName
        )) {
            throw (
                'Linux update manifest platform main asset fileName is ' +
                "required: $platform")
        }
        $artifacts.Add($platformNode)
        $installersProperty =
            $platformNode.PSObject.Properties['installers']
        if ($null -ne $installersProperty) {
            foreach ($installer in @($installersProperty.Value)) {
                if ($null -eq $installer) {
                    throw (
                        'Linux update manifest installer entry cannot be ' +
                        "null: $platform")
                }
                if ([string]::IsNullOrWhiteSpace(
                    [string]$installer.fileName
                )) {
                    throw (
                        'Linux update manifest installer fileName is ' +
                        "required: $platform")
                }
                $artifacts.Add($installer)
            }
        }
        foreach ($artifact in $artifacts) {
            $fileName = [string]$artifact.fileName
            if (
                [string]::IsNullOrWhiteSpace($fileName) -or
                $fileName.IndexOf('/') -ge 0 -or
                $fileName.IndexOf('\') -ge 0 -or
                -not [string]::Equals(
                    [IO.Path]::GetFileName($fileName),
                    $fileName,
                    [StringComparison]::Ordinal)
            ) {
                throw (
                    'Linux update manifest asset fileName is unsafe: ' +
                    $fileName)
            }
            $platformRoot =
                [IO.Path]::GetFullPath(
                    (Join-Path (
                        Join-Path $resolvedUpdatesRoot 'downloads'
                    ) $platform))
            $assetPath =
                [IO.Path]::GetFullPath(
                    (Join-Path $platformRoot $fileName))
            if (-not [string]::Equals(
                [IO.Path]::GetDirectoryName($assetPath),
                $platformRoot,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw 'Linux update manifest asset escaped its platform root.'
            }
            $null =
                Assert-GeoraePlanLinuxRegularDirectoryChain `
                    -Path (Split-Path -Parent $assetPath) `
                    -Label 'Linux update manifest asset parent'
            $assetItem =
                Get-Item -LiteralPath $assetPath -Force -ErrorAction Stop
            [long]$expectedSize = -1
            $expectedHash = ([string]$artifact.sha256).Trim()
            if (
                $assetItem.PSIsContainer -or
                ($assetItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                -not [long]::TryParse(
                    [string]$artifact.fileSize,
                    [Globalization.NumberStyles]::None,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$expectedSize) -or
                $expectedSize -lt 0 -or
                $expectedHash -notmatch '^[0-9A-Fa-f]{64}$' -or
                $assetItem.Length -ne $expectedSize
            ) {
                throw (
                    'Linux update manifest asset size/binding is invalid: ' +
                    $assetPath)
            }
            $actualHash =
                Get-GeoraePlanLinuxFileSha256 -Path $assetPath
            if (-not [string]::Equals(
                $actualHash,
                $expectedHash,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw (
                    'Linux update manifest asset SHA256 is invalid: ' +
                    $assetPath)
            }
        }
    }
}

function Copy-GeoraePlanDurableUpdateAssets {
    param(
        [Parameter(Mandatory = $true)][string]$SourceUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$DestinationUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][ref]$DestinationLease,
        [string]$Channel = 'stable',
        [string]$KillPointAfterFirstDirectory = ''
    )

    if ($null -ne $DestinationLease.Value) {
        throw 'Linux update destination lease output must start empty.'
    }
    $resolvedSourceRoot =
        [IO.Path]::GetFullPath($SourceUpdatesRoot)
    $resolvedDestinationRoot =
        [IO.Path]::GetFullPath($DestinationUpdatesRoot)
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $resolvedSourceRoot `
            -Label 'Linux update copy source'
    if (-not (Test-Path -LiteralPath $resolvedSourceRoot -PathType Container)) {
        throw "Linux update copy source was not found: $resolvedSourceRoot"
    }
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $resolvedDestinationRoot `
            -Label 'Linux update copy destination'
    [void][IO.Directory]::CreateDirectory($resolvedDestinationRoot)
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $resolvedDestinationRoot `
            -Label 'Linux update copy destination'
    $sourceDirectoryPaths =
        @(
            Get-GeoraePlanLinuxUpdateCopyDirectoryPaths `
                -SourceUpdatesRoot $resolvedSourceRoot)
    $sourceDirectoryLease = $null
    $destinationDirectoryLease = $null
    try {
        $sourceDirectoryLease =
            Open-GeoraePlanLinuxDirectoryChainLease `
                -DirectoryPaths $sourceDirectoryPaths
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $sourceDirectoryLease
        $verifiedSourceDirectoryPaths =
            @(
                Get-GeoraePlanLinuxUpdateCopyDirectoryPaths `
                    -SourceUpdatesRoot $resolvedSourceRoot)
        Assert-GeoraePlanLinuxDirectoryPathSet `
            -ExpectedPaths $sourceDirectoryPaths `
            -ActualPaths $verifiedSourceDirectoryPaths `
            -Label 'Linux update copy source'

        $destinationDirectoryPaths =
            [Collections.Generic.List[string]]::new()
        $destinationDirectoryPaths.Add($resolvedDestinationRoot)
        foreach ($sourceDirectoryPath in $sourceDirectoryPaths) {
            if ([string]::Equals(
                $sourceDirectoryPath,
                $resolvedSourceRoot,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                continue
            }
            $sourcePrefix =
                $resolvedSourceRoot +
                [IO.Path]::DirectorySeparatorChar
            if (-not $sourceDirectoryPath.StartsWith(
                $sourcePrefix,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw (
                    'Linux update copy source directory escaped its owned ' +
                    "root: $sourceDirectoryPath")
            }
            $relativeDirectory =
                $sourceDirectoryPath.Substring($sourcePrefix.Length)
            $destinationDirectory =
                [IO.Path]::GetFullPath(
                    (Join-Path $resolvedDestinationRoot $relativeDirectory))
            $destinationPrefix =
                $resolvedDestinationRoot +
                [IO.Path]::DirectorySeparatorChar
            if (-not $destinationDirectory.StartsWith(
                $destinationPrefix,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw (
                    'Linux update copy destination directory escaped its ' +
                    "owned root: $destinationDirectory")
            }
            [void][IO.Directory]::CreateDirectory($destinationDirectory)
            $destinationDirectoryPaths.Add($destinationDirectory)
        }
        $destinationDirectoryLease =
            Open-GeoraePlanLinuxDirectoryChainLease `
                -DirectoryPaths $destinationDirectoryPaths.ToArray()
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $destinationDirectoryLease

        $copiedDirectoryCount = 0
        foreach ($directoryName in @('manifest', 'downloads')) {
            $sourceDirectory =
                Join-Path $resolvedSourceRoot $directoryName
            if (Test-Path -LiteralPath $sourceDirectory) {
                Assert-GeoraePlanLinuxDirectoryChainLease `
                    -Lease $sourceDirectoryLease
                Assert-GeoraePlanLinuxDirectoryChainLease `
                    -Lease $destinationDirectoryLease
                $sourceCategoryPrefix =
                    [IO.Path]::GetFullPath($sourceDirectory) +
                    [IO.Path]::DirectorySeparatorChar
                foreach ($currentSourceDirectory in @(
                    $sourceDirectoryPaths |
                        Where-Object {
                            [string]::Equals(
                                $_,
                                [IO.Path]::GetFullPath($sourceDirectory),
                                [StringComparison]::OrdinalIgnoreCase) -or
                            $_.StartsWith(
                                $sourceCategoryPrefix,
                                [StringComparison]::OrdinalIgnoreCase)
                        } |
                        Sort-Object
                )) {
                    Assert-GeoraePlanLinuxDirectoryChainLease `
                        -Lease $sourceDirectoryLease
                    Assert-GeoraePlanLinuxDirectoryChainLease `
                        -Lease $destinationDirectoryLease
                    $sourcePrefix =
                        $resolvedSourceRoot +
                        [IO.Path]::DirectorySeparatorChar
                    $relativeDirectory =
                        $currentSourceDirectory.Substring(
                            $sourcePrefix.Length)
                    $currentDestinationDirectory =
                        [IO.Path]::GetFullPath(
                            (Join-Path `
                                $resolvedDestinationRoot `
                                $relativeDirectory))
                    foreach ($sourceFile in @(
                        Get-ChildItem `
                            -LiteralPath $currentSourceDirectory `
                            -File `
                            -Force `
                            -ErrorAction Stop
                    )) {
                        if (($sourceFile.Attributes -band
                            [IO.FileAttributes]::ReparsePoint) -ne 0
                        ) {
                            throw (
                                'Linux update copy source contains a reparse ' +
                                "file: $($sourceFile.FullName)")
                        }
                        $destinationFile =
                            Join-Path `
                                $currentDestinationDirectory `
                                $sourceFile.Name
                        $sourceStream = $null
                        $destinationStream = $null
                        try {
                            $sourceStream = [IO.File]::Open(
                                $sourceFile.FullName,
                                [IO.FileMode]::Open,
                                [IO.FileAccess]::Read,
                                [IO.FileShare]::Read)
                            $destinationStream = [IO.FileStream]::new(
                                $destinationFile,
                                [IO.FileMode]::CreateNew,
                                [IO.FileAccess]::Write,
                                [IO.FileShare]::None,
                                81920,
                                [IO.FileOptions]::WriteThrough)
                            $sourceStream.CopyTo($destinationStream)
                            $destinationStream.Flush($true)
                        }
                        finally {
                            if ($null -ne $destinationStream) {
                                $destinationStream.Dispose()
                            }
                            if ($null -ne $sourceStream) {
                                $sourceStream.Dispose()
                            }
                        }
                    }
                }
                $copiedDirectoryCount++
                if (
                    $copiedDirectoryCount -eq 1 -and
                    -not [string]::IsNullOrWhiteSpace(
                        $KillPointAfterFirstDirectory)
                ) {
                    Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
                        -Name $KillPointAfterFirstDirectory
                }
            }
        }
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $sourceDirectoryLease
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $destinationDirectoryLease
        $finalSourceDirectoryPaths =
            @(
                Get-GeoraePlanLinuxUpdateCopyDirectoryPaths `
                    -SourceUpdatesRoot $resolvedSourceRoot)
        Assert-GeoraePlanLinuxDirectoryPathSet `
            -ExpectedPaths $sourceDirectoryPaths `
            -ActualPaths $finalSourceDirectoryPaths `
            -Label 'Linux update copy source'
        $finalDestinationDirectoryPaths =
            @(
                Get-GeoraePlanLinuxUpdateCopyDirectoryPaths `
                    -SourceUpdatesRoot $resolvedDestinationRoot)
        Assert-GeoraePlanLinuxDirectoryPathSet `
            -ExpectedPaths $destinationDirectoryPaths.ToArray() `
            -ActualPaths $finalDestinationDirectoryPaths `
            -Label 'Linux update copy destination'
        Assert-GeoraePlanLinuxManifestReferencedAssets `
            -UpdatesRoot $resolvedDestinationRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $destinationDirectoryLease
        $DestinationLease.Value = $destinationDirectoryLease
        $destinationDirectoryLease = $null
    }
    finally {
        if ($null -ne $destinationDirectoryLease) {
            $destinationDirectoryLease.Dispose()
        }
        if ($null -ne $sourceDirectoryLease) {
            $sourceDirectoryLease.Dispose()
        }
    }
}

function Copy-GeoraePlanUpdateEvidenceFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][long]$ExpectedFileSize
    )

    $sourceItem =
        Get-Item -LiteralPath $SourcePath -Force -ErrorAction Stop
    $sourceHash = Get-GeoraePlanLinuxFileSha256 -Path $SourcePath
    if (
        $sourceItem.PSIsContainer -or
        $sourceItem.Length -ne $ExpectedFileSize -or
        -not [string]::Equals(
            $sourceHash,
            $ExpectedSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Update generation source evidence hash/size is invalid.'
    }
    $targetDirectory = Split-Path -Parent $TargetPath
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
        $targetItem =
            Get-Item -LiteralPath $TargetPath -Force -ErrorAction Stop
        $targetHash = Get-GeoraePlanLinuxFileSha256 -Path $TargetPath
        if (
            $targetItem.Length -ne $ExpectedFileSize -or
            -not [string]::Equals(
                $targetHash,
                $ExpectedSha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'Immutable update generation target already exists with ' +
                'different evidence.')
        }
        return
    }
    $temporaryPath = Join-Path $targetDirectory (
        '.' + [IO.Path]::GetFileName($TargetPath) + '.' +
        [Guid]::NewGuid().ToString('N') + '.pending')
    $sourceStream = $null
    $targetStream = $null
    try {
        $sourceStream = [IO.File]::Open(
            $SourcePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $targetStream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            81920,
            [IO.FileOptions]::WriteThrough)
        $sourceStream.CopyTo($targetStream)
        $targetStream.Flush($true)
    }
    finally {
        if ($null -ne $targetStream) {
            $targetStream.Dispose()
        }
        if ($null -ne $sourceStream) {
            $sourceStream.Dispose()
        }
    }
    try {
        $temporaryItem =
            Get-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop
        $temporaryHash =
            Get-GeoraePlanLinuxFileSha256 -Path $temporaryPath
        if (
            $temporaryItem.PSIsContainer -or
            ($temporaryItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $temporaryItem.Length -ne $ExpectedFileSize -or
            -not [string]::Equals(
                $temporaryHash,
                $ExpectedSha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'Copied update evidence hash/size does not match the ' +
                'verified source contract.')
        }
        [IO.File]::Move($temporaryPath, $TargetPath)
    }
    finally {
        Remove-Item `
            -LiteralPath $temporaryPath `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

function Get-GeoraePlanUpdatePointerEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$UpdatesRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$Channel = 'stable',
        [switch]$AllowMissing
    )

    $manifestRoot = Join-Path $UpdatesRoot 'manifest'
    $pointerPath = Join-Path $manifestRoot ($Channel + '.current.json')
    $pointerItem =
        Get-GeoraePlanLinuxRegularPointerItem `
            -Path $pointerPath `
            -Label 'Update manifest pointer' `
            -AllowMissing:$AllowMissing
    if ($null -eq $pointerItem) {
        if ($AllowMissing) {
            return $null
        }
        throw "Update manifest pointer is missing: $pointerPath"
    }
    $pointer = Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $expectedProperties = @(
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
    $actualProperties = @(
        $pointer.PSObject.Properties | ForEach-Object { $_.Name })
    [long]$runtimeSize = -1
    [long]$deliverySize = -1
    if (
        $actualProperties.Count -ne $expectedProperties.Count -or
        @($actualProperties | Where-Object {
            $_ -notin $expectedProperties
        }).Count -ne 0 -or
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
        [string]$pointer.manifestSha256 -notmatch
            '^[0-9A-Fa-f]{64}$' -or
        [string]$pointer.deliveryManifestSha256 -notmatch
            '^[0-9A-Fa-f]{64}$' -or
        [string]::IsNullOrWhiteSpace(
            [string]$pointer.deliveryManifestPath) -or
        -not [long]::TryParse(
            [string]$pointer.manifestFileSize,
            [ref]$runtimeSize) -or
        -not [long]::TryParse(
            [string]$pointer.deliveryManifestFileSize,
            [ref]$deliverySize) -or
        $runtimeSize -lt 0 -or
        $runtimeSize -ne $deliverySize -or
        -not [string]::Equals(
            [string]$pointer.manifestSha256,
            [string]$pointer.deliveryManifestSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Update manifest pointer evidence is invalid.'
    }
    $generationId = [string]$pointer.generationId
    $expectedRelativePath =
        'generations/{0}/{1}.json' -f $Channel, $generationId
    if (-not [string]::Equals(
        [string]$pointer.manifestRelativePath,
        $expectedRelativePath,
        [StringComparison]::Ordinal
    )) {
        throw 'Update manifest pointer runtime path is not canonical.'
    }
    $runtimePath = Join-Path (
        Join-Path (
            Join-Path $manifestRoot 'generations'
        ) $Channel
    ) ($generationId + '.json')
    $runtimeItem =
        Get-Item -LiteralPath $runtimePath -Force -ErrorAction Stop
    $runtimeHash = Get-GeoraePlanLinuxFileSha256 -Path $runtimePath
    if (
        $runtimeItem.PSIsContainer -or
        $runtimeItem.Length -ne $runtimeSize -or
        -not [string]::Equals(
            $runtimeHash,
            [string]$pointer.manifestSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Selected runtime manifest generation evidence is invalid.'
    }
    $manifest =
        Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    if (
        -not [string]::Equals(
            [string]$manifest.generationId,
            $generationId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$manifest.channel,
            $Channel,
            [StringComparison]::Ordinal)
    ) {
        throw 'Selected runtime manifest generation binding is invalid.'
    }
    $stagedDeliveryPath = Join-Path (
        Join-Path (
            Join-Path $manifestRoot 'delivery-generations'
        ) $Channel
    ) ($generationId + '.json')
    $expectedDeliveryPath = Join-Path (
        Join-Path (
            Join-Path $ProjectRoot (
                '배포\.georaeplan-release-generations')
        ) $Channel
    ) ($generationId + '.json')
    return [pscustomobject]@{
        Pointer = $pointer
        PointerPath = [IO.Path]::GetFullPath($pointerPath)
        PointerSha256 =
            Get-GeoraePlanLinuxFileSha256 -Path $pointerPath
        GenerationId = $generationId
        RuntimePath = [IO.Path]::GetFullPath($runtimePath)
        StagedDeliveryPath =
            [IO.Path]::GetFullPath($stagedDeliveryPath)
        ExpectedDeliveryPath =
            [IO.Path]::GetFullPath($expectedDeliveryPath)
        Sha256 = [string]$pointer.manifestSha256
        FileSize = $runtimeSize
    }
}

function Write-GeoraePlanUpdatePointerDeliveryPath {
    param(
        [Parameter(Mandatory = $true)]$Evidence
    )

    $Evidence.Pointer.deliveryManifestPath =
        $Evidence.ExpectedDeliveryPath
    $json = $Evidence.Pointer | ConvertTo-Json -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $directory = Split-Path -Parent $Evidence.PointerPath
    $temporaryPath = Join-Path $directory (
        '.pointer.' + [Guid]::NewGuid().ToString('N') + '.pending')
    $backupPath = Join-Path $directory (
        '.pointer.' + [Guid]::NewGuid().ToString('N') + '.backup')
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
        $Evidence.PointerPath,
        $backupPath,
        $true)
    Remove-Item -LiteralPath $backupPath -Force -ErrorAction Stop
}

function Initialize-GeoraePlanDurablePointerDeliveryEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$UpdatesRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$Channel = 'stable'
    )

    $evidence =
        Get-GeoraePlanUpdatePointerEvidence `
            -UpdatesRoot $UpdatesRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel `
            -AllowMissing
    if ($null -eq $evidence) {
        return $null
    }
    $deliverySource = if (
        Test-Path -LiteralPath $evidence.StagedDeliveryPath -PathType Leaf
    ) {
        $evidence.StagedDeliveryPath
    }
    elseif (
        Test-Path `
            -LiteralPath ([string]$evidence.Pointer.deliveryManifestPath) `
            -PathType Leaf
    ) {
        [string]$evidence.Pointer.deliveryManifestPath
    }
    else {
        $evidence.RuntimePath
    }
    Copy-GeoraePlanUpdateEvidenceFileAtomically `
        -SourcePath $deliverySource `
        -TargetPath $evidence.ExpectedDeliveryPath `
        -ExpectedSha256 $evidence.Sha256 `
        -ExpectedFileSize $evidence.FileSize
    Copy-GeoraePlanUpdateEvidenceFileAtomically `
        -SourcePath $evidence.ExpectedDeliveryPath `
        -TargetPath $evidence.StagedDeliveryPath `
        -ExpectedSha256 $evidence.Sha256 `
        -ExpectedFileSize $evidence.FileSize
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath(
            [string]$evidence.Pointer.deliveryManifestPath),
        $evidence.ExpectedDeliveryPath,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        Write-GeoraePlanUpdatePointerDeliveryPath -Evidence $evidence
    }
    return Get-GeoraePlanUpdatePointerEvidence `
        -UpdatesRoot $UpdatesRoot `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel
}

function Sync-GeoraePlanDurablePointerDeliveryEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$UpdatesRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$Channel = 'stable'
    )

    $evidence =
        Get-GeoraePlanUpdatePointerEvidence `
            -UpdatesRoot $UpdatesRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath(
            [string]$evidence.Pointer.deliveryManifestPath),
        $evidence.ExpectedDeliveryPath,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Published delivery generation path is not locally canonical.'
    }
    Copy-GeoraePlanUpdateEvidenceFileAtomically `
        -SourcePath $evidence.ExpectedDeliveryPath `
        -TargetPath $evidence.StagedDeliveryPath `
        -ExpectedSha256 $evidence.Sha256 `
        -ExpectedFileSize $evidence.FileSize
    return $evidence
}

function Remove-GeoraePlanLinuxUpdateOwnerMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Preparation', 'Cleanup')]
        [string]$Kind
    )

    $paths =
        Get-GeoraePlanLinuxUpdateOwnerMetadataPaths `
            -ProjectRoot $ProjectRoot `
            -Kind $Kind
    foreach ($path in @(
        $paths.PendingPath,
        $paths.BackupPath,
        $paths.MetadataPath
    )) {
        Remove-Item `
            -LiteralPath $path `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

function Resume-GeoraePlanDurableUpdatePreparation {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [string]$Channel = 'stable'
    )

    $paths =
        Get-GeoraePlanLinuxUpdateOwnerMetadataPaths `
            -ProjectRoot $ProjectRoot `
            -Kind 'Preparation'
    foreach ($ownedPath in @($paths.WorkRoot, $DurableUpdatesRoot)) {
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $ownedPath `
                -Label 'Durable Linux update preparation root'
    }
    $owner =
        Resume-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $DurableUpdatesRoot `
            -Kind 'Preparation' `
            -Channel $Channel
    $hasPreparationRoot = Test-Path -LiteralPath $paths.WorkRoot
    $hasDurableRoot = Test-Path -LiteralPath $DurableUpdatesRoot
    if ($null -eq $owner) {
        if ($hasPreparationRoot) {
            throw (
                'Durable Linux update preparation root exists without its ' +
                'external owner metadata.')
        }
        return $null
    }
    if ($hasPreparationRoot -and $hasDurableRoot) {
        throw (
            'Durable Linux update preparation has both staged and promoted ' +
            'roots; preserving both.')
    }
    if ([string]$owner.phase -eq 'Preparing') {
        if ($hasDurableRoot) {
            throw (
                'Preparing Linux update owner cannot bind a promoted root.')
        }
        if ($hasPreparationRoot) {
            Remove-GeoraePlanLinuxOwnedDirectoryTree `
                -DirectoryPath $paths.WorkRoot `
                -ExpectedPath $paths.WorkRoot
        }
        Remove-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -Kind 'Preparation'
        return $null
    }

    if (-not $hasPreparationRoot -and -not $hasDurableRoot) {
        throw (
            'Ready Linux update preparation owner has no owned root.')
    }
    $candidateRoot = if ($hasPreparationRoot) {
        $paths.WorkRoot
    }
    else {
        [IO.Path]::GetFullPath($DurableUpdatesRoot)
    }
    $candidateDirectoryLease = $null
    try {
        $candidateDirectoryPaths =
            @(
                Get-GeoraePlanLinuxUpdateCopyDirectoryPaths `
                    -SourceUpdatesRoot $candidateRoot)
        $candidateDirectoryLease =
            Open-GeoraePlanLinuxDirectoryChainLease `
                -DirectoryPaths $candidateDirectoryPaths
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $candidateDirectoryLease
        Assert-GeoraePlanLinuxManifestReferencedAssets `
            -UpdatesRoot $candidateRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        $candidatePointerSha256 =
            Get-GeoraePlanDurableUpdatePointerSha256 `
                -DurableUpdatesRoot $candidateRoot `
                -Channel $Channel
        if (-not [string]::Equals(
            $candidatePointerSha256,
            [string]$owner.seedPointerSha256,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw (
                'Ready Linux update preparation pointer does not match its ' +
                'external owner metadata.')
        }
        if ($hasPreparationRoot) {
            Assert-GeoraePlanLinuxDirectoryChainLease `
                -Lease $candidateDirectoryLease
            $null =
                Assert-GeoraePlanLinuxRegularDirectoryChain `
                    -Path $paths.WorkRoot `
                    -Label (
                        'Durable Linux update preparation promotion source')
            $null =
                Assert-GeoraePlanLinuxRegularDirectoryChain `
                    -Path $DurableUpdatesRoot `
                    -Label (
                        'Durable Linux update preparation promotion target')
            $candidateDirectoryLease.Dispose()
            $candidateDirectoryLease = $null
            [IO.Directory]::Move(
                $paths.WorkRoot,
                [IO.Path]::GetFullPath($DurableUpdatesRoot))
            $candidateRoot = [IO.Path]::GetFullPath($DurableUpdatesRoot)
            $promotedDirectoryPaths =
                @(
                    Get-GeoraePlanLinuxUpdateCopyDirectoryPaths `
                        -SourceUpdatesRoot $candidateRoot)
            $candidateDirectoryLease =
                Open-GeoraePlanLinuxDirectoryChainLease `
                    -DirectoryPaths $promotedDirectoryPaths
            Assert-GeoraePlanLinuxDirectoryChainLease `
                -Lease $candidateDirectoryLease
            Assert-GeoraePlanLinuxManifestReferencedAssets `
                -UpdatesRoot $candidateRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
            $promotedPointerSha256 =
                Get-GeoraePlanDurableUpdatePointerSha256 `
                    -DurableUpdatesRoot $candidateRoot `
                    -Channel $Channel
            if (-not [string]::Equals(
                $promotedPointerSha256,
                [string]$owner.seedPointerSha256,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw (
                    'Promoted Linux update preparation pointer does not ' +
                    'match its owner evidence.')
            }
            Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
                -Name 'AfterPreparationPromoteBeforeSeededState'
        }
        $state =
            Write-GeoraePlanDurableUpdateWrapperState `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Phase 'Seeded' `
                -SeedPointerSha256 (
                    [string]$owner.seedPointerSha256) `
                -Channel $Channel
        Remove-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -Kind 'Preparation'
        Write-Host (
            'linux_update_preparation_recovered=Seeded ' +
            "pointer_sha256=$($state.seedPointerSha256)")
        return $state
    }
    finally {
        if ($null -ne $candidateDirectoryLease) {
            $candidateDirectoryLease.Dispose()
        }
    }
}

function New-GeoraePlanDurableUpdatePreparation {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$SourceUpdatesRoot,
        [string]$Channel = 'stable'
    )

    $paths =
        Get-GeoraePlanLinuxUpdateOwnerMetadataPaths `
            -ProjectRoot $ProjectRoot `
            -Kind 'Preparation'
    foreach ($ownedPath in @($paths.WorkRoot, $DurableUpdatesRoot)) {
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $ownedPath `
                -Label 'New durable Linux update preparation root'
    }
    if (
        (Test-Path -LiteralPath $DurableUpdatesRoot) -or
        (Test-Path -LiteralPath $paths.WorkRoot)
    ) {
        throw 'New durable Linux update preparation roots are not empty.'
    }
    $null =
        Write-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $DurableUpdatesRoot `
            -Kind 'Preparation' `
            -Phase 'Preparing' `
            -Channel $Channel
    Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
        -Name 'AfterPreparationOwnerBeforeCopy'
    $preparationDirectoryLease = $null
    $promotedDirectoryLease = $null
    try {
        Copy-GeoraePlanDurableUpdateAssets `
            -SourceUpdatesRoot $SourceUpdatesRoot `
            -DestinationUpdatesRoot $paths.WorkRoot `
            -ProjectRoot $ProjectRoot `
            -DestinationLease ([ref]$preparationDirectoryLease) `
            -Channel $Channel `
            -KillPointAfterFirstDirectory 'DuringInitialBaselineCopy'
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $preparationDirectoryLease
        $null =
            Initialize-GeoraePlanDurablePointerDeliveryEvidence `
                -UpdatesRoot $paths.WorkRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
        Assert-GeoraePlanLinuxManifestReferencedAssets `
            -UpdatesRoot $paths.WorkRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $preparationDirectoryLease
        $seedPointerSha256 =
            Get-GeoraePlanDurableUpdatePointerSha256 `
                -DurableUpdatesRoot $paths.WorkRoot `
                -Channel $Channel
        $null =
            Write-GeoraePlanLinuxUpdateOwnerMetadata `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Kind 'Preparation' `
                -Phase 'Ready' `
                -SeedPointerSha256 $seedPointerSha256 `
                -Channel $Channel
        Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
            -Name 'AfterPreparationReadyBeforePromote'
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $preparationDirectoryLease
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $paths.WorkRoot `
                -Label 'Durable Linux update preparation promotion source'
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $DurableUpdatesRoot `
                -Label 'Durable Linux update preparation promotion target'
        $preparationDirectoryLease.Dispose()
        $preparationDirectoryLease = $null
        [IO.Directory]::Move(
            $paths.WorkRoot,
            [IO.Path]::GetFullPath($DurableUpdatesRoot))
        $promotedDirectoryPaths =
            @(
                Get-GeoraePlanLinuxUpdateCopyDirectoryPaths `
                    -SourceUpdatesRoot $DurableUpdatesRoot)
        $promotedDirectoryLease =
            Open-GeoraePlanLinuxDirectoryChainLease `
                -DirectoryPaths $promotedDirectoryPaths
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $promotedDirectoryLease
        Assert-GeoraePlanLinuxManifestReferencedAssets `
            -UpdatesRoot $DurableUpdatesRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        $promotedPointerSha256 =
            Get-GeoraePlanDurableUpdatePointerSha256 `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Channel $Channel
        if (-not [string]::Equals(
            $promotedPointerSha256,
            $seedPointerSha256,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw (
                'Promoted Linux update root does not match its Ready ' +
                'pointer evidence.')
        }
        Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
            -Name 'AfterPreparationPromoteBeforeSeededState'
        $state =
            Write-GeoraePlanDurableUpdateWrapperState `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Phase 'Seeded' `
                -SeedPointerSha256 $seedPointerSha256 `
                -Channel $Channel
        Remove-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -Kind 'Preparation'
        return $state
    }
    finally {
        if ($null -ne $promotedDirectoryLease) {
            $promotedDirectoryLease.Dispose()
        }
        if ($null -ne $preparationDirectoryLease) {
            $preparationDirectoryLease.Dispose()
        }
    }
}

function Resume-GeoraePlanDurableUpdateCleanup {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [string]$Channel = 'stable'
    )

    $paths =
        Get-GeoraePlanLinuxUpdateOwnerMetadataPaths `
            -ProjectRoot $ProjectRoot `
            -Kind 'Cleanup'
    foreach ($ownedPath in @($DurableUpdatesRoot, $paths.WorkRoot)) {
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $ownedPath `
                -Label 'Durable Linux update cleanup root'
    }
    $owner =
        Resume-GeoraePlanLinuxUpdateOwnerMetadata `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $DurableUpdatesRoot `
            -Kind 'Cleanup' `
            -Channel $Channel
    $hasDurableRoot = Test-Path -LiteralPath $DurableUpdatesRoot
    $hasCleanupRoot = Test-Path -LiteralPath $paths.WorkRoot
    if ($null -eq $owner) {
        if ($hasCleanupRoot) {
            throw (
                'Durable Linux update cleanup tombstone exists without its ' +
                'external owner metadata.')
        }
        return
    }
    if ($hasDurableRoot -and $hasCleanupRoot) {
        throw (
            'Durable Linux update cleanup has both source and tombstone roots.')
    }
    if ($hasDurableRoot) {
        $state =
            Resume-GeoraePlanDurableUpdateWrapperState `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Channel $Channel
        $durablePointerSha256 =
            Get-GeoraePlanDurableUpdatePointerSha256 `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -Channel $Channel
        $copiedPointerSha256 =
            Get-GeoraePlanDurableUpdatePointerSha256 `
                -DurableUpdatesRoot (
                    Join-Path `
                        ([IO.Path]::GetFullPath(
                            [string]$owner.copiedPublishRoot)) `
                        'updates') `
                -Channel $Channel
        if (
            $null -eq $state -or
            [string]$state.phase -ne 'Copied' -or
            -not [string]::Equals(
                $durablePointerSha256,
                [string]$owner.publishedPointerSha256,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                $copiedPointerSha256,
                [string]$owner.publishedPointerSha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'Durable Linux update cleanup owner evidence is not settled.')
        }
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $DurableUpdatesRoot `
                -Label 'Durable Linux update cleanup move source'
        $null =
            Assert-GeoraePlanLinuxRegularDirectoryChain `
                -Path $paths.WorkRoot `
                -Label 'Durable Linux update cleanup move target'
        [IO.Directory]::Move(
            [IO.Path]::GetFullPath($DurableUpdatesRoot),
            $paths.WorkRoot)
        $hasCleanupRoot = $true
        Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
            -Name 'AfterDurableCleanupMove'
    }
    if ($hasCleanupRoot) {
        Remove-GeoraePlanLinuxOwnedDirectoryTree `
            -DirectoryPath $paths.WorkRoot `
            -ExpectedPath $paths.WorkRoot `
            -KillPoint 'DuringDurableCleanupDelete'
    }
    Remove-GeoraePlanLinuxUpdateOwnerMetadata `
        -ProjectRoot $ProjectRoot `
        -Kind 'Cleanup'
    Write-Host 'linux_update_cleanup_recovered=complete'
}

function Invoke-GeoraePlanDurableUpdateAssetPublish {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$UpdateAssetScript,
        [hashtable]$UpdateAssetArguments = @{},
        [string]$Channel = 'stable'
    )

    if ($Channel -cne 'stable') {
        throw 'Linux release durable update publishing supports stable only.'
    }
    $ownedRoot =
        Assert-GeoraePlanDurableUpdateWorkRoot `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $DurableUpdatesRoot
    $resolvedPublishRoot = [IO.Path]::GetFullPath($PublishRoot)
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $resolvedPublishRoot `
            -Label 'Per-release publish root'
    if (-not (Test-Path `
        -LiteralPath $resolvedPublishRoot `
        -PathType Container
    )) {
        throw "Per-release publish root was not found: $resolvedPublishRoot"
    }
    $publishUpdatesRoot =
        [IO.Path]::GetFullPath((Join-Path $resolvedPublishRoot 'updates'))
    if ([string]::Equals(
        $publishUpdatesRoot,
        $ownedRoot,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'Per-release updates root cannot be the durable recovery root.'
    }
    if (-not (Test-Path -LiteralPath $UpdateAssetScript -PathType Leaf)) {
        throw "Update asset publish script was not found: $UpdateAssetScript"
    }

    $releaseTempRoot =
        [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'release-temp'))
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $releaseTempRoot `
            -Label 'Linux release-temp root'
    [void][IO.Directory]::CreateDirectory($releaseTempRoot)
    $directoryLease = $null
    $lease = $null
    $publishUpdatesDirectoryLease = $null
    try {
        $directoryLease =
            Open-GeoraePlanLinuxDirectoryChainLease `
                -DirectoryPaths @(
                    $ProjectRoot,
                    $releaseTempRoot,
                    $resolvedPublishRoot)
        Assert-GeoraePlanLinuxDirectoryChainLease -Lease $directoryLease
        $lease =
            Open-GeoraePlanDurableUpdateWorkLease -ProjectRoot $ProjectRoot
        Assert-GeoraePlanLinuxDirectoryChainLease -Lease $directoryLease
        Resume-GeoraePlanDurableUpdateCleanup `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $ownedRoot `
            -Channel $Channel
        $state =
            Resume-GeoraePlanDurableUpdatePreparation `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $ownedRoot `
                -Channel $Channel
        if (-not (Test-Path -LiteralPath $ownedRoot)) {
            $state =
                New-GeoraePlanDurableUpdatePreparation `
                    -ProjectRoot $ProjectRoot `
                    -DurableUpdatesRoot $ownedRoot `
                    -SourceUpdatesRoot $publishUpdatesRoot `
                    -Channel $Channel
        }
        elseif ($null -eq $state) {
            $state =
                Resume-GeoraePlanDurableUpdateWrapperState `
                    -ProjectRoot $ProjectRoot `
                    -DurableUpdatesRoot $ownedRoot `
                    -Channel $Channel
            if ($null -eq $state) {
                throw (
                    'Durable Linux update root exists without its wrapper ' +
                    'state; preserving it for inspection.')
            }
            Write-Host (
                'linux_update_recovery_root=reused ' +
                "path=$ownedRoot channel=$Channel phase=$($state.phase)")
            $null =
                Initialize-GeoraePlanDurablePointerDeliveryEvidence `
                    -UpdatesRoot $ownedRoot `
                    -ProjectRoot $ProjectRoot `
                    -Channel $Channel
        }

        $hasPendingEvidence =
            Test-GeoraePlanDurableUpdateTransactionEvidence `
                -DurableUpdatesRoot $ownedRoot `
                -Channel $Channel
        $shouldPublish = $false
        if ([string]$state.phase -eq 'Seeded') {
            $currentPointerSha256 =
                Get-GeoraePlanDurableUpdatePointerSha256 `
                    -DurableUpdatesRoot $ownedRoot `
                    -Channel $Channel
            if (
                -not $hasPendingEvidence -and
                -not [string]::IsNullOrWhiteSpace(
                    $currentPointerSha256) -and
                -not [string]::Equals(
                    $currentPointerSha256,
                    [string]$state.seedPointerSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                $publishedEvidence =
                    Sync-GeoraePlanDurablePointerDeliveryEvidence `
                        -UpdatesRoot $ownedRoot `
                        -ProjectRoot $ProjectRoot `
                        -Channel $Channel
                $state =
                    Write-GeoraePlanDurableUpdateWrapperState `
                        -ProjectRoot $ProjectRoot `
                        -DurableUpdatesRoot $ownedRoot `
                        -Phase 'Published' `
                        -SeedPointerSha256 (
                            [string]$state.seedPointerSha256) `
                        -PublishedPointerSha256 (
                            [string]$publishedEvidence.PointerSha256) `
                        -Channel $Channel
                Write-Host (
                    'linux_update_wrapper_recovered=Published ' +
                    "generation=$($publishedEvidence.GenerationId)")
            }
            else {
                $shouldPublish = $true
            }
        }
        elseif ($hasPendingEvidence) {
            throw (
                'Published/Copied Linux update wrapper state has pending ' +
                'publisher transaction evidence.')
        }

        if ($shouldPublish) {
            Assert-GeoraePlanLinuxDirectoryChainLease -Lease $directoryLease
            $publishArguments = @{}
            foreach ($key in $UpdateAssetArguments.Keys) {
                if ($key -in @(
                    'ProjectRoot',
                    'OutputRoot',
                    'Channel'
                )) {
                    throw (
                        'Durable update publish arguments cannot override ' +
                        "the owned $key binding.")
                }
                $publishArguments[$key] = $UpdateAssetArguments[$key]
            }
            $publishArguments.ProjectRoot = $ProjectRoot
            $publishArguments.OutputRoot = $ownedRoot
            $publishArguments.Channel = $Channel
            & $UpdateAssetScript @publishArguments
            Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
                -Name 'AfterPublisherBeforePublishedState'
            if (Test-GeoraePlanDurableUpdateTransactionEvidence `
                -DurableUpdatesRoot $ownedRoot `
                -Channel $Channel
            ) {
                throw (
                    'Update asset publisher returned with pending durable ' +
                    'transaction evidence.')
            }
            $publishedEvidence =
                Sync-GeoraePlanDurablePointerDeliveryEvidence `
                    -UpdatesRoot $ownedRoot `
                    -ProjectRoot $ProjectRoot `
                    -Channel $Channel
            $state =
                Write-GeoraePlanDurableUpdateWrapperState `
                    -ProjectRoot $ProjectRoot `
                    -DurableUpdatesRoot $ownedRoot `
                    -Phase 'Published' `
                    -SeedPointerSha256 (
                        [string]$state.seedPointerSha256) `
                    -PublishedPointerSha256 (
                        [string]$publishedEvidence.PointerSha256) `
                    -Channel $Channel
        }
        else {
            $currentPointerSha256 =
                Get-GeoraePlanDurableUpdatePointerSha256 `
                    -DurableUpdatesRoot $ownedRoot `
                    -Channel $Channel
            if (-not [string]::Equals(
                $currentPointerSha256,
                [string]$state.publishedPointerSha256,
                [StringComparison]::OrdinalIgnoreCase
            )) {
                throw (
                    'Durable Published pointer no longer matches wrapper ' +
                    'state.')
            }
        }

        Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
            -Name 'AfterPublishedStateBeforeCopy'
        Assert-GeoraePlanLinuxDirectoryChainLease -Lease $directoryLease
        if (Test-Path -LiteralPath $publishUpdatesRoot) {
            Remove-GeoraePlanLinuxOwnedDirectoryTree `
                -DirectoryPath $publishUpdatesRoot `
                -ExpectedPath $publishUpdatesRoot
        }
        Copy-GeoraePlanDurableUpdateAssets `
            -SourceUpdatesRoot $ownedRoot `
            -DestinationUpdatesRoot $publishUpdatesRoot `
            -ProjectRoot $ProjectRoot `
            -DestinationLease ([ref]$publishUpdatesDirectoryLease) `
            -Channel $Channel
        Assert-GeoraePlanLinuxDirectoryChainLease `
            -Lease $publishUpdatesDirectoryLease
        Assert-GeoraePlanLinuxManifestReferencedAssets `
            -UpdatesRoot $publishUpdatesRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
        $copiedPointerSha256 =
            Get-GeoraePlanDurableUpdatePointerSha256 `
                -DurableUpdatesRoot $publishUpdatesRoot `
                -Channel $Channel
        if (-not [string]::Equals(
            $copiedPointerSha256,
            [string]$state.publishedPointerSha256,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw 'Per-release copy does not match the Published pointer.'
        }
        Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
            -Name 'AfterDurableCopyBeforeCopiedState'
        $state =
            Write-GeoraePlanDurableUpdateWrapperState `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $ownedRoot `
                -Phase 'Copied' `
                -SeedPointerSha256 (
                    [string]$state.seedPointerSha256) `
                -PublishedPointerSha256 (
                    [string]$state.publishedPointerSha256) `
                -CopiedPublishRoot $resolvedPublishRoot `
                -Channel $Channel
        Invoke-GeoraePlanLinuxUpdateWrapperTestKillPoint `
            -Name 'AfterCopiedStateBeforeCleanup'
        Write-Host (
            'linux_update_assets_staged=durable-recovery ' +
            "channel=$Channel path=$publishUpdatesRoot")
        Assert-GeoraePlanLinuxDirectoryChainLease -Lease $directoryLease
        Remove-GeoraePlanDurableUpdateWorkRootIfSettled `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $ownedRoot `
            -Channel $Channel
    }
    finally {
        if ($null -ne $publishUpdatesDirectoryLease) {
            $publishUpdatesDirectoryLease.Dispose()
            $publishUpdatesDirectoryLease = $null
        }
        if ($null -ne $lease) {
            $lease.Dispose()
            $lease = $null
        }
        if ($null -ne $directoryLease) {
            $directoryLease.Dispose()
            $directoryLease = $null
        }
    }
}

function Assert-SafeReleaseId {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '^[A-Za-z0-9._-]+$') {
        throw "Invalid release id: $Value"
    }
    $windowsNormalizedValue = $Value.TrimEnd('.')
    if ($windowsNormalizedValue -in @(
        'update-assets-stable',
        'update-assets-stable.wrapper.lock'
    )) {
        throw "Release id is reserved for durable update recovery: $Value"
    }
}

function New-LinuxSshConfig {
    param(
        [Parameter(Mandatory = $true)][string]$HostName,
        [Parameter(Mandatory = $true)][string]$UserName,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$KeyPath,
        [Parameter(Mandatory = $true)][string]$RemoteRoot,
        [Parameter(Mandatory = $true)][string]$RemoteOpsPath
    )

    if ([string]::IsNullOrWhiteSpace($HostName) -or [string]::IsNullOrWhiteSpace($UserName)) {
        throw 'Linux PC SSH host/user is required.'
    }
    if ([string]::IsNullOrWhiteSpace($RemoteRoot) -or [string]::IsNullOrWhiteSpace($RemoteOpsPath)) {
        throw 'Linux PC remote root/ops path is required.'
    }
    if (-not (Test-Path -LiteralPath $KeyPath)) {
        throw "Linux PC SSH key was not found: $KeyPath"
    }

    return [pscustomobject]@{
        Host = $HostName.Trim()
        User = $UserName.Trim()
        Port = $Port
        KeyPath = (Resolve-Path -LiteralPath $KeyPath).Path
        RemoteRoot = $RemoteRoot.TrimEnd('/')
        RemoteOpsPath = $RemoteOpsPath.TrimEnd('/')
    }
}

function New-SshArgumentList {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [switch]$BatchMode
    )

    $args = @(
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'ConnectTimeout=15'
    )

    if ($BatchMode) {
        $args += @('-o', 'BatchMode=yes')
    }
    if ($Config.Port -gt 0) {
        $args += @('-p', $Config.Port.ToString())
    }
    if (-not [string]::IsNullOrWhiteSpace($Config.KeyPath)) {
        $args += @('-i', $Config.KeyPath)
    }

    $args += ('{0}@{1}' -f $Config.User, $Config.Host)
    return $args
}

function Invoke-SshCommand {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [Parameter(Mandatory = $true)][string]$Command,
        [switch]$IgnoreExitCode,
        [switch]$BatchMode
    )

    $sshExe = Resolve-SshExecutable
    $arguments = New-SshArgumentList -Config $Config -BatchMode:$BatchMode
    $arguments += $Command

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($sshExe)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Failed to start Linux PC ssh process.'
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        if (-not $IgnoreExitCode -and $process.ExitCode -ne 0) {
            $message = if ([string]::IsNullOrWhiteSpace($stderr)) { $stdout } else { $stderr }
            throw "Linux PC ssh command failed with exit code $($process.ExitCode): $message"
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
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

    throw 'Unable to resolve a writable temp directory for Linux PC release upload.'
}

function Invoke-SshTarUpload {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$RemoteDirectory,
        [Parameter(Mandatory = $true)]$Config
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        throw "SSH upload source directory not found: $SourceDirectory"
    }

    $tarExe = Resolve-TarExecutable
    $sshExe = Resolve-SshExecutable
    $quotedRemoteDirectory = Convert-ToSingleQuotedShellLiteral -Value $RemoteDirectory
    $remoteCommand = "rm -rf $quotedRemoteDirectory && mkdir -p $quotedRemoteDirectory && tar -xf - -C $quotedRemoteDirectory"

    $tarStartInfo = [System.Diagnostics.ProcessStartInfo]::new($tarExe)
    $tarStartInfo.UseShellExecute = $false
    $tarStartInfo.CreateNoWindow = $true
    $tarStartInfo.RedirectStandardOutput = $true
    $tarStartInfo.RedirectStandardError = $true
    $tarStartInfo.Arguments = (@('-C', $SourceDirectory, '-cf', '-', '.') |
        ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '

    $sshArguments = New-SshArgumentList -Config $Config -BatchMode
    $sshArguments += $remoteCommand
    $sshStartInfo = [System.Diagnostics.ProcessStartInfo]::new($sshExe)
    $sshStartInfo.UseShellExecute = $false
    $sshStartInfo.CreateNoWindow = $true
    $sshStartInfo.RedirectStandardInput = $true
    $sshStartInfo.RedirectStandardOutput = $true
    $sshStartInfo.RedirectStandardError = $true
    $sshStartInfo.Arguments = ($sshArguments |
        ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '

    $tarProcess = [System.Diagnostics.Process]::new()
    $tarProcess.StartInfo = $tarStartInfo
    $sshProcess = [System.Diagnostics.Process]::new()
    $sshProcess.StartInfo = $sshStartInfo
    $tarStarted = $false
    $sshStarted = $false
    $sshInputClosed = $false

    try {
        if (-not $sshProcess.Start()) {
            throw 'Failed to start Linux PC ssh upload process.'
        }
        $sshStarted = $true
        $sshStdoutTask = $sshProcess.StandardOutput.ReadToEndAsync()
        $sshStderrTask = $sshProcess.StandardError.ReadToEndAsync()

        if (-not $tarProcess.Start()) {
            throw 'Failed to start local tar upload process.'
        }
        $tarStarted = $true
        $tarStderrTask = $tarProcess.StandardError.ReadToEndAsync()

        $copyTask = $tarProcess.StandardOutput.BaseStream.CopyToAsync($sshProcess.StandardInput.BaseStream)
        $copyError = $null
        try {
            $null = $copyTask.GetAwaiter().GetResult()
        }
        catch {
            $copyError = $_
            if (-not $tarProcess.HasExited) {
                $tarProcess.Kill()
            }
        }
        finally {
            $sshProcess.StandardInput.Close()
            $sshInputClosed = $true
        }

        $tarProcess.WaitForExit()
        $sshProcess.WaitForExit()
        $tarStderr = $tarStderrTask.GetAwaiter().GetResult()
        $sshStdout = $sshStdoutTask.GetAwaiter().GetResult()
        $sshStderr = $sshStderrTask.GetAwaiter().GetResult()

        if ($null -ne $copyError) {
            throw "Linux PC ssh upload stream failed: $($copyError.Exception.Message) tar_stderr=$tarStderr ssh_stderr=$sshStderr"
        }
        if ($tarProcess.ExitCode -ne 0) {
            throw "Local tar upload failed with exit code $($tarProcess.ExitCode): $tarStderr"
        }
        if ($sshProcess.ExitCode -ne 0) {
            $message = if ([string]::IsNullOrWhiteSpace($sshStderr)) { $sshStdout } else { $sshStderr }
            throw "Linux PC ssh upload failed with exit code $($sshProcess.ExitCode): $message"
        }
    }
    finally {
        if ($sshStarted -and -not $sshInputClosed) {
            try {
                $sshProcess.StandardInput.Close()
            }
            catch {
            }
        }
        if ($tarStarted -and -not $tarProcess.HasExited) {
            try {
                $tarProcess.Kill()
                $tarProcess.WaitForExit()
            }
            catch {
            }
        }
        if ($sshStarted -and -not $sshProcess.HasExited) {
            try {
                $sshProcess.Kill()
                $sshProcess.WaitForExit()
            }
            catch {
            }
        }
        $tarProcess.Dispose()
        $sshProcess.Dispose()
    }
}

function Get-RemoteEnvMap {
    param([Parameter(Mandatory = $true)]$Config)

    $envPath = $Config.RemoteOpsPath + '/.env'
    $quotedEnvPath = Convert-ToSingleQuotedShellLiteral -Value $envPath
    $result = Invoke-SshCommand -Config $Config -Command "test -f $quotedEnvPath && cat $quotedEnvPath" -IgnoreExitCode -BatchMode
    $map = @{}
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.StdOut)) {
        return $map
    }

    foreach ($line in ($result.StdOut -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*#' -or $line -notmatch '=') {
            continue
        }

        $parts = $line -split '=', 2
        $key = $parts[0].Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $map[$key] = $parts[1].Trim()
        }
    }

    return $map
}

function Invoke-PublicHealthCheck {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Url
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 15
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
            throw "status=$($response.StatusCode)"
        }

        Write-Host "linux_pc_public_health_ok name=$Name status=$($response.StatusCode) url=$Url"
    }
    catch {
        throw "Linux PC public URL check failed: name=$Name url=$Url error=$($_.Exception.Message)"
    }
}

function Invoke-RemoteReadOnlyCheck {
    param([Parameter(Mandatory = $true)]$Config)

    $quotedOpsPath = Convert-ToSingleQuotedShellLiteral -Value $Config.RemoteOpsPath
    $remoteCommand = @(
        'set -e',
        "test -d $quotedOpsPath",
        "test -f $quotedOpsPath/apply-release.sh",
        "bash -n $quotedOpsPath/apply-release.sh",
        "docker ps --format '{{.Names}} {{.Status}}' | grep -E 'georaeplan|workplan' || true"
    ) -join '; '

    $output = Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode
    Write-Host 'linux_pc_remote_readonly_check_ok'
    ($output.StdOut -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Host $_ }
}

function Invoke-LinuxPcRemotePrune {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$KeepCount,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($KeepCount -lt 1) {
        return
    }

    $root = ($Config.RemoteRoot.TrimEnd('/') + '/' + $RelativePath.Trim('/'))
    $quotedRoot = Convert-ToSingleQuotedShellLiteral -Value $root
    $quotedPattern = Convert-ToSingleQuotedShellLiteral -Value $Pattern
    $quotedLabel = Convert-ToSingleQuotedShellLiteral -Value $Label
$remoteCommand = @"
set -e
set -o pipefail
root=$quotedRoot
pattern=$quotedPattern
keep=$KeepCount
label=$quotedLabel
if [ ! -d "`$root" ]; then
  echo "pruned label=`$label root=`$root total=0 keep=`$keep removed=0"
  exit 0
fi
real_root=`$(readlink -f "`$root")
if [ -z "`$real_root" ] || [ ! -d "`$real_root" ]; then
  echo "unsafe prune root: `$root" >&2
  exit 99
fi
tmp=`$(mktemp)
count_file=`$(mktemp)
trap 'rm -f "`$tmp" "`$count_file"' EXIT
find "`$real_root" -mindepth 1 -maxdepth 1 -type d -name "`$pattern" -printf '%T@ %p\n' | sort -rn > "`$tmp"
total=`$(wc -l < "`$tmp" | tr -d ' ')
echo 0 > "`$count_file"
if [ "`$total" -gt "`$keep" ]; then
  tail -n +`$((keep + 1)) "`$tmp" | cut -d' ' -f2- | while IFS= read -r target; do
    [ -z "`$target" ] && continue
    real_target=`$(readlink -f "`$target")
    case "`$real_target" in
      "`$real_root"/*)
        rm -rf -- "`$real_target"
        removed=`$(cat "`$count_file")
        removed=`$((removed + 1))
        echo "`$removed" > "`$count_file"
        ;;
      *)
        echo "unsafe prune target: `$real_target" >&2
        exit 99
        ;;
    esac
  done
fi
removed=`$(cat "`$count_file")
echo "pruned label=`$label root=`$real_root total=`$total keep=`$keep removed=`$removed"
"@

    $result = Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode
    ($result.StdOut -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Host "linux_pc_remote_prune $_" }
}

function Invoke-LinuxPcDiskPreflight {
    param(
        [Parameter(Mandatory = $true)]$Config,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int64]$MinimumFreeBytes,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($MinimumFreeBytes -le 0) {
        Write-Host "linux_pc_disk_preflight_skipped label=$Label minimum_bytes=$MinimumFreeBytes"
        return
    }

    $minimumFreeKilobytes = [int64][Math]::Ceiling($MinimumFreeBytes / 1024.0)
    $quotedPath = Convert-ToSingleQuotedShellLiteral -Value $Path
    $quotedLabel = Convert-ToSingleQuotedShellLiteral -Value $Label
    $remoteCommand = @"
set -e
set -o pipefail
path=$quotedPath
minimum_kb=$minimumFreeKilobytes
label=$quotedLabel
if [ ! -e "`$path" ]; then
  echo "disk preflight path does not exist: `$path" >&2
  exit 98
fi
available_kb=`$(df -Pk "`$path" | awk 'NR==2 {print `$4}')
if [ -z "`$available_kb" ]; then
  echo "disk preflight could not read available space for `$path" >&2
  exit 98
fi
if [ "`$available_kb" -lt "`$minimum_kb" ]; then
  echo "Linux PC free disk space is below the required threshold: label=`$label path=`$path available_kb=`$available_kb minimum_kb=`$minimum_kb" >&2
  exit 98
fi
echo "disk_preflight_ok label=`$label path=`$path available_kb=`$available_kb minimum_kb=`$minimum_kb"
"@

    $result = Invoke-SshCommand -Config $Config -Command $remoteCommand -BatchMode
    ($result.StdOut -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Host "linux_pc_disk_preflight_ok $_" }
}

function Invoke-ReleaseOperationalGate {
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [string]$SecretPath = '',
        [string]$OutputDirectory = '',
        [string[]]$AllowedIntegrityWarningCodes = @(),
        [string]$ReleaseId = '',
        [bool]$FailOnOperationalWarnings = $false,
        [bool]$AcceptLegacyAndroidDebugSigningWarning = $false,
        [string]$LocalCacheAppDataRoot = '',
        [string]$LocalCacheEvidenceDirectory = '',
        [bool]$RequireLocalCacheConsistencyCheck = $false,
        [bool]$FailOnLocalCacheWarning = $false,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedClientCompatibilityMode,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedClientCompatibilityEnabledPolicyCount,
        [bool]$AllowMissingClientCompatibilitySummary = $false
    )

    $operationalGateScript = Join-Path $Root 'tools\ops\Invoke-GeoraePlanOperationalGate.ps1'
    if (-not (Test-Path -LiteralPath $operationalGateScript)) {
        throw "$Phase operational gate script not found: $operationalGateScript"
    }
    if ([string]::IsNullOrWhiteSpace($BaseUrl) -or $BaseUrl -eq 'https://api.example.invalid') {
        throw "$Phase operational gate cannot run because BaseUrl is missing or placeholder."
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $safePhase = ($Phase -replace '[^A-Za-z0-9_-]', '-').Trim('-').ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($safePhase)) {
            $safePhase = 'operational'
        }
        $safeReleaseId = if ([string]::IsNullOrWhiteSpace($ReleaseId)) { Get-Date -Format 'yyyyMMdd-HHmmss' } else { $ReleaseId }
        $OutputDirectory = Join-Path $Root ("audit-output\$safePhase-operational-gate-$safeReleaseId")
    }

    $gateArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $operationalGateScript,
        '-ProjectRoot', $Root,
        '-BaseUrl', $BaseUrl,
        '-OutputDirectory', $OutputDirectory,
        '-ExpectedClientCompatibilityMode',
            $ExpectedClientCompatibilityMode,
        '-ExpectedClientCompatibilityEnabledPolicyCount',
            $ExpectedClientCompatibilityEnabledPolicyCount,
        '-FailOnIntegrityWarnings',
        '-SkipWriteSafetyChecks'
    )
    if (-not [string]::IsNullOrWhiteSpace($SecretPath)) {
        $gateArgs += @('-SecretPath', $SecretPath)
    }
    if ($AllowedIntegrityWarningCodes.Count -gt 0) {
        $gateArgs += @(
            '-AllowedIntegrityWarningCodes',
            ($AllowedIntegrityWarningCodes -join ','))
    }
    if ($FailOnOperationalWarnings) {
        $gateArgs += '-FailOnOperationalWarnings'
    }
    if ($AcceptLegacyAndroidDebugSigningWarning) {
        $gateArgs += '-AcceptLegacyAndroidDebugSigningWarning'
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalCacheAppDataRoot)) {
        $gateArgs += @('-LocalCacheAppDataRoot', $LocalCacheAppDataRoot)
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalCacheEvidenceDirectory)) {
        $gateArgs += @('-LocalCacheEvidenceDirectory', $LocalCacheEvidenceDirectory)
    }
    if ($RequireLocalCacheConsistencyCheck) {
        $gateArgs += '-RequireLocalCacheConsistencyCheck'
    }
    if ($FailOnLocalCacheWarning) {
        $gateArgs += '-FailOnLocalCacheWarning'
    }
    if ($AllowMissingClientCompatibilitySummary) {
        $gateArgs += '-AllowMissingClientCompatibilitySummary'
    }

    Write-Host "$($Phase)_operational_gate_start base_url=$BaseUrl output=$OutputDirectory"
    & powershell @gateArgs
    if ($LASTEXITCODE -ne 0) {
        throw "$Phase operational gate failed with exit code $LASTEXITCODE. Report directory: $OutputDirectory"
    }

    Write-Host "$($Phase)_operational_gate_done output=$OutputDirectory"
}


function Invoke-RentalTemplateItemReferenceGate {
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$OutputDirectory = '',
        [string]$ReleaseId = ''
    )

    $rentalTemplateItemReferenceGateScript = Join-Path $Root 'tools\linux\Test-GeoraePlanRentalTemplateItemReferenceGate.ps1'
    if (-not (Test-Path -LiteralPath $rentalTemplateItemReferenceGateScript)) {
        throw "$Phase rental template item reference gate script not found: $rentalTemplateItemReferenceGateScript"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $safePhase = ($Phase -replace '[^A-Za-z0-9_-]', '-').Trim('-').ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($safePhase)) {
            $safePhase = 'rental-template-item-reference'
        }
        $safeReleaseId = if ([string]::IsNullOrWhiteSpace($ReleaseId)) { Get-Date -Format 'yyyyMMdd-HHmmss' } else { $ReleaseId }
        $OutputDirectory = Join-Path $Root ("audit-output\$safePhase-rental-template-item-reference-gate-$safeReleaseId")
    }
    else {
        $OutputDirectory = Join-Path $OutputDirectory 'rental-template-item-reference-gate'
    }

    $rentalTemplateItemReferenceGateArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $rentalTemplateItemReferenceGateScript,
        '-ProjectRoot', $Root,
        '-OutputDirectory', $OutputDirectory,
        '-LinuxSshHost', $script:LinuxSshHost,
        '-LinuxSshPort', $script:LinuxSshPort,
        '-LinuxSshUser', $script:LinuxSshUser,
        '-LinuxSshKeyPath', $script:LinuxSshKeyPath,
        '-RemoteOpsDirectory', $script:LinuxRemoteOpsPath
    )

    Write-Host "$($Phase)_rental_template_item_reference_gate_start output=$OutputDirectory"
    & powershell @rentalTemplateItemReferenceGateArgs
    if ($LASTEXITCODE -ne 0) {
        throw "$Phase rental template item reference gate failed with exit code $LASTEXITCODE. Report directory: $OutputDirectory"
    }
    Write-Host "$($Phase)_rental_template_item_reference_gate_done output=$OutputDirectory"
}

function Invoke-AndroidSigningContinuityGate {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$Channel,
        [bool]$AcceptCertificateChange = $false
    )

    $androidSigningContinuityScript = Join-Path $Root 'tools\mobile\Test-GeoraePlanAndroidSigningContinuity.ps1'
    if (-not (Test-Path -LiteralPath $androidSigningContinuityScript)) {
        throw "Android signing continuity script not found: $androidSigningContinuityScript"
    }

    $manifestPath = Join-Path (Join-Path $PublishRoot 'updates\manifest') ($Channel + '.json')
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Android signing continuity manifest not found: $manifestPath"
    }

    $publishedManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $localAndroidFileName = [string]$publishedManifest.android.fileName
    if ([string]::IsNullOrWhiteSpace($localAndroidFileName)) {
        Write-Warning "Android signing continuity gate skipped because the published $Channel manifest has no Android package."
        Write-Host 'pre-deploy_android_signing_continuity=skipped reason=no-android-manifest-package'
        return
    }

    if (-not [string]::Equals(
            [System.IO.Path]::GetFileName($localAndroidFileName),
            $localAndroidFileName,
            [System.StringComparison]::Ordinal)) {
        throw "Android signing continuity manifest fileName must not contain a path: $localAndroidFileName"
    }

    $androidDownloadsRoot = Join-Path $PublishRoot 'updates\downloads\android'
    $localAndroidPackagePath = Join-Path $androidDownloadsRoot $localAndroidFileName
    if (-not (Test-Path -LiteralPath $localAndroidPackagePath)) {
        throw "Android signing continuity package referenced by the published manifest was not found: $localAndroidPackagePath"
    }
    $localAndroidPackage = Get-Item -LiteralPath $localAndroidPackagePath

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        throw 'Android signing continuity gate cannot run because BaseUrl is missing.'
    }

    $continuityArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $androidSigningContinuityScript,
        '-ProjectRoot', $Root,
        '-LocalApkPath', $localAndroidPackage.FullName,
        '-BaseUrl', $BaseUrl,
        '-Channel', $Channel
    )
    if ($AcceptCertificateChange) {
        $continuityArgs += '-AcceptCertificateChange'
    }

    Write-Host "pre-deploy_android_signing_continuity_start apk=$($localAndroidPackage.FullName) base_url=$BaseUrl"
    & powershell @continuityArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Android signing certificate continuity check failed.'
    }

    Write-Host 'pre-deploy_android_signing_continuity_done'
}

function Update-PublishedAppSettings {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][hashtable]$RemoteEnv
    )

    $publishedAppSettingsPath = Join-Path $PublishRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $publishedAppSettingsPath)) {
        return
    }

    $publishedSettings = Get-Content -LiteralPath $publishedAppSettingsPath -Raw | ConvertFrom-Json
    if (-not $publishedSettings.PSObject.Properties['Kestrel']) {
        $publishedSettings | Add-Member -NotePropertyName Kestrel -NotePropertyValue ([pscustomobject]@{})
    }
    if (-not $publishedSettings.Kestrel.PSObject.Properties['Endpoints']) {
        $publishedSettings.Kestrel | Add-Member -NotePropertyName Endpoints -NotePropertyValue ([pscustomobject]@{})
    }
    if (-not $publishedSettings.Kestrel.Endpoints.PSObject.Properties['Http']) {
        $publishedSettings.Kestrel.Endpoints | Add-Member -NotePropertyName Http -NotePropertyValue ([pscustomobject]@{})
    }
    if (-not $publishedSettings.Kestrel.Endpoints.Http.PSObject.Properties['Url']) {
        $publishedSettings.Kestrel.Endpoints.Http | Add-Member -NotePropertyName Url -NotePropertyValue 'http://0.0.0.0:8080'
    }
    $publishedSettings.Kestrel.Endpoints.Http.Url = 'http://0.0.0.0:8080'

    if (-not $publishedSettings.PSObject.Properties['ConnectionStrings']) {
        $publishedSettings | Add-Member -NotePropertyName ConnectionStrings -NotePropertyValue ([pscustomobject]@{})
    }

    $postgresPassword = if ($RemoteEnv.ContainsKey('POSTGRES_PASSWORD')) { "$($RemoteEnv['POSTGRES_PASSWORD'])".Trim() } else { '' }
    $postgresUser = if ($RemoteEnv.ContainsKey('POSTGRES_USER')) { "$($RemoteEnv['POSTGRES_USER'])".Trim() } else { 'georaeplan' }
    $itworldDbName = if ($RemoteEnv.ContainsKey('ITWORLD_POSTGRES_DB')) { "$($RemoteEnv['ITWORLD_POSTGRES_DB'])".Trim() } else { 'georaeplan_itworld' }
    if (-not [string]::IsNullOrWhiteSpace($postgresPassword) -and -not [string]::IsNullOrWhiteSpace($itworldDbName)) {
        $itworldConnection = "Host=postgres;Port=5432;Database=$itworldDbName;Username=$postgresUser;Password=$postgresPassword"
        if ($publishedSettings.ConnectionStrings.PSObject.Properties['ITWORLD']) {
            $publishedSettings.ConnectionStrings.ITWORLD = $itworldConnection
        }
        else {
            $publishedSettings.ConnectionStrings | Add-Member -NotePropertyName ITWORLD -NotePropertyValue $itworldConnection
        }
    }

    $publishedSettings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $publishedAppSettingsPath -Encoding UTF8
}

function Resolve-LiveUpdateRollbackBaselineTempRoot {
    foreach ($candidate in @($env:GEORAEPLAN_TEMP_ROOT, $env:TEMP, [System.IO.Path]::GetTempPath())) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        try {
            $resolved = [System.IO.Path]::GetFullPath($candidate)
            if (-not $resolved.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            New-Item -ItemType Directory -Force -Path $resolved | Out-Null
            return $resolved
        }
        catch {
            continue
        }
    }

    throw 'live 업데이트 기준선 임시 경로는 D 드라이브의 안전한 temp 경로여야 합니다.'
}

function Assert-SafeUpdatePackageFileName {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$BaselineLabel
    )

    if ([string]::IsNullOrWhiteSpace($FileName) -or
        $FileName.IndexOf('/') -ge 0 -or
        $FileName.IndexOf('\') -ge 0) {
        throw "$BaselineLabel $Platform 패키지 fileName이 안전하지 않습니다: $FileName"
    }
}

function Test-UpdatePackageFile {
    param(
        [Parameter(Mandatory = $true)]$Package,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$BaselineLabel
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "$BaselineLabel manifest가 참조하는 $platform 패키지를 찾을 수 없습니다: $PackagePath"
    }

    $packageInfo = Get-Item -LiteralPath $PackagePath -Force
    if (
        $packageInfo.PSIsContainer -or
        ($packageInfo.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "$BaselineLabel $platform 패키지가 정규 파일이 아닙니다: $PackagePath"
    }
    [long]$expectedSize = -1
    if (
        -not [long]::TryParse(
            [string]$Package.fileSize,
            [ref]$expectedSize) -or
        $expectedSize -le 0
    ) {
        throw "$BaselineLabel $platform 패키지 fileSize가 없어 크기 검증을 할 수 없습니다: $PackagePath"
    }
    if ($packageInfo.Length -ne $expectedSize) {
        throw "$BaselineLabel $platform 패키지 크기가 manifest와 다릅니다: $PackagePath"
    }

    $expectedHash = ([string]$Package.sha256).Trim()
    if ($expectedHash -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "$BaselineLabel $platform 패키지 sha256이 없어 무결성 검증을 할 수 없습니다: $PackagePath"
    }
    $actualHash = Get-GeoraePlanLinuxFileSha256 -Path $PackagePath
    if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$BaselineLabel $platform 패키지 SHA256이 manifest와 다릅니다: $PackagePath"
    }
}

function Get-UpdatePackageArtifacts {
    param(
        $Package,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$BaselineLabel
    )

    $artifacts = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Package) {
        return $artifacts.ToArray()
    }

    $mainFileName = ([string]$Package.fileName).Trim()
    [long]$mainFileSize = -1
    if (
        [string]::IsNullOrWhiteSpace($mainFileName) -or
        -not [long]::TryParse(
            [string]$Package.fileSize,
            [ref]$mainFileSize) -or
        $mainFileSize -le 0 -or
        [string]$Package.sha256 -notmatch '^[0-9A-Fa-f]{64}$'
    ) {
        throw (
            "$BaselineLabel $Platform 주 패키지의 " +
            'fileName/fileSize/sha256 metadata가 유효하지 않습니다.')
    }
    $artifacts.Add($Package) | Out-Null

    $installersProperty = $Package.PSObject.Properties['installers']
    if ($null -ne $installersProperty) {
        foreach ($installer in @($installersProperty.Value)) {
            if ($null -eq $installer) {
                throw "$BaselineLabel $Platform installer entry가 null입니다."
            }
            $installerFileName = ([string]$installer.fileName).Trim()
            [long]$installerFileSize = -1
            if (
                [string]::IsNullOrWhiteSpace($installerFileName) -or
                -not [long]::TryParse(
                    [string]$installer.fileSize,
                    [ref]$installerFileSize) -or
                $installerFileSize -le 0 -or
                [string]$installer.sha256 -notmatch '^[0-9A-Fa-f]{64}$'
            ) {
                throw (
                    "$BaselineLabel $Platform installer의 " +
                    'fileName/fileSize/sha256 metadata가 유효하지 않습니다.')
            }
            $artifacts.Add($installer) | Out-Null
        }
    }

    return $artifacts.ToArray()
}

function Resolve-UpdateBaseUri {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        throw "$Label base URL이 비어 있습니다."
    }

    $absoluteUri = $null
    if (-not [Uri]::TryCreate($BaseUrl.Trim(), [UriKind]::Absolute, [ref]$absoluteUri)) {
        throw "$Label base URL 형식이 올바르지 않습니다: $BaseUrl"
    }

    $isLoopback = $absoluteUri.IsLoopback -or [string]::Equals($absoluteUri.Host, 'localhost', [StringComparison]::OrdinalIgnoreCase)
    if (-not $isLoopback -and -not [string]::Equals($absoluteUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label base URL은 HTTPS만 허용됩니다: $BaseUrl"
    }

    return [Uri]($absoluteUri.AbsoluteUri.TrimEnd('/') + '/')
}

function Get-AllowedUpdateBaseUris {
    param([string[]]$BaseUrls)

    $seen = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    $allowedUris = New-Object System.Collections.Generic.List[Uri]
    foreach ($candidate in $BaseUrls) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $uri = Resolve-UpdateBaseUri -BaseUrl $candidate -Label '업데이트 기준선'
        if ($seen.Add($uri.AbsoluteUri)) {
            $allowedUris.Add($uri)
        }
    }

    if ($allowedUris.Count -eq 0) {
        throw '업데이트 기준선 허용 base URL을 하나 이상 제공해야 합니다.'
    }

    return $allowedUris.ToArray()
}

function Test-UpdateUriAllowedByBaseUrls {
    param(
        [Parameter(Mandatory = $true)][Uri]$CandidateUri,
        [Parameter(Mandatory = $true)][Uri[]]$AllowedBaseUris
    )

    foreach ($allowedBaseUri in $AllowedBaseUris) {
        if (-not [string]::Equals($CandidateUri.Scheme, $allowedBaseUri.Scheme, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not [string]::Equals($CandidateUri.Authority, $allowedBaseUri.Authority, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($allowedBaseUri.AbsolutePath) -or $allowedBaseUri.AbsolutePath -eq '/') {
            return $true
        }
        if ($CandidateUri.AbsoluteUri.StartsWith($allowedBaseUri.AbsoluteUri, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Resolve-VerifiedUpdatePackageUri {
    param(
        [Parameter(Mandatory = $true)][string]$PackageUrl,
        [Parameter(Mandatory = $true)][Uri]$ManifestBaseUri,
        [Parameter(Mandatory = $true)][Uri[]]$AllowedBaseUris,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$ExpectedFileName,
        [Parameter(Mandatory = $true)][string]$BaselineLabel
    )

    if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
        throw "$BaselineLabel $Platform 패키지 packageUrl이 비어 있습니다."
    }

    Assert-SafeUpdatePackageFileName -FileName $ExpectedFileName -Platform $Platform -BaselineLabel $BaselineLabel

    $packageUri = $null
    if (-not [Uri]::TryCreate($PackageUrl.Trim(), [UriKind]::Absolute, [ref]$packageUri)) {
        $packageUri = [Uri]::new($ManifestBaseUri, $PackageUrl.TrimStart('/'))
    }

    $isLoopback = $packageUri.IsLoopback -or [string]::Equals($packageUri.Host, 'localhost', [StringComparison]::OrdinalIgnoreCase)
    if (-not $isLoopback -and -not [string]::Equals($packageUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$BaselineLabel $Platform 패키지 URL은 HTTPS만 허용됩니다: $packageUri"
    }
    if (-not [string]::IsNullOrWhiteSpace($packageUri.Query) -or -not [string]::IsNullOrWhiteSpace($packageUri.Fragment)) {
        throw "$BaselineLabel $Platform 패키지 URL은 query/fragment를 포함할 수 없습니다: $packageUri"
    }
    $isSameOrigin = [string]::Equals($packageUri.Scheme, $ManifestBaseUri.Scheme, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($packageUri.Authority, $ManifestBaseUri.Authority, [StringComparison]::OrdinalIgnoreCase)
    if (-not $isSameOrigin -and -not (Test-UpdateUriAllowedByBaseUrls -CandidateUri $packageUri -AllowedBaseUris $AllowedBaseUris)) {
        throw "$BaselineLabel $Platform 패키지 URL은 same-origin 또는 허용된 base URL만 사용할 수 있습니다: $packageUri"
    }

    $expectedPathPrefix = "/updates/download/$Platform/"
    if (-not $packageUri.AbsolutePath.StartsWith($expectedPathPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$BaselineLabel $Platform 패키지 URL 경로가 update download 규칙과 다릅니다: $packageUri"
    }

    $encodedFileName = $packageUri.AbsolutePath.Substring($expectedPathPrefix.Length)
    if ([string]::IsNullOrWhiteSpace($encodedFileName) -or
        $encodedFileName.IndexOf('/') -ge 0 -or
        $encodedFileName.IndexOf('\') -ge 0) {
        throw "$BaselineLabel $Platform 패키지 URL fileName이 안전하지 않습니다: $packageUri"
    }

    $resolvedFileName = [Uri]::UnescapeDataString($encodedFileName)
    Assert-SafeUpdatePackageFileName -FileName $resolvedFileName -Platform $Platform -BaselineLabel $BaselineLabel
    if (-not [string]::Equals($ExpectedFileName, $resolvedFileName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$BaselineLabel $Platform 패키지 fileName과 URL이 다릅니다: expected=$ExpectedFileName actual=$resolvedFileName"
    }

    return $packageUri
}

function Copy-GeoraePlanPointerRollbackEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$SourceUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$TargetUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$Channel = 'stable',
        [string]$BaselineLabel = '업데이트 롤백 기준'
    )

    $sourceManifestRoot = Join-Path $SourceUpdatesRoot 'manifest'
    $pointerPath =
        Join-Path $sourceManifestRoot ($Channel + '.current.json')
    $pointerItem =
        Get-GeoraePlanLinuxRegularPointerItem `
            -Path $pointerPath `
            -Label "$BaselineLabel manifest pointer" `
            -AllowMissing
    if ($null -eq $pointerItem) {
        return $null
    }
    $evidence =
        Get-GeoraePlanUpdatePointerEvidence `
            -UpdatesRoot $SourceUpdatesRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
    $sourceCurrentPath =
        Join-Path $sourceManifestRoot ($Channel + '.json')
    $sourceCurrent =
        Get-Content -LiteralPath $sourceCurrentPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    if (
        $null -eq $sourceCurrent.PSObject.Properties['generationId'] -or
        $null -eq $sourceCurrent.PSObject.Properties['channel'] -or
        -not [string]::Equals(
            [string]$sourceCurrent.generationId,
            $evidence.GenerationId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$sourceCurrent.channel,
            $Channel,
            [StringComparison]::Ordinal)
    ) {
        throw "$BaselineLabel stable manifest와 pointer-selected 세대가 다릅니다."
    }

    $deliverySource = if (
        Test-Path `
            -LiteralPath $evidence.StagedDeliveryPath `
            -PathType Leaf
    ) {
        $evidence.StagedDeliveryPath
    }
    elseif (Test-Path `
        -LiteralPath ([string]$evidence.Pointer.deliveryManifestPath) `
        -PathType Leaf
    ) {
        [string]$evidence.Pointer.deliveryManifestPath
    }
    else {
        throw (
            "$BaselineLabel pointer-selected delivery 세대 증거가 없습니다: " +
            $evidence.GenerationId)
    }

    $targetManifestRoot = Join-Path $TargetUpdatesRoot 'manifest'
    $targetPointerPath =
        Join-Path $targetManifestRoot ($Channel + '.current.json')
    $targetRuntimePath = Join-Path (
        Join-Path (
            Join-Path $targetManifestRoot 'generations'
        ) $Channel
    ) ($evidence.GenerationId + '.json')
    $targetDeliveryPath = Join-Path (
        Join-Path (
            Join-Path $targetManifestRoot 'delivery-generations'
        ) $Channel
    ) ($evidence.GenerationId + '.json')
    $pointerHash = Get-GeoraePlanLinuxFileSha256 -Path $pointerPath
    Copy-GeoraePlanUpdateEvidenceFileAtomically `
        -SourcePath $evidence.RuntimePath `
        -TargetPath $targetRuntimePath `
        -ExpectedSha256 $evidence.Sha256 `
        -ExpectedFileSize $evidence.FileSize
    Copy-GeoraePlanUpdateEvidenceFileAtomically `
        -SourcePath $deliverySource `
        -TargetPath $targetDeliveryPath `
        -ExpectedSha256 $evidence.Sha256 `
        -ExpectedFileSize $evidence.FileSize
    Copy-GeoraePlanUpdateEvidenceFileAtomically `
        -SourcePath $pointerPath `
        -TargetPath $targetPointerPath `
        -ExpectedSha256 $pointerHash `
        -ExpectedFileSize ([long]$pointerItem.Length)
    return [pscustomobject]@{
        GenerationId = $evidence.GenerationId
        PointerPath = $targetPointerPath
        RuntimePath = $targetRuntimePath
        DeliveryPath = $targetDeliveryPath
    }
}

function Copy-VerifiedLiveUpdateRollbackBaselineFromSourceUpdatesRoot {
    param(
        [Parameter(Mandatory = $true)][string]$SourceUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [string]$Channel = 'stable',
        [string[]]$AllowedBaseUrls = @(),
        [string]$BaselineLabel = '현재 live 업데이트 기준선',
        [string]$ProjectRoot = ''
    )

    $baseUri = Resolve-UpdateBaseUri -BaseUrl $BaseUrl -Label $baselineLabel
    $allowedBaseUris = Get-AllowedUpdateBaseUris -BaseUrls (@($baseUri.AbsoluteUri) + @($AllowedBaseUrls))
    $sourceManifestRoot = Join-Path $SourceUpdatesRoot 'manifest'
    $sourceDownloadsRoot = Join-Path $SourceUpdatesRoot 'downloads'
    $targetUpdatesRoot = Join-Path $PublishRoot 'updates'
    $targetManifestRoot = Join-Path $targetUpdatesRoot 'manifest'
    $targetDownloadsRoot = Join-Path $targetUpdatesRoot 'downloads'
    $manifestFileName = $Channel + '.json'
    $sourceManifestPath = Join-Path $sourceManifestRoot $manifestFileName
    $targetManifestPath = Join-Path $targetManifestRoot $manifestFileName
    $copiedPackageCount = 0
    $pointerEvidence = $null

    if (-not (Test-Path -LiteralPath $sourceManifestPath)) {
        throw "$baselineLabel manifest 파일을 찾을 수 없습니다: $sourceManifestPath"
    }

    $manifestJson = Get-Content -LiteralPath $sourceManifestPath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($manifestJson)) {
        throw "$baselineLabel manifest 내용이 비어 있습니다: $sourceManifestPath"
    }

    $manifest = $manifestJson | ConvertFrom-Json
    foreach ($platform in @('desktop', 'android')) {
        $package = $manifest.$platform
        if ($null -eq $package) {
            continue
        }

        foreach ($artifact in @(
            Get-UpdatePackageArtifacts `
                -Package $package `
                -Platform $platform `
                -BaselineLabel $baselineLabel
        )) {
            $fileName = ([string]$artifact.fileName).Trim()
            Assert-SafeUpdatePackageFileName -FileName $fileName -Platform $platform -BaselineLabel $baselineLabel
            [void](Resolve-VerifiedUpdatePackageUri `
                -PackageUrl ([string]$artifact.packageUrl).Trim() `
                -ManifestBaseUri $baseUri `
                -AllowedBaseUris $allowedBaseUris `
                -Platform $platform `
                -ExpectedFileName $fileName `
                -BaselineLabel $baselineLabel)

            $sourcePackagePath = Join-Path (Join-Path $sourceDownloadsRoot $platform) $fileName
            Test-UpdatePackageFile `
                -Package $artifact `
                -PackagePath $sourcePackagePath `
                -Platform $platform `
                -BaselineLabel $baselineLabel
        }
    }

    New-Item -ItemType Directory -Force -Path $targetManifestRoot | Out-Null
    Copy-Item -LiteralPath $sourceManifestPath -Destination $targetManifestPath -Force

    foreach ($platform in @('desktop', 'android')) {
        $package = $manifest.$platform
        if ($null -eq $package) {
            continue
        }

        $targetPackageRoot = Join-Path $targetDownloadsRoot $platform
        New-Item -ItemType Directory -Force -Path $targetPackageRoot | Out-Null
        foreach ($artifact in @(
            Get-UpdatePackageArtifacts `
                -Package $package `
                -Platform $platform `
                -BaselineLabel $baselineLabel
        )) {
            $fileName = ([string]$artifact.fileName).Trim()
            $sourcePackagePath = Join-Path (Join-Path $sourceDownloadsRoot $platform) $fileName
            Copy-GeoraePlanUpdateEvidenceFileAtomically `
                -SourcePath $sourcePackagePath `
                -TargetPath (Join-Path $targetPackageRoot $fileName) `
                -ExpectedSha256 ([string]$artifact.sha256).Trim() `
                -ExpectedFileSize ([long]$artifact.fileSize)
            $copiedPackageCount++
        }
    }

    $sourcePointerItem =
        Get-GeoraePlanLinuxRegularPointerItem `
            -Path (
                Join-Path $sourceManifestRoot (
                    $Channel + '.current.json')) `
            -Label "$BaselineLabel manifest pointer" `
            -AllowMissing
    if ($null -ne $sourcePointerItem) {
        if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
            throw (
                "$BaselineLabel pointer 기준선을 복사하려면 ProjectRoot가 " +
                '필요합니다.')
        }
        $pointerEvidence =
            Copy-GeoraePlanPointerRollbackEvidence `
                -SourceUpdatesRoot $SourceUpdatesRoot `
                -TargetUpdatesRoot $targetUpdatesRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel `
                -BaselineLabel $BaselineLabel
    }

    $generationText = if ($null -eq $pointerEvidence) {
        'legacy'
    }
    else {
        [string]$pointerEvidence.GenerationId
    }
    Write-Host "live_update_rollback_baseline_seeded manifests=1 packages=$copiedPackageCount generation=$generationText base_url=$($baseUri.AbsoluteUri.TrimEnd('/')) source=linux_pc_live"
}

function Invoke-SshFileDownload {
    param(
        [Parameter(Mandatory = $true)][string]$RemotePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)]$Config
    )

    $sshExe = Resolve-SshExecutable
    $quotedRemotePath = Convert-ToSingleQuotedShellLiteral -Value $RemotePath
    $arguments = New-SshArgumentList -Config $Config -BatchMode
    $arguments += "cat $quotedRemotePath"

    $destinationDirectory = Split-Path -Parent $DestinationPath
    if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($sshExe)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $destinationStream = $null
    $downloadSucceeded = $false
    try {
        if (-not $process.Start()) {
            throw 'Failed to start Linux PC ssh download process.'
        }

        $destinationStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($destinationStream)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $null = $copyTask.GetAwaiter().GetResult()
        $destinationStream.Flush()
        $destinationStream.Dispose()
        $destinationStream = $null
        $process.WaitForExit()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        if ($process.ExitCode -ne 0) {
            throw "Linux PC ssh file download failed with exit code $($process.ExitCode): $stderr"
        }

        $downloadSucceeded = $true
    }
    finally {
        if ($null -ne $destinationStream) {
            $destinationStream.Dispose()
        }
        if (-not $downloadSucceeded) {
            Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
        }
        $process.Dispose()
    }
}

function Copy-RemoteUpdatePointerEvidenceToStaging {
    param(
        [Parameter(Mandatory = $true)][string]$RemoteUpdatesRoot,
        [Parameter(Mandatory = $true)][string]$StagingUpdatesRoot,
        [Parameter(Mandatory = $true)]$Config,
        [string]$Channel = 'stable',
        [string]$BaselineLabel = '현재 live 업데이트 기준선'
    )

    $remotePointerPath =
        $RemoteUpdatesRoot + '/manifest/' + $Channel + '.current.json'
    $stagingManifestRoot = Join-Path $StagingUpdatesRoot 'manifest'
    $stagingPointerPath =
        Join-Path $stagingManifestRoot ($Channel + '.current.json')
    New-Item `
        -ItemType Directory `
        -Force `
        -Path $stagingManifestRoot | Out-Null
    $quotedRemotePointerPath =
        Convert-ToSingleQuotedShellLiteral -Value $remotePointerPath
    $pointerResult = Invoke-SshCommand `
        -Config $Config `
        -Command (
            "if [ -L $quotedRemotePointerPath ]; then exit 45; " +
            "elif [ ! -e $quotedRemotePointerPath ]; then exit 44; " +
            "elif [ ! -f $quotedRemotePointerPath ]; then exit 45; " +
            "else exit 0; fi") `
        -IgnoreExitCode `
        -BatchMode
    if ($pointerResult.ExitCode -eq 44) {
        return $null
    }
    if ($pointerResult.ExitCode -eq 45) {
        throw (
            "$BaselineLabel pointer가 정규 파일이 아닙니다: " +
            $remotePointerPath)
    }
    if ($pointerResult.ExitCode -ne 0) {
        $message = if (
            [string]::IsNullOrWhiteSpace($pointerResult.StdErr)
        ) {
            $pointerResult.StdOut
        }
        else {
            $pointerResult.StdErr
        }
        throw (
            "$BaselineLabel pointer를 Linux PC에서 읽지 못했습니다: " +
            "$remotePointerPath / $message")
    }
    Invoke-SshFileDownload `
        -RemotePath $remotePointerPath `
        -DestinationPath $stagingPointerPath `
        -Config $Config
    $pointerJson =
        Get-Content -LiteralPath $stagingPointerPath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($pointerJson)) {
        throw "$BaselineLabel pointer 응답이 비어 있습니다."
    }
    $pointer = $pointerJson | ConvertFrom-Json
    $expectedProperties = @(
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
    $actualProperties = @(
        $pointer.PSObject.Properties | ForEach-Object { $_.Name })
    [long]$runtimeFileSize = -1
    [long]$deliveryFileSize = -1
    if (
        $actualProperties.Count -ne $expectedProperties.Count -or
        @($actualProperties | Where-Object {
            $_ -notin $expectedProperties
        }).Count -ne 0 -or
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
        [string]::IsNullOrWhiteSpace(
            [string]$pointer.deliveryManifestPath) -or
        -not [long]::TryParse(
            [string]$pointer.manifestFileSize,
            [ref]$runtimeFileSize) -or
        -not [long]::TryParse(
            [string]$pointer.deliveryManifestFileSize,
            [ref]$deliveryFileSize) -or
        $runtimeFileSize -lt 0 -or
        $runtimeFileSize -ne $deliveryFileSize -or
        -not [string]::Equals(
            [string]$pointer.manifestSha256,
            [string]$pointer.deliveryManifestSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$BaselineLabel pointer 값이 유효하지 않습니다."
    }
    $generationId = [string]$pointer.generationId
    $relativePath =
        'generations/{0}/{1}.json' -f $Channel, $generationId
    if (-not [string]::Equals(
        [string]$pointer.manifestRelativePath,
        $relativePath,
        [StringComparison]::Ordinal
    )) {
        throw "$BaselineLabel pointer runtime 경로가 canonical하지 않습니다."
    }

    $stagingRuntimePath = Join-Path (
        Join-Path (
            Join-Path $stagingManifestRoot 'generations'
        ) $Channel
    ) ($generationId + '.json')
    $stagingDeliveryPath = Join-Path (
        Join-Path (
            Join-Path $stagingManifestRoot 'delivery-generations'
        ) $Channel
    ) ($generationId + '.json')
    $remoteRuntimePath =
        $RemoteUpdatesRoot + '/manifest/generations/' +
        $Channel + '/' + $generationId + '.json'
    Invoke-SshFileDownload `
        -RemotePath $remoteRuntimePath `
        -DestinationPath $stagingRuntimePath `
        -Config $Config
    $runtimeItem =
        Get-Item -LiteralPath $stagingRuntimePath -Force -ErrorAction Stop
    $runtimeHash =
        Get-GeoraePlanLinuxFileSha256 -Path $stagingRuntimePath
    if (
        $runtimeItem.PSIsContainer -or
        $runtimeItem.Length -ne $runtimeFileSize -or
        -not [string]::Equals(
            $runtimeHash,
            [string]$pointer.manifestSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$BaselineLabel pointer-selected runtime 세대 증거가 다릅니다."
    }
    $runtimeManifest =
        Get-Content -LiteralPath $stagingRuntimePath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    if (
        -not [string]::Equals(
            [string]$runtimeManifest.generationId,
            $generationId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$runtimeManifest.channel,
            $Channel,
            [StringComparison]::Ordinal)
    ) {
        throw "$BaselineLabel pointer-selected runtime binding이 다릅니다."
    }
    Copy-GeoraePlanUpdateEvidenceFileAtomically `
        -SourcePath $stagingRuntimePath `
        -TargetPath $stagingDeliveryPath `
        -ExpectedSha256 $runtimeHash `
        -ExpectedFileSize ([long]$runtimeItem.Length)
    return $generationId
}

function Copy-LiveUpdateRollbackBaseline {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)]$Config,
        [string]$Channel = 'stable',
        [string[]]$AllowedBaseUrls = @(),
        [switch]$AllowMissingManifest
    )

    $baselineLabel = '현재 live 업데이트 기준선'
    $tempRoot = Resolve-LiveUpdateRollbackBaselineTempRoot
    $stagingRoot = Join-Path $tempRoot ("linux-live-update-baseline-" + [Guid]::NewGuid().ToString('N'))
    $stagingUpdatesRoot = Join-Path $stagingRoot 'updates'
    $stagingManifestRoot = Join-Path $stagingUpdatesRoot 'manifest'
    $stagingDownloadsRoot = Join-Path $stagingUpdatesRoot 'downloads'
    $remoteUpdatesRoot = $Config.RemoteRoot + '/app/live/updates'
    $remoteManifestPath = $remoteUpdatesRoot + '/manifest/' + $Channel + '.json'
    $manifestFileName = $Channel + '.json'
    $stagingManifestPath = Join-Path $stagingManifestRoot $manifestFileName
    $manifestJson = ''

    try {
        New-Item -ItemType Directory -Force -Path $stagingManifestRoot | Out-Null
        New-Item -ItemType Directory -Force -Path $stagingDownloadsRoot | Out-Null

        $quotedRemoteManifestPath = Convert-ToSingleQuotedShellLiteral -Value $remoteManifestPath
        $remoteManifestReadCommand =
            "if [ -L $quotedRemoteManifestPath ]; then exit 45; " +
            "elif [ ! -e $quotedRemoteManifestPath ]; then exit 44; " +
            "elif [ ! -f $quotedRemoteManifestPath ]; then exit 45; " +
            "else exit 0; fi"
        $manifestResult = Invoke-SshCommand `
            -Config $Config `
            -Command $remoteManifestReadCommand `
            -IgnoreExitCode `
            -BatchMode

        if ($manifestResult.ExitCode -eq 44) {
            if ($AllowMissingManifest) {
                Write-Host "live_update_rollback_baseline=initial_release manifest_status=missing channel=$Channel remote_path=$remoteManifestPath"
                return
            }

            throw "$baselineLabel manifest가 Linux PC live 경로에 없습니다: $remoteManifestPath"
        }

        if ($manifestResult.ExitCode -eq 45) {
            throw (
                "$baselineLabel manifest 경로가 정규 파일이 아닙니다: " +
                $remoteManifestPath)
        }

        if ($manifestResult.ExitCode -ne 0) {
            $message = if ([string]::IsNullOrWhiteSpace($manifestResult.StdErr)) { $manifestResult.StdOut } else { $manifestResult.StdErr }
            throw "$baselineLabel manifest를 Linux PC에서 읽지 못했습니다: $remoteManifestPath / $message"
        }

        Invoke-SshFileDownload `
            -RemotePath $remoteManifestPath `
            -DestinationPath $stagingManifestPath `
            -Config $Config
        $manifestJson =
            Get-Content -LiteralPath $stagingManifestPath -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($manifestJson)) {
            throw "$baselineLabel manifest 응답이 비어 있습니다: $remoteManifestPath"
        }

        $remoteGenerationId =
            Copy-RemoteUpdatePointerEvidenceToStaging `
                -RemoteUpdatesRoot $remoteUpdatesRoot `
                -StagingUpdatesRoot $stagingUpdatesRoot `
                -Config $Config `
                -Channel $Channel `
                -BaselineLabel $baselineLabel
        $manifest = $manifestJson | ConvertFrom-Json
        if (
            $null -ne $remoteGenerationId -and
            (
                -not [string]::Equals(
                    [string]$manifest.generationId,
                    [string]$remoteGenerationId,
                    [StringComparison]::Ordinal) -or
                -not [string]::Equals(
                    [string]$manifest.channel,
                    $Channel,
                    [StringComparison]::Ordinal)
            )
        ) {
            throw (
                "$baselineLabel stable manifest와 pointer-selected 세대가 " +
                '다릅니다.')
        }
        foreach ($platform in @('desktop', 'android')) {
            $package = $manifest.$platform
            if ($null -eq $package) {
                continue
            }

            foreach ($artifact in @(
                Get-UpdatePackageArtifacts `
                    -Package $package `
                    -Platform $platform `
                    -BaselineLabel $baselineLabel
            )) {
                $fileName = ([string]$artifact.fileName).Trim()
                Assert-SafeUpdatePackageFileName -FileName $fileName -Platform $platform -BaselineLabel $baselineLabel
                $remotePackagePath = $remoteUpdatesRoot + '/downloads/' + $platform + '/' + $fileName
                $localPackagePath = Join-Path (Join-Path $stagingDownloadsRoot $platform) $fileName
                Invoke-SshFileDownload -RemotePath $remotePackagePath -DestinationPath $localPackagePath -Config $Config
            }
        }

        Copy-VerifiedLiveUpdateRollbackBaselineFromSourceUpdatesRoot `
            -SourceUpdatesRoot $stagingUpdatesRoot `
            -BaseUrl $BaseUrl `
            -PublishRoot $PublishRoot `
            -Channel $Channel `
            -AllowedBaseUrls $AllowedBaseUrls `
            -BaselineLabel $baselineLabel `
            -ProjectRoot $ProjectRoot
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Copy-LocalUpdateRollbackBaseline {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [string]$Channel = 'stable'
    )

    $sourceUpdatesRoot = Join-Path $Root '배포\업데이트'
    $sourceManifestRoot = Join-Path $sourceUpdatesRoot 'manifest'
    $targetUpdatesRoot = Join-Path $PublishRoot 'updates'
    $targetManifestRoot = Join-Path $targetUpdatesRoot 'manifest'
    $manifestNames = @(
        ($Channel + '.json')
        ($Channel + '.previous.json')
    )
    $copiedManifestCount = 0
    $copiedPackageCount = 0
    $pointerEvidence = $null

    foreach ($manifestName in $manifestNames) {
        $sourceManifestPath = Join-Path $sourceManifestRoot $manifestName
        if (-not (Test-Path -LiteralPath $sourceManifestPath)) {
            continue
        }

        try {
            $manifest = Get-Content -LiteralPath $sourceManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            New-Item -ItemType Directory -Force -Path $targetManifestRoot | Out-Null
            Copy-Item -LiteralPath $sourceManifestPath -Destination (Join-Path $targetManifestRoot $manifestName) -Force
            $copiedManifestCount++

            foreach ($platform in @('desktop', 'android')) {
                $package = $manifest.$platform
                if ($null -eq $package) {
                    continue
                }

                $targetPackageRoot = Join-Path (Join-Path $targetUpdatesRoot 'downloads') $platform
                New-Item -ItemType Directory -Force -Path $targetPackageRoot | Out-Null
                foreach ($artifact in @(
                    Get-UpdatePackageArtifacts `
                        -Package $package `
                        -Platform $platform `
                        -BaselineLabel '롤백 기준'
                )) {
                    $fileName = ([string]$artifact.fileName).Trim()
                    Assert-SafeUpdatePackageFileName -FileName $fileName -Platform $platform -BaselineLabel '롤백 기준'
                    $sourcePackagePath = Join-Path (Join-Path (Join-Path $sourceUpdatesRoot 'downloads') $platform) $fileName
                    Test-UpdatePackageFile `
                        -Package $artifact `
                        -PackagePath $sourcePackagePath `
                        -Platform $platform `
                        -BaselineLabel '롤백 기준'
                    Copy-GeoraePlanUpdateEvidenceFileAtomically `
                        -SourcePath $sourcePackagePath `
                        -TargetPath (Join-Path $targetPackageRoot $fileName) `
                        -ExpectedSha256 ([string]$artifact.sha256).Trim() `
                        -ExpectedFileSize ([long]$artifact.fileSize)
                    $copiedPackageCount++
                }
            }
        }
        catch {
            throw "기존 업데이트 롤백 기준을 release staging에 복사하지 못했습니다: $sourceManifestPath / $($_.Exception.Message)"
        }
    }

    if ($copiedManifestCount -eq 0) {
        Write-Warning '기존 업데이트 manifest가 없어 이번 배포에는 이전 버전 롤백 기준이 생성되지 않을 수 있습니다.'
        return
    }

    $sourcePointerItem =
        Get-GeoraePlanLinuxRegularPointerItem `
            -Path (
                Join-Path $sourceManifestRoot (
                    $Channel + '.current.json')) `
            -Label '롤백 기준 manifest pointer' `
            -AllowMissing
    if ($null -ne $sourcePointerItem) {
        $pointerEvidence =
            Copy-GeoraePlanPointerRollbackEvidence `
                -SourceUpdatesRoot $sourceUpdatesRoot `
                -TargetUpdatesRoot $targetUpdatesRoot `
                -ProjectRoot $Root `
                -Channel $Channel `
                -BaselineLabel '롤백 기준'
    }
    $generationText = if ($null -eq $pointerEvidence) {
        'legacy'
    }
    else {
        [string]$pointerEvidence.GenerationId
    }
    Write-Host "update_rollback_baseline_seeded manifests=$copiedManifestCount packages=$copiedPackageCount generation=$generationText"
}

Assert-SafeReleaseId -Value $ReleaseId
$ProjectRoot = Resolve-ProjectRoot -ExplicitProjectRoot $ProjectRoot
$tempInitializer = Join-Path $ProjectRoot 'tools\common\Initialize-GeoraePlanTemp.ps1'
if (Test-Path -LiteralPath $tempInitializer) {
    . $tempInitializer -ProjectRoot $ProjectRoot
}
$dotnetExe = Resolve-DotnetCommand -Root $ProjectRoot
$env:DOTNET_EXE = $dotnetExe
$linuxConfig = New-LinuxSshConfig `
    -HostName $LinuxSshHost `
    -UserName $LinuxSshUser `
    -Port $LinuxSshPort `
    -KeyPath $LinuxSshKeyPath `
    -RemoteRoot $LinuxRemoteRoot `
    -RemoteOpsPath $LinuxRemoteOpsPath

if ($SkipConfigSync) {
    Write-Host 'linux_pc_config_sync=skip'
}
if ($AllowLegacyLiveMirror) {
    Write-Warning 'AllowLegacyLiveMirror is ignored for Linux PC deploy. SSH apply-release.sh is required.'
}
if ($AllowScheduledApplyTrigger) {
    Write-Warning 'AllowScheduledApplyTrigger is ignored for Linux PC deploy. Direct SSH apply-release.sh is used.'
}
if ($PreserveLiveUpdateAssets -and -not $MirrorToLive) {
    throw 'PreserveLiveUpdateAssets requires MirrorToLive so the verified live update baseline can be copied.'
}
if ($PreserveLiveAndroidUpdate -and -not $MirrorToLive) {
    throw 'PreserveLiveAndroidUpdate requires MirrorToLive so the verified live Android baseline can be copied.'
}
if ($PreserveLiveUpdateAssets -and $PreserveLiveAndroidUpdate) {
    throw 'PreserveLiveUpdateAssets cannot be combined with PreserveLiveAndroidUpdate.'
}

if ($MirrorToLive -and -not $SkipPlatformHealthChecks) {
    Invoke-PublicHealthCheck -Name 'trade' -Url 'https://trade.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'work' -Url 'https://work.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'itw' -Url 'https://itw.2884.kr/'
    Invoke-RemoteReadOnlyCheck -Config $linuxConfig
}

$remoteEnv = Get-RemoteEnvMap -Config $linuxConfig
$publicBaseUrl = if ($remoteEnv.ContainsKey('PUBLIC_BASE_URL')) { "$($remoteEnv['PUBLIC_BASE_URL'])".Trim() } else { '' }
$resolvedPreDeployBaseUrl = if (-not [string]::IsNullOrWhiteSpace($PreDeployBaseUrl)) { $PreDeployBaseUrl } elseif (-not [string]::IsNullOrWhiteSpace($PostDeployBaseUrl)) { $PostDeployBaseUrl } else { $publicBaseUrl }
$resolvedPostDeployBaseUrl = if (-not [string]::IsNullOrWhiteSpace($PostDeployBaseUrl)) { $PostDeployBaseUrl } elseif (-not [string]::IsNullOrWhiteSpace($PreDeployBaseUrl)) { $PreDeployBaseUrl } else { $publicBaseUrl }
$resolvedPreDeploySecretPath = if (-not [string]::IsNullOrWhiteSpace($PreDeploySecretPath)) { $PreDeploySecretPath } else { $PostDeploySecretPath }

if ($MirrorToLive -and -not $AcceptRentalTemplateItemReferenceRisk.IsPresent) {
    Invoke-RentalTemplateItemReferenceGate `
        -Phase 'pre-deploy-required-data' `
        -Root $ProjectRoot `
        -OutputDirectory $PreDeployOutputDirectory `
        -ReleaseId $ReleaseId
}
elseif ($MirrorToLive -and $AcceptRentalTemplateItemReferenceRisk.IsPresent) {
    Write-Warning 'Rental template item reference gate was skipped by explicit risk acceptance. Use only when known operating data candidates are intentionally excluded from the release decision.'
    Write-Host 'pre-deploy-required-data_rental_template_item_reference_gate=skipped risk=accepted'
}

if ($MirrorToLive -and -not $SkipPreDeployOperationalGate.IsPresent) {
    Invoke-ReleaseOperationalGate `
        -Phase 'pre-deploy' `
        -Root $ProjectRoot `
        -BaseUrl $resolvedPreDeployBaseUrl `
        -SecretPath $resolvedPreDeploySecretPath `
        -OutputDirectory $PreDeployOutputDirectory `
        -AllowedIntegrityWarningCodes $PreDeployAllowedIntegrityWarningCodes `
        -ReleaseId $ReleaseId `
        -FailOnOperationalWarnings ([bool]$FailOnOperationalWarnings) `
        -AcceptLegacyAndroidDebugSigningWarning ([bool]$AcceptLegacyAndroidDebugSigningWarning) `
        -LocalCacheAppDataRoot $LocalCacheAppDataRoot `
        -LocalCacheEvidenceDirectory $LocalCacheEvidenceDirectory `
        -RequireLocalCacheConsistencyCheck ([bool]$RequireLocalCacheConsistencyCheck) `
        -FailOnLocalCacheWarning ([bool]$FailOnLocalCacheWarning) `
        -ExpectedClientCompatibilityMode $ExpectedClientCompatibilityMode `
        -ExpectedClientCompatibilityEnabledPolicyCount `
            $ExpectedClientCompatibilityEnabledPolicyCount `
        -AllowMissingClientCompatibilitySummary `
            ([bool]$AllowLegacyPreDeployCompatibilitySummary)
}
elseif ($MirrorToLive -and $SkipPreDeployOperationalGate.IsPresent) {
    Write-Warning 'Pre-deploy operational gate was skipped by request. Use only when a separate strict gate has already passed.'
}

$solutionPath = Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName
$serverProject = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'Server') -Recurse -File -Filter '*.Server.Api.csproj' | Select-Object -First 1 -ExpandProperty FullName
if (-not $solutionPath) {
    throw "Solution file not found under: $ProjectRoot"
}
if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "Server project not found: $serverProject"
}

$localReleaseWorkRoot =
    [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'release-temp'))
$tempPublishRoot =
    [IO.Path]::GetFullPath(
        (Join-Path $localReleaseWorkRoot "linux-$ReleaseId"))
$durableUpdatesRoot =
    Join-Path $localReleaseWorkRoot 'linux-update-assets-stable'
$metadataPath = Join-Path $tempPublishRoot 'release-info.txt'
$releaseParentDirectoryLease = $null
$tempPublishDirectoryLease = $null
try {
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $localReleaseWorkRoot `
            -Label 'Linux release-temp root'
    [void][IO.Directory]::CreateDirectory($localReleaseWorkRoot)
    $releaseParentDirectoryLease =
        Open-GeoraePlanLinuxDirectoryChainLease `
            -DirectoryPaths @($ProjectRoot, $localReleaseWorkRoot)
    Assert-GeoraePlanLinuxDirectoryChainLease `
        -Lease $releaseParentDirectoryLease
    if (Test-Path -LiteralPath $tempPublishRoot) {
        Remove-GeoraePlanLinuxOwnedDirectoryTree `
            -DirectoryPath $tempPublishRoot `
            -ExpectedPath $tempPublishRoot
    }
    $null =
        Assert-GeoraePlanLinuxRegularDirectoryChain `
            -Path $tempPublishRoot `
            -Label 'Per-release publish root'
    [void][IO.Directory]::CreateDirectory($tempPublishRoot)
    $tempPublishDirectoryLease =
        Open-GeoraePlanLinuxDirectoryChainLease `
            -DirectoryPaths @($tempPublishRoot)
    Assert-GeoraePlanLinuxDirectoryChainLease `
        -Lease $tempPublishDirectoryLease
    if (-not $SkipBuild) {
        & $dotnetExe build $solutionPath -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet build failed.'
        }
    }

    & $dotnetExe publish $serverProject -c $Configuration -o $tempPublishRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    if ($MirrorToLive) {
        Copy-LiveUpdateRollbackBaseline `
            -BaseUrl $resolvedPreDeployBaseUrl `
            -PublishRoot $tempPublishRoot `
            -ProjectRoot $ProjectRoot `
            -Config $linuxConfig `
            -Channel 'stable' `
            -AllowedBaseUrls @($resolvedPostDeployBaseUrl, $publicBaseUrl) `
            -AllowMissingManifest:$AllowMissingLiveUpdateBaseline
    }
    else {
        Copy-LocalUpdateRollbackBaseline -Root $ProjectRoot -PublishRoot $tempPublishRoot -Channel 'stable'
    }

    $updateAssetScript = Join-Path $ProjectRoot 'tools\release\Publish-GeoraePlanUpdateAssets.ps1'
    if ($PreserveLiveUpdateAssets) {
        $preservedUpdatesRoot = Join-Path $tempPublishRoot 'updates'
        Assert-GeoraePlanLinuxManifestReferencedAssets `
            -UpdatesRoot $preservedUpdatesRoot `
            -ProjectRoot $ProjectRoot `
            -Channel 'stable'
        Write-Host "linux_update_assets_preserved=verified-live-baseline channel=stable path=$preservedUpdatesRoot"
    }
    elseif (Test-Path -LiteralPath $updateAssetScript) {
        $updateAssetArgs = @{
        }
        if (-not [string]::IsNullOrWhiteSpace($DesktopNotes)) {
            $updateAssetArgs.DesktopNotes = $DesktopNotes
        }
        if (-not [string]::IsNullOrWhiteSpace($AndroidNotes)) {
            $updateAssetArgs.AndroidNotes = $AndroidNotes
        }
        if ($PreserveLiveAndroidUpdate) {
            $updateAssetArgs.PreserveExistingAndroid = $true
        }

        Invoke-GeoraePlanDurableUpdateAssetPublish `
            -ProjectRoot $ProjectRoot `
            -DurableUpdatesRoot $durableUpdatesRoot `
            -PublishRoot $tempPublishRoot `
            -UpdateAssetScript $updateAssetScript `
            -UpdateAssetArguments $updateAssetArgs `
            -Channel 'stable'
    }

    if ($MirrorToLive -and $PreserveLiveUpdateAssets) {
        Write-Host 'pre-deploy_android_signing_continuity=not-applicable reason=verified-live-update-assets-preserved'
    }
    elseif ($MirrorToLive -and $PreserveLiveAndroidUpdate) {
        Write-Host 'pre-deploy_android_signing_continuity=not-applicable reason=verified-live-android-update-preserved'
    }
    elseif ($MirrorToLive -and -not $SkipAndroidSigningContinuityCheck.IsPresent) {
        Invoke-AndroidSigningContinuityGate `
            -Root $ProjectRoot `
            -PublishRoot $tempPublishRoot `
            -BaseUrl $resolvedPreDeployBaseUrl `
            -Channel 'stable' `
            -AcceptCertificateChange ([bool]$AcceptAndroidSigningCertificateChange)
    }
    elseif ($MirrorToLive -and $SkipAndroidSigningContinuityCheck.IsPresent) {
        Write-Warning 'Android signing continuity gate was skipped by request. Use only when there is no Android package change or a separate reinstall/migration plan has already been verified.'
    }

    Update-PublishedAppSettings -PublishRoot $tempPublishRoot -RemoteEnv $remoteEnv

    $commit = (& git -C $ProjectRoot rev-parse HEAD 2>$null)
    @(
        "release_id=$ReleaseId",
        "built_at=$([DateTimeOffset]::Now.ToString('o'))",
        "configuration=$Configuration",
        "commit=$commit",
        "target=Linux PC",
        "remote_root=$($linuxConfig.RemoteRoot)"
    ) | Set-Content -Path $metadataPath -Encoding UTF8

    $remoteReleaseRoot = $linuxConfig.RemoteRoot + '/releases/' + $ReleaseId

    if ($MirrorToLive) {
        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'app/backups' -Pattern 'live-*' -KeepCount $KeepReleaseCount -Label 'live-backups'
        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'releases' -Pattern '*' -KeepCount $KeepReleaseCount -Label 'releases'
    }

    Invoke-LinuxPcDiskPreflight -Config $linuxConfig -Path $linuxConfig.RemoteRoot -MinimumFreeBytes $MinimumLinuxFreeBytes -Label 'pre-upload'

    Write-Host "linux_pc_upload_start release_id=$ReleaseId remote_path=$remoteReleaseRoot"
    Invoke-SshTarUpload -SourceDirectory $tempPublishRoot -RemoteDirectory $remoteReleaseRoot -Config $linuxConfig
    Write-Host "linux_pc_upload_done release_id=$ReleaseId remote_path=$remoteReleaseRoot"

    if ($MirrorToLive) {
        Invoke-LinuxPcDiskPreflight -Config $linuxConfig -Path $linuxConfig.RemoteRoot -MinimumFreeBytes $MinimumLinuxFreeBytes -Label 'pre-apply'

        Write-Host "linux_pc_apply_release_mode=ssh host=$($linuxConfig.Host) user=$($linuxConfig.User) port=$($linuxConfig.Port)"
        $quotedOps = Convert-ToSingleQuotedShellLiteral -Value $linuxConfig.RemoteOpsPath
        $quotedReleaseId = Convert-ToSingleQuotedShellLiteral -Value $ReleaseId
        $applyCommand = "cd $quotedOps && HEALTH_CHECK_RETRIES=900 /bin/bash ./apply-release.sh $quotedReleaseId"
        $applyResult = Invoke-SshCommand -Config $linuxConfig -Command $applyCommand
        ($applyResult.StdOut -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Host $_ }
        if (-not [string]::IsNullOrWhiteSpace($applyResult.StdErr)) {
            ($applyResult.StdErr -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Write-Warning $_ }
        }

        if (-not $SkipPostDeployOperationalGate.IsPresent) {
            Invoke-ReleaseOperationalGate `
                -Phase 'post-deploy' `
                -Root $ProjectRoot `
                -BaseUrl $resolvedPostDeployBaseUrl `
                -SecretPath $PostDeploySecretPath `
                -OutputDirectory $PostDeployOutputDirectory `
                -AllowedIntegrityWarningCodes $PostDeployAllowedIntegrityWarningCodes `
                -ReleaseId $ReleaseId `
                -FailOnOperationalWarnings ([bool]$FailOnOperationalWarnings) `
                -AcceptLegacyAndroidDebugSigningWarning ([bool]$AcceptLegacyAndroidDebugSigningWarning) `
                -LocalCacheAppDataRoot $LocalCacheAppDataRoot `
                -LocalCacheEvidenceDirectory $LocalCacheEvidenceDirectory `
                -RequireLocalCacheConsistencyCheck ([bool]$RequireLocalCacheConsistencyCheck) `
                -FailOnLocalCacheWarning ([bool]$FailOnLocalCacheWarning) `
                -ExpectedClientCompatibilityMode `
                    $ExpectedClientCompatibilityMode `
                -ExpectedClientCompatibilityEnabledPolicyCount `
                    $ExpectedClientCompatibilityEnabledPolicyCount
        }
        else {
            Write-Warning 'Post-deploy operational gate was skipped by request. Use only when a separate strict gate has already passed.'
        }

        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'releases' -Pattern '*' -KeepCount $KeepReleaseCount -Label 'releases'
        Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'app/backups' -Pattern 'live-*' -KeepCount $KeepReleaseCount -Label 'live-backups'
    }

    Write-Host "publish_done release_id=$ReleaseId release_path=$remoteReleaseRoot"
    if ($MirrorToLive) {
        Write-Host "linux_pc_apply_release_done release_id=$ReleaseId host=$($linuxConfig.Host) user=$($linuxConfig.User)"
    }
}
finally {
    try {
        if (Test-Path -LiteralPath $tempPublishRoot) {
            if ($null -eq $tempPublishDirectoryLease) {
                throw (
                    'Per-release publish root cleanup requires its live ' +
                    'directory lease.')
            }
            Assert-GeoraePlanLinuxDirectoryChainLease `
                -Lease $tempPublishDirectoryLease
            Remove-GeoraePlanLinuxOwnedDirectoryTree `
                -DirectoryPath $tempPublishRoot `
                -ExpectedPath $tempPublishRoot `
                -RootLease $tempPublishDirectoryLease `
                -PreserveRoot
            Assert-GeoraePlanLinuxDirectoryChainLease `
                -Lease $tempPublishDirectoryLease
            $tempPublishDirectoryLease.Dispose()
            $tempPublishDirectoryLease = $null
            [IO.Directory]::Delete($tempPublishRoot, $false)
        }
        elseif ($null -ne $tempPublishDirectoryLease) {
            $tempPublishDirectoryLease.Dispose()
            $tempPublishDirectoryLease = $null
        }
    }
    finally {
        if ($null -ne $tempPublishDirectoryLease) {
            $tempPublishDirectoryLease.Dispose()
            $tempPublishDirectoryLease = $null
        }
        if ($null -ne $releaseParentDirectoryLease) {
            $releaseParentDirectoryLease.Dispose()
            $releaseParentDirectoryLease = $null
        }
    }
}

Write-Host "linux_pc_release_done release_id=$ReleaseId"

if ($MirrorToLive -and -not $SkipPlatformHealthChecks) {
    Invoke-PublicHealthCheck -Name 'trade' -Url 'https://trade.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'work' -Url 'https://work.2884.kr/healthz'
    Invoke-PublicHealthCheck -Name 'itw' -Url 'https://itw.2884.kr/'
}
