[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$AllowedParent = 'D:\DevCaches\georaeplan-private-artifacts',

    [string[]]$ProtectedRoot = @(),

    [switch]$Apply,

    [ValidateSet(
        'None',
        'BeforeCandidateMove',
        'AfterFinalQuarantineValidation',
        'AfterOnePurgeEntry')]
    [string]$TestFaultInjection = 'None'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$ownerFileName = '.georaeplan-artifact-owner.json'
$completionFileName = '.georaeplan-artifact-completion.json'
$parentOwnerFileName = '.georaeplan-retention-parent.json'
$parentLeaseFileName = '.georaeplan-retention-parent.lease'
$expectedOwner = 'georaeplan-private-guid-artifact'
$expectedParentOwnerKind = 'georaeplan-artifact-retention-parent'
$maximumMetadataBytes = 65536
$comparison = [StringComparison]::OrdinalIgnoreCase
$reservedRuntimeNames = @(
    '.georaeplan-runtime-ready',
    '.georaeplan-runtime-invalid',
    '.georaeplan-prepare.lock',
    '.georaeplan-prepare-gate.lock',
    '.georaeplan-artifact.active.lease',
    '.georaeplan-artifact-process.json',
    'Run-All.cmd',
    'Run-All.ps1',
    'Run-App.cmd',
    'Run-Server.cmd',
    'Launch-Test-App.vbs'
)

function Initialize-GeoraePlanArtifactRetentionNativeType {
    if ('GeoraePlan.ArtifactRetention.NativeEntry' -as [type]) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.ArtifactRetention
{
    public sealed class NativeEntry : IDisposable
    {
        private const uint FileReadAttributes = 0x80;
        private const uint FileListDirectory = 0x1;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint ShareRead = 0x1;
        private const uint ShareWrite = 0x2;
        private const uint ShareDelete = 0x4;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint DirectoryAttribute = 0x10;
        private const uint ReparsePointAttribute = 0x400;
        private const uint InvalidAttributes = 0xffffffff;

        private SafeFileHandle handle;

        private NativeEntry(
            string requestedPath,
            SafeFileHandle openedHandle,
            ByHandleFileInformation information,
            string finalPath)
        {
            RequestedPath = requestedPath;
            handle = openedHandle;
            VolumeSerialNumber = information.VolumeSerialNumber;
            FileId = ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow;
            NumberOfLinks = information.NumberOfLinks;
            IsDirectory =
                (information.FileAttributes & DirectoryAttribute) != 0;
            IsReparsePoint =
                (information.FileAttributes & ReparsePointAttribute) != 0;
            FinalPath = NormalizeFinalPath(finalPath);
        }

        public string RequestedPath { get; private set; }
        public string FinalPath { get; private set; }
        public uint VolumeSerialNumber { get; private set; }
        public ulong FileId { get; private set; }
        public uint NumberOfLinks { get; private set; }
        public bool IsDirectory { get; private set; }
        public bool IsReparsePoint { get; private set; }

        public static NativeEntry Open(string path, bool exclusive)
        {
            string fullPath = Path.GetFullPath(path);
            uint attributes = GetFileAttributesW(fullPath);
            if (attributes == InvalidAttributes)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Artifact retention path cannot be inspected.");
            }

            bool isDirectory = (attributes & DirectoryAttribute) != 0;
            uint desiredAccess = isDirectory
                ? FileListDirectory | FileReadAttributes
                : (exclusive ? GenericRead : FileReadAttributes);
            uint share = exclusive
                ? 0
                : ShareRead | ShareWrite | ShareDelete;
            uint flags = OpenReparsePoint |
                (isDirectory ? BackupSemantics : 0);
            SafeFileHandle openedHandle = CreateFileW(
                fullPath,
                desiredAccess,
                share,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (openedHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                openedHandle.Dispose();
                throw new Win32Exception(
                    error,
                    exclusive
                        ? "Artifact entry has an active lease."
                        : "Artifact entry cannot be opened.");
            }

            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(
                    openedHandle,
                    out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Artifact entry identity cannot be read.");
                }

                StringBuilder builder = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(
                    openedHandle,
                    builder,
                    (uint)builder.Capacity,
                    0);
                if (length == 0 || length >= builder.Capacity)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Artifact entry final path cannot be read.");
                }

                return new NativeEntry(
                    fullPath,
                    openedHandle,
                    information,
                    builder.ToString());
            }
            catch
            {
                openedHandle.Dispose();
                throw;
            }
        }

        public static NativeEntry OpenParentDirectoryLease(string path)
        {
            return OpenKnownEntry(
                path,
                true,
                FileListDirectory | FileReadAttributes,
                ShareRead | ShareWrite,
                OpenExisting);
        }

        public static NativeEntry OpenCoordinatorLease(string path)
        {
            return OpenKnownEntry(
                path,
                false,
                GenericRead | GenericWrite,
                0,
                OpenExisting);
        }

        public static NativeEntry OpenPurgeLease(
            string path,
            bool isDirectory)
        {
            uint desiredAccess = DeleteAccess | FileReadAttributes |
                (isDirectory ? FileListDirectory : GenericRead);
            return OpenKnownEntry(
                path,
                isDirectory,
                desiredAccess,
                ShareRead,
                OpenExisting);
        }

        public string ComputeSha256()
        {
            Validate();
            if (IsDirectory)
            {
                throw new IOException(
                    "A directory purge lease cannot be hashed.");
            }

            SafeFileHandle duplicate;
            if (!DuplicateHandle(
                GetCurrentProcess(),
                handle,
                GetCurrentProcess(),
                out duplicate,
                0,
                false,
                DuplicateSameAccess))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Artifact purge lease cannot be duplicated for hashing.");
            }

            try
            {
                using (FileStream stream = new FileStream(
                    duplicate,
                    FileAccess.Read))
                using (SHA256 algorithm = SHA256.Create())
                {
                    duplicate = null;
                    stream.Position = 0;
                    byte[] hash = algorithm.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    foreach (byte value in hash)
                    {
                        builder.Append(value.ToString("X2"));
                    }
                    return builder.ToString();
                }
            }
            finally
            {
                if (duplicate != null)
                {
                    duplicate.Dispose();
                }
            }
        }

        public void MarkDelete()
        {
            Validate();
            FileDispositionInformation disposition =
                new FileDispositionInformation();
            disposition.DeleteFile = true;
            if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf(typeof(FileDispositionInformation))))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Artifact purge lease cannot mark its exact entry for deletion.");
            }
        }

        public void Validate()
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed)
            {
                throw new IOException("Artifact retention lease is closed.");
            }
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Artifact retention lease identity cannot be refreshed.");
            }
            StringBuilder builder = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(
                handle,
                builder,
                (uint)builder.Capacity,
                0);
            if (length == 0 || length >= builder.Capacity)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Artifact retention lease final path cannot be refreshed.");
            }
            string refreshedPath = NormalizeFinalPath(builder.ToString());
            ulong refreshedFileId =
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow;
            if (
                information.VolumeSerialNumber != VolumeSerialNumber ||
                refreshedFileId != FileId ||
                !String.Equals(
                    refreshedPath,
                    FinalPath,
                    StringComparison.OrdinalIgnoreCase) ||
                ((information.FileAttributes & DirectoryAttribute) != 0) !=
                    IsDirectory ||
                (information.FileAttributes & ReparsePointAttribute) != 0 ||
                (!IsDirectory && information.NumberOfLinks != 1))
            {
                throw new IOException(
                    "Artifact retention lease identity changed.");
            }
        }

        private static NativeEntry OpenKnownEntry(
            string path,
            bool isDirectory,
            uint desiredAccess,
            uint share,
            uint creationDisposition)
        {
            string fullPath = Path.GetFullPath(path);
            uint flags = OpenReparsePoint |
                (isDirectory ? BackupSemantics : 0);
            SafeFileHandle openedHandle = CreateFileW(
                fullPath,
                desiredAccess,
                share,
                IntPtr.Zero,
                creationDisposition,
                flags,
                IntPtr.Zero);
            if (openedHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                openedHandle.Dispose();
                throw new Win32Exception(
                    error,
                    "Artifact retention lease cannot be acquired.");
            }
            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(
                    openedHandle,
                    out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Artifact retention lease identity cannot be read.");
                }
                bool actualDirectory =
                    (information.FileAttributes & DirectoryAttribute) != 0;
                if (
                    actualDirectory != isDirectory ||
                    (information.FileAttributes & ReparsePointAttribute) != 0 ||
                    (!actualDirectory && information.NumberOfLinks != 1))
                {
                    throw new IOException(
                        "Artifact retention lease is not an exact regular entry.");
                }
                StringBuilder builder = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(
                    openedHandle,
                    builder,
                    (uint)builder.Capacity,
                    0);
                if (length == 0 || length >= builder.Capacity)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Artifact retention lease final path cannot be read.");
                }
                NativeEntry result = new NativeEntry(
                    fullPath,
                    openedHandle,
                    information,
                    builder.ToString());
                if (!String.Equals(
                    Path.GetFullPath(result.FinalPath),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "Artifact retention lease path identity is invalid.");
                }
                return result;
            }
            catch
            {
                openedHandle.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (handle != null)
            {
                handle.Dispose();
                handle = null;
            }
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            if (path.StartsWith(
                uncPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(uncPrefix.Length);
            }
            if (path.StartsWith(
                devicePrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(devicePrefix.Length);
            }
            return path;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        private const uint DuplicateSameAccess = 0x2;
        private const int FileDispositionInfo = 4;

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

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcess,
            SafeFileHandle sourceHandle,
            IntPtr targetProcess,
            out SafeFileHandle targetHandle,
            uint desiredAccess,
            bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            ref FileDispositionInformation fileInformation,
            uint bufferSize);
    }
}
'@
}

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $volumeRoot = [IO.Path]::GetPathRoot($fullPath)
    $trimmedPath = $fullPath.TrimEnd([char[]]@('\', '/'))
    $trimmedRoot = $volumeRoot.TrimEnd([char[]]@('\', '/'))
    if ([string]::Equals(
        $trimmedPath,
        $trimmedRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
        return $volumeRoot.TrimEnd([char[]]@('\', '/')) +
            [IO.Path]::DirectorySeparatorChar
    }
    return $trimmedPath
}

function Test-PathEquals {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    return [string]::Equals(
        (Get-NormalizedFullPath -Path $Left),
        (Get-NormalizedFullPath -Path $Right),
        [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathInside {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Candidate
    )

    $parentPath = Get-NormalizedFullPath -Path $Parent
    $candidatePath = Get-NormalizedFullPath -Path $Candidate
    $volumeRoot = Get-NormalizedFullPath -Path ([IO.Path]::GetPathRoot($parentPath))
    $prefix = if (Test-PathEquals -Left $parentPath -Right $volumeRoot) {
        $parentPath
    }
    else {
        $parentPath + [IO.Path]::DirectorySeparatorChar
    }
    return $candidatePath.StartsWith(
        $prefix,
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-RegularEntryIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Exclusive
    )

    Initialize-GeoraePlanArtifactRetentionNativeType
    try {
        $entry = [GeoraePlan.ArtifactRetention.NativeEntry]::Open(
            $Path,
            [bool]$Exclusive)
    }
    catch {
        if ($Exclusive) {
            throw 'Artifact entry has an active lease or cannot be exclusively inspected.'
        }
        throw
    }
    try {
        if ($entry.IsReparsePoint) {
            throw 'Artifact retention refuses a reparse point.'
        }
        if (-not (Test-PathEquals -Left $entry.FinalPath -Right $Path)) {
            throw 'Artifact retention path does not match its physical path.'
        }
        if (-not $entry.IsDirectory -and $entry.NumberOfLinks -ne 1) {
            throw 'Artifact retention refuses a multiply-linked file.'
        }

        return [pscustomobject]@{
            Path = Get-NormalizedFullPath -Path $Path
            PhysicalPath = Get-NormalizedFullPath -Path $entry.FinalPath
            VolumeSerialNumber = ('{0:X8}' -f $entry.VolumeSerialNumber)
            FileId = ('{0:X16}' -f $entry.FileId)
            NumberOfLinks = [uint32]$entry.NumberOfLinks
            IsDirectory = [bool]$entry.IsDirectory
        }
    }
    finally {
        $entry.Dispose()
    }
}

function Convert-NativeEntryToIdentity {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][string]$ExpectedPath
    )

    $Entry.Validate()
    if ($Entry.IsReparsePoint) {
        throw 'Artifact retention held identity is a reparse point.'
    }
    if (-not (Test-PathEquals -Left $Entry.FinalPath -Right $ExpectedPath)) {
        throw 'Artifact retention held identity path changed.'
    }
    if (-not $Entry.IsDirectory -and $Entry.NumberOfLinks -ne 1) {
        throw 'Artifact retention held file has multiple hard links.'
    }
    return [pscustomobject]@{
        Path = Get-NormalizedFullPath -Path $ExpectedPath
        PhysicalPath = Get-NormalizedFullPath -Path $Entry.FinalPath
        VolumeSerialNumber = ('{0:X8}' -f $Entry.VolumeSerialNumber)
        FileId = ('{0:X16}' -f $Entry.FileId)
        NumberOfLinks = [uint32]$Entry.NumberOfLinks
        IsDirectory = [bool]$Entry.IsDirectory
    }
}

function Test-PathsOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    return (
        (Test-PathEquals -Left $Left -Right $Right) -or
        (Test-PathInside -Parent $Left -Candidate $Right) -or
        (Test-PathInside -Parent $Right -Candidate $Left))
}

function Get-ProtectedRootDescriptors {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string[]]$AdditionalRoot = @()
    )

    $paths = New-Object System.Collections.Generic.List[string]
    $paths.Add($ProjectRoot)
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
        $paths.Add((Join-Path $localAppData ([IO.Path]::GetFileName($ProjectRoot))))
    }
    $paths.Add('D:\DevCaches\georaeplan-v1-user-snapshots')
    $paths.Add('D:\DevCaches\georaeplan-v1-runtime-snapshots')
    $paths.Add('D:\DevCaches\georaeplan-protected-runtime-snapshots')
    foreach ($path in @($AdditionalRoot)) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            throw 'ProtectedRoot cannot contain an empty value.'
        }
        $paths.Add($path)
    }

    $seen = @{}
    $descriptors = New-Object System.Collections.Generic.List[object]
    foreach ($path in $paths) {
        $logicalPath = Get-NormalizedFullPath -Path $path
        if ($seen.ContainsKey($logicalPath)) {
            continue
        }
        $seen[$logicalPath] = $true
        $physicalPath = $null
        $volumeSerialNumber = $null
        $fileId = $null
        if (Test-Path -LiteralPath $logicalPath) {
            $item = Get-Item -LiteralPath $logicalPath -Force -ErrorAction Stop
            if ($item.PSIsContainer) {
                Assert-RegularDirectoryChain -Path $logicalPath
            }
            else {
                Assert-RegularDirectoryChain -Path ([IO.Path]::GetDirectoryName($logicalPath))
            }
            $identity = Get-RegularEntryIdentity -Path $logicalPath
            $physicalPath = $identity.PhysicalPath
            $volumeSerialNumber = $identity.VolumeSerialNumber
            $fileId = $identity.FileId
        }
        $descriptors.Add([pscustomobject]@{
            LogicalPath = $logicalPath
            PhysicalPath = $physicalPath
            VolumeSerialNumber = $volumeSerialNumber
            FileId = $fileId
        })
    }
    return $descriptors.ToArray()
}

function Assert-NoProtectedRootOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$LogicalPath,
        [Parameter(Mandatory = $true)][string]$PhysicalPath,
        [Parameter(Mandatory = $true)]$ProtectedRoots,
        [Parameter(Mandatory = $true)][string]$Label
    )

    foreach ($protected in @($ProtectedRoots)) {
        if (Test-PathsOverlap -Left $LogicalPath -Right $protected.LogicalPath) {
            throw (
                "$Label overlaps a protected root by logical path. " +
                "protected=$($protected.LogicalPath)")
        }
        if (
            -not [string]::IsNullOrWhiteSpace([string]$protected.PhysicalPath) -and
            (Test-PathsOverlap -Left $PhysicalPath -Right $protected.PhysicalPath)) {
            throw "$Label overlaps a protected root by physical identity/path."
        }
    }
}

function Assert-RegularDirectoryChain {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = Get-NormalizedFullPath -Path $Path
    $volumeRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($volumeRoot)) {
        throw 'AllowedParent must be an absolute path.'
    }

    $current = $volumeRoot.TrimEnd([char[]]@('\', '/'))
    if ($current.Length -eq 2 -and $current[1] -eq ':') {
        $current += [IO.Path]::DirectorySeparatorChar
    }
    $relative = $fullPath.Substring($volumeRoot.Length)
    foreach ($segment in $relative.Split(
        [char[]]@('\', '/'),
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        $identity = Get-RegularEntryIdentity -Path $current
        if (-not $identity.IsDirectory) {
            throw 'AllowedParent path chain contains a non-directory entry.'
        }
    }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($null -eq $Value) {
        throw "$Label is missing."
    }
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count) {
        throw "$Label has an invalid schema."
    }
    for ($index = 0; $index -lt $wanted.Count; $index++) {
        if (-not [string]::Equals(
            $actual[$index],
            $wanted[$index],
            [StringComparison]::Ordinal)) {
            throw "$Label has an invalid schema."
        }
    }
}

function Read-StrictMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $identity = Get-RegularEntryIdentity -Path $Path -Exclusive
    if ($identity.IsDirectory) {
        throw "$Label must be a regular file."
    }
    $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($file.Length -le 0 -or $file.Length -gt $maximumMetadataBytes) {
        throw "$Label has an invalid size."
    }
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$Label is not valid JSON."
    }
}

function Assert-BooleanTrue {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [bool] -or -not $Value) {
        throw "$Label must be true."
    }
}

function Assert-JsonString {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$AllowEmpty
    )

    if ($Value -isnot [string]) {
        throw "$Label must be a JSON string."
    }
    if (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label must not be empty."
    }
}

function Assert-JsonIntegerOne {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [int] -or $Value -ne 1) {
        throw "$Label must be the JSON integer 1."
    }
}

function Assert-RetentionParentOwner {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)]$ParentIdentity
    )

    $markerPath = Join-Path $ParentPath $parentOwnerFileName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw 'The dedicated retention parent owner marker is missing.'
    }
    $metadata = Read-StrictMetadata `
        -Path $markerPath `
        -Label 'retention parent owner metadata'
    Assert-ExactProperties `
        -Value $metadata `
        -Expected @(
            'schemaVersion', 'owner', 'parentId', 'parentPath',
            'parentPhysicalPath', 'parentVolumeSerialNumber',
            'parentFileId') `
        -Label 'retention parent owner metadata'
    Assert-JsonIntegerOne `
        -Value $metadata.schemaVersion `
        -Label 'retention parent owner schemaVersion'
    foreach ($propertyName in @(
        'owner', 'parentId', 'parentPath', 'parentPhysicalPath',
        'parentVolumeSerialNumber', 'parentFileId')) {
        Assert-JsonString `
            -Value $metadata.$propertyName `
            -Label "retention parent owner $propertyName"
    }
    if (([string]$metadata.owner) -cne $expectedParentOwnerKind) {
        throw 'The retention parent owner kind is invalid.'
    }
    if (([string]$metadata.parentId) -notmatch '\A[0-9A-Fa-f]{32}\z') {
        throw 'The retention parent owner ID is invalid.'
    }
    if (
        -not [IO.Path]::IsPathRooted([string]$metadata.parentPath) -or
        -not [IO.Path]::IsPathRooted([string]$metadata.parentPhysicalPath) -or
        -not (Test-PathEquals -Left ([string]$metadata.parentPath) -Right $ParentPath) -or
        -not (Test-PathEquals -Left ([string]$metadata.parentPhysicalPath) -Right $ParentIdentity.PhysicalPath) -or
        ([string]$metadata.parentVolumeSerialNumber) -ine
            ([string]$ParentIdentity.VolumeSerialNumber) -or
        ([string]$metadata.parentFileId) -ine
            ([string]$ParentIdentity.FileId)) {
        throw 'The retention parent owner marker does not match its physical identity.'
    }
    return [pscustomobject]@{
        MarkerPath = $markerPath
        MarkerSha256 = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash
        ParentId = [string]$metadata.parentId
    }
}

function Assert-HeldParentContract {
    param(
        [Parameter(Mandatory = $true)]$DirectoryLease,
        [Parameter(Mandatory = $true)]$CoordinatorLease,
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)]$ExpectedParentIdentity,
        [Parameter(Mandatory = $true)]$ExpectedParentOwner
    )

    $directoryIdentity = Convert-NativeEntryToIdentity `
        -Entry $DirectoryLease `
        -ExpectedPath $ParentPath
    $coordinatorIdentity = Convert-NativeEntryToIdentity `
        -Entry $CoordinatorLease `
        -ExpectedPath (Join-Path $ParentPath $parentLeaseFileName)
    if (
        -not $directoryIdentity.IsDirectory -or
        $coordinatorIdentity.IsDirectory -or
        -not [string]::Equals(
            $directoryIdentity.VolumeSerialNumber,
            $ExpectedParentIdentity.VolumeSerialNumber,
            $comparison) -or
        -not [string]::Equals(
            $directoryIdentity.FileId,
            $ExpectedParentIdentity.FileId,
            $comparison)) {
        throw 'The held retention parent physical identity changed.'
    }
    $freshOwner = Assert-RetentionParentOwner `
        -ParentPath $ParentPath `
        -ParentIdentity $directoryIdentity
    if (
        ([string]$freshOwner.MarkerSha256) -ine
            ([string]$ExpectedParentOwner.MarkerSha256)) {
        throw 'The retention parent owner marker changed while leased.'
    }
}

function Get-ArtifactTreeEntries {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $result = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($RootPath)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            if (
                (Test-PathEquals -Left $child.FullName -Right (Join-Path $RootPath $ownerFileName)) -or
                (Test-PathEquals -Left $child.FullName -Right (Join-Path $RootPath $completionFileName))) {
                continue
            }

            $relative = $child.FullName.Substring($RootPath.Length).TrimStart([char[]]@('\', '/'))
            $relative = $relative.Replace('\', '/')
            $leaf = $child.Name
            if (
                $reservedRuntimeNames -contains $leaf -or
                $leaf.EndsWith('.pid', $comparison) -or
                $leaf.EndsWith('.lock', $comparison) -or
                $leaf.EndsWith('.lck', $comparison) -or
                $leaf.EndsWith('.lease', $comparison)) {
                throw 'Artifact retention found an active/process/runtime marker.'
            }

            $identity = Get-RegularEntryIdentity -Path $child.FullName -Exclusive
            if ($child.PSIsContainer -ne $identity.IsDirectory) {
                throw 'Artifact entry type changed during inspection.'
            }

            $record = [ordered]@{
                relativePath = $relative
                kind = if ($identity.IsDirectory) { 'directory' } else { 'file' }
                sha256 = $null
            }
            if ($identity.IsDirectory) {
                $pending.Push($child.FullName)
            }
            else {
                $record.sha256 = (Get-FileHash -LiteralPath $child.FullName -Algorithm SHA256).Hash
            }
            $result.Add([pscustomobject]$record)
        }
    }

    return @($result | Sort-Object relativePath)
}

function Assert-NoRunningProcessUsesArtifact {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $normalizedRoot = Get-NormalizedFullPath -Path $RootPath
    $rootPrefix = $normalizedRoot +
        [IO.Path]::DirectorySeparatorChar
    try {
        $processes = Get-CimInstance -ClassName Win32_Process -ErrorAction Stop
    }
    catch {
        throw 'Artifact retention cannot prove that no process uses the candidate.'
    }

    foreach ($process in $processes) {
        if ([int64]$process.ProcessId -eq [int64]$PID) {
            continue
        }
        $executablePath = [string]$process.ExecutablePath
        if (
            -not [string]::IsNullOrWhiteSpace($executablePath) -and
            (Get-NormalizedFullPath -Path $executablePath).StartsWith(
                $rootPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Artifact retention found a running process inside the candidate.'
        }
        $commandLine = [string]$process.CommandLine
        if (
            -not [string]::IsNullOrWhiteSpace($commandLine) -and
            ($commandLine.IndexOf(
                $normalizedRoot,
                [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            throw 'Artifact retention found a running process referencing the candidate.'
        }
    }
}

function Assert-EvidenceBundle {
    param(
        [Parameter(Mandatory = $true)]$EvidenceBundle,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    Assert-ExactProperties `
        -Value $EvidenceBundle `
        -Expected @('path', 'sha256') `
        -Label 'completion.evidenceBundle'
    Assert-JsonString -Value $EvidenceBundle.path -Label 'completion.evidenceBundle.path'
    Assert-JsonString -Value $EvidenceBundle.sha256 -Label 'completion.evidenceBundle.sha256'
    if (-not [IO.Path]::IsPathRooted([string]$EvidenceBundle.path)) {
        throw 'Evidence bundle path must be absolute.'
    }
    $evidencePath = Get-NormalizedFullPath -Path ([string]$EvidenceBundle.path)
    if (
        (Test-PathEquals -Left $evidencePath -Right $RootPath) -or
        (Test-PathInside -Parent $RootPath -Candidate $evidencePath)) {
        throw 'Evidence bundle must be outside the artifact root.'
    }
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
        throw 'Evidence bundle is missing.'
    }
    Assert-RegularDirectoryChain -Path ([IO.Path]::GetDirectoryName($evidencePath))
    $identity = Get-RegularEntryIdentity -Path $evidencePath -Exclusive
    if ($identity.IsDirectory) {
        throw 'Evidence bundle must be a regular file.'
    }
    $expectedHash = [string]$EvidenceBundle.sha256
    if ($expectedHash -notmatch '\A[0-9A-Fa-f]{64}\z') {
        throw 'Evidence bundle SHA-256 is malformed.'
    }
    $actualHash = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $expectedHash, $comparison)) {
        throw 'Evidence bundle SHA-256 does not match.'
    }
    return [pscustomobject]@{
        Path = $evidencePath
        PhysicalPath = $identity.PhysicalPath
        VolumeSerialNumber = $identity.VolumeSerialNumber
        FileId = $identity.FileId
        Sha256 = $actualHash
    }
}

function Assert-EvidenceOutsideAllPlannedRoots {
    param([Parameter(Mandatory = $true)]$Plans)

    foreach ($plan in @($Plans)) {
        foreach ($other in @($Plans)) {
            if (
                (Test-PathInside `
                    -Parent $other.RootPath `
                    -Candidate $plan.EvidencePath) -or
                (Test-PathEquals `
                    -Left $other.RootPath `
                    -Right $plan.EvidencePath) -or
                (Test-PathInside `
                    -Parent $other.PhysicalPath `
                    -Candidate $plan.EvidencePhysicalPath) -or
                (Test-PathEquals `
                    -Left $other.PhysicalPath `
                    -Right $plan.EvidencePhysicalPath)) {
                throw 'Evidence bundle is inside another planned artifact root.'
            }
        }
    }
}

function Assert-ExpectedTree {
    param(
        [Parameter(Mandatory = $true)]$ExpectedEntries,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $expected = @($ExpectedEntries)
    $expectedByPath = @{}
    foreach ($entry in $expected) {
        Assert-ExactProperties `
            -Value $entry `
            -Expected @('relativePath', 'kind', 'sha256') `
            -Label 'completion.expectedEntries entry'
        Assert-JsonString -Value $entry.relativePath -Label 'completion.expectedEntries.relativePath'
        Assert-JsonString -Value $entry.kind -Label 'completion.expectedEntries.kind'
        $relative = [string]$entry.relativePath
        if (
            [string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or
            $relative.Contains('\') -or
            $relative.Split('/') -contains '..' -or
            $relative.StartsWith('./', [StringComparison]::Ordinal) -or
            $relative.EndsWith('/', [StringComparison]::Ordinal)) {
            throw 'Expected artifact relative path is unsafe.'
        }
        if ($expectedByPath.ContainsKey($relative)) {
            throw 'Expected artifact entries contain a duplicate path.'
        }
        if ($entry.kind -ne 'file' -and $entry.kind -ne 'directory') {
            throw 'Expected artifact entry kind is invalid.'
        }
        if (
            $entry.kind -eq 'file' -and
            ([string]$entry.sha256) -notmatch '\A[0-9A-Fa-f]{64}\z') {
            throw 'Expected artifact file SHA-256 is malformed.'
        }
        if ($entry.kind -eq 'directory' -and $null -ne $entry.sha256) {
            throw 'Expected artifact directory SHA-256 must be null.'
        }
        $expectedByPath[$relative] = $entry
    }

    $actual = @(Get-ArtifactTreeEntries -RootPath $RootPath)
    if ($actual.Count -ne $expectedByPath.Count) {
        throw 'Artifact tree contains a missing or unexpected entry.'
    }
    foreach ($entry in $actual) {
        if (-not $expectedByPath.ContainsKey($entry.relativePath)) {
            throw 'Artifact tree contains an unexpected entry.'
        }
        $expectedEntry = $expectedByPath[$entry.relativePath]
        if ($expectedEntry.kind -ne $entry.kind) {
            throw 'Artifact tree entry kind does not match.'
        }
        if (
            $entry.kind -eq 'file' -and
            -not [string]::Equals(
                [string]$expectedEntry.sha256,
                [string]$entry.sha256,
                $comparison)) {
            throw 'Artifact tree file SHA-256 does not match.'
        }
    }
}

function New-HeldPurgePlan {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)]$ExpectedEntries,
        [Parameter(Mandatory = $true)][string]$ExpectedVolumeSerialNumber,
        [Parameter(Mandatory = $true)][string]$ExpectedFileId,
        [Parameter(Mandatory = $true)][string]$OwnerSha256,
        [Parameter(Mandatory = $true)][string]$CompletionSha256
    )

    $expectedByPath = @{}
    foreach ($entry in @($ExpectedEntries)) {
        $expectedByPath[[string]$entry.relativePath] = [pscustomobject]@{
            Kind = [string]$entry.kind
            Sha256 = [string]$entry.sha256
        }
    }
    $expectedByPath[$ownerFileName] = [pscustomobject]@{
        Kind = 'file'
        Sha256 = $OwnerSha256
    }
    $expectedByPath[$completionFileName] = [pscustomobject]@{
        Kind = 'file'
        Sha256 = $CompletionSha256
    }

    $records = New-Object System.Collections.Generic.List[object]
    try {
        $rootLease = [GeoraePlan.ArtifactRetention.NativeEntry]::OpenPurgeLease(
            $RootPath,
            $true)
        if (
            -not [string]::Equals(
                ('{0:X8}' -f $rootLease.VolumeSerialNumber),
                $ExpectedVolumeSerialNumber,
                $comparison) -or
            -not [string]::Equals(
                ('{0:X16}' -f $rootLease.FileId),
                $ExpectedFileId,
                $comparison)) {
            $rootLease.Dispose()
            throw 'Quarantined artifact root identity changed before purge.'
        }
        [void]$records.Add([pscustomobject]@{
            RelativePath = $null
            Kind = 'directory'
            Sha256 = $null
            Depth = -1
            Lease = $rootLease
        })

        $pending = New-Object System.Collections.Generic.Stack[string]
        $pending.Push($RootPath)
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            foreach ($child in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
                $relative = $child.FullName.Substring($RootPath.Length).
                    TrimStart([char[]]@('\', '/')).Replace('\', '/')
                if (-not $expectedByPath.ContainsKey($relative)) {
                    throw 'Quarantined artifact tree contains an unexpected entry.'
                }
                $expected = $expectedByPath[$relative]
                $isDirectory = [string]::Equals(
                    $expected.Kind,
                    'directory',
                    [StringComparison]::Ordinal)
                $lease = $null
                try {
                    $lease = [GeoraePlan.ArtifactRetention.NativeEntry]::OpenPurgeLease(
                        $child.FullName,
                        $isDirectory)
                    if ($child.PSIsContainer -ne $lease.IsDirectory) {
                        throw 'Quarantined artifact entry type changed before purge.'
                    }
                    if (
                        -not $isDirectory -and
                        -not [string]::Equals(
                            $lease.ComputeSha256(),
                            [string]$expected.Sha256,
                            $comparison)) {
                        throw 'Quarantined artifact file changed before purge.'
                    }
                    [void]$records.Add([pscustomobject]@{
                        RelativePath = $relative
                        Kind = [string]$expected.Kind
                        Sha256 = [string]$expected.Sha256
                        Depth = @($relative.Split('/')).Count
                        Lease = $lease
                    })
                    $lease = $null
                    if ($isDirectory) {
                        $pending.Push($child.FullName)
                    }
                }
                finally {
                    if ($null -ne $lease) {
                        $lease.Dispose()
                    }
                }
            }
        }
        if ($records.Count -ne ($expectedByPath.Count + 1)) {
            throw 'Quarantined artifact tree contains a missing entry.'
        }
        return [pscustomobject]@{
            RootPath = $RootPath
            ExpectedByPath = $expectedByPath
            Records = $records
        }
    }
    catch {
        foreach ($record in $records) {
            if ($null -ne $record.Lease) {
                $record.Lease.Dispose()
                $record.Lease = $null
            }
        }
        throw
    }
}

function Assert-HeldPurgePlan {
    param([Parameter(Mandatory = $true)]$PurgePlan)

    $heldByPath = @{}
    foreach ($record in $PurgePlan.Records) {
        $record.Lease.Validate()
        if (
            $record.Kind -eq 'file' -and
            -not [string]::Equals(
                $record.Lease.ComputeSha256(),
                [string]$record.Sha256,
                $comparison)) {
            throw 'Held quarantine file changed before purge.'
        }
        if ($null -ne $record.RelativePath) {
            $heldByPath[[string]$record.RelativePath] = $record
        }
    }

    $seen = @{}
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push([string]$PurgePlan.RootPath)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            $relative = $child.FullName.Substring($PurgePlan.RootPath.Length).
                TrimStart([char[]]@('\', '/')).Replace('\', '/')
            if (-not $heldByPath.ContainsKey($relative)) {
                throw 'Quarantined artifact tree changed after purge leases were acquired.'
            }
            $record = $heldByPath[$relative]
            if ($child.PSIsContainer -ne ($record.Kind -eq 'directory')) {
                throw 'Quarantined artifact entry type changed while purge leases were held.'
            }
            $seen[$relative] = $true
            if ($record.Kind -eq 'directory') {
                $pending.Push($child.FullName)
            }
        }
    }
    if ($seen.Count -ne $heldByPath.Count) {
        throw 'Quarantined artifact entry disappeared while purge leases were held.'
    }
}

function Close-HeldPurgePlan {
    param([Parameter(Mandatory = $true)]$PurgePlan)

    foreach ($record in $PurgePlan.Records) {
        if ($null -ne $record.Lease) {
            $record.Lease.Dispose()
            $record.Lease = $null
        }
    }
}

function Invoke-HeldPurge {
    param(
        [Parameter(Mandatory = $true)]$PurgePlan,
        [Parameter(Mandatory = $true)][string]$FaultInjection
    )

    $payloadFiles = @(
        $PurgePlan.Records |
            Where-Object {
                $_.Kind -eq 'file' -and
                $_.RelativePath -ne $ownerFileName -and
                $_.RelativePath -ne $completionFileName
            } |
            Sort-Object RelativePath
    )
    $metadataFiles = @(
        $PurgePlan.Records |
            Where-Object {
                $_.Kind -eq 'file' -and
                ($_.RelativePath -eq $ownerFileName -or
                    $_.RelativePath -eq $completionFileName)
            } |
            Sort-Object RelativePath
    )
    $deletedPayload = $false
    foreach ($record in @($payloadFiles) + @($metadataFiles)) {
        $record.Lease.Validate()
        if (-not [string]::Equals(
            $record.Lease.ComputeSha256(),
            [string]$record.Sha256,
            $comparison)) {
            throw 'Held quarantine file changed at the purge boundary.'
        }
        $record.Lease.MarkDelete()
        $record.Lease.Dispose()
        $record.Lease = $null
        if ($payloadFiles -contains $record) {
            $deletedPayload = $true
        }
        if (
            $FaultInjection -eq 'AfterOnePurgeEntry' -and
            $deletedPayload) {
            throw 'Injected purge failure after one deletion; partial quarantine was preserved.'
        }
    }

    $directories = @(
        $PurgePlan.Records |
            Where-Object {
                $_.Kind -eq 'directory' -and
                $null -ne $_.RelativePath
            } |
            Sort-Object Depth -Descending
    )
    foreach ($record in $directories) {
        $record.Lease.Validate()
        $record.Lease.MarkDelete()
        $record.Lease.Dispose()
        $record.Lease = $null
    }

    $rootRecord = @(
        $PurgePlan.Records |
            Where-Object { $null -eq $_.RelativePath }
    )
    if ($rootRecord.Count -ne 1) {
        throw 'Held purge plan lost its exact root lease.'
    }
    $rootRecord[0].Lease.Validate()
    $rootRecord[0].Lease.MarkDelete()
    $rootRecord[0].Lease.Dispose()
    $rootRecord[0].Lease = $null
}

function Assert-ArtifactCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][IO.DirectoryInfo]$Candidate,
        [Parameter(Mandatory = $true)]$ProtectedRoots
    )

    $rootPath = Get-NormalizedFullPath -Path $Candidate.FullName
    if ($Candidate.Name -notmatch '\A[0-9A-Fa-f]{32}\z') {
        throw 'Artifact root name must be an exact 32-hex GUID.'
    }
    if (-not (Test-PathEquals -Left $Candidate.Parent.FullName -Right $ParentPath)) {
        throw 'Artifact root must be a direct child of AllowedParent.'
    }

    $rootIdentity = Get-RegularEntryIdentity -Path $rootPath -Exclusive
    if (-not $rootIdentity.IsDirectory) {
        throw 'Artifact root must be a regular directory.'
    }
    Assert-NoProtectedRootOverlap `
        -LogicalPath $rootPath `
        -PhysicalPath $rootIdentity.PhysicalPath `
        -ProtectedRoots $ProtectedRoots `
        -Label 'Artifact candidate'
    if (-not [string]::Equals(
        $rootIdentity.VolumeSerialNumber,
        (Get-RegularEntryIdentity -Path $ParentPath).VolumeSerialNumber,
        $comparison)) {
        throw 'Artifact root must remain on the AllowedParent volume.'
    }

    $ownerPath = Join-Path $rootPath $ownerFileName
    $completionPath = Join-Path $rootPath $completionFileName
    if (
        -not (Test-Path -LiteralPath $ownerPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $completionPath -PathType Leaf)) {
        throw 'Artifact ownership/completion metadata is missing.'
    }

    $owner = Read-StrictMetadata -Path $ownerPath -Label 'owner metadata'
    Assert-ExactProperties `
        -Value $owner `
        -Expected @(
            'schemaVersion', 'owner', 'artifactId', 'createdAtUtc',
            'rootPath', 'rootPhysicalPath', 'rootVolumeSerialNumber',
            'rootFileId') `
        -Label 'owner metadata'
    Assert-JsonIntegerOne -Value $owner.schemaVersion -Label 'owner metadata schemaVersion'
    Assert-JsonString -Value $owner.owner -Label 'owner metadata owner'
    Assert-JsonString -Value $owner.artifactId -Label 'owner metadata artifactId'
    Assert-JsonString -Value $owner.createdAtUtc -Label 'owner metadata createdAtUtc'
    Assert-JsonString -Value $owner.rootPath -Label 'owner metadata rootPath'
    Assert-JsonString -Value $owner.rootPhysicalPath -Label 'owner metadata rootPhysicalPath'
    Assert-JsonString -Value $owner.rootVolumeSerialNumber -Label 'owner metadata rootVolumeSerialNumber'
    Assert-JsonString -Value $owner.rootFileId -Label 'owner metadata rootFileId'
    if (-not [string]::Equals([string]$owner.owner, $expectedOwner, [StringComparison]::Ordinal)) {
        throw 'Owner metadata owner is invalid.'
    }
    if (-not [string]::Equals([string]$owner.artifactId, $Candidate.Name, $comparison)) {
        throw 'Owner metadata artifactId does not match the directory.'
    }
    if (
        -not [IO.Path]::IsPathRooted([string]$owner.rootPath) -or
        -not [IO.Path]::IsPathRooted([string]$owner.rootPhysicalPath)) {
        throw 'Owner metadata root paths must be absolute.'
    }
    $createdAt = [DateTimeOffset]::MinValue
    if (
        -not [DateTimeOffset]::TryParseExact(
            [string]$owner.createdAtUtc,
            'o',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$createdAt) -or
        $createdAt.Offset -ne [TimeSpan]::Zero) {
        throw 'Owner metadata createdAtUtc is invalid.'
    }
    if (
        -not (Test-PathEquals -Left ([string]$owner.rootPath) -Right $rootPath) -or
        -not (Test-PathEquals -Left ([string]$owner.rootPhysicalPath) -Right $rootIdentity.PhysicalPath) -or
        -not [string]::Equals([string]$owner.rootVolumeSerialNumber, $rootIdentity.VolumeSerialNumber, $comparison) -or
        -not [string]::Equals([string]$owner.rootFileId, $rootIdentity.FileId, $comparison)) {
        throw 'Owner metadata does not match the root physical identity.'
    }

    $completion = Read-StrictMetadata -Path $completionPath -Label 'completion metadata'
    Assert-ExactProperties `
        -Value $completion `
        -Expected @(
            'schemaVersion', 'artifactId', 'outcome', 'testGate',
            'gitPushGate', 'postflightGate', 'evidenceBundle',
            'expectedEntries') `
        -Label 'completion metadata'
    Assert-JsonIntegerOne -Value $completion.schemaVersion -Label 'completion metadata schemaVersion'
    Assert-JsonString -Value $completion.artifactId -Label 'completion metadata artifactId'
    Assert-JsonString -Value $completion.outcome -Label 'completion metadata outcome'
    if (
        -not [string]::Equals([string]$completion.artifactId, $Candidate.Name, $comparison) -or
        -not [string]::Equals([string]$completion.outcome, 'succeeded', [StringComparison]::Ordinal)) {
        throw 'Completion metadata identity/outcome is invalid.'
    }
    Assert-ExactProperties -Value $completion.testGate -Expected @('passed') -Label 'completion.testGate'
    Assert-BooleanTrue -Value $completion.testGate.passed -Label 'completion.testGate.passed'
    Assert-ExactProperties `
        -Value $completion.gitPushGate `
        -Expected @('passed', 'commitSha', 'remote') `
        -Label 'completion.gitPushGate'
    Assert-BooleanTrue -Value $completion.gitPushGate.passed -Label 'completion.gitPushGate.passed'
    Assert-JsonString -Value $completion.gitPushGate.commitSha -Label 'completion.gitPushGate.commitSha'
    Assert-JsonString -Value $completion.gitPushGate.remote -Label 'completion.gitPushGate.remote'
    if (
        ([string]$completion.gitPushGate.commitSha) -notmatch '\A[0-9A-Fa-f]{40}\z' -or
        [string]::IsNullOrWhiteSpace([string]$completion.gitPushGate.remote)) {
        throw 'Completion Git push evidence is invalid.'
    }
    Assert-ExactProperties -Value $completion.postflightGate -Expected @('passed') -Label 'completion.postflightGate'
    Assert-BooleanTrue -Value $completion.postflightGate.passed -Label 'completion.postflightGate.passed'
    $evidence = Assert-EvidenceBundle `
        -EvidenceBundle $completion.evidenceBundle `
        -RootPath $rootPath
    Assert-ExpectedTree -ExpectedEntries $completion.expectedEntries -RootPath $rootPath
    Assert-NoRunningProcessUsesArtifact -RootPath $rootPath

    $freshIdentity = Get-RegularEntryIdentity -Path $rootPath -Exclusive
    if (
        -not [string]::Equals($freshIdentity.PhysicalPath, $rootIdentity.PhysicalPath, $comparison) -or
        -not [string]::Equals($freshIdentity.VolumeSerialNumber, $rootIdentity.VolumeSerialNumber, $comparison) -or
        -not [string]::Equals($freshIdentity.FileId, $rootIdentity.FileId, $comparison)) {
        throw 'Artifact root identity changed during validation.'
    }

    return [pscustomobject]@{
        ArtifactId = $Candidate.Name
        RootPath = $rootPath
        PhysicalPath = $rootIdentity.PhysicalPath
        VolumeSerialNumber = $rootIdentity.VolumeSerialNumber
        FileId = $rootIdentity.FileId
        OwnerMetadataSha256 = (Get-FileHash -LiteralPath $ownerPath -Algorithm SHA256).Hash
        CompletionMetadataSha256 = (Get-FileHash -LiteralPath $completionPath -Algorithm SHA256).Hash
        EvidencePath = $evidence.Path
        EvidencePhysicalPath = $evidence.PhysicalPath
        EvidenceVolumeSerialNumber = $evidence.VolumeSerialNumber
        EvidenceFileId = $evidence.FileId
        EvidenceSha256 = $evidence.Sha256
    }
}

function Remove-ValidatedArtifactCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)]$Plan,
        [Parameter(Mandatory = $true)]$ProtectedRoots,
        [Parameter(Mandatory = $true)]$ParentDirectoryLease,
        [Parameter(Mandatory = $true)]$ParentCoordinatorLease,
        [Parameter(Mandatory = $true)]$ParentIdentity,
        [Parameter(Mandatory = $true)]$ParentOwner,
        [Parameter(Mandatory = $true)][string]$FaultInjection
    )

    Assert-HeldParentContract `
        -DirectoryLease $ParentDirectoryLease `
        -CoordinatorLease $ParentCoordinatorLease `
        -ParentPath $ParentPath `
        -ExpectedParentIdentity $ParentIdentity `
        -ExpectedParentOwner $ParentOwner
    $candidate = Get-Item -LiteralPath $Plan.RootPath -Force -ErrorAction Stop
    $fresh = Assert-ArtifactCandidate `
        -ParentPath $ParentPath `
        -Candidate $candidate `
        -ProtectedRoots $ProtectedRoots
    if (
        -not [string]::Equals($fresh.PhysicalPath, $Plan.PhysicalPath, $comparison) -or
        -not [string]::Equals($fresh.VolumeSerialNumber, $Plan.VolumeSerialNumber, $comparison) -or
        -not [string]::Equals($fresh.FileId, $Plan.FileId, $comparison) -or
        -not [string]::Equals($fresh.OwnerMetadataSha256, $Plan.OwnerMetadataSha256, $comparison) -or
        -not [string]::Equals($fresh.CompletionMetadataSha256, $Plan.CompletionMetadataSha256, $comparison) -or
        -not [string]::Equals($fresh.EvidencePhysicalPath, $Plan.EvidencePhysicalPath, $comparison) -or
        -not [string]::Equals($fresh.EvidenceVolumeSerialNumber, $Plan.EvidenceVolumeSerialNumber, $comparison) -or
        -not [string]::Equals($fresh.EvidenceFileId, $Plan.EvidenceFileId, $comparison) -or
        -not [string]::Equals($fresh.EvidenceSha256, $Plan.EvidenceSha256, $comparison)) {
        throw 'Artifact root identity changed after preflight.'
    }

    if ($FaultInjection -eq 'BeforeCandidateMove') {
        Write-Output (
            'artifact_retention_test_hook=before_candidate_move artifact_id=' +
            $fresh.ArtifactId)
        Start-Sleep -Seconds 3
    }
    Assert-HeldParentContract `
        -DirectoryLease $ParentDirectoryLease `
        -CoordinatorLease $ParentCoordinatorLease `
        -ParentPath $ParentPath `
        -ExpectedParentIdentity $ParentIdentity `
        -ExpectedParentOwner $ParentOwner
    $tombstonePath = Join-Path $ParentPath (
        '.georaeplan-retention-' + $fresh.ArtifactId + '-' +
        [Guid]::NewGuid().ToString('N') + '.quarantine')
    [IO.Directory]::Move($fresh.RootPath, $tombstonePath)
    Assert-HeldParentContract `
        -DirectoryLease $ParentDirectoryLease `
        -CoordinatorLease $ParentCoordinatorLease `
        -ParentPath $ParentPath `
        -ExpectedParentIdentity $ParentIdentity `
        -ExpectedParentOwner $ParentOwner
    $tombstoneIdentity = Get-RegularEntryIdentity -Path $tombstonePath -Exclusive
    if (
        -not [string]::Equals($tombstoneIdentity.VolumeSerialNumber, $fresh.VolumeSerialNumber, $comparison) -or
        -not [string]::Equals($tombstoneIdentity.FileId, $fresh.FileId, $comparison)) {
        throw 'Quarantined artifact identity does not match the validated root; quarantine was preserved.'
    }

    $tombstoneOwnerPath = Join-Path $tombstonePath $ownerFileName
    $tombstoneCompletionPath = Join-Path $tombstonePath $completionFileName
    $ownerHash = (Get-FileHash -LiteralPath $tombstoneOwnerPath -Algorithm SHA256).Hash
    $completionHash = (Get-FileHash -LiteralPath $tombstoneCompletionPath -Algorithm SHA256).Hash
    if (
        -not [string]::Equals($ownerHash, $fresh.OwnerMetadataSha256, $comparison) -or
        -not [string]::Equals($completionHash, $fresh.CompletionMetadataSha256, $comparison)) {
        throw 'Quarantined artifact metadata changed; quarantine was preserved.'
    }
    $tombstoneCompletion = Read-StrictMetadata `
        -Path $tombstoneCompletionPath `
        -Label 'quarantined completion metadata'
    Assert-ExpectedTree `
        -ExpectedEntries $tombstoneCompletion.expectedEntries `
        -RootPath $tombstonePath
    $purgePlan = $null
    try {
        $purgePlan = New-HeldPurgePlan `
            -RootPath $tombstonePath `
            -ExpectedEntries $tombstoneCompletion.expectedEntries `
            -ExpectedVolumeSerialNumber $fresh.VolumeSerialNumber `
            -ExpectedFileId $fresh.FileId `
            -OwnerSha256 $fresh.OwnerMetadataSha256 `
            -CompletionSha256 $fresh.CompletionMetadataSha256
        Assert-HeldPurgePlan -PurgePlan $purgePlan

        if ($FaultInjection -eq 'AfterFinalQuarantineValidation') {
            Write-Output (
                'artifact_retention_test_hook=after_final_quarantine_validation ' +
                'artifact_id=' + $fresh.ArtifactId)
            Start-Sleep -Seconds 3
        }

        Assert-HeldParentContract `
            -DirectoryLease $ParentDirectoryLease `
            -CoordinatorLease $ParentCoordinatorLease `
            -ParentPath $ParentPath `
            -ExpectedParentIdentity $ParentIdentity `
            -ExpectedParentOwner $ParentOwner
        Assert-HeldPurgePlan -PurgePlan $purgePlan
        Invoke-HeldPurge `
            -PurgePlan $purgePlan `
            -FaultInjection $FaultInjection
    }
    finally {
        if ($null -ne $purgePlan) {
            Close-HeldPurgePlan -PurgePlan $purgePlan
        }
    }
}

if (
    $TestFaultInjection -ne 'None' -and
    -not [string]::Equals(
        [Environment]::GetEnvironmentVariable(
            'GEORAEPLAN_ARTIFACT_RETENTION_TEST_MODE'),
        '1',
        [StringComparison]::Ordinal)) {
    throw 'Artifact retention fault injection is restricted to explicit test mode.'
}

$projectRoot = Get-NormalizedFullPath -Path (Join-Path $PSScriptRoot '..\..')
$allowedParentPath = Get-NormalizedFullPath -Path $AllowedParent
if (-not (Test-Path -LiteralPath $allowedParentPath -PathType Container)) {
    throw 'AllowedParent must be the existing dedicated retention parent.'
}
Assert-RegularDirectoryChain -Path $allowedParentPath
Initialize-GeoraePlanArtifactRetentionNativeType
$parentDirectoryLease = $null
$parentCoordinatorLease = $null
try {
    try {
        $parentDirectoryLease =
            [GeoraePlan.ArtifactRetention.NativeEntry]::OpenParentDirectoryLease(
                $allowedParentPath)
        $parentCoordinatorLease =
            [GeoraePlan.ArtifactRetention.NativeEntry]::OpenCoordinatorLease(
                (Join-Path $allowedParentPath $parentLeaseFileName))
    }
    catch {
        throw 'The parent/producer retention lease cannot be acquired; no artifact was changed.'
    }
    $parentIdentity = Convert-NativeEntryToIdentity `
        -Entry $parentDirectoryLease `
        -ExpectedPath $allowedParentPath
    if (-not $parentIdentity.IsDirectory) {
        throw 'AllowedParent must be a regular directory.'
    }
    $parentOwner = Assert-RetentionParentOwner `
        -ParentPath $allowedParentPath `
        -ParentIdentity $parentIdentity
    $protectedRoots = @(
        Get-ProtectedRootDescriptors `
            -ProjectRoot $projectRoot `
            -AdditionalRoot $ProtectedRoot
    )
    Assert-NoProtectedRootOverlap `
        -LogicalPath $allowedParentPath `
        -PhysicalPath $parentIdentity.PhysicalPath `
        -ProtectedRoots $protectedRoots `
        -Label 'Dedicated retention parent'
    Assert-HeldParentContract `
        -DirectoryLease $parentDirectoryLease `
        -CoordinatorLease $parentCoordinatorLease `
        -ParentPath $allowedParentPath `
        -ExpectedParentIdentity $parentIdentity `
        -ExpectedParentOwner $parentOwner

    $guidDirectories = @(
        Get-ChildItem -LiteralPath $allowedParentPath -Force -Directory |
            Where-Object { $_.Name -match '\A[0-9A-Fa-f]{32}\z' } |
            Sort-Object Name
    )
    $plans = New-Object System.Collections.Generic.List[object]
    foreach ($directory in $guidDirectories) {
        $plans.Add((Assert-ArtifactCandidate `
            -ParentPath $allowedParentPath `
            -Candidate $directory `
            -ProtectedRoots $protectedRoots))
    }
    Assert-EvidenceOutsideAllPlannedRoots -Plans $plans.ToArray()
    Assert-HeldParentContract `
        -DirectoryLease $parentDirectoryLease `
        -CoordinatorLease $parentCoordinatorLease `
        -ParentPath $allowedParentPath `
        -ExpectedParentIdentity $parentIdentity `
        -ExpectedParentOwner $parentOwner

    if (-not $Apply) {
        foreach ($plan in $plans) {
            Write-Output ("artifact_retention=DRY_RUN artifact_id={0} action=would_quarantine_and_purge" -f $plan.ArtifactId)
        }
        Write-Output ("artifact_retention=DRY_RUN candidate_count={0}" -f $plans.Count)
        return
    }

    foreach ($plan in $plans) {
        Remove-ValidatedArtifactCandidate `
            -ParentPath $allowedParentPath `
            -Plan $plan `
            -ProtectedRoots $protectedRoots `
            -ParentDirectoryLease $parentDirectoryLease `
            -ParentCoordinatorLease $parentCoordinatorLease `
            -ParentIdentity $parentIdentity `
            -ParentOwner $parentOwner `
            -FaultInjection $TestFaultInjection
        Write-Output ("artifact_retention=APPLIED artifact_id={0} action=quarantined_and_purged" -f $plan.ArtifactId)
    }
    Write-Output ("artifact_retention=APPLIED purged_count={0}" -f $plans.Count)
}
finally {
    if ($null -ne $parentCoordinatorLease) {
        $parentCoordinatorLease.Dispose()
    }
    if ($null -ne $parentDirectoryLease) {
        $parentDirectoryLease.Dispose()
    }
}
