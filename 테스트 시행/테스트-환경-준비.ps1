[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$Configuration = 'Debug',
    [string]$OutputRoot,
    [string]$SourceAppRoot,
    [string]$SourceApiBaseUrl,
    [string]$SourceUsersSnapshotPath,
    [string]$SourceUsersSnapshotSha256,
    [string]$AndroidPackagePath,
    [string]$ApkAnalyzerPath,
    [string]$JavaSdkDirectory,
    [switch]$SkipBuild,
    [switch]$SkipDataCopy,
    [switch]$SkipServerSeed,
    [switch]$AllowRemoteSourceApi,
    [switch]$AllowFallbackOperationalUsers,
    [switch]$ResetUnresolvedUserPasswordsForIsolatedTest,
    [switch]$ResetAllUserPasswordsForIsolatedTest,
    [switch]$CanonicalizeLegacyInvoiceSeed,
    [string]$CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256,
    [switch]$AllowDirtySeedFailure,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

if ($ResetAllUserPasswordsForIsolatedTest) {
    if ($SkipServerSeed) {
        throw '-ResetAllUserPasswordsForIsolatedTest requires server seed preparation.'
    }
    if ($AllowFallbackOperationalUsers) {
        throw (
            '-ResetAllUserPasswordsForIsolatedTest cannot be combined with ' +
            '-AllowFallbackOperationalUsers.')
    }
    if ($AllowRemoteSourceApi) {
        throw (
            '-ResetAllUserPasswordsForIsolatedTest cannot be combined with ' +
            '-AllowRemoteSourceApi.')
    }
    if ($ResetUnresolvedUserPasswordsForIsolatedTest) {
        throw (
            '-ResetAllUserPasswordsForIsolatedTest cannot be combined with ' +
            '-ResetUnresolvedUserPasswordsForIsolatedTest.')
    }
    if ([string]::IsNullOrWhiteSpace($SourceUsersSnapshotPath)) {
        throw (
            '-ResetAllUserPasswordsForIsolatedTest requires ' +
            '-SourceUsersSnapshotPath.')
    }
    if (
        [string]::IsNullOrWhiteSpace($SourceUsersSnapshotSha256) -or
        $SourceUsersSnapshotSha256.Trim() -cnotmatch '^[A-Fa-f0-9]{64}$'
    ) {
        throw (
            '-ResetAllUserPasswordsForIsolatedTest requires a valid ' +
            '-SourceUsersSnapshotSha256.')
    }
}

function Resolve-DotnetCommand {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

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

    throw "테스트 환경 준비용 dotnet 실행 파일을 찾지 못했습니다. ProjectRoot=$ProjectRoot"
}

function New-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding($false)
}

function New-Utf8BomEncoding {
    return New-Object System.Text.UTF8Encoding($true)
}

function Assert-SafeSourceApiBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [switch]$AllowRemote
    )

    $sourceApiUri = $null
    if (
        -not [Uri]::TryCreate(
            $BaseUrl.Trim(),
            [UriKind]::Absolute,
            [ref]$sourceApiUri) -or
        ($sourceApiUri.Scheme -ine [Uri]::UriSchemeHttp -and
         $sourceApiUri.Scheme -ine [Uri]::UriSchemeHttps)
    ) {
        throw 'SourceApiBaseUrl must be an absolute HTTP(S) URL.'
    }

    if (
        -not [string]::IsNullOrEmpty($sourceApiUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($sourceApiUri.Query) -or
        -not [string]::IsNullOrEmpty($sourceApiUri.Fragment)
    ) {
        throw (
            'SourceApiBaseUrl cannot contain user information, a query, ' +
            'or a fragment.')
    }

    if (-not $sourceApiUri.IsLoopback -and -not $AllowRemote) {
        throw (
            'Remote SourceApiBaseUrl is blocked for isolated test seeding. ' +
            'Use a loopback URL or explicitly pass -AllowRemoteSourceApi.')
    }
    if (
        -not $sourceApiUri.IsLoopback -and
        $sourceApiUri.Scheme -ine [Uri]::UriSchemeHttps
    ) {
        throw 'Remote SourceApiBaseUrl must use HTTPS.'
    }

    return $sourceApiUri.AbsoluteUri.TrimEnd('/')
}

function Get-IsolatedBuildEnvironmentPaths {
    $buildCacheRoot = 'D:\DevCaches\georaeplan-v1-prepare'
    return [ordered]@{
        TEMP = Join-Path $buildCacheRoot 'temp'
        TMP = Join-Path $buildCacheRoot 'temp'
        NUGET_PACKAGES = Join-Path $buildCacheRoot 'nuget\packages'
        NUGET_HTTP_CACHE_PATH = Join-Path $buildCacheRoot 'nuget\http-cache'
        NUGET_PLUGINS_CACHE_PATH = Join-Path $buildCacheRoot 'nuget\plugins-cache'
        DOTNET_CLI_HOME = Join-Path $buildCacheRoot 'dotnet-home'
    }
}

function Initialize-IsolatedBuildEnvironmentOnD {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$EnvironmentPaths
    )

    foreach ($path in @($environmentPaths.Values | Sort-Object -Unique)) {
        $fullPath = [IO.Path]::GetFullPath($path)
        if (
            -not [string]::Equals(
                [IO.Path]::GetPathRoot($fullPath),
                'D:\',
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'The isolated build environment must remain on D:.'
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            throw (
                'Unsafe isolated build cache: cache directories must be ' +
                "provisioned before preparation. Path=$fullPath")
        }
        $sentinelPath = Join-Path $fullPath '.georaeplan-build-cache-lease'
        if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
            throw (
                'Unsafe isolated build cache: cache lease sentinel must be ' +
                "provisioned before preparation. Path=$sentinelPath")
        }
        $sentinelItem = Get-Item `
            -LiteralPath $sentinelPath `
            -Force `
            -ErrorAction Stop
        if (
            $sentinelItem.PSIsContainer -or
            ($sentinelItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw (
                'Unsafe isolated build cache: cache lease sentinel must be ' +
                "a plain file. Path=$sentinelPath")
        }
    }

    foreach ($name in $environmentPaths.Keys) {
        [Environment]::SetEnvironmentVariable(
            $name,
            [IO.Path]::GetFullPath([string]$environmentPaths[$name]),
            'Process')
    }
}

function Initialize-TestEnvironmentFinalPathNativeMethods {
    if ($null -ne ('GeoraePlan.TestEnvironment.FinalPathNativeMethods' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.TestEnvironment
{
    public static class FinalPathNativeMethods
    {
        private const int MaximumPrivateTreeEntries = 8192;
        private const long MaximumPrivateTreeBytes = 805306368L;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint FileShareDelete = 0x00000004;
        public const uint DeleteAccess = 0x00010000;
        public const uint SynchronizeAccess = 0x00100000;
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileListDirectory = 0x00000001;
        public const uint FileReadAttributes = 0x00000080;
        public const uint OpenExisting = 3;
        public const uint FileFlagBackupSemantics = 0x02000000;
        public const uint FileFlagOpenReparsePoint = 0x00200000;
        public const int FileDispositionInfoClass = 4;
        public const int FileRenameInfoClass = 3;
        private const uint ObjectCaseInsensitive = 0x00000040;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileCreate = 2;
        private const uint FileOpen = 1;
        private const uint FileDirectoryFile = 0x00000001;
        private const uint FileNonDirectoryFile = 0x00000040;
        private const uint FileSynchronousIoNonAlert = 0x00000020;
        private const uint FileOpenReparsePoint = 0x00200000;
        private const int FileRenameInformationClass = 10;
        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusNoMoreFiles = unchecked((int)0x80000006);
        private const int FileIdBothDirectoryInformationClass = 37;

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
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
        public static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            ref FileDispositionInformation fileInformation,
            uint bufferSize);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "SetFileInformationByHandle",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandleBuffer(
            SafeFileHandle file,
            int fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(
            out IntPtr fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(
            IntPtr fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryDirectoryFile(
            IntPtr fileHandle,
            IntPtr eventHandle,
            IntPtr apcRoutine,
            IntPtr apcContext,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass,
            [MarshalAs(UnmanagedType.Bool)] bool returnSingleEntry,
            IntPtr fileName,
            [MarshalAs(UnmanagedType.Bool)] bool restartScan);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFilePointerEx(
            SafeFileHandle file,
            long distance,
            out long newPosition,
            uint moveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetEndOfFile(SafeFileHandle file);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(
            SafeFileHandle file,
            byte[] buffer,
            uint bytesToWrite,
            out uint bytesWritten,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(
            SafeFileHandle file,
            byte[] buffer,
            uint bytesToRead,
            out uint bytesRead,
            IntPtr overlapped);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(
            string newFileName,
            string existingFileName,
            IntPtr securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushFileBuffers(SafeFileHandle file);

        [DllImport(
            "advapi32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                string securityDescriptor,
                uint revision,
                out IntPtr descriptor,
                out uint descriptorSize);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(
            string path,
            ref SecurityAttributes securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static void CreatePrivateDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            IntPtr descriptor = CreatePrivateSecurityDescriptor();

            try
            {
                SecurityAttributes attributes = new SecurityAttributes {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                    SecurityDescriptor = descriptor,
                    InheritHandle = false
                };
                if (!CreateDirectoryW(fullPath, ref attributes))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to atomically create the private directory.");
                }
            }
            finally
            {
                LocalFree(descriptor);
            }

        }

        private static IntPtr CreatePrivateSecurityDescriptor()
        {
            SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User;
            string sid = userSid.Value;
            string sddl =
                "O:" + sid +
                "G:" + sid +
                "D:P" +
                "(A;OICI;FA;;;" + sid + ")" +
                "(A;OICI;FA;;;SY)";
            IntPtr descriptor;
            uint descriptorSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                1,
                out descriptor,
                out descriptorSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to create the private directory security descriptor.");
            }
            return descriptor;
        }

        public static SafeFileHandle CreatePrivateDirectoryUnderHeldParent(
            SafeFileHandle parentHandle,
            string expectedParent,
            string leaf)
        {
            if (parentHandle == null || parentHandle.IsInvalid ||
                parentHandle.IsClosed)
            {
                throw new InvalidOperationException(
                    "The private directory parent lease is invalid.");
            }
            if (string.IsNullOrEmpty(leaf) ||
                !string.Equals(leaf, Path.GetFileName(leaf),
                    StringComparison.Ordinal) ||
                leaf == "." || leaf == "..")
            {
                throw new InvalidOperationException(
                    "The private directory leaf is unsafe.");
            }

            string fullParent = Path.GetFullPath(expectedParent);
            ByHandleFileInformation parentInformation =
                GetFileInformation(parentHandle);
            FileAttributes parentAttributes =
                (FileAttributes)parentInformation.FileAttributes;
            if ((parentAttributes & FileAttributes.Directory) == 0 ||
                (parentAttributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(
                    fullParent,
                    Path.GetFullPath(GetFinalPath(parentHandle)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The private directory parent identity changed.");
            }

            IntPtr descriptor = CreatePrivateSecurityDescriptor();
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr unicodeBuffer = IntPtr.Zero;
            IntPtr rawHandle = IntPtr.Zero;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(leaf);
                UnicodeString name = new UnicodeString {
                    Length = checked((ushort)(leaf.Length * 2)),
                    MaximumLength = checked((ushort)((leaf.Length + 1) * 2)),
                    Buffer = nameBuffer
                };
                unicodeBuffer = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(name, unicodeBuffer, false);
                ObjectAttributes attributes = new ObjectAttributes {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = parentHandle.DangerousGetHandle(),
                    ObjectName = unicodeBuffer,
                    Attributes = ObjectCaseInsensitive,
                    SecurityDescriptor = descriptor,
                    SecurityQualityOfService = IntPtr.Zero
                };
                IoStatusBlock statusBlock;
                int status = NtCreateFile(
                    out rawHandle,
                    DeleteAccess | FileListDirectory | FileReadAttributes |
                        SynchronizeAccess,
                    ref attributes,
                    out statusBlock,
                    IntPtr.Zero,
                    FileAttributeNormal,
                    FileShareRead | FileShareWrite,
                    FileCreate,
                    FileDirectoryFile | FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    IntPtr.Zero,
                    0);
                if (status < 0 || rawHandle == IntPtr.Zero ||
                    rawHandle == new IntPtr(-1))
                {
                    throw new InvalidOperationException(
                        "Unable to create the private directory relative to " +
                        "the held parent. NTSTATUS=0x" +
                        status.ToString("X8"));
                }
                SafeFileHandle result = new SafeFileHandle(rawHandle, true);
                rawHandle = IntPtr.Zero;
                string expectedPath = Path.Combine(fullParent, leaf);
                if (!string.Equals(
                    Path.GetFullPath(GetFinalPath(result)),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    result.Dispose();
                    throw new InvalidOperationException(
                        "The handle-relative private directory escaped its parent.");
                }
                AssertPrivateDirectoryAcl(expectedPath);
                return result;
            }
            finally
            {
                if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                    new SafeFileHandle(rawHandle, true).Dispose();
                if (unicodeBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(unicodeBuffer);
                if (nameBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(nameBuffer);
                LocalFree(descriptor);
            }
        }

        public static SafeFileHandle CreateNewHeldFileUnderDirectory(
            SafeFileHandle parentHandle,
            string expectedParent,
            string leaf,
            byte[] content,
            string precreateHookKind)
        {
            if (content == null)
                throw new ArgumentNullException("content");
            string expectedPath = ValidateHeldParentAndLeaf(
                parentHandle,
                expectedParent,
                leaf);
            RunPrecreateHardlinkTestHook(
                precreateHookKind,
                expectedPath);
            SafeFileHandle handle = OpenRelativeRegularFile(
                parentHandle,
                leaf,
                FileCreate);
            try
            {
                ValidateHeldSingleLinkRegularFile(handle, expectedPath);
                WriteHeldFileBytes(handle, content);
                ValidateHeldSingleLinkRegularFile(handle, expectedPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public static SafeFileHandle CreateNewHeldPreparationLeaseUnderDirectory(
            SafeFileHandle parentHandle,
            string expectedParent,
            string leaf,
            string precreateHookKind)
        {
            string expectedPath = ValidateHeldParentAndLeaf(
                parentHandle,
                expectedParent,
                leaf);
            RunPrecreateHardlinkTestHook(
                precreateHookKind,
                expectedPath);
            SafeFileHandle handle = OpenRelativePreparationLeaseFile(
                parentHandle,
                leaf,
                FileCreate);
            try
            {
                ValidateHeldSingleLinkRegularFile(handle, expectedPath);
                WriteHeldFileBytes(handle, new byte[0]);
                ValidateHeldSingleLinkRegularFile(handle, expectedPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public static SafeFileHandle ReopenHeldPreparationLeaseForDeletion(
            SafeFileHandle parentHandle,
            string expectedParent,
            string leaf,
            uint expectedVolumeSerialNumber,
            uint expectedFileIndexHigh,
            uint expectedFileIndexLow)
        {
            string expectedPath = ValidateHeldParentAndLeaf(
                parentHandle,
                expectedParent,
                leaf);
            SafeFileHandle handle = OpenRelativeRegularFile(
                parentHandle,
                leaf,
                FileOpen);
            try
            {
                ValidateHeldSingleLinkRegularFile(handle, expectedPath);
                ByHandleFileInformation information =
                    GetFileInformation(handle);
                if (
                    information.VolumeSerialNumber !=
                        expectedVolumeSerialNumber ||
                    information.FileIndexHigh != expectedFileIndexHigh ||
                    information.FileIndexLow != expectedFileIndexLow)
                {
                    throw new InvalidOperationException(
                        "The staged preparation lifetime lease identity changed.");
                }
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public static SafeFileHandle OpenOrCreateHeldRuntimeInvalidMarker(
            SafeFileHandle parentHandle,
            string expectedParent,
            string leaf,
            byte[] newMarkerBytes,
            out bool priorExists,
            out byte[] priorBytes)
        {
            if (newMarkerBytes == null)
                throw new ArgumentNullException("newMarkerBytes");
            string expectedPath = ValidateHeldParentAndLeaf(
                parentHandle,
                expectedParent,
                leaf);
            SafeFileHandle handle;
            int openStatus;
            handle = TryOpenRelativeRegularFile(
                parentHandle,
                leaf,
                FileOpen,
                out openStatus);
            if (handle == null)
            {
                if (openStatus != StatusObjectNameNotFound)
                    throw new InvalidOperationException(
                        "Unable to open the exact runtime invalid marker. " +
                        "NTSTATUS=0x" + openStatus.ToString("X8"));
                handle = OpenRelativeRegularFile(
                    parentHandle,
                    leaf,
                    FileCreate);
                priorExists = false;
                priorBytes = null;
            }
            else
            {
                priorExists = true;
                priorBytes = null;
            }

            try
            {
                ValidateHeldSingleLinkRegularFile(handle, expectedPath);
                if (priorExists)
                    priorBytes = ReadHeldFileBytes(handle, 65536);
                RunExactNameSwapTestHook(
                    "INVALID_PRELEASE",
                    new string[] { expectedPath });
                ValidateHeldSingleLinkRegularFile(handle, expectedPath);
                if (!priorExists)
                    WriteHeldFileBytes(handle, newMarkerBytes);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static string ValidateHeldParentAndLeaf(
            SafeFileHandle parentHandle,
            string expectedParent,
            string leaf)
        {
            if (parentHandle == null || parentHandle.IsInvalid ||
                parentHandle.IsClosed)
            {
                throw new InvalidOperationException(
                    "The held file parent lease is invalid.");
            }
            if (string.IsNullOrEmpty(leaf) ||
                !string.Equals(
                    leaf,
                    Path.GetFileName(leaf),
                    StringComparison.Ordinal) ||
                leaf == "." || leaf == "..")
            {
                throw new InvalidOperationException(
                    "The held file leaf is unsafe.");
            }
            string fullParent = Path.GetFullPath(expectedParent);
            ByHandleFileInformation parentInformation =
                GetFileInformation(parentHandle);
            FileAttributes parentAttributes =
                (FileAttributes)parentInformation.FileAttributes;
            if ((parentAttributes & FileAttributes.Directory) == 0 ||
                (parentAttributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(
                    fullParent,
                    Path.GetFullPath(GetFinalPath(parentHandle)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The held file parent identity changed.");
            }
            return Path.Combine(fullParent, leaf);
        }

        private static SafeFileHandle OpenRelativeRegularFile(
            SafeFileHandle parentHandle,
            string leaf,
            uint disposition)
        {
            int status;
            SafeFileHandle handle = TryOpenRelativeRegularFile(
                parentHandle,
                leaf,
                disposition,
                out status);
            if (handle == null)
                throw new InvalidOperationException(
                    "Unable to acquire the exact relative file handle. " +
                    "NTSTATUS=0x" + status.ToString("X8"));
            return handle;
        }

        private static SafeFileHandle OpenRelativePreparationLeaseFile(
            SafeFileHandle parentHandle,
            string leaf,
            uint disposition)
        {
            int status;
            SafeFileHandle handle = TryOpenRelativeRegularFile(
                parentHandle,
                leaf,
                disposition,
                GenericRead | GenericWrite | FileReadAttributes |
                    SynchronizeAccess,
                FileShareRead,
                out status);
            if (handle == null)
                throw new InvalidOperationException(
                    "Unable to acquire the staged preparation lease handle. " +
                    "NTSTATUS=0x" + status.ToString("X8"));
            return handle;
        }

        private static SafeFileHandle TryOpenRelativeRegularFile(
            SafeFileHandle parentHandle,
            string leaf,
            uint disposition,
            out int status)
        {
            return TryOpenRelativeRegularFile(
                parentHandle,
                leaf,
                disposition,
                DeleteAccess | GenericRead | GenericWrite |
                    FileReadAttributes | SynchronizeAccess,
                FileShareRead | FileShareWrite,
                out status);
        }

        private static SafeFileHandle TryOpenRelativeRegularFile(
            SafeFileHandle parentHandle,
            string leaf,
            uint disposition,
            uint desiredAccess,
            uint shareAccess,
            out int status)
        {
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr unicodeBuffer = IntPtr.Zero;
            IntPtr rawHandle = IntPtr.Zero;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(leaf);
                UnicodeString name = new UnicodeString {
                    Length = checked((ushort)(leaf.Length * 2)),
                    MaximumLength = checked((ushort)((leaf.Length + 1) * 2)),
                    Buffer = nameBuffer
                };
                unicodeBuffer = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(name, unicodeBuffer, false);
                ObjectAttributes attributes = new ObjectAttributes {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = parentHandle.DangerousGetHandle(),
                    ObjectName = unicodeBuffer,
                    Attributes = ObjectCaseInsensitive,
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero
                };
                IoStatusBlock statusBlock;
                status = NtCreateFile(
                    out rawHandle,
                    desiredAccess,
                    ref attributes,
                    out statusBlock,
                    IntPtr.Zero,
                    FileAttributeNormal,
                    shareAccess,
                    disposition,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    IntPtr.Zero,
                    0);
                if (status < 0 || rawHandle == IntPtr.Zero ||
                    rawHandle == new IntPtr(-1))
                {
                    if (rawHandle != IntPtr.Zero &&
                        rawHandle != new IntPtr(-1))
                    {
                        new SafeFileHandle(rawHandle, true).Dispose();
                    }
                    return null;
                }
                SafeFileHandle result = new SafeFileHandle(rawHandle, true);
                rawHandle = IntPtr.Zero;
                return result;
            }
            finally
            {
                if (unicodeBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(unicodeBuffer);
                if (nameBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static void ValidateHeldSingleLinkRegularFile(
            SafeFileHandle handle,
            string expectedPath)
        {
            ByHandleFileInformation information = GetFileInformation(handle);
            FileAttributes attributes =
                (FileAttributes)information.FileAttributes;
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0 ||
                information.NumberOfLinks != 1 ||
                !string.Equals(
                    Path.GetFullPath(expectedPath),
                    Path.GetFullPath(GetFinalPath(handle)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The held regular file identity is unsafe.");
            }
        }

        private static byte[] ReadHeldFileBytes(
            SafeFileHandle handle,
            int maximumBytes)
        {
            ByHandleFileInformation information = GetFileInformation(handle);
            long length =
                ((long)information.FileSizeHigh << 32) |
                information.FileSizeLow;
            if (length < 0 || length > maximumBytes)
                throw new InvalidOperationException(
                    "The held file exceeds its snapshot bound.");
            long position;
            if (!SetFilePointerEx(handle, 0, out position, 0))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to seek the held file for reading.");
            byte[] result = new byte[(int)length];
            uint read;
            if (result.Length != 0 &&
                (!ReadFile(
                    handle,
                    result,
                    checked((uint)result.Length),
                    out read,
                    IntPtr.Zero) ||
                 read != result.Length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to snapshot the held file.");
            }
            return result;
        }

        private static void WriteHeldFileBytes(
            SafeFileHandle handle,
            byte[] bytes)
        {
            long position;
            if (!SetFilePointerEx(handle, 0, out position, 0) ||
                !SetEndOfFile(handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to truncate the held file.");
            }
            uint written;
            if (bytes.Length != 0 &&
                (!WriteFile(
                    handle,
                    bytes,
                    checked((uint)bytes.Length),
                    out written,
                    IntPtr.Zero) ||
                 written != bytes.Length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to write the held file.");
            }
            if (!FlushFileBuffers(handle))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to durably flush the held file.");
        }

        private static void RunPrecreateHardlinkTestHook(
            string kind,
            string expectedPath)
        {
            if (string.IsNullOrEmpty(kind) ||
                !string.Equals(
                    Environment.GetEnvironmentVariable(
                        "GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS"),
                    "1",
                    StringComparison.Ordinal))
            {
                return;
            }
            string prefix =
                "GEORAEPLAN_PREPARATION_TEST_" + kind + "_";
            string source = Environment.GetEnvironmentVariable(
                prefix + "SOURCE");
            string protectedPath = Environment.GetEnvironmentVariable(
                prefix + "PROTECTED");
            if (string.IsNullOrEmpty(source) ||
                string.IsNullOrEmpty(protectedPath) ||
                !string.Equals(
                    Path.GetFullPath(source),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!CreateHardLinkW(expectedPath, protectedPath, IntPtr.Zero))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to inject the deterministic pre-create hardlink.");
            Environment.SetEnvironmentVariable(
                prefix + "RESULT",
                "hardlinked",
                EnvironmentVariableTarget.Process);
        }

        public static void AssertPrivateDirectoryAcl(string path)
        {
            string fullPath = Path.GetFullPath(path);
            SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User;
            SecurityIdentifier systemSid =
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            DirectorySecurity security = Directory.GetAccessControl(
                fullPath,
                AccessControlSections.Owner | AccessControlSections.Access);
            SecurityIdentifier owner =
                (SecurityIdentifier)security.GetOwner(
                    typeof(SecurityIdentifier));
            if (!owner.Equals(userSid) || !security.AreAccessRulesProtected)
                throw new InvalidOperationException(
                    "The private directory owner or DACL protection is invalid.");

            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                false,
                typeof(SecurityIdentifier));
            if (rules.Count != 2)
                throw new InvalidOperationException(
                    "The private directory DACL is not minimal.");

            var identities = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (FileSystemAccessRule rule in rules)
            {
                SecurityIdentifier identity =
                    (SecurityIdentifier)rule.IdentityReference;
                if (
                    rule.AccessControlType != AccessControlType.Allow ||
                    rule.IsInherited ||
                    rule.FileSystemRights != FileSystemRights.FullControl ||
                    rule.InheritanceFlags !=
                        (InheritanceFlags.ContainerInherit |
                         InheritanceFlags.ObjectInherit) ||
                    rule.PropagationFlags != PropagationFlags.None ||
                    (!identity.Equals(userSid) &&
                     !identity.Equals(systemSid)))
                {
                    throw new InvalidOperationException(
                        "The private directory DACL contains an unsafe rule.");
                }
                identities.Add(identity.Value);
            }
            if (
                !identities.Contains(userSid.Value) ||
                !identities.Contains(systemSid.Value))
            {
                throw new InvalidOperationException(
                    "The private directory DACL omits a required principal.");
            }
        }

        public static void AssertPrivateTreeAcl(string root)
        {
            string fullRoot = Path.GetFullPath(root);
            AssertPrivateDirectoryAcl(fullRoot);
            SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User;
            SecurityIdentifier systemSid =
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            foreach (string path in GetPrivateTreeEntries(fullRoot))
            {
                FileAttributes attributes = File.GetAttributes(path);
                FileSystemSecurity security =
                    (attributes & FileAttributes.Directory) != 0
                        ? (FileSystemSecurity)Directory.GetAccessControl(
                            path,
                            AccessControlSections.Owner |
                            AccessControlSections.Access)
                        : File.GetAccessControl(
                            path,
                            AccessControlSections.Owner |
                            AccessControlSections.Access);
                SecurityIdentifier owner =
                    (SecurityIdentifier)security.GetOwner(
                        typeof(SecurityIdentifier));
                if (!owner.Equals(userSid))
                    throw new InvalidOperationException(
                        "The private tree contains a foreign owner.");

                AuthorizationRuleCollection rules = security.GetAccessRules(
                    true,
                    true,
                    typeof(SecurityIdentifier));
                if (rules.Count == 0)
                    throw new InvalidOperationException(
                        "The private tree contains an empty DACL.");
                foreach (FileSystemAccessRule rule in rules)
                {
                    SecurityIdentifier identity =
                        (SecurityIdentifier)rule.IdentityReference;
                    if (
                        rule.AccessControlType != AccessControlType.Allow ||
                        (!identity.Equals(userSid) &&
                         !identity.Equals(systemSid)))
                    {
                        throw new InvalidOperationException(
                            "The private tree contains an unsafe access rule.");
                    }
                }
            }
        }

        public static string[] GetBoundedGuidChildDirectories(
            SafeFileHandle parentHandle,
            string parent,
            int maximumRawChildren,
            int maximumGuidDirectories,
            int maximumMilliseconds)
        {
            if (parentHandle == null || parentHandle.IsInvalid)
                throw new InvalidOperationException(
                    "The secure work parent lease is invalid.");
            if (
                maximumRawChildren <= 0 ||
                maximumGuidDirectories <= 0 ||
                maximumMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "Secure work scan bounds must be positive.");
            }

            string fullParent = Path.GetFullPath(parent);
            ByHandleFileInformation parentInformation =
                GetFileInformation(parentHandle);
            FileAttributes parentAttributes =
                (FileAttributes)parentInformation.FileAttributes;
            if (
                (parentAttributes & FileAttributes.Directory) == 0 ||
                (parentAttributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(
                    fullParent,
                    Path.GetFullPath(GetFinalPath(parentHandle)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The secure work parent identity changed.");
            }

            var candidates = new List<string>();
            int rawChildren = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (IEnumerator<string> enumerator =
                Directory.EnumerateFileSystemEntries(fullParent).GetEnumerator())
            {
                while (
                    rawChildren < maximumRawChildren &&
                    candidates.Count < maximumGuidDirectories &&
                    stopwatch.ElapsedMilliseconds < maximumMilliseconds &&
                    enumerator.MoveNext())
                {
                    rawChildren++;
                    if (stopwatch.ElapsedMilliseconds >= maximumMilliseconds)
                        break;

                    string path = Path.GetFullPath(enumerator.Current);
                    string leaf = Path.GetFileName(path);
                    if (!IsLowerHexGuidLeaf(leaf))
                        continue;

                    try
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if (
                            (attributes & FileAttributes.Directory) != 0 &&
                            (attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            candidates.Add(path);
                        }
                    }
                    catch (IOException)
                    {
                        // A raced child is not a stale-work candidate.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // An inaccessible child is not a stale-work candidate.
                    }
                }
            }
            return candidates.ToArray();
        }

        private static bool IsLowerHexGuidLeaf(string leaf)
        {
            if (leaf == null || leaf.Length != 32)
                return false;
            for (int index = 0; index < leaf.Length; index++)
            {
                char character = leaf[index];
                if (
                    (character < '0' || character > '9') &&
                    (character < 'a' || character > 'f'))
                {
                    return false;
                }
            }
            return true;
        }

        private static List<string> GetPrivateTreeEntries(string root)
        {
            var entries = new List<string>();
            var pending = new Stack<string>();
            long totalBytes = 0;
            pending.Push(Path.GetFullPath(root));
            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                foreach (string path in
                    Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException(
                            "The private tree contains a reparse point.");
                    entries.Add(path);
                    if (entries.Count > MaximumPrivateTreeEntries)
                        throw new InvalidOperationException(
                            "The private tree entry limit was exceeded.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(path);
                    else
                    {
                        long length = new FileInfo(path).Length;
                        if (
                            length < 0 ||
                            totalBytes > MaximumPrivateTreeBytes - length)
                        {
                            throw new InvalidOperationException(
                                "The private tree byte limit was exceeded.");
                        }
                        totalBytes += length;
                    }
                }
            }
            return entries;
        }

        public static void DeletePrivateTreeAndRoot(
            SafeFileHandle rootHandle,
            string root)
        {
            DeletePrivateTreeAndRootCore(rootHandle, root, true);
        }

        public static void DeletePrivatePromotionTreeAndRoot(
            SafeFileHandle rootHandle,
            string root)
        {
            DeletePrivateTreeAndRootCore(rootHandle, root, false);
        }

        private static void DeletePrivateTreeAndRootCore(
            SafeFileHandle rootHandle,
            string root,
            bool requirePrivateChildAcls)
        {
            if (rootHandle == null || rootHandle.IsInvalid)
                throw new InvalidOperationException(
                    "The private root delete handle is invalid.");

            string fullRoot = Path.GetFullPath(root);
            string finalRoot = Path.GetFullPath(GetFinalPath(rootHandle));
            ByHandleFileInformation information = GetFileInformation(rootHandle);
            FileAttributes rootAttributes =
                (FileAttributes)information.FileAttributes;
            if (
                !string.Equals(
                    fullRoot,
                    finalRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                (rootAttributes & FileAttributes.Directory) == 0 ||
                (rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The private root delete handle identity changed.");
            }

            if (requirePrivateChildAcls)
                AssertPrivateTreeAcl(fullRoot);
            else
                AssertPrivateDirectoryAcl(fullRoot);
            var heldEntries = new List<HeldPrivateTreeEntry>();
            try
            {
                heldEntries.AddRange(
                    OpenHeldPrivateTreeEntriesFromRoot(
                        rootHandle,
                        fullRoot));

                RunExactNameSwapTestHook(
                    "CHILD",
                    heldEntries.ConvertAll(delegate(HeldPrivateTreeEntry entry) {
                        return entry.Path;
                    }).ToArray());

                heldEntries.Sort(delegate(
                    HeldPrivateTreeEntry left,
                    HeldPrivateTreeEntry right) {
                    if (left.IsDirectory != right.IsDirectory)
                        return left.IsDirectory ? 1 : -1;
                    return right.Path.Length.CompareTo(left.Path.Length);
                });
                foreach (HeldPrivateTreeEntry entry in heldEntries)
                {
                    ValidateHeldPrivateTreeEntry(entry, fullRoot);
                    SetDeleteDisposition(entry.Handle);
                    entry.Handle.Dispose();
                }
            }
            finally
            {
                foreach (HeldPrivateTreeEntry entry in heldEntries)
                    entry.Dispose();
            }

            finalRoot = Path.GetFullPath(GetFinalPath(rootHandle));
            if (!string.Equals(
                fullRoot,
                finalRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The private root identity changed during cleanup.");
            }
            FileDispositionInformation disposition =
                new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                rootHandle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf(
                    typeof(FileDispositionInformation))))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to delete the exact private root handle.");
            }
        }

        private sealed class HeldPrivateTreeEntry : IDisposable
        {
            public string Path;
            public bool IsDirectory;
            public SafeFileHandle Handle;

            public void Dispose()
            {
                if (Handle != null && !Handle.IsClosed)
                    Handle.Dispose();
            }
        }

        private sealed class DirectoryIdentityEntry
        {
            public string Name;
            public FileAttributes Attributes;
            public ulong FileId;
        }

        private static List<HeldPrivateTreeEntry>
            OpenHeldPrivateTreeEntriesFromRoot(
                SafeFileHandle rootHandle,
                string root)
        {
            var result = new List<HeldPrivateTreeEntry>();
            var pending = new Queue<HeldPrivateTreeEntry>();
            var rootEntry = new HeldPrivateTreeEntry {
                Path = Path.GetFullPath(root),
                IsDirectory = true,
                Handle = rootHandle
            };
            long totalBytes = 0;
            try
            {
                OpenAndRetainHeldDirectoryChildren(
                    rootEntry,
                    root,
                    result,
                    pending,
                    ref totalBytes);

                while (pending.Count != 0)
                {
                    HeldPrivateTreeEntry parent = pending.Dequeue();
                    OpenAndRetainHeldDirectoryChildren(
                        parent,
                        root,
                        result,
                        pending,
                        ref totalBytes);
                }
                return result;
            }
            catch
            {
                foreach (HeldPrivateTreeEntry entry in result)
                    entry.Dispose();
                throw;
            }
        }

        private static void OpenAndRetainHeldDirectoryChildren(
            HeldPrivateTreeEntry parent,
            string root,
            List<HeldPrivateTreeEntry> result,
            Queue<HeldPrivateTreeEntry> pending,
            ref long totalBytes)
        {
            HeldPrivateTreeEntry firstRetained = null;
            int recordCount = 0;
            long runningTotalBytes = totalBytes;
            EnumerateHeldDirectory(
                parent.Handle,
                delegate(DirectoryIdentityEntry child) {
                    recordCount++;
                    if (recordCount == 2 && firstRetained != null)
                        RunSecondRecordRenameTestHook(firstRetained);
                    HeldPrivateTreeEntry entry =
                        OpenHeldEnumeratedChild(parent, child, root);
                    result.Add(entry);
                    if (firstRetained == null)
                        firstRetained = entry;
                    if (entry.IsDirectory)
                        pending.Enqueue(entry);
                    else
                        AccumulateHeldFileBytes(
                            entry,
                            ref runningTotalBytes);
                    if (result.Count > MaximumPrivateTreeEntries)
                        throw new InvalidOperationException(
                            "The private tree entry limit was exceeded.");
                });
            totalBytes = runningTotalBytes;
        }

        private static void AccumulateHeldFileBytes(
            HeldPrivateTreeEntry entry,
            ref long totalBytes)
        {
            ByHandleFileInformation information =
                GetFileInformation(entry.Handle);
            long length =
                ((long)information.FileSizeHigh << 32) |
                information.FileSizeLow;
            if (length < 0 ||
                totalBytes > MaximumPrivateTreeBytes - length)
            {
                throw new InvalidOperationException(
                    "The private tree byte limit was exceeded.");
            }
            totalBytes += length;
        }

        private static HeldPrivateTreeEntry OpenHeldEnumeratedChild(
            HeldPrivateTreeEntry parent,
            DirectoryIdentityEntry child,
            string root)
        {
            string expectedPath = Path.Combine(parent.Path, child.Name);
            AssertStrictDescendant(expectedPath, root);
            RunPreopenChildSwapTestHook(expectedPath);
            bool isDirectory =
                (child.Attributes & FileAttributes.Directory) != 0;
            SafeFileHandle handle = OpenRelativeTreeChild(
                parent.Handle,
                child.Name,
                isDirectory);
            var entry = new HeldPrivateTreeEntry {
                Path = Path.GetFullPath(expectedPath),
                IsDirectory = isDirectory,
                Handle = handle
            };
            try
            {
                ValidateHeldPrivateTreeEntry(entry, root);
                ByHandleFileInformation information =
                    GetFileInformation(handle);
                ulong actualFileId =
                    ((ulong)information.FileIndexHigh << 32) |
                    information.FileIndexLow;
                if (actualFileId != child.FileId)
                    throw new InvalidOperationException(
                        "An enumerated private tree child identity changed.");
                return entry;
            }
            catch
            {
                entry.Dispose();
                throw;
            }
        }

        private static SafeFileHandle OpenRelativeTreeChild(
            SafeFileHandle parentHandle,
            string leaf,
            bool isDirectory)
        {
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr unicodeBuffer = IntPtr.Zero;
            IntPtr rawHandle = IntPtr.Zero;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(leaf);
                UnicodeString name = new UnicodeString {
                    Length = checked((ushort)(leaf.Length * 2)),
                    MaximumLength = checked((ushort)((leaf.Length + 1) * 2)),
                    Buffer = nameBuffer
                };
                unicodeBuffer = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(name, unicodeBuffer, false);
                ObjectAttributes attributes = new ObjectAttributes {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = parentHandle.DangerousGetHandle(),
                    ObjectName = unicodeBuffer,
                    Attributes = ObjectCaseInsensitive,
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero
                };
                IoStatusBlock statusBlock;
                uint desiredAccess =
                    DeleteAccess | FileReadAttributes | SynchronizeAccess;
                if (isDirectory)
                    desiredAccess |= FileListDirectory;
                uint options =
                    FileSynchronousIoNonAlert | FileOpenReparsePoint |
                    (isDirectory
                        ? FileDirectoryFile
                        : FileNonDirectoryFile);
                int status = NtCreateFile(
                    out rawHandle,
                    desiredAccess,
                    ref attributes,
                    out statusBlock,
                    IntPtr.Zero,
                    FileAttributeNormal,
                    FileShareRead | FileShareWrite,
                    FileOpen,
                    options,
                    IntPtr.Zero,
                    0);
                if (status < 0 || rawHandle == IntPtr.Zero ||
                    rawHandle == new IntPtr(-1))
                {
                    throw new InvalidOperationException(
                        "Unable to retain an enumerated private child. " +
                        "NTSTATUS=0x" + status.ToString("X8"));
                }
                SafeFileHandle result = new SafeFileHandle(rawHandle, true);
                rawHandle = IntPtr.Zero;
                return result;
            }
            finally
            {
                if (rawHandle != IntPtr.Zero &&
                    rawHandle != new IntPtr(-1))
                {
                    new SafeFileHandle(rawHandle, true).Dispose();
                }
                if (unicodeBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(unicodeBuffer);
                if (nameBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static void EnumerateHeldDirectory(
            SafeFileHandle directoryHandle,
            Action<DirectoryIdentityEntry> bindEntry)
        {
            const int BufferSize = 65536;
            const int FileNameOffset = 104;
            if (bindEntry == null)
                throw new ArgumentNullException("bindEntry");
            IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
            bool restart = true;
            try
            {
                while (true)
                {
                    IoStatusBlock statusBlock;
                    int status = NtQueryDirectoryFile(
                        directoryHandle.DangerousGetHandle(),
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        out statusBlock,
                        buffer,
                        BufferSize,
                        FileIdBothDirectoryInformationClass,
                        true,
                        IntPtr.Zero,
                        restart);
                    restart = false;
                    if (status == StatusNoMoreFiles)
                        break;
                    if (status < 0)
                        throw new InvalidOperationException(
                            "Unable to enumerate the held private directory. " +
                            "NTSTATUS=0x" + status.ToString("X8"));
                    int offset = 0;
                    while (true)
                    {
                        IntPtr entry = new IntPtr(buffer.ToInt64() + offset);
                        int nextOffset = Marshal.ReadInt32(entry, 0);
                        int nameLength = Marshal.ReadInt32(entry, 60);
                        if (nameLength < 0 || (nameLength & 1) != 0 ||
                            FileNameOffset + nameLength >
                                BufferSize - offset)
                        {
                            throw new InvalidOperationException(
                                "The held directory entry buffer is invalid.");
                        }
                        string name = Marshal.PtrToStringUni(
                            new IntPtr(entry.ToInt64() + FileNameOffset),
                            nameLength / 2);
                        if (name != "." && name != "..")
                        {
                            bindEntry(new DirectoryIdentityEntry {
                                Name = name,
                                Attributes = (FileAttributes)
                                    Marshal.ReadInt32(entry, 56),
                                FileId = unchecked((ulong)
                                    Marshal.ReadInt64(entry, 96))
                            });
                        }
                        if (nextOffset == 0)
                            break;
                        if (nextOffset < FileNameOffset ||
                            offset > BufferSize - nextOffset)
                        {
                            throw new InvalidOperationException(
                                "The held directory entry offset is invalid.");
                        }
                        offset += nextOffset;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void RunSecondRecordRenameTestHook(
            HeldPrivateTreeEntry firstRetained)
        {
            const string Request =
                "GEORAEPLAN_PREPARATION_TEST_CHILD_SECOND_RECORD_RENAME";
            const string Result =
                "GEORAEPLAN_PREPARATION_TEST_CHILD_SECOND_RECORD_RESULT";
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(
                        "GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS"),
                    "1",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Environment.GetEnvironmentVariable(Request),
                    "1",
                    StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(Result)))
            {
                return;
            }
            try
            {
                string retained =
                    firstRetained.Path + ".second-record-retained";
                if (firstRetained.IsDirectory)
                    Directory.Move(firstRetained.Path, retained);
                else
                    File.Move(firstRetained.Path, retained);
                Environment.SetEnvironmentVariable(
                    Result,
                    "renamed",
                    EnvironmentVariableTarget.Process);
                throw new InvalidOperationException(
                    "The first enumerated child was not retained.");
            }
            catch (IOException)
            {
                Environment.SetEnvironmentVariable(
                    Result,
                    "blocked",
                    EnvironmentVariableTarget.Process);
            }
            catch (UnauthorizedAccessException)
            {
                Environment.SetEnvironmentVariable(
                    Result,
                    "blocked",
                    EnvironmentVariableTarget.Process);
            }
        }

        private static void RunPreopenChildSwapTestHook(
            string expectedPath)
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS"),
                "1",
                StringComparison.Ordinal))
            {
                return;
            }
            const string Prefix =
                "GEORAEPLAN_PREPARATION_TEST_CHILD_PREOPEN_SWAP_";
            string source = Environment.GetEnvironmentVariable(
                Prefix + "SOURCE");
            string protectedPath = Environment.GetEnvironmentVariable(
                Prefix + "PROTECTED");
            if (string.IsNullOrEmpty(source) ||
                string.IsNullOrEmpty(protectedPath) ||
                !string.Equals(
                    Path.GetFullPath(source),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string retained = expectedPath + ".test-retained";
            File.Move(expectedPath, retained);
            if (!CreateHardLinkW(expectedPath, protectedPath, IntPtr.Zero))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to inject the pre-open child substitution.");
            Environment.SetEnvironmentVariable(
                Prefix + "RESULT",
                "swapped",
                EnvironmentVariableTarget.Process);
        }

        private static HeldPrivateTreeEntry OpenHeldPrivateTreeEntry(
            string path,
            string root)
        {
            string expectedPath = Path.GetFullPath(path);
            AssertStrictDescendant(expectedPath, root);
            FileAttributes namedAttributes = File.GetAttributes(expectedPath);
            bool isDirectory =
                (namedAttributes & FileAttributes.Directory) != 0;
            uint flags = FileFlagOpenReparsePoint;
            if (isDirectory)
                flags |= FileFlagBackupSemantics;
            SafeFileHandle handle = CreateFileW(
                expectedPath,
                DeleteAccess | FileReadAttributes,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    "Unable to retain a private tree child identity.");
            }
            var entry = new HeldPrivateTreeEntry {
                Path = expectedPath,
                IsDirectory = isDirectory,
                Handle = handle
            };
            try
            {
                ValidateHeldPrivateTreeEntry(entry, root);
                return entry;
            }
            catch
            {
                entry.Dispose();
                throw;
            }
        }

        private static void ValidateHeldPrivateTreeEntry(
            HeldPrivateTreeEntry entry,
            string root)
        {
            AssertStrictDescendant(entry.Path, root);
            ByHandleFileInformation information =
                GetFileInformation(entry.Handle);
            FileAttributes attributes =
                (FileAttributes)information.FileAttributes;
            bool isDirectory =
                (attributes & FileAttributes.Directory) != 0;
            if (isDirectory != entry.IsDirectory ||
                (attributes & FileAttributes.ReparsePoint) != 0 ||
                (!isDirectory && information.NumberOfLinks != 1) ||
                !string.Equals(
                    entry.Path,
                    Path.GetFullPath(GetFinalPath(entry.Handle)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A retained private tree child identity changed.");
            }
        }

        private static void AssertExactPrivateTreeFile(
            string path,
            string root)
        {
            string expectedPath = Path.GetFullPath(path);
            AssertStrictDescendant(expectedPath, root);
            using (SafeFileHandle handle = CreateFileW(
                expectedPath,
                FileReadAttributes,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to preflight a private tree file.");
                ByHandleFileInformation information = GetFileInformation(handle);
                FileAttributes attributes =
                    (FileAttributes)information.FileAttributes;
                if (
                    (attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    information.NumberOfLinks != 1 ||
                    !string.Equals(
                        expectedPath,
                        Path.GetFullPath(GetFinalPath(handle)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A private tree file failed deletion preflight.");
                }
            }
        }

        private static void AssertExactPrivateTreeDirectory(
            string path,
            string root)
        {
            string expectedPath = Path.GetFullPath(path);
            AssertStrictDescendant(expectedPath, root);
            using (SafeFileHandle handle = CreateFileW(
                expectedPath,
                FileReadAttributes,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to preflight a private tree directory.");
                ByHandleFileInformation information = GetFileInformation(handle);
                FileAttributes attributes =
                    (FileAttributes)information.FileAttributes;
                if (
                    (attributes & FileAttributes.Directory) == 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    !string.Equals(
                        expectedPath,
                        Path.GetFullPath(GetFinalPath(handle)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A private tree directory failed deletion preflight.");
                }
            }
        }

        private static void DeleteExactPrivateTreeFile(
            string path,
            string root)
        {
            string expectedPath = Path.GetFullPath(path);
            AssertStrictDescendant(expectedPath, root);
            using (SafeFileHandle handle = CreateFileW(
                expectedPath,
                DeleteAccess | FileReadAttributes,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to open a private tree file for deletion.");
                ByHandleFileInformation information = GetFileInformation(handle);
                FileAttributes attributes =
                    (FileAttributes)information.FileAttributes;
                string finalPath = Path.GetFullPath(GetFinalPath(handle));
                if (
                    (attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    information.NumberOfLinks != 1 ||
                    !string.Equals(
                        expectedPath,
                        finalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A private tree file identity changed.");
                }
                SetDeleteDisposition(handle);
            }
        }

        private static void DeleteExactPrivateTreeDirectory(
            string path,
            string root)
        {
            string expectedPath = Path.GetFullPath(path);
            AssertStrictDescendant(expectedPath, root);
            using (SafeFileHandle handle = CreateFileW(
                expectedPath,
                DeleteAccess | FileReadAttributes,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to open a private tree directory for deletion.");
                ByHandleFileInformation information = GetFileInformation(handle);
                FileAttributes attributes =
                    (FileAttributes)information.FileAttributes;
                string finalPath = Path.GetFullPath(GetFinalPath(handle));
                if (
                    (attributes & FileAttributes.Directory) == 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    !string.Equals(
                        expectedPath,
                        finalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A private tree directory identity changed.");
                }
                SetDeleteDisposition(handle);
            }
        }

        private static void AssertStrictDescendant(
            string candidate,
            string root)
        {
            string rootPrefix =
                Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(candidate).StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A private tree cleanup target escaped its root.");
            }
        }

        private static void SetDeleteDisposition(SafeFileHandle handle)
        {
            FileDispositionInformation disposition =
                new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf(
                    typeof(FileDispositionInformation))))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to delete an exact private tree handle.");
            }
        }

        public static string ReadExactSingleLinkUtf8File(
            string path,
            string root)
        {
            string expectedPath = Path.GetFullPath(path);
            AssertStrictDescendant(expectedPath, root);
            SafeFileHandle handle = CreateFileW(
                expectedPath,
                GenericRead | FileReadAttributes,
                FileShareRead | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to open the private marker.");
            }
            using (handle)
            {
                ByHandleFileInformation information = GetFileInformation(handle);
                FileAttributes attributes =
                    (FileAttributes)information.FileAttributes;
                long length =
                    ((long)information.FileSizeHigh << 32) |
                    information.FileSizeLow;
                string finalPath = Path.GetFullPath(GetFinalPath(handle));
                if (
                    (attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    information.NumberOfLinks != 1 ||
                    length < 0 ||
                    length > 128 ||
                    !string.Equals(
                        expectedPath,
                        finalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The private marker identity is unsafe.");
                }
                using (var stream = new FileStream(
                    handle,
                    FileAccess.Read,
                    4096,
                    false))
                using (var reader = new StreamReader(
                    stream,
                    new UTF8Encoding(false, true),
                    false,
                    128,
                    false))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        public static string ReadHeldExactSingleLinkUtf8File(
            SafeFileHandle handle,
            string path,
            string root)
        {
            string expectedPath = Path.GetFullPath(path);
            AssertStrictDescendant(expectedPath, root);
            ValidateHeldSingleLinkRegularFile(handle, expectedPath);
            byte[] bytes = ReadHeldFileBytes(handle, 128);
            return new UTF8Encoding(false, true).GetString(bytes);
        }


        public static uint GetLinkCount(string fileName)
        {
            using (SafeFileHandle handle = CreateFileW(
                fileName,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new InvalidOperationException(
                        "Unable to open file for link-count validation.");

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                    throw new InvalidOperationException(
                        "Unable to read file identity.");

                return information.NumberOfLinks;
            }
        }

        public static void DeleteExactSingleLinkRegularFile(
            string fileName)
        {
            string expectedPath = Path.GetFullPath(fileName);
            using (SafeFileHandle handle = CreateFileW(
                expectedPath,
                DeleteAccess | FileReadAttributes,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new InvalidOperationException(
                        "Unable to open the copied file for guarded deletion.");

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                    throw new InvalidOperationException(
                        "Unable to inspect the copied file before deletion.");

                FileAttributes attributes =
                    (FileAttributes)information.FileAttributes;
                string finalPath = Path.GetFullPath(GetFinalPath(handle));
                if (
                    (attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    information.NumberOfLinks != 1 ||
                    !string.Equals(
                        finalPath,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The copied file is not safe for guarded deletion.");
                }

                FileDispositionInformation disposition =
                    new FileDispositionInformation {
                        DeleteFile = true
                    };
                if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoClass,
                    ref disposition,
                    (uint)Marshal.SizeOf(
                        typeof(FileDispositionInformation))))
                {
                    throw new InvalidOperationException(
                        "Unable to delete the guarded copied file.");
                }
            }
        }

        public static void DeleteHeldExactSingleLinkRegularFile(
            SafeFileHandle handle,
            string expectedFileName)
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed)
                throw new InvalidOperationException(
                    "The held private sentinel handle is invalid.");

            string expectedPath = Path.GetFullPath(expectedFileName);
            ByHandleFileInformation information = GetFileInformation(handle);
            FileAttributes attributes =
                (FileAttributes)information.FileAttributes;
            string finalPath = Path.GetFullPath(GetFinalPath(handle));
            if (
                (attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0 ||
                information.NumberOfLinks != 1 ||
                !string.Equals(
                    finalPath,
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The held private sentinel identity changed.");
            }
            RunExactNameSwapTestHook(
                "INVALID",
                new string[] { expectedPath });
            finalPath = Path.GetFullPath(GetFinalPath(handle));
            if (!string.Equals(
                finalPath,
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The held exact file path changed before deletion.");
            }
            SetDeleteDisposition(handle);
        }

        public static void OverwriteHeldExactSingleLinkRegularFile(
            SafeFileHandle handle,
            string expectedFileName,
            byte[] bytes)
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed)
                throw new InvalidOperationException(
                    "The held exact file handle is invalid.");
            if (bytes == null)
                throw new ArgumentNullException("bytes");
            string expectedPath = Path.GetFullPath(expectedFileName);
            ByHandleFileInformation information = GetFileInformation(handle);
            FileAttributes attributes =
                (FileAttributes)information.FileAttributes;
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0 ||
                information.NumberOfLinks != 1 ||
                !string.Equals(
                    expectedPath,
                    Path.GetFullPath(GetFinalPath(handle)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The held exact file identity changed before overwrite.");
            }
            long position;
            if (!SetFilePointerEx(handle, 0, out position, 0) ||
                !SetEndOfFile(handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to truncate the held exact file.");
            }
            uint written;
            if (bytes.Length != 0 &&
                (!WriteFile(
                    handle,
                    bytes,
                    checked((uint)bytes.Length),
                    out written,
                    IntPtr.Zero) ||
                 written != bytes.Length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to overwrite the held exact file.");
            }
            if (!FlushFileBuffers(handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to durably flush the held exact file.");
            }
        }

        public static void MoveExactPathByHandle(
            string source,
            string destination,
            bool isDirectory)
        {
            string fullSource = Path.GetFullPath(source);
            string fullDestination = Path.GetFullPath(destination);
            string destinationParent = Path.GetDirectoryName(fullDestination);
            string destinationLeaf = Path.GetFileName(fullDestination);
            if (string.IsNullOrEmpty(destinationParent) ||
                string.IsNullOrEmpty(destinationLeaf) ||
                File.Exists(fullDestination) ||
                Directory.Exists(fullDestination))
            {
                throw new InvalidOperationException(
                    "The handle-bound move destination is unsafe.");
            }

            uint sourceFlags = FileFlagOpenReparsePoint;
            if (isDirectory)
                sourceFlags |= FileFlagBackupSemantics;
            using (SafeFileHandle sourceHandle = CreateFileW(
                fullSource,
                DeleteAccess | FileReadAttributes,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                sourceFlags,
                IntPtr.Zero))
            using (SafeFileHandle parentHandle = CreateFileW(
                destinationParent,
                FileListDirectory | FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (sourceHandle.IsInvalid || parentHandle.IsInvalid)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to retain handle-bound move identities.");
                ByHandleFileInformation sourceInformation =
                    GetFileInformation(sourceHandle);
                FileAttributes sourceAttributes =
                    (FileAttributes)sourceInformation.FileAttributes;
                ByHandleFileInformation parentInformation =
                    GetFileInformation(parentHandle);
                FileAttributes parentAttributes =
                    (FileAttributes)parentInformation.FileAttributes;
                if (((sourceAttributes & FileAttributes.Directory) != 0) !=
                        isDirectory ||
                    (sourceAttributes & FileAttributes.ReparsePoint) != 0 ||
                    (!isDirectory && sourceInformation.NumberOfLinks != 1) ||
                    (parentAttributes & FileAttributes.Directory) == 0 ||
                    (parentAttributes & FileAttributes.ReparsePoint) != 0 ||
                    !string.Equals(
                        fullSource,
                        Path.GetFullPath(GetFinalPath(sourceHandle)),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        Path.GetFullPath(destinationParent),
                        Path.GetFullPath(GetFinalPath(parentHandle)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A handle-bound move identity is unsafe.");
                }

                RunExactNameSwapTestHook(
                    "CHILD",
                    new string[] { fullSource });
                RenameHeldPath(
                    sourceHandle,
                    parentHandle,
                    destinationLeaf);
                using (SafeFileHandle destinationHandle = CreateFileW(
                    fullDestination,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    sourceFlags,
                    IntPtr.Zero))
                {
                    if (destinationHandle.IsInvalid)
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Unable to revalidate the renamed destination.");
                    ByHandleFileInformation destinationInformation =
                        GetFileInformation(destinationHandle);
                    if (destinationInformation.VolumeSerialNumber !=
                            sourceInformation.VolumeSerialNumber ||
                        destinationInformation.FileIndexHigh !=
                            sourceInformation.FileIndexHigh ||
                        destinationInformation.FileIndexLow !=
                            sourceInformation.FileIndexLow ||
                        !string.Equals(
                            fullDestination,
                            Path.GetFullPath(GetFinalPath(destinationHandle)),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The renamed destination identity changed.");
                    }
                }
            }
        }

        private static void RenameHeldPath(
            SafeFileHandle sourceHandle,
            SafeFileHandle destinationParentHandle,
            string destinationLeaf)
        {
            byte[] nameBytes = Encoding.Unicode.GetBytes(destinationLeaf);
            int rootOffset = IntPtr.Size == 8 ? 8 : 4;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + 4;
            int totalSize = checked(nameOffset + nameBytes.Length);
            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                for (int index = 0; index < totalSize; index++)
                    Marshal.WriteByte(buffer, index, 0);
                Marshal.WriteByte(buffer, 0, 0);
                Marshal.WriteIntPtr(
                    buffer,
                    rootOffset,
                    destinationParentHandle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
                Marshal.Copy(nameBytes, 0,
                    new IntPtr(buffer.ToInt64() + nameOffset),
                    nameBytes.Length);
                IoStatusBlock statusBlock;
                int status = NtSetInformationFile(
                    sourceHandle.DangerousGetHandle(),
                    out statusBlock,
                    buffer,
                    checked((uint)totalSize),
                    FileRenameInformationClass);
                if (status < 0)
                {
                    throw new InvalidOperationException(
                        "Unable to rename the exact held path. NTSTATUS=0x" +
                        status.ToString("X8"));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void RunExactNameSwapTestHook(
            string kind,
            string[] heldPaths)
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS"),
                "1",
                StringComparison.Ordinal))
            {
                return;
            }
            string prefix =
                "GEORAEPLAN_PREPARATION_TEST_" + kind + "_SWAP_";
            string source = Environment.GetEnvironmentVariable(
                prefix + "SOURCE");
            string protectedPath = Environment.GetEnvironmentVariable(
                prefix + "PROTECTED");
            if (string.IsNullOrEmpty(source) ||
                string.IsNullOrEmpty(protectedPath))
            {
                return;
            }
            string fullSource = Path.GetFullPath(source);
            bool matched = false;
            foreach (string heldPath in heldPaths)
            {
                if (string.Equals(
                    fullSource,
                    Path.GetFullPath(heldPath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
                return;

            string retained = fullSource + ".test-retained";
            try
            {
                FileAttributes attributes = File.GetAttributes(fullSource);
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Move(fullSource, retained);
                else
                    File.Move(fullSource, retained);
                if (Directory.Exists(protectedPath))
                    Directory.Move(protectedPath, fullSource);
                else
                    File.Move(protectedPath, fullSource);
                Environment.SetEnvironmentVariable(
                    prefix + "RESULT", "swapped",
                    EnvironmentVariableTarget.Process);
                throw new InvalidOperationException(
                    "An exact-name substitution unexpectedly succeeded.");
            }
            catch (IOException)
            {
                Environment.SetEnvironmentVariable(
                    prefix + "RESULT", "blocked",
                    EnvironmentVariableTarget.Process);
            }
            catch (UnauthorizedAccessException)
            {
                Environment.SetEnvironmentVariable(
                    prefix + "RESULT", "blocked",
                    EnvironmentVariableTarget.Process);
            }
        }

        public static ByHandleFileInformation GetFileInformation(
            SafeFileHandle handle)
        {
            if (handle == null || handle.IsInvalid)
                throw new InvalidOperationException(
                    "Unable to inspect an invalid file handle.");

            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
                throw new InvalidOperationException(
                    "Unable to read file identity.");

            return information;
        }

        public static string GetFinalPath(SafeFileHandle handle)
        {
            if (handle == null || handle.IsInvalid)
                throw new InvalidOperationException(
                    "Unable to inspect an invalid file handle.");

            var path = new StringBuilder(512);
            var length = GetFinalPathNameByHandleW(
                handle,
                path,
                (uint)path.Capacity,
                0);
            if (length >= path.Capacity)
            {
                path = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(
                    handle,
                    path,
                    (uint)path.Capacity,
                    0);
            }
            if (length == 0 || length >= path.Capacity)
                throw new InvalidOperationException(
                    "Unable to resolve final file path.");

            var finalPath = path.ToString();
            if (finalPath.StartsWith(
                @"\\?\UNC\",
                StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + finalPath.Substring(8);
            }
            if (finalPath.StartsWith(
                @"\\?\",
                StringComparison.OrdinalIgnoreCase))
            {
                return finalPath.Substring(4);
            }

            return finalPath;
        }

        public static void AssertNoDuplicateJsonObjectProperties(string json)
        {
            AssertNoDuplicateJsonObjectPropertiesAndDepth(json, 64);
        }

        public static void AssertNoDuplicateJsonObjectPropertiesAndDepth(
            string json,
            int maximumDepth)
        {
            new JsonPropertyScanner(json, maximumDepth).Parse();
        }

        private sealed class JsonPropertyScanner
        {
            private readonly string _json;
            private readonly int _maximumDepth;
            private int _index;

            public JsonPropertyScanner(string json, int maximumDepth)
            {
                if (json == null)
                    throw new ArgumentNullException("json");
                if (maximumDepth <= 0)
                    throw new ArgumentOutOfRangeException("maximumDepth");
                _json = json;
                _maximumDepth = maximumDepth;
            }

            public void Parse()
            {
                ParseValue(1);
                SkipWhitespace();
                if (_index != _json.Length)
                    throw new InvalidOperationException(
                        "Unexpected JSON content.");
            }

            private void ParseValue(int depth)
            {
                if (depth > _maximumDepth)
                    throw new InvalidOperationException(
                        "JSON nesting depth exceeds the permitted limit.");
                SkipWhitespace();
                if (_index >= _json.Length)
                    throw new InvalidOperationException(
                        "Unexpected end of JSON.");

                switch (_json[_index])
                {
                    case '{':
                        ParseObject(depth);
                        return;
                    case '[':
                        ParseArray(depth);
                        return;
                    case '"':
                        ParseString();
                        return;
                    default:
                        ParsePrimitive();
                        return;
                }
            }

            private void ParseObject(int depth)
            {
                _index++;
                SkipWhitespace();
                var names = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                if (Consume('}'))
                    return;

                while (true)
                {
                    SkipWhitespace();
                    if (_index >= _json.Length || _json[_index] != '"')
                        throw new InvalidOperationException(
                            "JSON object property name is invalid.");
                    var name = ParseString();
                    if (!names.Add(name))
                        throw new InvalidOperationException(
                            "JSON object contains a duplicate property.");

                    SkipWhitespace();
                    Expect(':');
                    ParseValue(depth + 1);
                    SkipWhitespace();
                    if (Consume('}'))
                        return;
                    Expect(',');
                }
            }

            private void ParseArray(int depth)
            {
                _index++;
                SkipWhitespace();
                if (Consume(']'))
                    return;

                while (true)
                {
                    ParseValue(depth + 1);
                    SkipWhitespace();
                    if (Consume(']'))
                        return;
                    Expect(',');
                }
            }

            private string ParseString()
            {
                Expect('"');
                var value = new StringBuilder();
                while (_index < _json.Length)
                {
                    var current = _json[_index++];
                    if (current == '"')
                        return value.ToString();
                    if (current < 0x20)
                        throw new InvalidOperationException(
                            "JSON string contains a control character.");
                    if (current != '\\')
                    {
                        value.Append(current);
                        continue;
                    }

                    if (_index >= _json.Length)
                        throw new InvalidOperationException(
                            "JSON string escape is incomplete.");
                    var escaped = _json[_index++];
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
                            if (_index + 4 > _json.Length)
                                throw new InvalidOperationException(
                                    "JSON Unicode escape is incomplete.");
                            ushort codePoint;
                            if (!ushort.TryParse(
                                _json.Substring(_index, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out codePoint))
                            {
                                throw new InvalidOperationException(
                                    "JSON Unicode escape is invalid.");
                            }
                            value.Append((char)codePoint);
                            _index += 4;
                            break;
                        default:
                            throw new InvalidOperationException(
                                "JSON string escape is invalid.");
                    }
                }

                throw new InvalidOperationException(
                    "JSON string is incomplete.");
            }

            private void ParsePrimitive()
            {
                var start = _index;
                while (_index < _json.Length)
                {
                    var current = _json[_index];
                    if (
                        char.IsWhiteSpace(current) ||
                        current == ',' ||
                        current == ']' ||
                        current == '}')
                    {
                        break;
                    }
                    _index++;
                }
                if (_index == start)
                    throw new InvalidOperationException(
                        "JSON primitive is invalid.");
            }

            private bool Consume(char expected)
            {
                if (_index < _json.Length && _json[_index] == expected)
                {
                    _index++;
                    return true;
                }
                return false;
            }

            private void Expect(char expected)
            {
                if (!Consume(expected))
                    throw new InvalidOperationException(
                        "JSON structure is invalid.");
            }

            private void SkipWhitespace()
            {
                while (
                    _index < _json.Length &&
                    char.IsWhiteSpace(_json[_index]))
                {
                    _index++;
                }
            }
        }
    }
}
'@ | Out-Null
}

function ConvertTo-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $volumeRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($volumeRoot)) {
        throw "Path must resolve to an absolute path: $Path"
    }

    if ([string]::Equals(
            $fullPath,
            $volumeRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        return $volumeRoot
    }

    return $fullPath.TrimEnd([char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ))
}

function Get-FinalExistingPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $shareMode =
        $nativeType::FileShareRead -bor
        $nativeType::FileShareWrite -bor
        $nativeType::FileShareDelete
    $handle = $nativeType::CreateFileW(
        $Path,
        0,
        $shareMode,
        [IntPtr]::Zero,
        $nativeType::OpenExisting,
        $nativeType::FileFlagBackupSemantics,
        [IntPtr]::Zero)
    if ($handle.IsInvalid) {
        $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $handle.Dispose()
        throw (New-Object ComponentModel.Win32Exception(
            $error,
            "Final physical path handle open failed: $Path"))
    }

    try {
        $buffer = New-Object Text.StringBuilder 512
        $result = $nativeType::GetFinalPathNameByHandleW(
            $handle,
            $buffer,
            [uint32]$buffer.Capacity,
            0)
        if ($result -ge $buffer.Capacity) {
            $buffer = New-Object Text.StringBuilder ([int]$result + 1)
            $result = $nativeType::GetFinalPathNameByHandleW(
                $handle,
                $buffer,
                [uint32]$buffer.Capacity,
                0)
        }
        if ($result -eq 0 -or $result -ge $buffer.Capacity) {
            $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw (New-Object ComponentModel.Win32Exception(
                $error,
                "Final physical path lookup failed: $Path"))
        }

        $finalPath = $buffer.ToString()
    }
    finally {
        $handle.Dispose()
    }

    if ($finalPath.StartsWith(
            '\\?\UNC\',
            [StringComparison]::OrdinalIgnoreCase)) {
        $finalPath = '\\' + $finalPath.Substring(8)
    }
    elseif ($finalPath.StartsWith(
            '\\?\',
            [StringComparison]::OrdinalIgnoreCase)) {
        $finalPath = $finalPath.Substring(4)
    }

    return ConvertTo-NormalizedFullPath -Path $finalPath
}

function Resolve-PhysicalPathIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    $missingSegments = New-Object 'Collections.Generic.Stack[string]'
    $existingPath = $fullPath
    while (-not (Test-Path -LiteralPath $existingPath)) {
        $parent = [IO.Directory]::GetParent($existingPath)
        if ($null -eq $parent) {
            throw "No existing ancestor was found for path: $Path"
        }

        $missingSegments.Push([IO.Path]::GetFileName($existingPath))
        $existingPath = $parent.FullName
    }

    $existingItem = Get-Item -LiteralPath $existingPath -Force
    if ($missingSegments.Count -gt 0 -and -not $existingItem.PSIsContainer) {
        throw "A path ancestor is not a directory: $existingPath"
    }

    $physicalPath = Get-FinalExistingPath -Path $existingPath
    while ($missingSegments.Count -gt 0) {
        $physicalPath = Join-Path $physicalPath $missingSegments.Pop()
    }

    return ConvertTo-NormalizedFullPath -Path $physicalPath
}

function Enter-PlainDirectoryAncestorChainLease {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    $nearestExistingAncestor = $fullPath
    while (-not (Test-Path -LiteralPath $nearestExistingAncestor)) {
        $parent = [IO.Directory]::GetParent($nearestExistingAncestor)
        if ($null -eq $parent) {
            throw (
                'Unsafe isolated build cache: no existing ancestor was ' +
                "found for $Description. Path=$Path")
        }
        $nearestExistingAncestor = $parent.FullName
    }

    $existingItem = Get-Item `
        -LiteralPath $nearestExistingAncestor `
        -Force `
        -ErrorAction Stop
    if (-not $existingItem.PSIsContainer) {
        throw (
            'Unsafe isolated build cache: nearest existing ancestor is ' +
            "not a directory for $Description. Path=$nearestExistingAncestor")
    }

    try {
        $lease = Enter-SourceAppRootIdentityLease `
            -Path $nearestExistingAncestor
    }
    catch {
        throw [InvalidOperationException]::new(
            (
                'Unsafe isolated build cache: a reparse point, ' +
                'non-directory, or changed physical ancestor was found for ' +
                "$Description. Path=$nearestExistingAncestor"
            ),
            $_.Exception)
    }
    $lease | Add-Member `
        -MemberType NoteProperty `
        -Name RequestedPath `
        -Value $fullPath
    $lease | Add-Member `
        -MemberType NoteProperty `
        -Name NearestExistingAncestor `
        -Value $nearestExistingAncestor
    return $lease
}

function Enter-IsolatedBuildCacheLeafMutationLease {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    $sentinelPath = Join-Path $fullPath '.georaeplan-build-cache-lease'
    $stream = $null
    try {
        Initialize-TestEnvironmentFinalPathNativeMethods
        $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
        $sentinelItem = Get-Item `
            -LiteralPath $sentinelPath `
            -Force `
            -ErrorAction Stop
        if (
            $sentinelItem.PSIsContainer -or
            ($sentinelItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw (
                'Unsafe isolated build cache: cache lease sentinel is not ' +
                "a plain file. Path=$sentinelPath")
        }
        $stream = [IO.File]::Open(
            $sentinelPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            ([IO.FileShare]::Read -bor [IO.FileShare]::Write))
        $sentinelFinalPath = ConvertTo-NormalizedFullPath -Path (
            $nativeType::GetFinalPath($stream.SafeFileHandle))
        if (-not [string]::Equals(
                $sentinelFinalPath,
                (ConvertTo-NormalizedFullPath -Path $sentinelPath),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Unsafe isolated build cache: cache lease sentinel changed ' +
                "physical identity. Path=$sentinelPath")
        }
        $lease = [pscustomobject]@{
            Path = $fullPath
            SentinelPath = $sentinelPath
            Stream = $stream
        }
        $lease | Add-Member `
            -MemberType ScriptMethod `
            -Name Dispose `
            -Value {
                if ($null -ne $this.Stream) {
                    $this.Stream.Dispose()
                }
            }
        $stream = $null
        return $lease
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Enter-IsolatedBuildEnvironmentPreflightLease {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$EnvironmentPaths,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$ScriptRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$SourceAppRoot,
        [Parameter(Mandatory = $true)][string]$DesktopSourceRoot,
        [Parameter(Mandatory = $true)][string]$ServerSourceRoot,
        [Parameter(Mandatory = $true)]
        [string]$SourceUsersSnapshotAllowedRoot,
        [AllowNull()][AllowEmptyString()][string]$SourceUsersSnapshotPath
    )

    $leases = [Collections.Generic.List[object]]::new()
    $physicalCachePaths = [ordered]@{}
    try {
        foreach ($cachePath in @(
            $EnvironmentPaths.Values | Sort-Object -Unique
        )) {
            $logicalCachePath =
                ConvertTo-NormalizedFullPath -Path ([string]$cachePath)
            if (-not [string]::Equals(
                    [IO.Path]::GetPathRoot($logicalCachePath),
                    'D:\',
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    'Unsafe isolated build cache: every cache path must ' +
                    "remain on D:. Path=$logicalCachePath")
            }

            $leases.Add((
                Enter-PlainDirectoryAncestorChainLease `
                    -Path $logicalCachePath `
                    -Description 'build cache path'))
            $physicalCachePaths[$logicalCachePath] =
                Resolve-PhysicalPathIdentity -Path $logicalCachePath
        }

        $protectedPaths = [ordered]@{
            ProjectRoot = $ProjectRoot
            ScriptRoot = $ScriptRoot
            OutputRoot = $OutputRoot
            CurrentRuntimeRoot = Join-Path $ScriptRoot '실행환경'
            SourceAppRoot = $SourceAppRoot
            DesktopSourceRoot = $DesktopSourceRoot
            ServerSourceRoot = $ServerSourceRoot
            SourceUsersSnapshotAllowedRoot = $SourceUsersSnapshotAllowedRoot
        }
        if (-not [string]::IsNullOrWhiteSpace($SourceUsersSnapshotPath)) {
            $protectedPaths['SourceUsersSnapshot'] = $SourceUsersSnapshotPath
        }
        $runtimeSnapshotIndex = 0
        foreach ($runtimeSnapshot in @(
            Get-ChildItem `
                -LiteralPath $ScriptRoot `
                -Directory `
                -Force `
                -Filter '실행환경-원본스냅샷-*' `
                -ErrorAction Stop
        )) {
            $runtimeSnapshotIndex++
            $protectedPaths[
                "ProtectedRuntimeSnapshot$runtimeSnapshotIndex"
            ] = $runtimeSnapshot.FullName
        }

        $physicalProtectedPaths = [ordered]@{}
        foreach ($entry in $protectedPaths.GetEnumerator()) {
            $protectedPath =
                ConvertTo-NormalizedFullPath -Path ([string]$entry.Value)
            $protectedLeasePath = if (
                Test-Path -LiteralPath $protectedPath -PathType Leaf
            ) {
                Split-Path -Parent $protectedPath
            }
            else {
                $protectedPath
            }
            $leases.Add((
                Enter-PlainDirectoryAncestorChainLease `
                    -Path $protectedLeasePath `
                    -Description ([string]$entry.Key)))
            $physicalProtectedPaths[$entry.Key] =
                Resolve-PhysicalPathIdentity -Path $protectedPath
        }

        foreach ($cacheEntry in $physicalCachePaths.GetEnumerator()) {
            foreach ($protectedEntry in $physicalProtectedPaths.GetEnumerator()) {
                $cacheOverlapsProtected =
                    (Test-PathSameOrDescendant `
                        -CandidatePath $cacheEntry.Value `
                        -ParentPath $protectedEntry.Value) -or
                    (Test-PathSameOrDescendant `
                        -CandidatePath $protectedEntry.Value `
                        -ParentPath $cacheEntry.Value)
                if ($cacheOverlapsProtected) {
                    throw (
                        'Unsafe isolated build cache: cache path overlaps ' +
                        "$($protectedEntry.Key). " +
                        "CachePath=$($cacheEntry.Key) " +
                        "ProtectedPath=$($protectedEntry.Value)")
                }
            }
        }

        $result = [pscustomobject]@{
            Leases = $leases
            MutationLeases = [Collections.Generic.List[object]]::new()
            PhysicalCachePaths = $physicalCachePaths
        }
        $result | Add-Member `
            -MemberType ScriptMethod `
            -Name Dispose `
            -Value {
                foreach ($entry in @($this.Leases)) {
                    if ($null -ne $entry) {
                        $entry.Dispose()
                    }
                }
                foreach ($entry in @($this.MutationLeases)) {
                    if ($null -ne $entry) {
                        $entry.Dispose()
                    }
                }
            }
        return $result
    }
    catch {
        foreach ($entry in @($leases)) {
            if ($null -ne $entry) {
                $entry.Dispose()
            }
        }
        throw
    }
}

function Assert-IsolatedBuildEnvironmentInitialized {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$EnvironmentPaths,
        [Parameter(Mandatory = $true)][object]$PreflightLease
    )

    foreach ($cachePath in @(
        $EnvironmentPaths.Values | Sort-Object -Unique
    )) {
        $logicalCachePath =
            ConvertTo-NormalizedFullPath -Path ([string]$cachePath)
        $validationLease = $null
        $mutationLease = $null
        try {
            $validationLease =
                Enter-PlainDirectoryAncestorChainLease `
                    -Path $logicalCachePath `
                    -Description 'initialized build cache path'
            if (-not (Test-Path -LiteralPath $logicalCachePath -PathType Container)) {
                throw (
                    'Unsafe isolated build cache: initialized path is not ' +
                    "a directory. Path=$logicalCachePath")
            }
            $physicalCachePath =
                Resolve-PhysicalPathIdentity -Path $logicalCachePath
            $expectedPhysicalPath = [string](
                $PreflightLease.PhysicalCachePaths[$logicalCachePath])
            if (-not [string]::Equals(
                    $physicalCachePath,
                    $expectedPhysicalPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    'Unsafe isolated build cache: physical path changed ' +
                    "during initialization. Path=$logicalCachePath")
            }
            $PreflightLease.Leases.Add($validationLease)
            $validationLease = $null
            $mutationLease = Enter-IsolatedBuildCacheLeafMutationLease `
                -Path $logicalCachePath
            Assert-SourceAppRootIdentityLease `
                -Lease $PreflightLease.Leases[
                    $PreflightLease.Leases.Count - 1]
            $physicalCachePath =
                Resolve-PhysicalPathIdentity -Path $logicalCachePath
            if (-not [string]::Equals(
                    $physicalCachePath,
                    $expectedPhysicalPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    'Unsafe isolated build cache: physical path changed ' +
                    "while acquiring its mutation lease. Path=$logicalCachePath")
            }
            $PreflightLease.MutationLeases.Add($mutationLease)
            $mutationLease = $null
        }
        finally {
            if ($null -ne $mutationLease) {
                $mutationLease.Dispose()
            }
            if ($null -ne $validationLease) {
                $validationLease.Dispose()
            }
        }
    }
}

function Assert-IsolatedBuildEnvironmentPreflightLease {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$EnvironmentPaths,
        [Parameter(Mandatory = $true)][object]$PreflightLease
    )

    if (
        $null -eq $PreflightLease -or
        $null -eq $PreflightLease.Leases -or
        @($PreflightLease.Leases).Count -eq 0
    ) {
        throw 'Unsafe isolated build cache: preflight identity lease is missing.'
    }
    if (
        $null -eq $PreflightLease.MutationLeases -or
        @($PreflightLease.MutationLeases).Count -eq 0
    ) {
        throw 'Unsafe isolated build cache: mutation lease is missing.'
    }
    foreach ($mutationLease in @($PreflightLease.MutationLeases)) {
        if (
            $null -eq $mutationLease.Stream -or
            $mutationLease.Stream.SafeFileHandle.IsClosed -or
            $mutationLease.Stream.SafeFileHandle.IsInvalid
        ) {
            throw 'Unsafe isolated build cache: mutation lease was released.'
        }
    }
    foreach ($lease in @($PreflightLease.Leases)) {
        Assert-SourceAppRootIdentityLease -Lease $lease
    }
    foreach ($cachePath in @(
        $EnvironmentPaths.Values | Sort-Object -Unique
    )) {
        $logicalCachePath =
            ConvertTo-NormalizedFullPath -Path ([string]$cachePath)
        if (-not (Test-Path -LiteralPath $logicalCachePath -PathType Container)) {
            throw (
                'Unsafe isolated build cache: cache path disappeared before ' +
                "build. Path=$logicalCachePath")
        }
        $actualPhysicalPath =
            Resolve-PhysicalPathIdentity -Path $logicalCachePath
        $expectedPhysicalPath = [string](
            $PreflightLease.PhysicalCachePaths[$logicalCachePath])
        if (-not [string]::Equals(
                $actualPhysicalPath,
                $expectedPhysicalPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Unsafe isolated build cache: physical path changed before ' +
                "build. Path=$logicalCachePath")
        }
    }
}

function Enter-SourceAppRootIdentityLease {
    param([Parameter(Mandatory = $true)][string]$Path)

    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    $volumeRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($volumeRoot)) {
        throw "SourceAppRoot must resolve to an absolute path: $Path"
    }

    $pathComponents = [Collections.Generic.List[string]]::new()
    $pathComponents.Add($volumeRoot)
    $relativePath = $fullPath.Substring($volumeRoot.Length).Trim(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    $currentPath = $volumeRoot
    if (-not [string]::IsNullOrWhiteSpace($relativePath)) {
        foreach ($segment in $relativePath.Split([char[]]@('\', '/'))) {
            if ([string]::IsNullOrWhiteSpace($segment)) {
                continue
            }
            $currentPath = Join-Path $currentPath $segment
            $pathComponents.Add($currentPath)
        }
    }

    $entries = [Collections.Generic.List[object]]::new()
    try {
        foreach ($componentPath in $pathComponents) {
            $normalizedComponent =
                ConvertTo-NormalizedFullPath -Path $componentPath
            $item = Get-Item `
                -LiteralPath $normalizedComponent `
                -Force `
                -ErrorAction Stop
            if (
                -not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'SourceAppRoot path contains a reparse point or ' +
                    "non-directory ancestor: $normalizedComponent")
            }

            $handle = $nativeType::CreateFileW(
                $normalizedComponent,
                $nativeType::FileReadAttributes,
                ($nativeType::FileShareRead -bor $nativeType::FileShareWrite),
                [IntPtr]::Zero,
                $nativeType::OpenExisting,
                $nativeType::FileFlagBackupSemantics,
                [IntPtr]::Zero)
            if ($handle.IsInvalid) {
                $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                $handle.Dispose()
                throw (New-Object ComponentModel.Win32Exception(
                    $error,
                    "SourceAppRoot identity lease open failed: $normalizedComponent"))
            }

            try {
                $finalPath = ConvertTo-NormalizedFullPath -Path (
                    $nativeType::GetFinalPath($handle))
                $information = $nativeType::GetFileInformation($handle)
                $attributes = [IO.FileAttributes]$information.FileAttributes
                if (
                    -not [string]::Equals(
                        $normalizedComponent,
                        $finalPath,
                        [StringComparison]::OrdinalIgnoreCase) -or
                    ($attributes -band [IO.FileAttributes]::Directory) -eq 0 -or
                    ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
                ) {
                    throw (
                        'SourceAppRoot path contains a reparse point or ' +
                        "changed physical identity: $normalizedComponent")
                }

                $entries.Add([pscustomobject]@{
                    Path = $normalizedComponent
                    Handle = $handle
                    VolumeSerialNumber = [uint32]$information.VolumeSerialNumber
                    FileIndexHigh = [uint32]$information.FileIndexHigh
                    FileIndexLow = [uint32]$information.FileIndexLow
                })
                $handle = $null
            }
            finally {
                if ($null -ne $handle) {
                    $handle.Dispose()
                }
            }
        }

        $lease = [pscustomobject]@{
            Root = $fullPath
            Entries = @($entries)
        }
        $lease | Add-Member `
            -MemberType ScriptMethod `
            -Name Dispose `
            -Value {
                foreach ($entry in @($this.Entries)) {
                    if ($null -ne $entry.Handle) {
                        $entry.Handle.Dispose()
                    }
                }
            }
        return $lease
    }
    catch {
        foreach ($entry in @($entries)) {
            if ($null -ne $entry.Handle) {
                $entry.Handle.Dispose()
            }
        }
        throw
    }
}

function Assert-SourceAppRootIdentityLease {
    param([Parameter(Mandatory = $true)][object]$Lease)

    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    if ($null -eq $Lease -or @($Lease.Entries).Count -eq 0) {
        throw 'SourceAppRoot identity lease is missing.'
    }

    foreach ($expected in @($Lease.Entries)) {
        $freshHandle = $null
        try {
            $item = Get-Item `
                -LiteralPath ([string]$expected.Path) `
                -Force `
                -ErrorAction Stop
            if (
                -not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw 'The path is no longer a plain directory.'
            }

            $freshHandle = $nativeType::CreateFileW(
                [string]$expected.Path,
                $nativeType::FileReadAttributes,
                ($nativeType::FileShareRead -bor $nativeType::FileShareWrite),
                [IntPtr]::Zero,
                $nativeType::OpenExisting,
                $nativeType::FileFlagBackupSemantics,
                [IntPtr]::Zero)
            if ($freshHandle.IsInvalid) {
                $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                throw (New-Object ComponentModel.Win32Exception(
                    $error,
                    'SourceAppRoot identity revalidation handle open failed.'))
            }

            $leasedInformation =
                $nativeType::GetFileInformation($expected.Handle)
            $freshInformation =
                $nativeType::GetFileInformation($freshHandle)
            $leasedFinalPath = ConvertTo-NormalizedFullPath -Path (
                $nativeType::GetFinalPath($expected.Handle))
            $freshFinalPath = ConvertTo-NormalizedFullPath -Path (
                $nativeType::GetFinalPath($freshHandle))
            if (
                -not [string]::Equals(
                    [string]$expected.Path,
                    $leasedFinalPath,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [string]$expected.Path,
                    $freshFinalPath,
                    [StringComparison]::OrdinalIgnoreCase) -or
                [uint32]$leasedInformation.VolumeSerialNumber -ne
                    [uint32]$expected.VolumeSerialNumber -or
                [uint32]$leasedInformation.FileIndexHigh -ne
                    [uint32]$expected.FileIndexHigh -or
                [uint32]$leasedInformation.FileIndexLow -ne
                    [uint32]$expected.FileIndexLow -or
                [uint32]$freshInformation.VolumeSerialNumber -ne
                    [uint32]$expected.VolumeSerialNumber -or
                [uint32]$freshInformation.FileIndexHigh -ne
                    [uint32]$expected.FileIndexHigh -or
                [uint32]$freshInformation.FileIndexLow -ne
                    [uint32]$expected.FileIndexLow
            ) {
                throw 'The physical directory identity changed.'
            }
        }
        catch {
            throw (
                'SourceAppRoot physical identity changed during snapshot copy. ' +
                "Path=$($expected.Path) Reason=$($_.Exception.Message)")
        }
        finally {
            if ($null -ne $freshHandle) {
                $freshHandle.Dispose()
            }
        }
    }
}

function Test-PathSameOrDescendant {
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $candidate = ConvertTo-NormalizedFullPath -Path $CandidatePath
    $parent = ConvertTo-NormalizedFullPath -Path $ParentPath
    if ([string]::Equals(
            $candidate,
            $parent,
            [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $parentPrefix = if (
        $parent.EndsWith(
            [IO.Path]::DirectorySeparatorChar.ToString(),
            [StringComparison]::Ordinal)
    ) {
        $parent
    }
    else {
        $parent + [IO.Path]::DirectorySeparatorChar
    }
    return $candidate.StartsWith(
        $parentPrefix,
        [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeTestEnvironmentOutputRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$ScriptRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$SourceAppRoot,
        [Parameter(Mandatory = $true)][string]$DesktopSourceRoot,
        [Parameter(Mandatory = $true)][string]$ServerSourceRoot
    )

    $physicalOutputRoot =
        Resolve-PhysicalPathIdentity -Path $OutputRoot
    $physicalProjectRoot =
        Resolve-PhysicalPathIdentity -Path $ProjectRoot
    $requiredWritableVolumeRoot = 'D:\'
    $logicalOutputVolumeRoot = [IO.Path]::GetPathRoot(
        [IO.Path]::GetFullPath($OutputRoot))
    $logicalProjectVolumeRoot = [IO.Path]::GetPathRoot(
        [IO.Path]::GetFullPath($ProjectRoot))
    $logicalScriptVolumeRoot = [IO.Path]::GetPathRoot(
        [IO.Path]::GetFullPath($ScriptRoot))
    if (
        -not [string]::Equals(
            $logicalOutputVolumeRoot,
            $requiredWritableVolumeRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $logicalProjectVolumeRoot,
            $requiredWritableVolumeRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $logicalScriptVolumeRoot,
            $requiredWritableVolumeRoot,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw (
            'Unsafe OutputRoot: V1 restore writable roots must stay on D:. ' +
            "ProjectRoot=$ProjectRoot ScriptRoot=$ScriptRoot OutputRoot=$OutputRoot")
    }

    $outputVolumeRoot = [IO.Path]::GetPathRoot($physicalOutputRoot)
    $projectVolumeRoot = [IO.Path]::GetPathRoot($physicalProjectRoot)
    if (
        [string]::IsNullOrWhiteSpace($outputVolumeRoot) -or
        [string]::IsNullOrWhiteSpace($projectVolumeRoot) -or
        -not [string]::Equals(
            $outputVolumeRoot,
            $projectVolumeRoot,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw (
            'Unsafe OutputRoot: test output must stay on the project volume. ' +
            "ProjectRoot=$ProjectRoot OutputRoot=$OutputRoot")
    }

    $protectedAncestors = [ordered]@{
        ProjectRoot = $physicalProjectRoot
        ScriptRoot = Resolve-PhysicalPathIdentity -Path $ScriptRoot
    }
    foreach ($entry in $protectedAncestors.GetEnumerator()) {
        if (Test-PathSameOrDescendant `
                -CandidatePath $entry.Value `
                -ParentPath $physicalOutputRoot) {
            throw "Unsafe OutputRoot: it is the same as or contains $($entry.Key). OutputRoot=$OutputRoot"
        }
    }

    $bidirectionalProtectedRoots = [ordered]@{
        SourceAppRoot = Resolve-PhysicalPathIdentity -Path $SourceAppRoot
        DesktopSourceRoot = Resolve-PhysicalPathIdentity -Path $DesktopSourceRoot
        ServerSourceRoot = Resolve-PhysicalPathIdentity -Path $ServerSourceRoot
    }
    foreach ($entry in $bidirectionalProtectedRoots.GetEnumerator()) {
        $outputContainsProtected =
            Test-PathSameOrDescendant `
                -CandidatePath $entry.Value `
                -ParentPath $physicalOutputRoot
        $protectedContainsOutput =
            Test-PathSameOrDescendant `
                -CandidatePath $physicalOutputRoot `
                -ParentPath $entry.Value
        if ($outputContainsProtected -or $protectedContainsOutput) {
            throw "Unsafe OutputRoot: it overlaps $($entry.Key). OutputRoot=$OutputRoot"
        }
    }

    if (Test-Path -LiteralPath $OutputRoot -PathType Container) {
        foreach ($topLevelEntry in Get-ChildItem -LiteralPath $OutputRoot -Force) {
            if (
                ($topLevelEntry.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'Unsafe OutputRoot: top-level reparse point found. ' +
                    "Path=$($topLevelEntry.FullName)")
            }
            if (
                -not $topLevelEntry.PSIsContainer -and
                [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::GetLinkCount(
                    $topLevelEntry.FullName) -ne 1
            ) {
                throw (
                    'Unsafe OutputRoot: top-level file has multiple hard links. ' +
                    "Path=$($topLevelEntry.FullName)")
            }
        }
    }

    foreach ($childName in @(
        'App',
        'Server',
        'AppData',
        'ServerData',
        'RuntimeLogs',
        'Mobile'
    )) {
        $childPath = Join-Path $OutputRoot $childName
        $physicalChildPath = Resolve-PhysicalPathIdentity -Path $childPath
        $childIsWithinOutputRoot =
            Test-PathSameOrDescendant `
                -CandidatePath $physicalChildPath `
                -ParentPath $physicalOutputRoot
        $childEqualsOutputRoot = [string]::Equals(
            $physicalChildPath,
            $physicalOutputRoot,
            [StringComparison]::OrdinalIgnoreCase)
        if (-not $childIsWithinOutputRoot -or $childEqualsOutputRoot) {
            throw "Unsafe OutputRoot: $childName resolves outside the isolated root. OutputRoot=$OutputRoot"
        }

        if (-not (Test-Path -LiteralPath $childPath)) {
            continue
        }

        $pendingDirectories =
            New-Object 'Collections.Generic.Queue[string]'
        $pendingDirectories.Enqueue($childPath)
        while ($pendingDirectories.Count -gt 0) {
            $directory = $pendingDirectories.Dequeue()
            foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
                if (
                    ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne
                    0
                ) {
                    throw "Unsafe OutputRoot: nested reparse point found under $childName. Path=$($entry.FullName)"
                }

                if ($entry.PSIsContainer) {
                    $pendingDirectories.Enqueue($entry.FullName)
                }
                elseif (
                    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::GetLinkCount(
                        $entry.FullName) -ne 1
                ) {
                    throw (
                        'Unsafe OutputRoot: nested file has multiple hard links ' +
                        "under $childName. Path=$($entry.FullName)")
                }
            }
        }
    }
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content,
        [switch]$WithBom
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $encoding = if ($WithBom) { New-Utf8BomEncoding } else { New-Utf8NoBomEncoding }
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Set-RuntimeInvalidationMarker {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    if (Test-Path -LiteralPath $Path) {
        return
    }

    $temporaryPath =
        $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Write-Utf8File `
            -Path $temporaryPath `
            -Content (@(
                'runtime_invalid=True',
                "reason=$Reason",
                "invalidated_at_utc=$([DateTimeOffset]::UtcNow.ToString('O'))"
            ) -join [Environment]::NewLine) `
            -WithBom
        [IO.File]::Move($temporaryPath, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item `
                -LiteralPath $temporaryPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'The runtime invalidation marker could not be established.'
    }
}

function Enter-PreparationGateLease {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(1, 200)][int]$Attempts = 40,
        [ValidateRange(1, 1000)][int]$RetryDelayMilliseconds = 50
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            return [IO.File]::Open(
                $Path,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
        }
        catch {
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds $RetryDelayMilliseconds
                continue
            }

            throw [InvalidOperationException]::new(
                (
                    '다른 테스트 환경 준비 또는 런타임 시작 작업이 같은 ' +
                    "OutputRoot를 사용 중입니다: $Path"
                ),
                $_.Exception)
        }
    }
}

function Assert-PreparationExclusionLease {
    param(
        [Parameter(Mandatory = $true)][object]$Lease,
        [Parameter(Mandatory = $true)][string]$InvalidMarkerPath
    )

    if (
        $null -eq $Lease -or
        -not $Lease.CanRead -or
        -not $Lease.CanWrite
    ) {
        throw 'The preparation-exclusive runtime lease is not held.'
    }
    if (-not (Test-Path -LiteralPath $InvalidMarkerPath -PathType Leaf)) {
        throw 'The runtime invalidation marker was lost during preparation.'
    }
}

function Remove-RuntimeInvalidationMarker {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(1, 100)][int]$Attempts = 20
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) {
            return
        }
        try {
            Remove-Item `
                -LiteralPath $Path `
                -Force `
                -ErrorAction Stop
        }
        catch {
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds 50
            }
        }
    }

    if (Test-Path -LiteralPath $Path) {
        throw (
            'The runtime invalidation marker could not be cleared; ' +
            'the runtime remains blocked.')
    }
}

function Enter-RuntimeInvalidationMarkerIdentityLease {
    param([Parameter(Mandatory = $true)][string]$Path)

    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    $handle = $nativeType::CreateFileW(
        $fullPath,
        ($nativeType::DeleteAccess -bor $nativeType::GenericWrite -bor
         $nativeType::FileReadAttributes),
        ($nativeType::FileShareRead -bor $nativeType::FileShareWrite),
        [IntPtr]::Zero,
        $nativeType::OpenExisting,
        $nativeType::FileFlagOpenReparsePoint,
        [IntPtr]::Zero)
    if ($handle.IsInvalid) {
        $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $handle.Dispose()
        throw (New-Object ComponentModel.Win32Exception(
            $error,
            'Unable to retain the runtime invalidation marker identity.'))
    }
    try {
        $information = $nativeType::GetFileInformation($handle)
        if (
            ($information.FileAttributes -band
                [uint32][IO.FileAttributes]::Directory) -ne 0 -or
            ($information.FileAttributes -band
                [uint32][IO.FileAttributes]::ReparsePoint) -ne 0 -or
            [uint32]$information.NumberOfLinks -ne 1 -or
            -not [string]::Equals(
                (ConvertTo-NormalizedFullPath `
                    -Path ($nativeType::GetFinalPath($handle))),
                $fullPath,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'The runtime invalidation marker identity is unsafe.'
        }
        return $handle
    }
    catch {
        $handle.Dispose()
        throw
    }
}

function Enter-RuntimeInvalidationMarkerTransactionState {
    param(
        [Parameter(Mandatory = $true)][object]$OutputRootLease,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $fullRoot = ConvertTo-NormalizedFullPath -Path $OutputRoot
    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    if (-not [string]::Equals(
            (Split-Path -Parent $fullPath),
            $fullRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The runtime invalidation marker escaped OutputRoot.'
    }
    $content = @(
        'runtime_invalid=True',
        "reason=$Reason",
        "invalidated_at_utc=$([DateTimeOffset]::UtcNow.ToString('O'))"
    ) -join [Environment]::NewLine
    $encoding = New-Utf8BomEncoding
    [byte[]]$markerBytes = @(
        $encoding.GetPreamble()
        $encoding.GetBytes($content)
    )
    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $priorExists = $false
    [byte[]]$priorBytes = $null
    $lease = $nativeType::OpenOrCreateHeldRuntimeInvalidMarker(
        $OutputRootLease,
        $fullRoot,
        (Split-Path -Leaf $fullPath),
        $markerBytes,
        [ref]$priorExists,
        [ref]$priorBytes)
    return [pscustomobject]@{
        Lease = $lease
        Snapshot = [pscustomobject]@{
            Path = $fullPath
            Exists = [bool]$priorExists
            Bytes = $priorBytes
        }
    }
}

function Restore-HeldRuntimeInvalidationMarkerSnapshot {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][object]$Lease
    )

    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    if ([bool]$Snapshot.Exists) {
        $nativeType::OverwriteHeldExactSingleLinkRegularFile(
            $Lease,
            [string]$Snapshot.Path,
            [byte[]]$Snapshot.Bytes)
    }
    else {
        $nativeType::DeleteHeldExactSingleLinkRegularFile(
            $Lease,
            [string]$Snapshot.Path)
    }
}

function Publish-TestFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$TemporaryPath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $targetDirectory = Split-Path -Parent $TargetPath
    $backupPath = Join-Path $targetDirectory (
        ".$([IO.Path]::GetFileName($TargetPath))." +
        [Guid]::NewGuid().ToString('N') +
        '.bak')
    if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
        try {
            [IO.File]::Replace(
                $TemporaryPath,
                $TargetPath,
                $backupPath,
                $true)
            Remove-Item `
                -LiteralPath $backupPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
        catch {
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                Remove-Item `
                    -LiteralPath $backupPath `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
            throw
        }
    }
    else {
        [IO.File]::Move($TemporaryPath, $TargetPath)
    }
}

function Get-TestCsprojPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectFile,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    [xml]$project = Get-Content -LiteralPath $ProjectFile -Raw -Encoding UTF8
    $values = @(
        $project.Project.PropertyGroup |
            ForEach-Object { $_.$PropertyName } |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { ([string]$_).Trim() } |
            Select-Object -Unique
    )
    if ($values.Count -ne 1) {
        throw (
            "Android project property must have exactly one value. " +
            "property=$PropertyName count=$($values.Count)")
    }

    return $values[0]
}

function Initialize-TestAndroidPackageMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$MobileProject,
        [string]$AndroidPackagePath,
        [string]$ApkAnalyzerPath,
        [string]$JavaSdkDirectory,
        [object]$ValidatedSnapshot,
        [switch]$InspectOnly,
        [ref]$SnapshotReference
    )

    $mobileRoot = Join-Path $OutputRoot 'Mobile'
    $sidecarPath = Join-Path $mobileRoot 'android-package.metadata.json'
    $candidatePath = ''
    if ($null -ne $ValidatedSnapshot) {
        $candidatePath = [string]$ValidatedSnapshot.SnapshotPath
    }
    elseif (-not [string]::IsNullOrWhiteSpace($AndroidPackagePath)) {
        $candidatePath = if ([IO.Path]::IsPathRooted($AndroidPackagePath)) {
            $AndroidPackagePath
        }
        else {
            Join-Path $ProjectRoot $AndroidPackagePath
        }
        if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
            throw "Requested Android test APK not found: $candidatePath"
        }
    }
    elseif (Test-Path -LiteralPath $sidecarPath -PathType Leaf) {
        try {
            $existingSidecar =
                Get-Content -LiteralPath $sidecarPath -Raw -Encoding UTF8 |
                    ConvertFrom-Json
        }
        catch {
            throw "Existing Android test metadata sidecar is invalid: $sidecarPath"
        }
        $existingFileName = ([string]$existingSidecar.fileName).Trim()
        if (
            [string]::IsNullOrWhiteSpace($existingFileName) -or
            -not [string]::Equals(
                [IO.Path]::GetFileName($existingFileName),
                $existingFileName,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [IO.Path]::GetExtension($existingFileName),
                '.apk',
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Existing Android test metadata sidecar has an unsafe fileName.'
        }
        $candidatePath = Join-Path $mobileRoot $existingFileName
        if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
            throw (
                'Existing Android test metadata sidecar references a missing APK: ' +
                $candidatePath)
        }
    }
    else {
        foreach ($fileName in @(
            '거래플랜-Mobile-Test-Debug.apk',
            'kr.georaeplan.mobile-Signed.apk'
        )) {
            $candidate = Join-Path $mobileRoot $fileName
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $candidatePath = $candidate
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($candidatePath)) {
        $unexpectedApks = @(
            Get-ChildItem `
                -LiteralPath $mobileRoot `
                -File `
                -Filter '*.apk' `
                -ErrorAction SilentlyContinue
        )
        if ($unexpectedApks.Count -gt 0) {
            throw (
                'Android test APK exists without a recognized source or metadata ' +
                "sidecar: $($unexpectedApks[0].FullName)")
        }
        if ($InspectOnly) {
            if ($null -eq $SnapshotReference) {
                throw 'InspectOnly requires a snapshot reference.'
            }
            $SnapshotReference.Value = $null
            return
        }
        return [pscustomobject]@{
            State = 'absent'
            FileName = 'none'
            Sha256 = 'none'
            MetadataSha256 = 'none'
        }
    }

    $expectedApplicationId =
        Get-TestCsprojPropertyValue `
            -ProjectFile $MobileProject `
            -PropertyName 'ApplicationId'
    $expectedVersionName =
        Get-TestCsprojPropertyValue `
            -ProjectFile $MobileProject `
            -PropertyName 'ApplicationDisplayVersion'
    $expectedVersionCode =
        Get-TestCsprojPropertyValue `
            -ProjectFile $MobileProject `
            -PropertyName 'ApplicationVersion'
    if ($expectedVersionName -notmatch '^\d+(?:\.\d+)+$') {
        throw (
            'Android project ApplicationDisplayVersion is not a safe ' +
            'dotted version.')
    }
    $metadata = $ValidatedSnapshot
    $ownsMetadata = $false
    $ownershipTransferred = $false
    try {
        if ($null -eq $metadata) {
            $metadata = New-GeoraePlanAndroidApkSnapshot `
                -ApkPath $candidatePath `
                -ProjectRoot $ProjectRoot `
                -ApkAnalyzerPath $ApkAnalyzerPath `
                -JavaSdkDirectory $JavaSdkDirectory `
                -SourceName 'test runtime candidate'
            $ownsMetadata = $true
        }
        Assert-GeoraePlanAndroidApkSnapshot `
            -Snapshot $metadata `
            -SourceName 'test runtime candidate'
        Assert-GeoraePlanAndroidApkMetadata `
            -Metadata $metadata `
            -ExpectedApplicationId $expectedApplicationId `
            -ExpectedVersionName $expectedVersionName `
            -ExpectedVersionCode $expectedVersionCode `
            -SourceName 'test runtime candidate'

        if ($InspectOnly) {
            if ($null -eq $SnapshotReference) {
                throw 'InspectOnly requires a snapshot reference.'
            }
            $stateProperty = $metadata.PSObject.Properties['State']
            if ($null -eq $stateProperty) {
                $metadata | Add-Member `
                    -NotePropertyName State `
                    -NotePropertyValue 'present'
            }
            else {
                $stateProperty.Value = 'present'
            }
            $SnapshotReference.Value = $metadata
            $ownershipTransferred = $true
            return
        }
        if ($ownsMetadata) {
            return Initialize-TestAndroidPackageMetadata `
                -ProjectRoot $ProjectRoot `
                -OutputRoot $OutputRoot `
                -MobileProject $MobileProject `
                -ApkAnalyzerPath $ApkAnalyzerPath `
                -JavaSdkDirectory $JavaSdkDirectory `
                -ValidatedSnapshot $metadata
        }
    }
    finally {
        if ($ownsMetadata -and -not $ownershipTransferred) {
            Remove-GeoraePlanAndroidApkSnapshot -Snapshot $metadata
        }
    }

    New-Item -ItemType Directory -Force -Path $mobileRoot | Out-Null
    $runtimeFileName = "tradeplan-android-test-v$($metadata.VersionName).apk"
    $runtimeApkPath = Join-Path $mobileRoot $runtimeFileName
    $candidateFullPath = [IO.Path]::GetFullPath($candidatePath)
    $runtimeFullPath = [IO.Path]::GetFullPath($runtimeApkPath)
    if (-not [string]::Equals(
        $candidateFullPath,
        $runtimeFullPath,
        [StringComparison]::OrdinalIgnoreCase)
    ) {
        $runtimeTempPath = Join-Path $mobileRoot (
            ".$runtimeFileName.$([Guid]::NewGuid().ToString('N')).tmp")
        try {
            Assert-GeoraePlanAndroidApkSnapshot `
                -Snapshot $metadata `
                -SourceName 'test runtime publish source'
            Copy-Item `
                -LiteralPath $metadata.SnapshotPath `
                -Destination $runtimeTempPath `
                -Force
            Assert-GeoraePlanAndroidApkSnapshot `
                -Snapshot $metadata `
                -SourceName 'test runtime publish source after copy'
            $runtimeTempFile = Get-Item -LiteralPath $runtimeTempPath
            $runtimeTempHash = (
                Get-FileHash -LiteralPath $runtimeTempPath -Algorithm SHA256
            ).Hash
            if (
                $runtimeTempFile.Length -ne $metadata.FileSize -or
                -not [string]::Equals(
                    $runtimeTempHash,
                    $metadata.Sha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw 'Android test APK changed while copying into the runtime.'
            }
            Publish-TestFileAtomically `
                -TemporaryPath $runtimeTempPath `
                -TargetPath $runtimeFullPath
        }
        finally {
            if (Test-Path -LiteralPath $runtimeTempPath -PathType Leaf) {
                Remove-Item `
                    -LiteralPath $runtimeTempPath `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
        }
    }

    $runtimeFile = Get-Item -LiteralPath $runtimeFullPath
    $runtimeHash = (
        Get-FileHash -LiteralPath $runtimeFullPath -Algorithm SHA256
    ).Hash
    if (
        $runtimeFile.Length -ne $metadata.FileSize -or
        -not [string]::Equals(
            $runtimeHash,
            $metadata.Sha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Android test APK does not match the validated candidate.'
    }

    $sidecar = [ordered]@{
        schemaVersion = 1
        fileName = $runtimeFileName
        applicationId = $metadata.ApplicationId
        versionName = $metadata.VersionName
        versionCode = [long]$metadata.VersionCode
        sha256 = $metadata.Sha256
        fileSize = [long]$metadata.FileSize
    }
    $sidecarTempPath = Join-Path $mobileRoot (
        '.android-package.metadata.' +
        [Guid]::NewGuid().ToString('N') +
        '.tmp')
    try {
        Write-Utf8File `
            -Path $sidecarTempPath `
            -Content ($sidecar | ConvertTo-Json -Depth 4)
        Publish-TestFileAtomically `
            -TemporaryPath $sidecarTempPath `
            -TargetPath $sidecarPath
    }
    finally {
        if (Test-Path -LiteralPath $sidecarTempPath -PathType Leaf) {
            Remove-Item `
                -LiteralPath $sidecarTempPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
    $sidecarSha256 = (
        Get-FileHash -LiteralPath $sidecarPath -Algorithm SHA256
    ).Hash

    return [pscustomobject]@{
        State = 'present'
        FileName = $runtimeFileName
        Sha256 = $runtimeHash
        MetadataSha256 = $sidecarSha256
    }
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $DotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 명령이 실패했습니다. args=$($Arguments -join ' ')"
    }
}

function Invoke-DotnetWithOutput {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $quoteArgument = {
        param([string]$Value)

        if ($null -eq $Value -or $Value.Length -eq 0) {
            return '""'
        }

        if ($Value -notmatch '[\s"]') {
            return $Value
        }

        return '"' + ($Value.Replace('"', '\"')) + '"'
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $DotnetExe
    $startInfo.Arguments = (($Arguments | ForEach-Object { & $quoteArgument $_ }) -join ' ')
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode

    $output = @()
    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        $output += ($stdout -split "`r?`n")
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        $output += ($stderr -split "`r?`n")
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = (@($output) | ForEach-Object { $_.ToString() })
        Text = ((@($output) | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

function Invoke-HiddenSetApiBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string[]]$AppSettingsPaths
    )

    $toPowerShellLiteral = {
        param([Parameter(Mandatory = $true)][string]$Value)

        return "'" + $Value.Replace("'", "''") + "'"
    }
    $scriptLiteral =
        & $toPowerShellLiteral ([IO.Path]::GetFullPath($ScriptPath))
    $baseUrlLiteral = & $toPowerShellLiteral $BaseUrl
    $appSettingsLiterals = @(
        $AppSettingsPaths |
            ForEach-Object {
                & $toPowerShellLiteral ([IO.Path]::GetFullPath($_))
            }
    )
    if ($appSettingsLiterals.Count -eq 0) {
        throw 'At least one appsettings path is required.'
    }

    $command = (
        '$ProgressPreference = ''SilentlyContinue''; ' +
        '$ErrorActionPreference = ''Stop''; & ' +
        $scriptLiteral +
        ' -BaseUrl ' +
        $baseUrlLiteral +
        ' -AppSettingsPaths @(' +
        ($appSettingsLiterals -join ',') +
        ') 3>&1 4>&1 5>&1 6>&1'
    )
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($command))
    $windowsPowerShellPath = Join-Path `
        ([Environment]::SystemDirectory) `
        'WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShellPath -PathType Leaf)) {
        throw 'The absolute Windows PowerShell path was not found.'
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $windowsPowerShellPath
    $startInfo.Arguments = (
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
        "-EncodedCommand $encodedCommand"
    )
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The hidden Set-ApiBaseUrl process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdout
            StandardError = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        if ($AllowFailure) { return '' }
        throw 'git 명령을 찾지 못했습니다.'
    }

    Push-Location $ProjectRoot
    try {
        $output = & $git.Source @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git 명령이 실패했습니다. args=$($Arguments -join ' ')"
    }

    return (@($output) | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
}

function Find-FirstFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Filter
    )

    $match = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Filter | Select-Object -First 1
    if ($null -eq $match) {
        throw "필수 파일을 찾지 못했습니다. Filter=$Filter Root=$Root"
    }

    return $match.FullName
}

function Find-DeploymentRoot {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidate = Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Set-ApiBaseUrl.ps1') } |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw '배포 실행 스크립트 루트를 찾지 못했습니다.'
    }

    return $candidate.FullName
}

function Build-ChangedFilesMarkdown {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$GeneratedAt,
        [Parameter(Mandatory = $true)][string]$Branch,
        [Parameter(Mandatory = $true)][string]$Commit
    )

    $statusText = Get-GitOutput -ProjectRoot $ProjectRoot -Arguments @('-c', 'core.quotepath=false', 'status', '--short') -AllowFailure
    $lines = @($statusText -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('# 최근 수정 파일')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("- 생성 시각: $GeneratedAt")
    [void]$builder.AppendLine(('- Git 브랜치: `{0}`' -f $Branch))
    [void]$builder.AppendLine(('- Git 커밋: `{0}`' -f $Commit))
    [void]$builder.AppendLine()

    if ($lines.Count -eq 0) {
        [void]$builder.AppendLine('현재 Git 기준 수정/추가 파일이 없습니다.')
        return $builder.ToString().TrimEnd()
    }

    [void]$builder.AppendLine('## Git status --short')
    [void]$builder.AppendLine()
    foreach ($line in $lines) {
        [void]$builder.AppendLine(('- `{0}`' -f $line))
    }
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## 확인 메모')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('- 위 파일이 이번 테스트 대상입니다.')
    [void]$builder.AppendLine('- 테스트 완료 전에는 Linux PC/Git 반영을 진행하지 않습니다.')
    return $builder.ToString().TrimEnd()
}

function Build-ChecklistContent {
    param(
        [Parameter(Mandatory = $true)][string]$TemplatePath,
        [Parameter(Mandatory = $true)][hashtable]$Tokens
    )

    $content = Get-Content -LiteralPath $TemplatePath -Raw
    foreach ($key in $Tokens.Keys) {
        $token = ('{{{{{0}}}}}' -f $key)
        $content = $content.Replace($token, $Tokens[$key])
    }

    return $content
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$ExcludeDirectories = @(),
        [string[]]$ExcludeFiles = @()
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $arguments = @(
        $Source,
        $Destination,
        '/MIR',
        '/R:2',
        '/W:2',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS',
        '/NP',
        '/XJ'
    )

    if ($ExcludeDirectories.Count -gt 0) {
        $arguments += '/XD'
        $arguments += $ExcludeDirectories
    }

    if ($ExcludeFiles.Count -gt 0) {
        $arguments += '/XF'
        $arguments += $ExcludeFiles
    }

    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination"
    }
}

function Invoke-WithProcessEnvironment {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Variables,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $backup = @{}
    foreach ($key in $Variables.Keys) {
        $backup[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$Variables[$key], 'Process')
    }

    try {
        return & $Action
    }
    finally {
        foreach ($key in $backup.Keys) {
            [Environment]::SetEnvironmentVariable($key, $backup[$key], 'Process')
        }
    }
}

function Get-FreeTcpPort {
    param([int]$StartingPort = 19080)

    $port = $StartingPort
    while ($true) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        }
        catch {
            $port++
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }
}

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$TimeoutSeconds = 40
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            $healthPayload = $response.Content | ConvertFrom-Json
            if (
                [int]$response.StatusCode -eq 200 -and
                (
                    [string]::Equals(
                        [string]$healthPayload.status,
                        'ok',
                        [StringComparison]::OrdinalIgnoreCase) -or
                    [string]::Equals(
                        [string]$healthPayload.status,
                        'ready',
                        [StringComparison]::OrdinalIgnoreCase)
                )
            ) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Initialize-StoredCredentialBoundedProcessCapture {
    if ($null -ne ('GeoraePlan.TestEnvironment.BoundedProcessCapture' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.TestEnvironment
{
    public sealed class BoundedProcessCaptureResult
    {
        public int ExitCode { get; set; }
        public string Stdout { get; set; }
        public string Stderr { get; set; }
        public string FailureReason { get; set; }
    }

    public static class BoundedProcessCapture
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint WaitTimeout = 0x00000102;
        private const uint WaitObject0 = 0x00000000;
        private const uint StillActive = 259;
        private static readonly IntPtr ProcThreadAttributeHandleList =
            new IntPtr(0x00020002);
        private const int StdInputHandle = -10;

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved;
            public string Desktop;
            public string Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public short ShowWindow;
            public short Reserved2Length;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
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
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(
            IntPtr securityAttributes,
            string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(
            IntPtr job,
            IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(
            IntPtr job,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(
            IntPtr process,
            uint exitCode);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out IntPtr readPipe,
            out IntPtr writePipe,
            ref SecurityAttributes attributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(
            IntPtr handle,
            uint mask,
            uint flags);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(
            IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            IntPtr handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(
            IntPtr process,
            out uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int standardHandle);

        public static BoundedProcessCaptureResult Run(
            string fileName,
            string arguments,
            string workingDirectory,
            int timeoutMilliseconds,
            int maximumStdoutBytes,
            int maximumStderrBytes)
        {
            var result = new BoundedProcessCaptureResult {
                ExitCode = -1,
                Stdout = String.Empty,
                Stderr = String.Empty,
                FailureReason = String.Empty
            };
            if (timeoutMilliseconds <= 0 ||
                maximumStdoutBytes <= 0 ||
                maximumStderrBytes <= 0)
            {
                result.FailureReason = "capture_failed";
                return result;
            }

            IntPtr job = IntPtr.Zero;
            var processInformation = new ProcessInformation();
            IntPtr stdoutRead = IntPtr.Zero;
            IntPtr stdoutWrite = IntPtr.Zero;
            IntPtr stderrRead = IntPtr.Zero;
            IntPtr stderrWrite = IntPtr.Zero;
            Stream stdoutStream = null;
            Stream stderrStream = null;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr inheritedHandles = IntPtr.Zero;
            Thread stdoutThread = null;
            Thread stderrThread = null;
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();
            var failureLock = new object();
            string failureReason = String.Empty;

            Action<string> fail = reason => {
                lock (failureLock)
                {
                    if (failureReason.Length == 0)
                        failureReason = reason;
                }
                if (job != IntPtr.Zero)
                    TerminateJobObject(job, 253);
            };

            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero || !EnableKillOnJobClose(job))
                    throw new InvalidOperationException();

                bool isCommandScript =
                    fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                string commandInterpreter =
                    Environment.GetEnvironmentVariable("ComSpec");
                string launchFile = isCommandScript
                    ? (String.IsNullOrEmpty(commandInterpreter)
                        ? "cmd.exe"
                        : commandInterpreter)
                    : fileName;
                string launchArguments = isCommandScript
                    ? "/d /c call \"" +
                        fileName.Replace("\"", "\"\"") +
                        "\" " + arguments
                    : arguments;

                var pipeAttributes = new SecurityAttributes {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                    InheritHandle = true
                };
                if (!CreatePipe(
                        out stdoutRead,
                        out stdoutWrite,
                        ref pipeAttributes,
                        0) ||
                    !SetHandleInformation(
                        stdoutRead,
                        HandleFlagInherit,
                        0) ||
                    !CreatePipe(
                        out stderrRead,
                        out stderrWrite,
                        ref pipeAttributes,
                        0) ||
                    !SetHandleInformation(
                        stderrRead,
                        HandleFlagInherit,
                        0))
                {
                    throw new InvalidOperationException();
                }

                IntPtr attributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    1,
                    0,
                    ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!InitializeProcThreadAttributeList(
                        attributeList,
                        1,
                        0,
                        ref attributeListSize))
                {
                    throw new InvalidOperationException();
                }
                inheritedHandles =
                    Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(
                    inheritedHandles,
                    0,
                    stdoutWrite);
                Marshal.WriteIntPtr(
                    inheritedHandles,
                    IntPtr.Size,
                    stderrWrite);
                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        ProcThreadAttributeHandleList,
                        inheritedHandles,
                        new IntPtr(IntPtr.Size * 2),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new InvalidOperationException();
                }
                var startupInfo = new StartupInfoEx {
                    AttributeList = attributeList,
                    StartupInfo = new StartupInfo {
                        Size = Marshal.SizeOf(typeof(StartupInfoEx)),
                        Flags = StartfUseStdHandles,
                        StandardInput = IntPtr.Zero,
                        StandardOutput = stdoutWrite,
                        StandardError = stderrWrite
                    }
                };
                var commandLine = new StringBuilder(
                    "\"" + launchFile.Replace("\"", "\\\"") + "\" " +
                    launchArguments);
                if (!CreateProcessW(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        CreateSuspended |
                            CreateNoWindow |
                            ExtendedStartupInfoPresent,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out processInformation))
                {
                    throw new InvalidOperationException();
                }
                CloseHandle(stdoutWrite);
                stdoutWrite = IntPtr.Zero;
                CloseHandle(stderrWrite);
                stderrWrite = IntPtr.Zero;

                if (!AssignProcessToJobObject(
                        job,
                        processInformation.Process))
                {
                    TerminateProcess(processInformation.Process, 254);
                    WaitForSingleObject(processInformation.Process, 5000);
                    throw new InvalidOperationException();
                }

                stdoutStream = new FileStream(
                    new SafeFileHandle(stdoutRead, true),
                    FileAccess.Read);
                stdoutRead = IntPtr.Zero;
                stderrStream = new FileStream(
                    new SafeFileHandle(stderrRead, true),
                    FileAccess.Read);
                stderrRead = IntPtr.Zero;
                stdoutThread = StartReader(
                    stdoutStream,
                    stdout,
                    maximumStdoutBytes,
                    "stdout_limit",
                    fail);
                stderrThread = StartReader(
                    stderrStream,
                    stderr,
                    maximumStderrBytes,
                    "stderr_limit",
                    fail);

                if (ResumeThread(processInformation.Thread) == UInt32.MaxValue)
                    throw new InvalidOperationException();

                var waitResult = WaitForSingleObject(
                    processInformation.Process,
                    (uint)timeoutMilliseconds);
                if (waitResult == WaitTimeout)
                    fail("timeout");
                else if (waitResult != WaitObject0)
                    fail("capture_failed");

                if (WaitForSingleObject(
                        processInformation.Process,
                        5000) != WaitObject0)
                {
                    fail("capture_failed");
                }
                if (job != IntPtr.Zero &&
                    !TerminateJobObject(job, 0))
                {
                    fail("capture_failed");
                }
                if (stdoutThread != null &&
                    !stdoutThread.Join(5000))
                {
                    fail("capture_failed");
                }
                if (stderrThread != null &&
                    !stderrThread.Join(5000))
                {
                    fail("capture_failed");
                }

                lock (failureLock)
                    result.FailureReason = failureReason;
                if (result.FailureReason.Length != 0)
                    return result;

                uint exitCode;
                if (!GetExitCodeProcess(
                        processInformation.Process,
                        out exitCode) ||
                    exitCode == StillActive)
                {
                    throw new InvalidOperationException();
                }
                result.ExitCode = unchecked((int)exitCode);
                var strictUtf8 = new UTF8Encoding(false, true);
                try
                {
                    result.Stdout = strictUtf8.GetString(stdout.ToArray());
                    result.Stderr = strictUtf8.GetString(stderr.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    result.Stdout = String.Empty;
                    result.Stderr = String.Empty;
                    result.FailureReason = "encoding_invalid";
                }
                return result;
            }
            catch
            {
                if (processInformation.Process != IntPtr.Zero)
                {
                    TerminateProcess(processInformation.Process, 254);
                    WaitForSingleObject(processInformation.Process, 5000);
                }
                if (job != IntPtr.Zero)
                    TerminateJobObject(job, 254);
                result.Stdout = String.Empty;
                result.Stderr = String.Empty;
                result.FailureReason = "capture_failed";
                return result;
            }
            finally
            {
                if (result.FailureReason.Length != 0)
                {
                    result.Stdout = String.Empty;
                    result.Stderr = String.Empty;
                }
                if (stdoutStream != null)
                    stdoutStream.Dispose();
                if (stderrStream != null)
                    stderrStream.Dispose();
                if (processInformation.Thread != IntPtr.Zero)
                    CloseHandle(processInformation.Thread);
                if (processInformation.Process != IntPtr.Zero)
                    CloseHandle(processInformation.Process);
                if (stdoutRead != IntPtr.Zero)
                    CloseHandle(stdoutRead);
                if (stdoutWrite != IntPtr.Zero)
                    CloseHandle(stdoutWrite);
                if (stderrRead != IntPtr.Zero)
                    CloseHandle(stderrRead);
                if (stderrWrite != IntPtr.Zero)
                    CloseHandle(stderrWrite);
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (inheritedHandles != IntPtr.Zero)
                    Marshal.FreeHGlobal(inheritedHandles);
                stdout.Dispose();
                stderr.Dispose();
                if (job != IntPtr.Zero)
                    CloseHandle(job);
            }
        }

        private static Thread StartReader(
            Stream source,
            MemoryStream destination,
            int maximumBytes,
            string limitReason,
            Action<string> fail)
        {
            var thread = new Thread(() => {
                var buffer = new byte[4096];
                try
                {
                    while (true)
                    {
                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                            return;
                        if (destination.Length + read > maximumBytes)
                        {
                            destination.SetLength(0);
                            fail(limitReason);
                            return;
                        }
                        destination.Write(buffer, 0, read);
                    }
                }
                catch
                {
                    fail("capture_failed");
                }
                finally
                {
                    Array.Clear(buffer, 0, buffer.Length);
                }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        private static bool EnableKillOnJobClose(IntPtr job)
        {
            var information = new ExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags =
                JobObjectLimitKillOnJobClose;
            int size = Marshal.SizeOf(typeof(ExtendedLimitInformation));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, pointer, false);
                return SetInformationJobObject(job, 9, pointer, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
'@
}

function Get-StoredCredentialSourceManifestSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$SyncDiagProject
    )

    $projectFullPath = [IO.Path]::GetFullPath($SyncDiagProject)
    $projectDirectory = Split-Path -Parent $projectFullPath
    $repositoryRoot = Split-Path -Parent (Split-Path -Parent $projectDirectory)
    # Pin the explicit SyncDiag project-reference closure without allowing
    # unrelated test evidence or output files to invalidate the artifact.
    $sourceRoots = @(
        $projectDirectory,
        (Join-Path $repositoryRoot 'Desktop\거래플랜.Desktop.App'),
        (Join-Path $repositoryRoot 'Shared'),
        (Join-Path $repositoryRoot 'Updater\거래플랜.Updater')
    )
    $files = @(
        foreach ($sourceRoot in $sourceRoots) {
            if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
                continue
            }
            Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
                Where-Object {
                    $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
                    $_.Extension -in @(
                        '.cs',
                        '.csproj',
                        '.props',
                        '.targets',
                        '.xaml',
                        '.resx',
                        '.config',
                        '.json',
                        '.xml',
                        '.lock',
                        '.sln'
                    )
                }
        }
    )
    $rootBuildFiles = @(
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'global.json'
    ) | ForEach-Object {
        $candidate = Join-Path $repositoryRoot $_
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            Get-Item -LiteralPath $candidate
        }
    }
    $files = @($files + $rootBuildFiles | Sort-Object FullName -Unique)
    if ($files.Count -eq 0) {
        throw 'Stored credential source manifest is empty.'
    }

    $manifestLines = @(
        $files | ForEach-Object {
            $_.FullName + '|' +
                (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    )
    $manifestBytes = [Text.Encoding]::UTF8.GetBytes(
        $manifestLines -join "`n")
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            return (
                [BitConverter]::ToString(
                    $sha256.ComputeHash($manifestBytes))
            ).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        [Array]::Clear($manifestBytes, 0, $manifestBytes.Length)
    }
}

function Remove-StaleSecureIsolatedWorkDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][object]$ParentLease
    )

    if ($null -eq $ParentLease -or $ParentLease.IsInvalid) {
        throw 'The secure work parent lease is invalid.'
    }
    $candidatePaths =
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            GetBoundedGuidChildDirectories(
                $ParentLease,
                $ParentPath,
                256,
                64,
                250)

    foreach ($candidatePath in $candidatePaths) {
        $staleLease = $null
        try {
            $staleLease =
                Open-StoredCredentialArtifactDirectoryLease `
                    -Path $candidatePath `
                    -DeleteCapable
        }
        catch {
            # Active work roots and roots that cannot be pinned are untouched.
            continue
        }

        try {
            try {
                [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                    AssertPrivateDirectoryAcl($candidatePath)
            }
            catch {
                # Preserve weak-era or foreign directories without traversal.
                continue
            }
            $markerPath = Join-Path `
                $candidatePath `
                '.georaeplan-secure-work-v1'
            if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
                continue
            }
            try {
                $markerContent =
                    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                        ReadExactSingleLinkUtf8File(
                            $markerPath,
                            $candidatePath)
            }
            catch {
                continue
            }
            if ($markerContent -cne 'georaeplan-secure-work-v1') {
                continue
            }
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                AssertPrivateTreeAcl($candidatePath)
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                DeletePrivateTreeAndRoot(
                    $staleLease,
                    $candidatePath)
        }
        catch {
            # A verified residue that cannot be safely removed is preserved.
            continue
        }
        finally {
            $staleLease.Dispose()
        }
    }
}

function New-SecureIsolatedWorkDirectory {
    param([Parameter(Mandatory = $true)][string]$Parent)

    Initialize-TestEnvironmentFinalPathNativeMethods
    $parentPath = [IO.Path]::GetFullPath($Parent)
    if (
        -not [string]::Equals(
            [IO.Path]::GetPathRoot($parentPath),
            'D:\',
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'A secure isolated work directory must remain on D:.'
    }
    New-Item -ItemType Directory -Path $parentPath -Force | Out-Null
    $parentLease = $null
    $rootPath = Join-Path $parentPath ([Guid]::NewGuid().ToString('N'))
    $rootLease = $null
    try {
        $parentLease =
            Open-StoredCredentialArtifactDirectoryLease -Path $parentPath
        Remove-StaleSecureIsolatedWorkDirectories `
            -ParentPath $parentPath `
            -ParentLease $parentLease
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            CreatePrivateDirectory($rootPath)
        $rootLease =
            Open-StoredCredentialArtifactDirectoryLease `
                -Path $rootPath `
                -DeleteCapable
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            AssertPrivateDirectoryAcl($rootPath)
        [IO.File]::WriteAllText(
            (Join-Path $rootPath '.georaeplan-secure-work-v1'),
            'georaeplan-secure-work-v1',
            (New-Object Text.UTF8Encoding($false)))
        return [pscustomobject]@{
            Parent = $parentPath
            Root = [IO.Path]::GetFullPath($rootPath)
            ParentLease = $parentLease
            RootLease = $rootLease
        }
    }
    catch {
        try {
            if ($null -ne $rootLease) {
                [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                    DeletePrivateTreeAndRoot($rootLease, $rootPath)
            }
        }
        finally {
            if ($null -ne $rootLease) {
                $rootLease.Dispose()
            }
            if ($null -ne $parentLease) {
                $parentLease.Dispose()
            }
        }
        # A root that could not be opened and pinned is intentionally retained.
        throw
    }
}

function Remove-SecureIsolatedWorkDirectory {
    param([Parameter(Mandatory = $true)][object]$WorkDirectory)

    $rootLease = $WorkDirectory.RootLease
    $parentLease = $WorkDirectory.ParentLease
    $rootPath = $null
    try {
        $rootPath = [IO.Path]::GetFullPath([string]$WorkDirectory.Root)
        $parentPath = [IO.Path]::GetFullPath([string]$WorkDirectory.Parent)
        if (
            -not $rootPath.StartsWith(
                $parentPath + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($rootPath) -cnotmatch '^[a-f0-9]{32}$'
        ) {
            throw 'The secure isolated work directory identity is invalid.'
        }
        if ($null -eq $rootLease) {
            throw 'The secure isolated work directory root lease is unavailable.'
        }
        if ($null -eq $parentLease) {
            throw 'The secure isolated work directory parent lease is unavailable.'
        }
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            AssertPrivateTreeAcl($rootPath)
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            DeletePrivateTreeAndRoot(
                $rootLease,
                $rootPath)
    }
    finally {
        if ($null -ne $rootLease) {
            $rootLease.Dispose()
            $WorkDirectory.RootLease = $null
        }
        if ($null -ne $parentLease) {
            $parentLease.Dispose()
            $WorkDirectory.ParentLease = $null
        }
    }
    if (
        $null -eq $rootPath -or
        (Test-Path -LiteralPath $rootPath)
    ) {
        throw 'The secure isolated work directory cleanup failed.'
    }
}

function Open-StoredCredentialArtifactDirectoryLease {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$DeleteCapable
    )

    Initialize-TestEnvironmentFinalPathNativeMethods
    $fullPath = [IO.Path]::GetFullPath($Path)
    $current = Get-Item -LiteralPath $fullPath -Force
    while ($null -ne $current) {
        if (
            ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw 'Stored credential artifact ancestry contains a reparse point.'
        }
        $current = $current.Parent
    }
    if (
        -not [string]::Equals(
            [IO.Path]::GetPathRoot($fullPath),
            'D:\',
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Stored credential artifact cache must remain on D:.'
    }

    $desiredAccess = if ($DeleteCapable) {
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::DeleteAccess -bor
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                FileListDirectory -bor
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                FileReadAttributes
    }
    else {
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            FileListDirectory -bor
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            FileReadAttributes
    }
    $handle =
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::CreateFileW(
            $fullPath,
            $desiredAccess,
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::FileShareRead -bor
                [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::FileShareWrite,
            [IntPtr]::Zero,
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::OpenExisting,
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::FileFlagBackupSemantics -bor
                [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::FileFlagOpenReparsePoint,
            [IntPtr]::Zero)
    if ($handle.IsInvalid) {
        $handle.Dispose()
        throw 'Stored credential artifact directory lease could not be acquired.'
    }
    try {
        $information =
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                GetFileInformation($handle)
        $attributes = [IO.FileAttributes]$information.FileAttributes
        $finalPath = [IO.Path]::GetFullPath(
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                GetFinalPath($handle))
        if (
            ($attributes -band [IO.FileAttributes]::Directory) -eq 0 -or
            ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not [string]::Equals(
                $finalPath,
                $fullPath,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Stored credential artifact directory identity is unsafe.'
        }
        return $handle
    }
    catch {
        $handle.Dispose()
        throw
    }
}

function Open-StoredCredentialArtifactTreeLease {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$IncludeControlManifest
    )

    $rootPath = [IO.Path]::GetFullPath($Root)
    $manifestPath = Join-Path $rootPath 'artifact-manifest.txt'
    $directoryPaths = @(
        $rootPath
        Get-ChildItem -LiteralPath $rootPath -Recurse -Directory -Force |
            ForEach-Object { $_.FullName }
    ) | Sort-Object -Unique
    $directoryLeases = New-Object 'Collections.Generic.List[object]'
    $fileLeases = New-Object 'Collections.Generic.List[object]'
    $maximumArtifactFileCount = 2048
    $maximumArtifactTotalBytes = 536870912L
    try {
        foreach ($directoryPath in $directoryPaths) {
            $directoryLeases.Add(
                (Open-StoredCredentialArtifactDirectoryLease `
                    -Path $directoryPath))
        }
        $filePaths = @(
            @(
                Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force |
                    Where-Object {
                        $IncludeControlManifest -or
                        -not [string]::Equals(
                            $_.FullName,
                            $manifestPath,
                            [StringComparison]::OrdinalIgnoreCase)
                    } |
                    ForEach-Object { $_.FullName }
            ) | Sort-Object -Unique
        )
        if ($filePaths.Count -gt $maximumArtifactFileCount) {
            throw 'Stored credential artifact file count limit was exceeded.'
        }
        $artifactTotalBytes = 0L
        foreach ($filePath in $filePaths) {
            $item = Get-Item -LiteralPath $filePath -Force
            if (
                ($item.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw 'Stored credential artifact file is a reparse point.'
            }
            if (
                [long]$item.Length -lt 0 -or
                $artifactTotalBytes -gt
                    $maximumArtifactTotalBytes - [long]$item.Length
            ) {
                throw 'Stored credential artifact byte limit was exceeded.'
            }
            $artifactTotalBytes += [long]$item.Length
            $stream = New-Object IO.FileStream(
                $filePath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::Read)
            try {
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $hash = (
                        [BitConverter]::ToString(
                            $sha256.ComputeHash($stream))
                    ).Replace('-', '')
                }
                finally {
                    $sha256.Dispose()
                    $stream.Position = 0
                }
                $finalPath = [IO.Path]::GetFullPath(
                    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                        GetFinalPath($stream.SafeFileHandle))
                if (
                    -not [string]::Equals(
                        $finalPath,
                        [IO.Path]::GetFullPath($filePath),
                        [StringComparison]::OrdinalIgnoreCase)
                ) {
                    throw 'Stored credential artifact file identity is unsafe.'
                }
                $fileLeases.Add([pscustomobject]@{
                    RelativePath =
                        $filePath.Substring($rootPath.Length).
                            TrimStart('\', '/')
                    Length = [long]$stream.Length
                    Sha256 = $hash
                    Stream = $stream
                })
            }
            catch {
                $stream.Dispose()
                throw
            }
        }
        return [pscustomobject]@{
            Root = $rootPath
            DirectoryPaths = @($directoryPaths)
            DirectoryLeases = $directoryLeases
            Files = $fileLeases
            IncludeControlManifest = [bool]$IncludeControlManifest
            MaximumFileCount = $maximumArtifactFileCount
            MaximumTotalBytes = $maximumArtifactTotalBytes
            TotalBytes = $artifactTotalBytes
        }
    }
    catch {
        foreach ($file in $fileLeases) {
            $file.Stream.Dispose()
        }
        foreach ($lease in $directoryLeases) {
            $lease.Dispose()
        }
        throw
    }
}

function Assert-StoredCredentialArtifactTreeIntegrity {
    param([Parameter(Mandatory = $true)][object]$Tree)

    $manifestPath = Join-Path $Tree.Root 'artifact-manifest.txt'
    $actualDirectories = @(
        @(
            $Tree.Root
            Get-ChildItem -LiteralPath $Tree.Root -Recurse -Directory -Force |
                ForEach-Object { $_.FullName }
        ) | Sort-Object -Unique
    )
    $expectedDirectories = @($Tree.DirectoryPaths | Sort-Object -Unique)
    if (
        $actualDirectories.Count -ne $expectedDirectories.Count -or
        @(
            Compare-Object `
                -ReferenceObject $expectedDirectories `
                -DifferenceObject $actualDirectories `
                -CaseSensitive
        ).Count -ne 0
    ) {
        throw 'Stored credential artifact directory set changed.'
    }

    $actualFiles = @(
        @(
            Get-ChildItem -LiteralPath $Tree.Root -Recurse -File -Force |
                Where-Object {
                    [bool]$Tree.IncludeControlManifest -or
                    -not [string]::Equals(
                        $_.FullName,
                        $manifestPath,
                        [StringComparison]::OrdinalIgnoreCase)
                } |
                ForEach-Object {
                    $_.FullName.Substring($Tree.Root.Length).
                        TrimStart('\', '/')
                }
        ) | Sort-Object -Unique
    )
    $expectedFiles = @(
        @(
            $Tree.Files | ForEach-Object { $_.RelativePath }
        ) | Sort-Object -Unique
    )
    if (
        $expectedFiles.Count -gt [int]$Tree.MaximumFileCount -or
        [long]$Tree.TotalBytes -gt [long]$Tree.MaximumTotalBytes
    ) {
        throw 'Stored credential artifact bounds changed.'
    }
    if (
        $actualFiles.Count -ne $expectedFiles.Count -or
        @(
            Compare-Object `
                -ReferenceObject $expectedFiles `
                -DifferenceObject $actualFiles `
                -CaseSensitive
        ).Count -ne 0
    ) {
        throw 'Stored credential artifact file set changed.'
    }

    foreach ($file in $Tree.Files) {
        $itemPath = Join-Path $Tree.Root $file.RelativePath
        $item = Get-Item -LiteralPath $itemPath -Force
        if (
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            [long]$file.Stream.Length -ne [long]$file.Length
        ) {
            throw 'Stored credential artifact file identity changed.'
        }
        $file.Stream.Position = 0
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $actualHash = (
                [BitConverter]::ToString(
                    $sha256.ComputeHash($file.Stream))
            ).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
            $file.Stream.Position = 0
        }
        if ($actualHash -cne [string]$file.Sha256) {
            throw 'Stored credential artifact file hash changed.'
        }
    }
}

function Close-StoredCredentialArtifactTreeLease {
    param([Parameter(Mandatory = $true)][object]$Tree)

    $firstDisposeFailure = $null
    $manifestStreamProperty =
        $Tree.PSObject.Properties['ManifestStream']
    if (
        $null -ne $manifestStreamProperty -and
        $null -ne $manifestStreamProperty.Value
    ) {
        try {
            $manifestStreamProperty.Value.Dispose()
        }
        catch {
            if ($null -eq $firstDisposeFailure) {
                $firstDisposeFailure = $_
            }
        }
    }
    foreach ($file in $Tree.Files) {
        try {
            $file.Stream.Dispose()
        }
        catch {
            if ($null -eq $firstDisposeFailure) {
                $firstDisposeFailure = $_
            }
        }
    }
    foreach ($lease in $Tree.DirectoryLeases) {
        try {
            $lease.Dispose()
        }
        catch {
            if ($null -eq $firstDisposeFailure) {
                $firstDisposeFailure = $_
            }
        }
    }
    if ($null -ne $firstDisposeFailure) {
        throw $firstDisposeFailure
    }
}

function New-StoredCredentialEnvelopeArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$ConfigurationName
    )

    Initialize-StoredCredentialBoundedProcessCapture
    $projectFullPath = [IO.Path]::GetFullPath($SyncDiagProject)
    $sourceSha256 = Get-StoredCredentialSourceManifestSha256 `
        -SyncDiagProject $projectFullPath
    $cacheRoot =
        'D:\DevCaches\georaeplan-v1-prepare\stored-credential-envelope'
    $cacheRootLease = $null
    $workDirectory = $null
    $artifactTree = $null
    try {
    New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
    $cacheRootLease =
        Open-StoredCredentialArtifactDirectoryLease -Path $cacheRoot
    $workDirectory =
        New-SecureIsolatedWorkDirectory -Parent $cacheRoot
    $buildArtifactsRoot =
        Join-Path $workDirectory.Root 'build-artifacts'
    $artifactRoot = Join-Path $workDirectory.Root 'publish'
    New-Item -ItemType Directory -Path `
        $buildArtifactsRoot,
        $artifactRoot |
        Out-Null

    $restoreArguments = @(
        'restore',
        $projectFullPath,
        '--force',
        '-p:SkipCopyUpdaterOutput=true',
        '-p:UseArtifactsOutput=true',
        "-p:ArtifactsPath=$buildArtifactsRoot"
    )
    $quotedRestoreArguments = @(
        $restoreArguments | ForEach-Object {
            '"' + ([string]$_).Replace('"', '\"') + '"'
        }
    )
    $restore = [GeoraePlan.TestEnvironment.BoundedProcessCapture]::Run(
        $DotnetExe,
        ($quotedRestoreArguments -join ' '),
        (Split-Path -Parent $projectFullPath),
        120000,
        1048576,
        65536)
    if (
        -not [string]::IsNullOrEmpty([string]$restore.FailureReason) -or
        [int]$restore.ExitCode -ne 0
    ) {
        throw (
            'Stored credential artifact fresh restore failed. ' +
            "exit_code=$([int]$restore.ExitCode) " +
            "failure_reason=$([string]$restore.FailureReason)")
    }

    $publishArguments = @(
        'publish',
        $projectFullPath,
        '-c',
        $ConfigurationName,
        '--no-restore',
        '-o',
        $artifactRoot,
        '--no-self-contained',
        '-p:SkipCopyUpdaterOutput=true',
        '-p:UseArtifactsOutput=true',
        "-p:ArtifactsPath=$buildArtifactsRoot",
        "-p:PublishDir=$artifactRoot/"
    )
    $quotedPublishArguments = @(
        $publishArguments | ForEach-Object {
            '"' + ([string]$_).Replace('"', '\"') + '"'
        }
    )
    $publish = [GeoraePlan.TestEnvironment.BoundedProcessCapture]::Run(
        $DotnetExe,
        ($quotedPublishArguments -join ' '),
        (Split-Path -Parent $projectFullPath),
        120000,
        1048576,
        65536)
    if (
        -not [string]::IsNullOrEmpty([string]$publish.FailureReason) -or
        [int]$publish.ExitCode -ne 0
    ) {
        throw 'Stored credential artifact publish failed.'
    }

    $artifactDll = Join-Path $artifactRoot 'SyncDiag.dll'
    if (-not (Test-Path -LiteralPath $artifactDll -PathType Leaf)) {
        throw 'Stored credential artifact DLL was not produced.'
    }
    $sourceSha256After = Get-StoredCredentialSourceManifestSha256 `
        -SyncDiagProject $projectFullPath
    if ($sourceSha256After -cne $sourceSha256) {
        throw 'Stored credential sources changed during publish.'
    }
    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
        AssertPrivateTreeAcl($workDirectory.Root)
    $artifactTree =
        Open-StoredCredentialArtifactTreeLease -Root $artifactRoot
    Assert-StoredCredentialArtifactTreeIntegrity -Tree $artifactTree
    $artifactRecord = @(
        $artifactTree.Files |
            Where-Object {
                $_.RelativePath -ceq 'SyncDiag.dll'
            }
    )
    if ($artifactRecord.Count -ne 1) {
        throw 'Stored credential artifact tree lacks one SyncDiag DLL.'
    }
    $artifactSha256 = [string]$artifactRecord[0].Sha256
    $manifestPath = Join-Path $artifactRoot 'artifact-manifest.txt'
    $manifestLines = @(
        'schemaVersion=1'
        "sourceSha256=$sourceSha256"
        "artifactSha256=$artifactSha256"
    )
    $manifestLines += @(
        $artifactTree.Files |
            Sort-Object RelativePath |
            ForEach-Object {
                'file=' + $_.RelativePath + '|' +
                    [string]$_.Length + '|' + $_.Sha256
            }
    )
    $manifestContent = $manifestLines -join [Environment]::NewLine
    Write-Utf8File -Path $manifestPath -Content $manifestContent
    $manifestStream = New-Object IO.FileStream(
        $manifestPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $artifactTree | Add-Member `
        -NotePropertyName ManifestStream `
        -NotePropertyValue $manifestStream
    $artifactTree | Add-Member `
        -NotePropertyName ManifestContent `
        -NotePropertyValue $manifestContent
    Assert-StoredCredentialArtifactTreeIntegrity -Tree $artifactTree

    return [pscustomobject]@{
        Root = $artifactRoot
        DllPath = $artifactDll
        ManifestPath = $manifestPath
        SourceSha256 = $sourceSha256
        ArtifactSha256 = $artifactSha256
        TreeLease = $artifactTree
        CacheRootLease = $cacheRootLease
        WorkDirectory = $workDirectory
    }
    }
    catch {
        $preparationFailure = $_
        try {
            try {
                if ($null -ne $artifactTree) {
                    Close-StoredCredentialArtifactTreeLease `
                        -Tree $artifactTree
                }
            }
            catch {
                # Preserve the first artifact preparation failure.
            }
        }
        finally {
            try {
                try {
                    if ($null -ne $workDirectory) {
                        Remove-SecureIsolatedWorkDirectory `
                            -WorkDirectory $workDirectory
                    }
                }
                catch {
                    # Preserve the first artifact preparation failure.
                }
            }
            finally {
                try {
                    if ($null -ne $cacheRootLease) {
                        $cacheRootLease.Dispose()
                    }
                }
                catch {
                    # Preserve the first artifact preparation failure.
                }
            }
        }
        throw $preparationFailure
    }
}

function Invoke-StoredCredentialEnvelopeProcess {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [int]$TimeoutMilliseconds = 30000,
        [int]$MaximumStdoutBytes = 393216,
        [int]$MaximumStderrBytes = 8192
    )

    $projectFullPath = [IO.Path]::GetFullPath($SyncDiagProject)
    $configurationName = if (
        -not [string]::IsNullOrWhiteSpace([string]$Configuration)
    ) {
        [string]$Configuration
    }
    else {
        'Debug'
    }

    $artifact = $null
    try {
        $artifact = New-StoredCredentialEnvelopeArtifact `
            -DotnetExe $DotnetExe `
            -SyncDiagProject $projectFullPath `
            -ConfigurationName $configurationName
        if (
            (Get-StoredCredentialSourceManifestSha256 `
                -SyncDiagProject $projectFullPath) -cne
                    [string]$artifact.SourceSha256 -or
            [IO.File]::ReadAllText($artifact.ManifestPath) -cne
                [string]$artifact.TreeLease.ManifestContent
        ) {
            throw 'Stored credential artifact integrity changed.'
        }
        Assert-StoredCredentialArtifactTreeIntegrity `
            -Tree $artifact.TreeLease
    }
    catch {
        if ($null -ne $artifact) {
            try {
                try {
                    Close-StoredCredentialArtifactTreeLease `
                        -Tree $artifact.TreeLease
                }
                catch {
                    # The preparation failure remains the reported failure.
                }
            }
            finally {
                try {
                    try {
                        Remove-SecureIsolatedWorkDirectory `
                            -WorkDirectory $artifact.WorkDirectory
                    }
                    catch {
                        # The preparation failure remains the reported failure.
                    }
                }
                finally {
                    try {
                        $artifact.CacheRootLease.Dispose()
                    }
                    catch {
                        # The preparation failure remains the reported failure.
                    }
                }
            }
        }
        return [pscustomobject]@{
            ExitCode = -1
            Stdout = ''
            Stderr = ''
            FailureReason = 'artifact_preparation_failed'
            InvocationMode = 'pinned-publish-artifact'
        }
    }
    $projectDirectory = Split-Path -Parent $projectFullPath
    $arguments = @(
        [string]$artifact.DllPath,
        'stored-credential-envelopes'
    )

    $quotedArguments = @(
        $arguments | ForEach-Object {
            '"' + ([string]$_).Replace('"', '\"') + '"'
        }
    )
    Initialize-StoredCredentialBoundedProcessCapture
    try {
        $capture = [GeoraePlan.TestEnvironment.BoundedProcessCapture]::Run(
            $DotnetExe,
            ($quotedArguments -join ' '),
            $projectDirectory,
            $TimeoutMilliseconds,
            $MaximumStdoutBytes,
            $MaximumStderrBytes)
        Assert-StoredCredentialArtifactTreeIntegrity `
            -Tree $artifact.TreeLease
        if (
            [IO.File]::ReadAllText($artifact.ManifestPath) -cne
                [string]$artifact.TreeLease.ManifestContent
        ) {
            throw 'Stored credential artifact manifest changed during execution.'
        }
    }
    catch {
        $capture = [pscustomobject]@{
            ExitCode = -1
            Stdout = ''
            Stderr = ''
            FailureReason = 'artifact_integrity_failed'
        }
    }
    finally {
        $cleanupFailure = $null
        try {
            try {
                Close-StoredCredentialArtifactTreeLease `
                    -Tree $artifact.TreeLease
            }
            catch {
                $cleanupFailure = $_
            }
        }
        finally {
            try {
                try {
                    Remove-SecureIsolatedWorkDirectory `
                        -WorkDirectory $artifact.WorkDirectory
                }
                catch {
                    if ($null -eq $cleanupFailure) {
                        $cleanupFailure = $_
                    }
                }
            }
            finally {
                try {
                    $artifact.CacheRootLease.Dispose()
                }
                catch {
                    if ($null -eq $cleanupFailure) {
                        $cleanupFailure = $_
                    }
                }
            }
        }
        if ($null -ne $cleanupFailure) {
            throw $cleanupFailure
        }
    }
    return [pscustomobject]@{
        ExitCode = [int]$capture.ExitCode
        Stdout = [string]$capture.Stdout
        Stderr = [string]$capture.Stderr
        FailureReason = [string]$capture.FailureReason
        InvocationMode = 'pinned-publish-artifact'
    }
}

function ConvertFrom-StoredCredentialEnvelopeProcessResult {
    param(
        [Parameter(Mandatory = $true)][object]$Result,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $failureReason = 'invalid-envelope-or-decryption'
    try {
        Add-Type -AssemblyName System.Security -ErrorAction Stop
        $childFailureReason = [string]$Result.FailureReason
        if (-not [string]::IsNullOrEmpty($childFailureReason)) {
            if (@(
                    'timeout',
                    'stdout_limit',
                    'stderr_limit',
                    'capture_failed',
                    'encoding_invalid',
                    'artifact_preparation_failed',
                    'artifact_integrity_failed'
                ) -ccontains $childFailureReason) {
                $failureReason = 'child-' + $childFailureReason
            }
            throw 'Stored credential envelope child process was rejected.'
        }
        if (
            [int]$Result.ExitCode -ne 0 -or
            -not [string]::IsNullOrEmpty([string]$Result.Stderr)
        ) {
            throw 'Stored credential envelope child process failed.'
        }

        $jsonText = [string]$Result.Stdout
        if ($jsonText.Length -gt 393216) {
            throw 'Stored credential envelope stdout exceeds the limit.'
        }
        if ($jsonText.EndsWith("`r`n", [StringComparison]::Ordinal)) {
            $jsonText = $jsonText.Substring(0, $jsonText.Length - 2)
        }
        elseif (
            $jsonText.EndsWith("`n", [StringComparison]::Ordinal) -or
            $jsonText.EndsWith("`r", [StringComparison]::Ordinal)
        ) {
            $jsonText = $jsonText.Substring(0, $jsonText.Length - 1)
        }
        if (
            [string]::IsNullOrWhiteSpace($jsonText) -or
            $jsonText.IndexOf("`r", [StringComparison]::Ordinal) -ge 0 -or
            $jsonText.IndexOf("`n", [StringComparison]::Ordinal) -ge 0
        ) {
            throw 'Stored credential envelope stdout must be exactly one line.'
        }

        $jsonDepth = 0
        $jsonInString = $false
        $jsonEscaped = $false
        foreach ($jsonCharacter in $jsonText.ToCharArray()) {
            if ($jsonInString) {
                if ($jsonEscaped) {
                    $jsonEscaped = $false
                }
                elseif ($jsonCharacter -eq '\') {
                    $jsonEscaped = $true
                }
                elseif ($jsonCharacter -eq '"') {
                    $jsonInString = $false
                }
                continue
            }
            if ($jsonCharacter -eq '"') {
                $jsonInString = $true
                continue
            }
            if ($jsonCharacter -eq '{' -or $jsonCharacter -eq '[') {
                $jsonDepth++
                if ($jsonDepth -gt 12) {
                    throw 'Stored credential JSON nesting is too deep.'
                }
            }
            elseif ($jsonCharacter -eq '}' -or $jsonCharacter -eq ']') {
                $jsonDepth--
                if ($jsonDepth -lt 0) {
                    throw 'Stored credential JSON nesting is invalid.'
                }
            }
        }
        if ($jsonInString -or $jsonDepth -ne 0) {
            throw 'Stored credential JSON structure is invalid.'
        }

        if (
            $null -ne (
                'GeoraePlan.TestEnvironment.FinalPathNativeMethods' -as
                    [type])
        ) {
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                AssertNoDuplicateJsonObjectPropertiesAndDepth($jsonText, 12)
        }
        else {
            foreach ($knownPropertyName in @(
                'schemaVersion',
                'protection',
                'credentials'
            )) {
                $propertyPattern =
                    '"(?i:' +
                    [regex]::Escape($knownPropertyName) +
                    ')"\s*:'
                if (
                    [regex]::Matches(
                        $jsonText,
                        $propertyPattern).Count -gt 1
                ) {
                    throw 'Stored credential JSON contains a duplicate property.'
                }
            }
            $credentialObjects =
                [regex]::Matches($jsonText, '\{[^{}]*\}')
            foreach ($credentialObject in $credentialObjects) {
                foreach ($knownPropertyName in @(
                    'OfficeCode',
                    'TenantCode',
                    'Username',
                    'PasswordProtected',
                    'SavedAtUtc'
                )) {
                    $propertyPattern =
                        '"(?i:' +
                        [regex]::Escape($knownPropertyName) +
                        ')"\s*:'
                    if (
                        [regex]::Matches(
                            $credentialObject.Value,
                            $propertyPattern).Count -gt 1
                    ) {
                        throw 'Stored credential JSON contains a duplicate property.'
                    }
                }
            }
        }
        $parsed = $jsonText | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $parsed) {
            throw 'Stored credential envelope is empty.'
        }

        $envelopeFields = @($parsed.PSObject.Properties.Name)
        $requiredEnvelopeFields = @(
            'schemaVersion',
            'protection',
            'credentials'
        )
        if (
            @(
                $requiredEnvelopeFields |
                    Where-Object { $envelopeFields -cnotcontains $_ }
            ).Count -gt 0 -or
            @(
                $envelopeFields |
                    Where-Object { $requiredEnvelopeFields -cnotcontains $_ }
            ).Count -gt 0 -or
            $parsed.schemaVersion -isnot [int] -or
            [int]$parsed.schemaVersion -ne 1 -or
            $parsed.protection -isnot [string] -or
            [string]$parsed.protection -cne 'DPAPI-CurrentUser' -or
            $parsed.credentials -isnot [Array]
        ) {
            throw 'Stored credential envelope schema is invalid.'
        }

        $credentials = @($parsed.credentials | ForEach-Object { $_ })
        if ($credentials.Count -gt 16) {
            throw 'Stored credential envelope contains too many credentials.'
        }
        $requiredCredentialFields = @(
            'OfficeCode',
            'TenantCode',
            'Username',
            'PasswordProtected',
            'SavedAtUtc'
        )
        $validatedCredentials =
            New-Object 'Collections.Generic.List[object]'
        $seenOffices =
            [Collections.Generic.HashSet[string]]::new(
                [StringComparer]::OrdinalIgnoreCase)
        $seenUsernames =
            [Collections.Generic.HashSet[string]]::new(
                [StringComparer]::OrdinalIgnoreCase)
        foreach ($credential in $credentials) {
            $fieldNames = @($credential.PSObject.Properties.Name)
            if (
                @(
                    $requiredCredentialFields |
                        Where-Object { $fieldNames -cnotcontains $_ }
                ).Count -gt 0 -or
                @(
                    $fieldNames |
                        Where-Object {
                            $requiredCredentialFields -cnotcontains $_
                        }
                ).Count -gt 0 -or
                @(
                    $requiredCredentialFields |
                        Where-Object {
                            $property =
                                $credential.PSObject.Properties[$_]
                            $null -eq $property -or
                            $property.Value -isnot [string]
                        }
                ).Count -gt 0 -or
                [string]::IsNullOrWhiteSpace(
                    [string]$credential.OfficeCode) -or
                [string]::IsNullOrWhiteSpace(
                    [string]$credential.TenantCode) -or
                [string]::IsNullOrWhiteSpace(
                    [string]$credential.Username) -or
                [string]::IsNullOrEmpty(
                    [string]$credential.PasswordProtected) -or
                [string]::IsNullOrWhiteSpace(
                    [string]$credential.SavedAtUtc) -or
                ([string]$credential.OfficeCode).Length -gt 64 -or
                ([string]$credential.TenantCode).Length -gt 64 -or
                ([string]$credential.Username).Length -gt 256 -or
                ([string]$credential.PasswordProtected).Length -gt 24576 -or
                ([string]$credential.SavedAtUtc).Length -gt 64
            ) {
                throw 'Stored credential output shape is invalid.'
            }

            $savedAtUtc = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParseExact(
                    [string]$credential.SavedAtUtc,
                    'O',
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind,
                    [ref]$savedAtUtc) -or
                $savedAtUtc.Offset -ne [TimeSpan]::Zero) {
                throw 'Stored credential timestamp is invalid.'
            }

            if (
                -not $seenOffices.Add([string]$credential.OfficeCode) -or
                -not $seenUsernames.Add([string]$credential.Username)
            ) {
                throw 'Stored credential envelope contains a duplicate.'
            }

            [byte[]]$protectedBytes = $null
            [byte[]]$plainBytes = $null
            try {
                $protectedText = [string]$credential.PasswordProtected
                $protectedBytes = [Convert]::FromBase64String($protectedText)
                if (
                    $protectedBytes.Length -eq 0 -or
                    [Convert]::ToBase64String($protectedBytes) -cne
                        $protectedText
                ) {
                    throw 'Stored credential ciphertext is invalid.'
                }
                $plainBytes =
                    [System.Security.Cryptography.ProtectedData]::Unprotect(
                        $protectedBytes,
                        $null,
                        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                $strictUtf8 =
                    New-Object Text.UTF8Encoding($false, $true)
                $password = $strictUtf8.GetString($plainBytes)
                if ([string]::IsNullOrEmpty($password)) {
                    throw 'Stored credential plaintext is empty.'
                }

                $validatedCredentials.Add([pscustomobject]@{
                    OfficeCode = [string]$credential.OfficeCode
                    TenantCode = [string]$credential.TenantCode
                    Username = [string]$credential.Username
                    Password = $password
                    SavedAtUtc =
                        $savedAtUtc.ToUniversalTime().ToString(
                            'O',
                            [Globalization.CultureInfo]::InvariantCulture)
                })
            }
            finally {
                if ($null -ne $plainBytes) {
                    [Array]::Clear(
                        $plainBytes,
                        0,
                        $plainBytes.Length)
                }
                if ($null -ne $protectedBytes) {
                    [Array]::Clear(
                        $protectedBytes,
                        0,
                        $protectedBytes.Length)
                }
            }
        }
        $credentials = @($validatedCredentials | ForEach-Object { $_ })
    }
    catch {
        Write-Utf8File -Path $LogPath -Content (@(
            "stored_credentials_exit_code=$($result.ExitCode)",
            "stored_credentials_error=$failureReason",
            'stored_credentials_child_output_redacted=True'
        ) -join [Environment]::NewLine)
        throw (
            '저장된 동기화 로그인 결과 형식 검증 실패. 자식 프로세스 ' +
            "출력은 재출력하지 않았습니다. 상태 로그: $LogPath")
    }

    $sanitized = @(
        $credentials | ForEach-Object {
            [pscustomobject]@{
                OfficeCode = [string]$_.OfficeCode
                TenantCode = [string]$_.TenantCode
                Username = [string]$_.Username
                SavedAtUtc = [string]$_.SavedAtUtc
            }
        }
    )
    Write-Utf8File -Path $LogPath -Content (
        [pscustomobject]@{
            schemaVersion = 1
            protection = 'DPAPI-CurrentUser'
            credentialCount = $sanitized.Count
            invocationMode = [string]$Result.InvocationMode
            credentials = $sanitized
        } | ConvertTo-Json -Depth 10)
    return $credentials
}

function Get-StoredSyncCredentialsFromLocalState {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $result = Invoke-WithProcessEnvironment -Variables @{
        GEORAEPLAN_APP_ROOT = $AppRoot
        GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
        GEORAEPLAN_TEST_MODE = '1'
        GEORAEPLAN_TEST_SEED_MODE = '1'
        GEORAEPLAN_TEST_SEED_ROOT = $AppRoot
    } -Action {
        Invoke-StoredCredentialEnvelopeProcess `
            -DotnetExe $DotnetExe `
            -SyncDiagProject $SyncDiagProject
    }

    return @(
        ConvertFrom-StoredCredentialEnvelopeProcessResult `
            -Result $result `
            -LogPath $LogPath
    )
}

function Get-SourceUsersFromApi {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [object[]]$StoredCredentials = @(),
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $trimmedBaseUrl = $BaseUrl.TrimEnd('/')
    $attempts = @()

    foreach ($credential in $StoredCredentials) {
        $username = [string]$credential.Username
        $password = [string]$credential.Password
        if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrEmpty($password)) {
            continue
        }

        try {
            $login = Invoke-RestMethod `
                -Method Post `
                -Uri ($trimmedBaseUrl + '/auth/login') `
                -ContentType 'application/json' `
                -Body (@{ username = $username; password = $password } | ConvertTo-Json) `
                -TimeoutSec 20

            $token = if ($login.token) { [string]$login.token } elseif ($login.accessToken) { [string]$login.accessToken } else { '' }
            if ([string]::IsNullOrWhiteSpace($token)) {
                throw "token missing for $username"
            }

            $loginRole = [string]$login.user.role
            $loginScopeType = [string]$login.user.scopeType
            if (
                -not [string]::Equals(
                    $loginRole,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    $loginScopeType,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw (
                    "system configuration scope required for complete user export: " +
                    "username=$username role=$loginRole scopeType=$loginScopeType")
            }

            $users = @(
                Invoke-RestMethod `
                    -Method Get `
                    -Uri ($trimmedBaseUrl + '/users') `
                    -Headers @{ Authorization = "Bearer $token" } `
                    -TimeoutSec 20 |
                    ForEach-Object { $_ }
            )

            $payload = [pscustomobject]@{
                sourceMode = 'authenticated-source-api'
                isComplete = $true
                userCount = $users.Count
                permissionCount = @(
                    $users |
                        ForEach-Object { @($_.permissions).Count } |
                        Measure-Object -Sum
                )[0].Sum
                scopeCounts =
                    Get-SourceUsersSnapshotScopeCounts -Users $users
            }

            Write-Utf8File -Path $LogPath -Content ($payload | ConvertTo-Json -Depth 30)
            return [pscustomobject]@{
                LoginUsername = $username
                LoginRole = $loginRole
                LoginScopeType = $loginScopeType
                IsComplete = $true
                Users = $users
            }
        }
        catch {
            $attempts += [pscustomobject]@{
                errorType = $_.Exception.GetType().Name
                result = 'rejected'
            }
        }
    }

    $failurePayload = [pscustomobject]@{
        sourceMode = 'authenticated-source-api'
        isComplete = $false
        attemptCount = $attempts.Count
        attempts = $attempts
    }
    Write-Utf8File -Path $LogPath -Content ($failurePayload | ConvertTo-Json -Depth 20)
    return $null
}

function Get-SourceUsersSnapshotKnownPermissions {
    return @(
        'Amount.ViewPurchase',
        'Amount.ViewSales',
        'CompanyProfile.Edit',
        'Customer.Edit',
        'Data.BackupRestore',
        'Delivery.Edit',
        'Delivery.ViewAll',
        'Inventory.Reset',
        'Invoice.Edit',
        'Item.Edit',
        'Payment.Edit',
        'Rental.AssetEdit',
        'Rental.EditAll',
        'Rental.Import',
        'Rental.ProfileEdit',
        'Rental.SettingsEdit',
        'Rental.ViewAll',
        'Settings.Edit'
    )
}

function Get-SourceUsersSnapshotTextSha256 {
    param([Parameter(Mandatory = $true)][string]$Text)

    $encoding = New-Object Text.UTF8Encoding($false)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($encoding.GetBytes($Text))).
                Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-Utf8TextSha256 {
    param([Parameter(Mandatory = $true)][string]$Text)

    $encoding = New-Object Text.UTF8Encoding($false)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($encoding.GetBytes($Text))).
                Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-SourceUsersSnapshotOrdinalSortKey {
    param([Parameter(Mandatory = $true)][string]$Text)

    $encoding = New-Object Text.UTF8Encoding($false)
    return [BitConverter]::ToString($encoding.GetBytes($Text)).
        Replace('-', '')
}

function ConvertTo-SourceUsersSnapshotCanonicalJsonString {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    foreach ($character in $Text.ToCharArray()) {
        $code = [int]$character
        switch ($code) {
            8 { [void]$builder.Append('\b'); continue }
            9 { [void]$builder.Append('\t'); continue }
            10 { [void]$builder.Append('\n'); continue }
            12 { [void]$builder.Append('\f'); continue }
            13 { [void]$builder.Append('\r'); continue }
            34 { [void]$builder.Append('\"'); continue }
            92 { [void]$builder.Append('\\'); continue }
        }

        if ($code -lt 0x20 -or $code -gt 0x7E) {
            [void]$builder.Append(
                ('\u{0:X4}' -f $code))
        }
        else {
            [void]$builder.Append($character)
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-SourceUsersSnapshotCanonicalJson {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Users
    )

    $sortedUsers =
        New-Object 'Collections.Generic.SortedDictionary[string,object]' (
            [StringComparer]::Ordinal)
    foreach ($user in $Users) {
        $sortKey =
            Get-SourceUsersSnapshotOrdinalSortKey `
                -Text ([string]$user.username)
        $sortedUsers.Add($sortKey, $user)
    }

    $canonicalUsers = @()
    foreach ($user in $sortedUsers.Values) {
        $sortedPermissions =
            New-Object 'Collections.Generic.SortedDictionary[string,string]' (
                [StringComparer]::Ordinal)
        foreach ($permissionValue in @($user.permissions)) {
            $permission = [string]$permissionValue
            $permissionKey =
                Get-SourceUsersSnapshotOrdinalSortKey -Text $permission
            $sortedPermissions.Add($permissionKey, $permission)
        }
        $permissionJson = @(
            $sortedPermissions.Values |
                ForEach-Object {
                    ConvertTo-SourceUsersSnapshotCanonicalJsonString `
                        -Text ([string]$_)
                }
        ) -join ','
        $canonicalUsers += (
            '{' +
            '"username":' +
                (ConvertTo-SourceUsersSnapshotCanonicalJsonString `
                    -Text ([string]$user.username)) +
            ',"role":' +
                (ConvertTo-SourceUsersSnapshotCanonicalJsonString `
                    -Text ([string]$user.role)) +
            ',"tenantCode":' +
                (ConvertTo-SourceUsersSnapshotCanonicalJsonString `
                    -Text ([string]$user.tenantCode)) +
            ',"officeCode":' +
                (ConvertTo-SourceUsersSnapshotCanonicalJsonString `
                    -Text ([string]$user.officeCode)) +
            ',"scopeType":' +
                (ConvertTo-SourceUsersSnapshotCanonicalJsonString `
                    -Text ([string]$user.scopeType)) +
            ',"isActive":' +
                $(if ([bool]$user.isActive) { 'true' } else { 'false' }) +
            ',"permissions":[' +
                $permissionJson +
            ']}')
    }

    return '[' + ($canonicalUsers -join ',') + ']'
}

function Get-SourceUsersSnapshotScopeCounts {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Users
    )

    $groups = @{}
    foreach ($user in $Users) {
        $key = @(
            [string]$user.tenantCode,
            [string]$user.officeCode,
            [string]$user.role,
            [string]$user.scopeType,
            [string][bool]$user.isActive
        ) -join [char]0x1F
        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = [pscustomobject][ordered]@{
                tenantCode = [string]$user.tenantCode
                officeCode = [string]$user.officeCode
                role = [string]$user.role
                scopeType = [string]$user.scopeType
                isActive = [bool]$user.isActive
                userCount = 0
                permissionCount = 0
            }
        }

        $groups[$key].userCount++
        $groups[$key].permissionCount += @($user.permissions).Count
    }

    return @(
        $groups.Values |
            Sort-Object `
                -Property tenantCode, officeCode, role, scopeType, isActive
    )
}

function Assert-SourceUsersSnapshotAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    $allowedSids =
        New-Object 'Collections.Generic.HashSet[string]' (
            [StringComparer]::Ordinal)
    $currentSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    [void]$allowedSids.Add($currentSid)
    [void]$allowedSids.Add('S-1-5-18')
    [void]$allowedSids.Add('S-1-5-32-544')

    $rootAcl = Get-Acl -LiteralPath $AllowedRoot
    $rootOwnerSid = (
        New-Object Security.Principal.NTAccount($rootAcl.Owner)
    ).Translate([Security.Principal.SecurityIdentifier]).Value
    if (
        -not $rootAcl.AreAccessRulesProtected -or
        -not $allowedSids.Contains($rootOwnerSid)
    ) {
        throw 'Source users snapshot allowed root ACL is not protected.'
    }

    foreach ($acl in @($rootAcl, (Get-Acl -LiteralPath $Path))) {
        $ownerSid = (
            New-Object Security.Principal.NTAccount($acl.Owner)
        ).Translate([Security.Principal.SecurityIdentifier]).Value
        if (-not $allowedSids.Contains($ownerSid)) {
            throw 'Source users snapshot owner is not trusted.'
        }
        foreach ($rule in $acl.GetAccessRules(
                $true,
                $true,
                [Security.Principal.SecurityIdentifier])) {
            if (
                $rule.AccessControlType -eq
                    [Security.AccessControl.AccessControlType]::Allow -and
                -not $allowedSids.Contains($rule.IdentityReference.Value)
            ) {
                throw (
                    'Source users snapshot ACL grants access to an ' +
                    'unsupported identity.')
            }
        }
    }
}

function Import-SourceUsersSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[A-Fa-f0-9]{64}$')]
        [string]$ExpectedSha256,
        [switch]$RequireProtectedAcl,
        [ValidateRange(1, 168)][int]$MaximumAgeHours = 24
    )

    if (
        [string]::IsNullOrWhiteSpace($Path) -or
        $Path -notmatch '^[A-Za-z]:[\\/]' -or
        $Path.StartsWith('\\', [StringComparison]::Ordinal)
    ) {
        throw 'SourceUsersSnapshotPath must be a local drive absolute path.'
    }
    if (
        [string]::IsNullOrWhiteSpace($AllowedRoot) -or
        $AllowedRoot -notmatch '^[A-Za-z]:[\\/]' -or
        $AllowedRoot.StartsWith('\\', [StringComparison]::Ordinal)
    ) {
        throw 'Source users snapshot allowed root must be a local drive absolute path.'
    }

    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    $allowedFullPath = ConvertTo-NormalizedFullPath -Path $AllowedRoot
    if (-not (Test-Path -LiteralPath $allowedFullPath -PathType Container)) {
        throw 'Source users snapshot allowed root does not exist.'
    }
    if (-not [string]::Equals(
            [IO.Path]::GetExtension($fullPath),
            '.json',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source users snapshot must use the .json extension.'
    }

    $volumeRoot = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Substring($volumeRoot.Length).Contains(':')) {
        throw 'Source users snapshot alternate data streams are not allowed.'
    }
    $allowedPrefix =
        $allowedFullPath.TrimEnd([char[]]@('\', '/')) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $allowedPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source users snapshot must be inside the dedicated allowed root.'
    }

    $allowedRootItem = Get-Item -LiteralPath $allowedFullPath -Force
    if (($allowedRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Source users snapshot allowed root cannot be a reparse point.'
    }
    $physicalAllowedRoot =
        Resolve-PhysicalPathIdentity -Path $allowedFullPath
    if (-not [string]::Equals(
            $allowedFullPath,
            $physicalAllowedRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source users snapshot allowed root cannot traverse a reparse point.'
    }
    if ($RequireProtectedAcl) {
        Assert-SourceUsersSnapshotAcl `
            -Path $fullPath `
            -AllowedRoot $allowedFullPath
    }

    Initialize-TestEnvironmentFinalPathNativeMethods
    try {
        $stream = [IO.File]::Open(
            $fullPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::None)
    }
    catch {
        throw 'SourceUsersSnapshotPath must identify an exclusively readable file.'
    }

    try {
        $nativeType =
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
        $fileInformation =
            $nativeType::GetFileInformation($stream.SafeFileHandle)
        $attributes = [IO.FileAttributes]$fileInformation.FileAttributes
        if (
            ($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or
            ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw 'Source users snapshot must be a regular non-reparse file.'
        }
        if ($fileInformation.NumberOfLinks -ne 1) {
            throw 'Source users snapshot must be a single-link regular file.'
        }

        $finalPath = ConvertTo-NormalizedFullPath `
            -Path ($nativeType::GetFinalPath($stream.SafeFileHandle))
        if (-not [string]::Equals(
                $fullPath,
                $finalPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Source users snapshot path cannot traverse a reparse point.'
        }
        if (-not $finalPath.StartsWith(
                $allowedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Source users snapshot physical path escaped the allowed root.'
        }

        if ($stream.Length -le 0 -or $stream.Length -gt 1MB) {
            throw 'Source users snapshot size must be between 1 byte and 1 MiB.'
        }
        $bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read(
                $bytes,
                $offset,
                $bytes.Length - $offset)
            if ($read -le 0) {
                throw 'Source users snapshot changed while it was being read.'
            }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) {
            throw 'Source users snapshot changed while it was being read.'
        }
    }
    finally {
        $stream.Dispose()
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $snapshotSha256 =
            [BitConverter]::ToString($sha256.ComputeHash($bytes)).
                Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
    if (-not [string]::Equals(
            $snapshotSha256,
            $ExpectedSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source users snapshot SHA-256 does not match the expected value.'
    }

    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    try {
        $jsonText = $utf8.GetString($bytes)
        if ($jsonText.Length -gt 0 -and $jsonText[0] -eq [char]0xFEFF) {
            $jsonText = $jsonText.Substring(1)
        }
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            AssertNoDuplicateJsonObjectProperties($jsonText)
        $snapshot = $jsonText | ConvertFrom-Json
    }
    catch {
        throw 'Source users snapshot must be valid strict UTF-8 JSON with unique fields.'
    }
    if ($null -eq $snapshot) {
        throw 'Source users snapshot JSON cannot be null.'
    }

    $requiredRootFields = @(
        'schemaVersion',
        'sourceKind',
        'generatedAtUtc',
        'isComplete',
        'userCount',
        'permissionCount',
        'scopeCounts',
        'canonicalSha256',
        'users'
    )
    $rootFields = @($snapshot.PSObject.Properties.Name)
    if (
        @($requiredRootFields | Where-Object { $rootFields -cnotcontains $_ }).Count -gt 0 -or
        @($rootFields | Where-Object { $requiredRootFields -cnotcontains $_ }).Count -gt 0
    ) {
        throw 'Source users snapshot contains missing or unsupported root fields.'
    }
    if (
        ($snapshot.schemaVersion -isnot [int] -and
         $snapshot.schemaVersion -isnot [long]) -or
        [long]$snapshot.schemaVersion -ne 1
    ) {
        throw 'Source users snapshot schemaVersion must be integer 1.'
    }
    if (
        -not [string]::Equals(
            [string]$snapshot.sourceKind,
            'georaeplan-user-permission-snapshot-v1',
            [StringComparison]::Ordinal)
    ) {
        throw 'Source users snapshot sourceKind is not supported.'
    }
    if ($snapshot.isComplete -isnot [bool] -or -not [bool]$snapshot.isComplete) {
        throw 'Source users snapshot must declare isComplete=true.'
    }
    if (
        ($snapshot.userCount -isnot [int] -and
         $snapshot.userCount -isnot [long]) -or
        ($snapshot.permissionCount -isnot [int] -and
         $snapshot.permissionCount -isnot [long]) -or
        [long]$snapshot.userCount -lt 0 -or
        [long]$snapshot.permissionCount -lt 0
    ) {
        throw 'Source users snapshot counts must be non-negative integers.'
    }
    if (
        $snapshot.users -isnot [Array] -or
        $snapshot.scopeCounts -isnot [Array]
    ) {
        throw 'Source users snapshot users and scopeCounts must be arrays.'
    }
    if (
        [string]$snapshot.canonicalSha256 -cnotmatch
            '^[A-Fa-f0-9]{64}$'
    ) {
        throw 'Source users snapshot canonicalSha256 is invalid.'
    }

    $generatedAtText = [string]$snapshot.generatedAtUtc
    $generatedAtUtc = [DateTimeOffset]::MinValue
    if (
        $snapshot.generatedAtUtc -isnot [string] -or
        -not $generatedAtText.EndsWith(
            'Z',
            [StringComparison]::Ordinal) -or
        -not [DateTimeOffset]::TryParse(
            $generatedAtText,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$generatedAtUtc) -or
        $generatedAtUtc.Offset -ne [TimeSpan]::Zero
    ) {
        throw 'Source users snapshot generatedAtUtc must be an explicit UTC timestamp.'
    }
    $nowUtc = [DateTimeOffset]::UtcNow
    if ($generatedAtUtc -gt $nowUtc.AddMinutes(5)) {
        throw 'Source users snapshot generatedAtUtc is in the future.'
    }
    if ($generatedAtUtc -lt $nowUtc.AddHours(-$MaximumAgeHours)) {
        throw 'Source users snapshot is stale.'
    }

    $rawUsers = @($snapshot.users | ForEach-Object { $_ })
    if ($rawUsers.Count -eq 0 -or [long]$snapshot.userCount -ne $rawUsers.Count) {
        throw 'Source users snapshot userCount does not match a non-empty users array.'
    }

    $requiredUserFields = @(
        'username',
        'role',
        'tenantCode',
        'officeCode',
        'scopeType',
        'isActive',
        'permissions'
    )
    $usernames =
        New-Object 'Collections.Generic.HashSet[string]' (
            [StringComparer]::OrdinalIgnoreCase)
    $knownPermissions =
        New-Object 'Collections.Generic.HashSet[string]' (
            [StringComparer]::Ordinal)
    foreach ($knownPermission in Get-SourceUsersSnapshotKnownPermissions) {
        [void]$knownPermissions.Add($knownPermission)
    }

    $normalizedUsers = @()
    $permissionCount = 0
    foreach ($user in $rawUsers) {
        if ($null -eq $user) {
            throw 'Source users snapshot contains a null user.'
        }
        $userFields = @($user.PSObject.Properties.Name)
        if (
            @($requiredUserFields | Where-Object { $userFields -cnotcontains $_ }).Count -gt 0 -or
            @($userFields | Where-Object { $requiredUserFields -cnotcontains $_ }).Count -gt 0
        ) {
            throw 'Source users snapshot contains missing or unsupported user fields.'
        }

        $username = [string]$user.username
        if (
            $user.username -isnot [string] -or
            [string]::IsNullOrWhiteSpace($username) -or
            $username.Length -gt 128 -or
            -not [string]::Equals(
                $username,
                $username.Trim(),
                [StringComparison]::Ordinal) -or
            $username.IndexOfAny([char[]]"`0`r`n`t") -ge 0
        ) {
            throw 'Source users snapshot contains an invalid username.'
        }
        if (-not $usernames.Add($username)) {
            throw 'Source users snapshot contains duplicate usernames.'
        }

        $role = [string]$user.role
        $tenantCode = [string]$user.tenantCode
        $officeCode = [string]$user.officeCode
        $scopeType = [string]$user.scopeType
        if ($role -cnotin @('Admin', 'User')) {
            throw 'Source users snapshot contains an unsupported role.'
        }
        if ($tenantCode -cnotin @('USENET_GROUP', 'ITWORLD')) {
            throw 'Source users snapshot contains an unsupported tenantCode.'
        }
        if ($officeCode -cnotin @('USENET', 'ITWORLD', 'YEONSU')) {
            throw 'Source users snapshot contains an unsupported officeCode.'
        }
        if ($scopeType -cnotin @('Admin', 'TenantAll', 'OfficeOnly')) {
            throw 'Source users snapshot contains an unsupported scopeType.'
        }
        if (
            ($tenantCode -ceq 'ITWORLD' -and $officeCode -cne 'ITWORLD') -or
            ($tenantCode -ceq 'USENET_GROUP' -and
             $officeCode -cnotin @('USENET', 'YEONSU'))
        ) {
            throw 'Source users snapshot contains an incompatible tenant/office pair.'
        }
        if ($scopeType -ceq 'Admin' -and $role -cne 'Admin') {
            throw 'Source users snapshot Admin scope requires the Admin role.'
        }
        if ($user.isActive -isnot [bool]) {
            throw 'Source users snapshot isActive values must be boolean.'
        }
        if ($null -eq $user.permissions -or $user.permissions -isnot [Array]) {
            throw 'Source users snapshot permissions must be arrays.'
        }

        $permissions = @($user.permissions | ForEach-Object { [string]$_ })
        $permissionSet =
            New-Object 'Collections.Generic.HashSet[string]' (
                [StringComparer]::OrdinalIgnoreCase)
        foreach ($permission in $permissions) {
            if (
                -not $knownPermissions.Contains($permission) -or
                -not $permissionSet.Add($permission)
            ) {
                throw 'Source users snapshot contains an unsupported or duplicate permission.'
            }
        }
        $permissionCount += $permissions.Count

        $normalizedUsers += [pscustomobject][ordered]@{
            username = $username
            role = $role
            tenantCode = $tenantCode
            officeCode = $officeCode
            scopeType = $scopeType
            isActive = [bool]$user.isActive
            permissions = @($permissions | Sort-Object)
        }
    }

    if ([long]$snapshot.permissionCount -ne $permissionCount) {
        throw 'Source users snapshot permissionCount does not match users.'
    }
    $activeSystemAdminCount = @(
        $normalizedUsers |
            Where-Object {
                [bool]$_.isActive -and
                [string]$_.role -ceq 'Admin' -and
                [string]$_.scopeType -ceq 'Admin'
            }
    ).Count
    if ($activeSystemAdminCount -eq 0) {
        throw 'Source users snapshot has no active Admin/Admin user.'
    }

    $requiredScopeFields = @(
        'tenantCode',
        'officeCode',
        'role',
        'scopeType',
        'isActive',
        'userCount',
        'permissionCount'
    )
    $normalizedScopeCounts = @()
    $scopeKeys =
        New-Object 'Collections.Generic.HashSet[string]' (
            [StringComparer]::Ordinal)
    foreach ($scopeCount in @($snapshot.scopeCounts)) {
        if ($null -eq $scopeCount) {
            throw 'Source users snapshot contains a null scope count.'
        }
        $scopeFields = @($scopeCount.PSObject.Properties.Name)
        if (
            @($requiredScopeFields | Where-Object { $scopeFields -cnotcontains $_ }).Count -gt 0 -or
            @($scopeFields | Where-Object { $requiredScopeFields -cnotcontains $_ }).Count -gt 0
        ) {
            throw 'Source users snapshot contains missing or unsupported scope count fields.'
        }
        if (
            [string]$scopeCount.tenantCode -cnotin @('USENET_GROUP', 'ITWORLD') -or
            [string]$scopeCount.officeCode -cnotin @('USENET', 'ITWORLD', 'YEONSU') -or
            [string]$scopeCount.role -cnotin @('Admin', 'User') -or
            [string]$scopeCount.scopeType -cnotin @('Admin', 'TenantAll', 'OfficeOnly') -or
            $scopeCount.isActive -isnot [bool] -or
            ($scopeCount.userCount -isnot [int] -and
             $scopeCount.userCount -isnot [long]) -or
            ($scopeCount.permissionCount -isnot [int] -and
             $scopeCount.permissionCount -isnot [long]) -or
            [long]$scopeCount.userCount -le 0 -or
            [long]$scopeCount.permissionCount -lt 0
        ) {
            throw 'Source users snapshot contains an invalid scope count.'
        }
        $scopeKey = @(
            [string]$scopeCount.tenantCode,
            [string]$scopeCount.officeCode,
            [string]$scopeCount.role,
            [string]$scopeCount.scopeType,
            [string][bool]$scopeCount.isActive
        ) -join [char]0x1F
        if (-not $scopeKeys.Add($scopeKey)) {
            throw 'Source users snapshot contains a duplicate scope count.'
        }
        $normalizedScopeCounts += [pscustomobject][ordered]@{
            tenantCode = [string]$scopeCount.tenantCode
            officeCode = [string]$scopeCount.officeCode
            role = [string]$scopeCount.role
            scopeType = [string]$scopeCount.scopeType
            isActive = [bool]$scopeCount.isActive
            userCount = [long]$scopeCount.userCount
            permissionCount = [long]$scopeCount.permissionCount
        }
    }

    $expectedScopeCounts = @(
        Get-SourceUsersSnapshotScopeCounts -Users $normalizedUsers
    )
    $expectedScopeJson = ConvertTo-Json `
        -InputObject @($expectedScopeCounts) `
        -Depth 10 `
        -Compress
    $actualScopeJson = ConvertTo-Json `
        -InputObject @(
            $normalizedScopeCounts |
                Sort-Object `
                    -Property tenantCode, officeCode, role, scopeType, isActive
        ) `
        -Depth 10 `
        -Compress
    if (-not [string]::Equals(
            $expectedScopeJson,
            $actualScopeJson,
            [StringComparison]::Ordinal)) {
        throw 'Source users snapshot scopeCounts do not match users.'
    }

    $canonicalJson =
        Get-SourceUsersSnapshotCanonicalJson -Users $normalizedUsers
    $canonicalSha256 =
        Get-SourceUsersSnapshotTextSha256 -Text $canonicalJson
    if (-not [string]::Equals(
            $canonicalSha256,
            [string]$snapshot.canonicalSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source users snapshot canonicalSha256 does not match users.'
    }

    return [pscustomobject]@{
        SchemaVersion = 1
        SourceKind = 'georaeplan-user-permission-snapshot-v1'
        GeneratedAtUtc = $generatedAtUtc.UtcDateTime.ToString('O')
        IsComplete = $true
        UserCount = $normalizedUsers.Count
        PermissionCount = $permissionCount
        ScopeCounts = $expectedScopeCounts
        CanonicalSha256 = $canonicalSha256
        SnapshotSha256 = $snapshotSha256
        Users = $normalizedUsers
    }
}

function Resolve-SourceUsersSnapshot {
    param(
        [AllowNull()][object]$FileSnapshot,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [object[]]$StoredCredentials = @(),
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    if ($null -ne $FileSnapshot) {
        $logPayload = [pscustomobject]@{
            sourceMode = 'validated-file-snapshot'
            sourceKind = [string]$FileSnapshot.SourceKind
            generatedAtUtc = [string]$FileSnapshot.GeneratedAtUtc
            isComplete = [bool]$FileSnapshot.IsComplete
            userCount = [int]$FileSnapshot.UserCount
            permissionCount = [int]$FileSnapshot.PermissionCount
            scopeCounts = @($FileSnapshot.ScopeCounts)
            canonicalSha256 = [string]$FileSnapshot.CanonicalSha256
            snapshotSha256 = [string]$FileSnapshot.SnapshotSha256
        }
        Write-Utf8File `
            -Path $LogPath `
            -Content ($logPayload | ConvertTo-Json -Depth 30)
        return $FileSnapshot
    }

    return Get-SourceUsersFromApi `
        -BaseUrl $BaseUrl `
        -StoredCredentials $StoredCredentials `
        -LogPath $LogPath
}

function Get-FallbackOperationalUsers {
    $adminPermissions = @(
        'Amount.ViewPurchase',
        'Amount.ViewSales',
        'CompanyProfile.Edit',
        'Data.BackupRestore',
        'Delivery.ViewAll',
        'Rental.EditAll',
        'Rental.Import',
        'Rental.SettingsEdit',
        'Rental.ViewAll',
        'Settings.Edit'
    )

    return @(
        [pscustomobject]@{
            username = 'admin'
            role = 'Admin'
            officeCode = 'USENET'
            tenantCode = 'USENET_GROUP'
            scopeType = 'Admin'
            isActive = $true
            permissions = $adminPermissions
        },
        [pscustomobject]@{
            username = 'usenet'
            role = 'Admin'
            officeCode = 'USENET'
            tenantCode = 'USENET_GROUP'
            scopeType = 'TenantAll'
            isActive = $true
            permissions = $adminPermissions
        },
        [pscustomobject]@{
            username = 'itworld'
            role = 'Admin'
            officeCode = 'ITWORLD'
            tenantCode = 'ITWORLD'
            scopeType = 'TenantAll'
            isActive = $true
            permissions = $adminPermissions
        },
        [pscustomobject]@{
            username = 'yeonsu'
            role = 'User'
            officeCode = 'YEONSU'
            tenantCode = 'USENET_GROUP'
            scopeType = 'OfficeOnly'
            isActive = $true
            permissions = @()
        }
    )
}

function Resolve-IsolatedSourceUsers {
    param(
        [AllowNull()][object]$SourceUsersSnapshot,
        [object[]]$StoredCredentials = @(),
        [switch]$AllowFallback
    )

    $sourceUsers = @(
        if ($null -ne $SourceUsersSnapshot) {
            $SourceUsersSnapshot.Users |
                ForEach-Object { $_ }
        }
    )

    if ($sourceUsers.Count -gt 0) {
        if (-not [bool]$SourceUsersSnapshot.IsComplete) {
            throw (
                '원본 서버 사용자 응답이 전체 목록임을 확인할 수 없어 ' +
                '사용자/권한 복원을 중단합니다.')
        }

        return $sourceUsers
    }

    $usableStoredCredentials = @(
        $StoredCredentials |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_.Username) -and
                -not [string]::IsNullOrEmpty([string]$_.Password)
            }
    )
    if ($usableStoredCredentials.Count -gt 0) {
        throw (
            '저장된 원본 계정이 있지만 원본 서버 사용자/권한 목록을 읽지 못했습니다. ' +
            '기본 계정으로 대체하면 권한이 누락될 수 있어 복원을 중단합니다.')
    }

    if ($AllowFallback) {
        return @(Get-FallbackOperationalUsers)
    }

    throw (
        '원본 서버 사용자/권한 목록을 읽지 못했습니다. ' +
        '신규 빈 테스트 환경에서만 -AllowFallbackOperationalUsers를 명시할 수 있습니다.')
}

function Resolve-IsolatedUserDefinitions {
    param(
        [object[]]$SourceUsers = @(),
        [object[]]$StoredCredentials = @(),
        [switch]$ResetUnresolvedPasswords,
        [switch]$ResetAllPasswords
    )

    if ($ResetAllPasswords -and $ResetUnresolvedPasswords) {
        throw (
            'ResetAllPasswords cannot be combined with ' +
            'ResetUnresolvedPasswords.')
    }
    if ($ResetAllPasswords -and @($StoredCredentials).Count -ne 0) {
        throw 'ResetAllPasswords requires an empty StoredCredentials set.'
    }

    $passwordMap = @{}
    foreach ($credential in $StoredCredentials) {
        $username = [string]$credential.Username
        $password = [string]$credential.Password
        if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrEmpty($password)) {
            continue
        }

        if (
            $passwordMap.ContainsKey($username) -and
            -not [string]::Equals(
                [string]$passwordMap[$username],
                $password,
                [StringComparison]::Ordinal)
        ) {
            throw "동일 원본 사용자에 서로 다른 저장 비밀번호가 있어 복원을 중단합니다. username=$username"
        }

        $passwordMap[$username] = $password
    }

    $resolvedUsers = @()
    $sourceUsernames =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($sourceUser in $SourceUsers) {
        $username = [string]$sourceUser.username
        if ([string]::IsNullOrWhiteSpace($username)) {
            throw '원본 사용자 목록에 빈 username 행이 있어 복원을 중단합니다.'
        }
        if (-not $sourceUsernames.Add($username)) {
            throw "원본 사용자 목록에 중복 username이 있어 복원을 중단합니다. username=$username"
        }

        $sourcePropertyNames = @($sourceUser.PSObject.Properties.Name)
        foreach ($requiredProperty in @(
            'role',
            'officeCode',
            'tenantCode',
            'scopeType',
            'isActive',
            'permissions'
        )) {
            if ($sourcePropertyNames -notcontains $requiredProperty) {
                throw (
                    '원본 사용자 필수 필드가 없어 복원을 중단합니다. ' +
                    "username=$username field=$requiredProperty")
            }
        }

        foreach ($requiredTextField in @(
            @{ Name = 'role'; Value = [string]$sourceUser.role },
            @{ Name = 'officeCode'; Value = [string]$sourceUser.officeCode },
            @{ Name = 'tenantCode'; Value = [string]$sourceUser.tenantCode },
            @{ Name = 'scopeType'; Value = [string]$sourceUser.scopeType }
        )) {
            if ([string]::IsNullOrWhiteSpace($requiredTextField.Value)) {
                throw (
                    '원본 사용자 필수 값이 비어 있어 복원을 중단합니다. ' +
                    "username=$username field=$($requiredTextField.Name)")
            }
        }

        if ($sourceUser.isActive -isnot [bool]) {
            throw (
                '원본 사용자 isActive 형식이 boolean이 아니어서 복원을 중단합니다. ' +
                "username=$username")
        }

        $permissions = @()
        if ($null -ne $sourceUser.permissions) {
            $permissions = @($sourceUser.permissions | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        }

        $passwordWasReset = $false
        $resolvedPassword = if ($ResetAllPasswords) {
            $passwordWasReset = $true
            '1234'
        }
        elseif ($passwordMap.ContainsKey($username)) {
            [string]$passwordMap[$username]
        }
        elseif ($ResetUnresolvedPasswords) {
            $passwordWasReset = $true
            '1234'
        }
        else {
            throw (
                '원본 사용자 비밀번호를 검증할 수 없어 격리 복원을 중단합니다. ' +
                "username=$username " +
                '격리 테스트 복사본에서만 ' +
                '-ResetUnresolvedUserPasswordsForIsolatedTest를 명시할 수 있습니다.')
        }

        $resolvedUsers += [pscustomobject]@{
            Username = $username
            Role = [string]$sourceUser.role
            OfficeCode = [string]$sourceUser.officeCode
            TenantCode = [string]$sourceUser.tenantCode
            ScopeType = [string]$sourceUser.scopeType
            IsActive = [bool]$sourceUser.isActive
            Permissions = $permissions
            Password = $resolvedPassword
            PasswordWasReset = $passwordWasReset
        }
    }

    $activeSystemAdmins = @(
        $resolvedUsers |
            Where-Object {
                [bool]$_.IsActive -and
                [string]::Equals(
                    [string]$_.Role,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals(
                    [string]$_.ScopeType,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    if ($activeSystemAdmins.Count -eq 0) {
        throw (
            '원본 사용자 목록에 활성 Admin 역할·Admin 범위 사용자가 없어 ' +
            '완전한 사용자 복원을 중단합니다.')
    }

    return $resolvedUsers
}

function Assert-IsolatedAllUserPasswordResetResult {
    param(
        [Parameter(Mandatory = $true)][object[]]$SourceUsers,
        [Parameter(Mandatory = $true)][object[]]$ResolvedUsers
    )

    $sourceCount = @($SourceUsers).Count
    $resolvedCount = @($ResolvedUsers).Count
    $resetCount = @(
        $ResolvedUsers |
            Where-Object {
                [bool]$_.PasswordWasReset -and
                [string]$_.Password -ceq '1234'
            }
    ).Count
    if (
        $sourceCount -le 0 -or
        $resolvedCount -ne $sourceCount -or
        $resetCount -ne $sourceCount
    ) {
        throw (
            'The isolated all-user password reset result is incomplete. ' +
            "sourceCount=$sourceCount resolvedCount=$resolvedCount " +
            "resetCount=$resetCount")
    }
}

function Is-AdminUsername {
    param([string]$Username)

    $normalized = if ($null -eq $Username) { '' } else { $Username.Trim() }
    return [string]::Equals($normalized, 'admin', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-IsolatedVerificationAdmin {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Users
    )

    $eligibleAdmins = @(
        $Users |
            Where-Object {
                [bool]$_.IsActive -and
                [string]::Equals(
                    [string]$_.Role,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals(
                    [string]$_.ScopeType,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase) -and
                -not [string]::IsNullOrWhiteSpace([string]$_.Password)
            } |
            Sort-Object `
                @{ Expression = {
                    if (Is-AdminUsername -Username ([string]$_.Username)) {
                        0
                    }
                    else {
                        1
                    }
                } },
                @{ Expression = { [string]$_.Username } }
    )
    if ($eligibleAdmins.Count -eq 0) {
        throw (
            '활성 Admin 역할·Admin 범위 사용자와 검증 비밀번호가 없어 ' +
            '격리 서버 사용자 전체 상태를 검증할 수 없습니다.')
    }

    return $eligibleAdmins[0]
}

function Get-NormalizedPermissionSet {
    param([object[]]$Permissions = @())

    return @(
        $Permissions |
            ForEach-Object { [string]$_ } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_.Trim().ToUpperInvariant() } |
            Sort-Object -Unique
    )
}

function Get-IsolatedUserStateDifferences {
    param(
        [Parameter(Mandatory = $true)][object]$Expected,
        [Parameter(Mandatory = $true)][object]$Actual
    )

    $differences = [Collections.Generic.List[string]]::new()
    foreach ($field in @(
        @{ Name = 'Role'; Expected = [string]$Expected.Role; Actual = [string]$Actual.role },
        @{ Name = 'TenantCode'; Expected = [string]$Expected.TenantCode; Actual = [string]$Actual.tenantCode },
        @{ Name = 'OfficeCode'; Expected = [string]$Expected.OfficeCode; Actual = [string]$Actual.officeCode },
        @{ Name = 'ScopeType'; Expected = [string]$Expected.ScopeType; Actual = [string]$Actual.scopeType }
    )) {
        if (-not [string]::Equals(
                $field.Expected.Trim(),
                $field.Actual.Trim(),
                [StringComparison]::OrdinalIgnoreCase)) {
            $differences.Add([string]$field.Name)
        }
    }

    if ([bool]$Expected.IsActive -ne [bool]$Actual.isActive) {
        $differences.Add('IsActive')
    }

    $expectedPermissions = @(
        Get-NormalizedPermissionSet -Permissions @($Expected.Permissions)
    )
    $actualPermissions = @(
        Get-NormalizedPermissionSet -Permissions @($Actual.permissions)
    )
    if (-not [string]::Equals(
            [string]::Join("`n", $expectedPermissions),
            [string]::Join("`n", $actualPermissions),
            [StringComparison]::Ordinal)) {
        $differences.Add('Permissions')
    }

    return @($differences)
}

function Assert-IsolatedLoopbackBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl
    )

    $uri = $null
    if (
        [string]::IsNullOrWhiteSpace($BaseUrl) -or
        -not [Uri]::TryCreate(
            $BaseUrl,
            [UriKind]::Absolute,
            [ref]$uri) -or
        (
            -not [string]::Equals(
                $uri.Scheme,
                [Uri]::UriSchemeHttp,
                [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals(
                $uri.Scheme,
                [Uri]::UriSchemeHttps,
                [StringComparison]::OrdinalIgnoreCase)
        ) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        -not [string]::Equals(
            $uri.AbsolutePath,
            '/',
            [StringComparison]::Ordinal) -or
        -not $uri.IsLoopback
    ) {
        throw (
            'Isolated server user operations require an absolute root-only ' +
            'HTTP(S) loopback base URL.')
    }

    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Assert-IsolatedServerUserState {
    param(
        [Parameter(Mandatory = $true)][string]$TargetBaseUrl,
        [Parameter(Mandatory = $true)][string]$AdminPassword,
        [Parameter(Mandatory = $true)][object[]]$Users,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [object[]]$Actions = @()
    )

    $trimmedTargetBaseUrl =
        Assert-IsolatedLoopbackBaseUrl -BaseUrl $TargetBaseUrl
    $desiredAdmin = @(
        Get-IsolatedVerificationAdmin -Users $Users
    )

    $verificationAdminPassword = if (
        -not [string]::IsNullOrWhiteSpace([string]$desiredAdmin[0].Password)
    ) {
        [string]$desiredAdmin[0].Password
    }
    else {
        $AdminPassword
    }

    $adminLogin = Invoke-RestMethod `
        -Method Post `
        -Uri ($trimmedTargetBaseUrl + '/auth/login') `
        -ContentType 'application/json' `
        -Body (@{
                username = [string]$desiredAdmin[0].Username
                password = $verificationAdminPassword
            } | ConvertTo-Json) `
        -TimeoutSec 20

    $adminToken = if ($adminLogin.token) {
        [string]$adminLogin.token
    }
    elseif ($adminLogin.accessToken) {
        [string]$adminLogin.accessToken
    }
    else {
        ''
    }
    if ([string]::IsNullOrWhiteSpace($adminToken)) {
        throw '격리 테스트 서버 admin 검증 토큰을 가져오지 못했습니다.'
    }

    $headers = @{ Authorization = "Bearer $adminToken" }
    $postSyncUsers = @(
        Invoke-RestMethod `
            -Method Get `
            -Uri ($trimmedTargetBaseUrl + '/users') `
            -Headers $headers `
            -TimeoutSec 20 |
            ForEach-Object { $_ }
    )
    $structuralVerifications = @()
    $postSyncByUsername = @{}
    foreach ($postSyncUser in $postSyncUsers) {
        $postSyncUsername = [string]$postSyncUser.username
        if ([string]::IsNullOrWhiteSpace($postSyncUsername)) {
            $structuralVerifications += [pscustomobject]@{
                username = ''
                ok = $false
                differences = @('EmptyUsername')
            }
            continue
        }

        if ($postSyncByUsername.ContainsKey($postSyncUsername)) {
            $structuralVerifications += [pscustomobject]@{
                username = $postSyncUsername
                ok = $false
                differences = @('DuplicateUsername')
            }
            continue
        }

        $postSyncByUsername[$postSyncUsername] = $postSyncUser
    }

    $desiredByUsername = @{}
    foreach ($user in $Users) {
        $username = [string]$user.Username
        if ([string]::IsNullOrWhiteSpace($username)) {
            $structuralVerifications += [pscustomobject]@{
                username = ''
                ok = $false
                differences = @('EmptyDesiredUsername')
            }
            continue
        }

        if ($desiredByUsername.ContainsKey($username)) {
            $structuralVerifications += [pscustomobject]@{
                username = $username
                ok = $false
                differences = @('DuplicateDesiredUsername')
            }
            continue
        }

        $desiredByUsername[$username] = $user
        if (-not $postSyncByUsername.ContainsKey($username)) {
            $structuralVerifications += [pscustomobject]@{
                username = $username
                ok = $false
                differences = @('MissingUser')
            }
            continue
        }

        $differences = @(
            Get-IsolatedUserStateDifferences `
                -Expected $user `
                -Actual $postSyncByUsername[$username]
        )
        $structuralVerifications += [pscustomobject]@{
            username = $username
            ok = $differences.Count -eq 0
            differences = $differences
        }
    }

    foreach ($postSyncUser in $postSyncUsers) {
        $postSyncUsername = [string]$postSyncUser.username
        if (
            -not [string]::IsNullOrWhiteSpace($postSyncUsername) -and
            -not $desiredByUsername.ContainsKey($postSyncUsername)
        ) {
            $structuralVerifications += [pscustomobject]@{
                username = $postSyncUsername
                ok = $false
                differences = @('UnexpectedUser')
            }
        }
    }

    $loginVerifications = @()
    foreach ($user in $Users | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Password) }) {
        try {
            $login = Invoke-RestMethod `
                -Method Post `
                -Uri ($trimmedTargetBaseUrl + '/auth/login') `
                -ContentType 'application/json' `
                -Body (@{ username = [string]$user.Username; password = [string]$user.Password } | ConvertTo-Json) `
                -TimeoutSec 20

            if ([bool]$user.IsActive) {
                $loginToken = if ($login.token) {
                    [string]$login.token
                }
                elseif ($login.accessToken) {
                    [string]$login.accessToken
                }
                else {
                    ''
                }
                if ([string]::IsNullOrWhiteSpace($loginToken)) {
                    $loginVerifications += [pscustomobject]@{
                        username = [string]$user.Username
                        ok = $false
                        expectedActive = $true
                        differences = @('MissingToken')
                    }
                    continue
                }

                $sessionActual = [pscustomobject]@{
                    role = [string]$login.user.role
                    tenantCode = [string]$login.user.tenantCode
                    officeCode = [string]$login.user.officeCode
                    scopeType = [string]$login.user.scopeType
                    isActive = $true
                    permissions = @($login.user.permissions)
                }
                $sessionDifferences = @(
                    Get-IsolatedUserStateDifferences `
                        -Expected $user `
                        -Actual $sessionActual
                )
                if (-not [string]::Equals(
                        [string]$login.user.username,
                        [string]$user.Username,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    $sessionDifferences += 'Username'
                }
                $loginVerifications += [pscustomobject]@{
                    username = [string]$user.Username
                    ok = $sessionDifferences.Count -eq 0
                    expectedActive = $true
                    differences = $sessionDifferences
                }
            }
            else {
                $loginVerifications += [pscustomobject]@{
                    username = [string]$user.Username
                    ok = $false
                    expectedActive = $false
                    differences = @('InactiveLoginSucceeded')
                }
            }
        }
        catch {
            $statusCode = -1
            if ($_.Exception.Response) {
                $statusCode = [int]$_.Exception.Response.StatusCode
            }

            $expectedInactiveRejection =
                -not [bool]$user.IsActive -and
                $statusCode -in @(401, 403)
            $loginVerifications += [pscustomobject]@{
                username = [string]$user.Username
                ok = $expectedInactiveRejection
                expectedActive = [bool]$user.IsActive
                status = $statusCode
                differences = if ($expectedInactiveRejection) {
                    @()
                }
                elseif ([bool]$user.IsActive) {
                    @('ActiveLoginFailed')
                }
                else {
                    @('InactiveLoginUnexpectedFailure')
                }
            }
        }
    }

    $logPayload = [pscustomobject]@{
        targetBaseUrl = $trimmedTargetBaseUrl
        actions = @($Actions)
        structuralVerifications = $structuralVerifications
        loginVerifications = $loginVerifications
    }
    Write-Utf8File -Path $LogPath -Content ($logPayload | ConvertTo-Json -Depth 30)

    $structuralFailures = @(
        $structuralVerifications |
            Where-Object { -not [bool]$_.ok }
    )
    $loginFailures = @(
        $loginVerifications |
            Where-Object { -not [bool]$_.ok }
    )
    if ($structuralFailures.Count -gt 0 -or $loginFailures.Count -gt 0) {
        $failedUsers = @(
            $structuralFailures |
                ForEach-Object { [string]$_.username }
            $loginFailures |
                ForEach-Object { [string]$_.username }
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
        throw "격리 테스트 서버 계정/권한 복원 검증 실패: $($failedUsers -join ', ')"
    }
}

function Sync-IsolatedServerUsers {
    param(
        [Parameter(Mandatory = $true)][string]$TargetBaseUrl,
        [Parameter(Mandatory = $true)][string]$AdminPassword,
        [Parameter(Mandatory = $true)][object[]]$Users,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $trimmedTargetBaseUrl =
        Assert-IsolatedLoopbackBaseUrl -BaseUrl $TargetBaseUrl
    $actions = @()

    $adminLogin = Invoke-RestMethod `
        -Method Post `
        -Uri ($trimmedTargetBaseUrl + '/auth/login') `
        -ContentType 'application/json' `
        -Body (@{ username = 'admin'; password = $AdminPassword } | ConvertTo-Json) `
        -TimeoutSec 20

    $adminToken = if ($adminLogin.token) { [string]$adminLogin.token } elseif ($adminLogin.accessToken) { [string]$adminLogin.accessToken } else { '' }
    if ([string]::IsNullOrWhiteSpace($adminToken)) {
        throw '격리 테스트 서버 admin 로그인 토큰을 가져오지 못했습니다.'
    }

    $headers = @{ Authorization = "Bearer $adminToken" }
    $getRequiredRevision = {
        param($Account)

        [long]$revision = 0
        if (
            $null -eq $Account -or
            -not [long]::TryParse(
                [string]$Account.revision,
                [ref]$revision) -or
            $revision -le 0
        ) {
            throw (
                '격리 테스트 서버 사용자 변경에 필요한 ' +
                '현재 revision을 확인하지 못했습니다.')
        }

        return $revision
    }
    $existingUsers = @(
        Invoke-RestMethod -Method Get -Uri ($trimmedTargetBaseUrl + '/users') -Headers $headers -TimeoutSec 20 |
            ForEach-Object { $_ }
    )
    $existingByUsername = @{}
    foreach ($existingUser in $existingUsers) {
        $existingUsername = [string]$existingUser.username
        if ($existingByUsername.ContainsKey($existingUsername)) {
            throw "격리 테스트 서버에 중복 username이 있어 복원을 중단합니다. username=$existingUsername"
        }

        $existingByUsername[$existingUsername] = $existingUser
    }

    $usersToSync = @(
        $Users |
            Sort-Object `
                @{ Expression = { if (Is-AdminUsername -Username ([string]$_.Username)) { 1 } else { 0 } } },
                @{ Expression = { [string]$_.Username } }
    )

    foreach ($user in $usersToSync) {
        $permissions = @($user.Permissions | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        $payload = @{
            username = [string]$user.Username
            role = [string]$user.Role
            tenantCode = [string]$user.TenantCode
            officeCode = [string]$user.OfficeCode
            scopeType = [string]$user.ScopeType
            isActive = [bool]$user.IsActive
            permissions = $permissions
        }

        $existingUser = $null
        if ($existingByUsername.ContainsKey([string]$user.Username)) {
            $existingUser = $existingByUsername[[string]$user.Username]
        }

        if ($null -ne $existingUser) {
            $currentRevision =
                & $getRequiredRevision $existingUser
            if (-not [string]::IsNullOrWhiteSpace([string]$user.Password)) {
                Invoke-RestMethod `
                    -Method Put `
                    -Uri ($trimmedTargetBaseUrl + '/users/' + [string]$existingUser.id + '/password') `
                    -Headers $headers `
                    -ContentType 'application/json' `
                    -Body (@{
                            expectedRevision = $currentRevision
                            password = [string]$user.Password
                        } | ConvertTo-Json) `
                    -TimeoutSec 20 | Out-Null

                if (Is-AdminUsername -Username ([string]$user.Username)) {
                    $refreshedAdminLogin = Invoke-RestMethod `
                        -Method Post `
                        -Uri ($trimmedTargetBaseUrl + '/auth/login') `
                        -ContentType 'application/json' `
                        -Body (@{
                                username = [string]$existingUser.username
                                password = [string]$user.Password
                            } | ConvertTo-Json) `
                        -TimeoutSec 20
                    $refreshedAdminToken = if ($refreshedAdminLogin.token) {
                        [string]$refreshedAdminLogin.token
                    }
                    elseif ($refreshedAdminLogin.accessToken) {
                        [string]$refreshedAdminLogin.accessToken
                    }
                    else {
                        ''
                    }
                    if ([string]::IsNullOrWhiteSpace($refreshedAdminToken)) {
                        throw (
                            '격리 테스트 서버 admin 비밀번호 변경 후 ' +
                            '검증 토큰을 갱신하지 못했습니다.')
                    }
                    $headers = @{
                        Authorization = "Bearer $refreshedAdminToken"
                    }
                }

                # Windows PowerShell 5.1 can emit an Invoke-RestMethod JSON
                # array as one pipeline object. Normalize it before filtering,
                # matching the initial and cleanup user-list reads.
                $refreshedUserCandidates = @(
                    Invoke-RestMethod `
                        -Method Get `
                        -Uri ($trimmedTargetBaseUrl + '/users') `
                        -Headers $headers `
                        -TimeoutSec 20 |
                        ForEach-Object { $_ }
                )
                $refreshedUsers = @(
                    $refreshedUserCandidates |
                        Where-Object {
                            [string]::Equals(
                                [string]$_.id,
                                [string]$existingUser.id,
                                [StringComparison]::OrdinalIgnoreCase)
                        }
                )
                if ($refreshedUsers.Count -ne 1) {
                    $idPresentCount = @(
                        $refreshedUserCandidates |
                            Where-Object {
                                -not [string]::IsNullOrWhiteSpace(
                                    [string]$_.id)
                            }
                    ).Count
                    throw (
                        '비밀번호 변경 후 격리 테스트 서버 사용자 ' +
                        'revision을 다시 불러오지 못했습니다. ' +
                        "returnedCount=$($refreshedUserCandidates.Count) " +
                        "idPresentCount=$idPresentCount " +
                        "matchingCount=$($refreshedUsers.Count)")
                }
                $existingUser = $refreshedUsers[0]
                $currentRevision =
                    & $getRequiredRevision $existingUser
            }

            $payload['expectedRevision'] = $currentRevision
            Invoke-RestMethod `
                -Method Put `
                -Uri ($trimmedTargetBaseUrl + '/users/' + [string]$existingUser.id) `
                -Headers $headers `
                -ContentType 'application/json' `
                -Body ($payload | ConvertTo-Json -Depth 10) `
                -TimeoutSec 20 | Out-Null

            $actions += [pscustomobject]@{
                action = 'update'
                username = [string]$user.Username
                passwordUpdated = -not [string]::IsNullOrWhiteSpace([string]$user.Password)
            }
            continue
        }

        if ([string]::IsNullOrWhiteSpace([string]$user.Password)) {
            throw (
                '격리 테스트 서버에 생성할 사용자의 검증 비밀번호가 없습니다. ' +
                "username=$([string]$user.Username)")
        }

        $createPayload = @{
            username = [string]$user.Username
            password = [string]$user.Password
            role = [string]$user.Role
            tenantCode = [string]$user.TenantCode
            officeCode = [string]$user.OfficeCode
            scopeType = [string]$user.ScopeType
            isActive = [bool]$user.IsActive
            permissions = $permissions
        }

        Invoke-RestMethod `
            -Method Post `
            -Uri ($trimmedTargetBaseUrl + '/users') `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body ($createPayload | ConvertTo-Json -Depth 10) `
            -TimeoutSec 20 | Out-Null

        $actions += [pscustomobject]@{
            action = 'create'
            username = [string]$user.Username
        }
    }

    $verificationAdmin = @(
        Get-IsolatedVerificationAdmin -Users $Users
    )[0]
    $verificationAdminLogin = Invoke-RestMethod `
        -Method Post `
        -Uri ($trimmedTargetBaseUrl + '/auth/login') `
        -ContentType 'application/json' `
        -Body (@{
                username = [string]$verificationAdmin.Username
                password = [string]$verificationAdmin.Password
            } | ConvertTo-Json) `
        -TimeoutSec 20
    $verificationAdminToken = if ($verificationAdminLogin.token) {
        [string]$verificationAdminLogin.token
    }
    elseif ($verificationAdminLogin.accessToken) {
        [string]$verificationAdminLogin.accessToken
    }
    else {
        ''
    }
    if ([string]::IsNullOrWhiteSpace($verificationAdminToken)) {
        throw '복원된 격리 테스트 서버 시스템 관리자 토큰을 가져오지 못했습니다.'
    }

    $cleanupHeaders = @{
        Authorization = "Bearer $verificationAdminToken"
    }
    $usersBeforeCleanup = @(
        Invoke-RestMethod `
            -Method Get `
            -Uri ($trimmedTargetBaseUrl + '/users') `
            -Headers $cleanupHeaders `
            -TimeoutSec 20 |
            ForEach-Object { $_ }
    )
    $desiredUsernames =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($desiredUser in $Users) {
        [void]$desiredUsernames.Add([string]$desiredUser.Username)
    }

    foreach ($existingUser in $usersBeforeCleanup) {
        $existingUsername = [string]$existingUser.username
        if ($desiredUsernames.Contains($existingUsername)) {
            continue
        }

        $deleteRevision =
            & $getRequiredRevision $existingUser
        Invoke-RestMethod `
            -Method Delete `
            -Uri (
                $trimmedTargetBaseUrl +
                '/users/' +
                [string]$existingUser.id +
                '?expectedRevision=' +
                $deleteRevision.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)
            ) `
            -Headers $cleanupHeaders `
            -TimeoutSec 20 |
            Out-Null
        $actions += [pscustomobject]@{
            action = 'delete'
            username = $existingUsername
        }
    }

    Assert-IsolatedServerUserState `
        -TargetBaseUrl $trimmedTargetBaseUrl `
        -AdminPassword $AdminPassword `
        -Users $Users `
        -LogPath $LogPath `
        -Actions $actions
}

function Assert-NoSqliteSidecars {
    param([Parameter(Mandatory = $true)][string]$DatabasePath)

    $sidecars = @(
        "$DatabasePath-wal",
        "$DatabasePath-shm",
        "$DatabasePath-journal"
    )
    $present = @($sidecars | Where-Object { Test-Path -LiteralPath $_ })
    if ($present.Count -gt 0) {
        throw "독립 SQLite 스냅샷 복사를 위해 WAL/SHM/journal이 없어야 합니다: $($present -join ', ')"
    }
}

function Assert-CopiedSnapshotTargetSafeForRemoval {
    param(
        [Parameter(Mandatory = $true)][string]$TargetRoot,
        [Parameter(Mandatory = $true)][string]$TargetDatabase
    )

    $targetRootFullPath =
        ConvertTo-NormalizedFullPath -Path $TargetRoot
    $targetDatabaseFullPath =
        ConvertTo-NormalizedFullPath -Path $TargetDatabase
    $outputRootFullPath =
        ConvertTo-NormalizedFullPath `
            -Path (Split-Path -Parent $targetRootFullPath)
    if (
        -not (
            Test-PathSameOrDescendant `
                -CandidatePath $targetDatabaseFullPath `
                -ParentPath $targetRootFullPath) -or
        -not (
            Test-PathSameOrDescendant `
                -CandidatePath $targetDatabaseFullPath `
                -ParentPath $outputRootFullPath) -or
        -not (Test-Path -LiteralPath $targetDatabaseFullPath -PathType Leaf)
    ) {
        throw 'The copied snapshot target is outside its isolated OutputRoot.'
    }

    Initialize-TestEnvironmentFinalPathNativeMethods
    $targetLease = $null
    try {
        $targetLease = [IO.File]::Open(
            $targetDatabaseFullPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::None)
        $information =
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                GetFileInformation($targetLease.SafeFileHandle)
        $finalPath =
            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                GetFinalPath($targetLease.SafeFileHandle)
        $attributes = [IO.FileAttributes]$information.FileAttributes
        if (
            ($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or
            ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            [uint32]$information.NumberOfLinks -ne 1 -or
            -not [string]::Equals(
                (ConvertTo-NormalizedFullPath -Path $finalPath),
                $targetDatabaseFullPath,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'The copied snapshot target must be an exact regular, ' +
                'non-reparse, single-link file.')
        }
    }
    finally {
        if ($null -ne $targetLease) {
            $targetLease.Dispose()
        }
    }

    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
        DeleteExactSingleLinkRegularFile($targetDatabaseFullPath)
    if (Test-Path -LiteralPath $targetDatabaseFullPath) {
        throw 'The copied snapshot target was not removed.'
    }
}

function Copy-StableStandaloneSqliteSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDatabase,
        [Parameter(Mandatory = $true)][string]$TargetDatabase
    )

    if (-not (Test-Path -LiteralPath $SourceDatabase -PathType Leaf)) {
        throw "원본 SQLite DB를 찾지 못했습니다: $SourceDatabase"
    }

    Assert-NoSqliteSidecars -DatabasePath $SourceDatabase
    $sourceLease = $null
    try {
        $sourceLease = [IO.File]::Open(
            $SourceDatabase,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $sourceLength = $sourceLease.Length
        $sourceLastWriteUtc = [IO.File]::GetLastWriteTimeUtc($SourceDatabase)
        Assert-NoSqliteSidecars -DatabasePath $SourceDatabase

        $targetDirectory = Split-Path -Parent $TargetDatabase
        New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
        $targetStream = $null
        try {
            $targetStream = [IO.File]::Open(
                $TargetDatabase,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            $sourceLease.CopyTo($targetStream)
            $targetStream.Flush($true)
        }
        finally {
            if ($null -ne $targetStream) {
                $targetStream.Dispose()
            }
        }

        Assert-NoSqliteSidecars -DatabasePath $SourceDatabase
        if (
            $sourceLease.Length -ne $sourceLength -or
            [IO.File]::GetLastWriteTimeUtc($SourceDatabase) -ne $sourceLastWriteUtc
        ) {
            throw "복사 중 원본 SQLite DB가 변경되어 스냅샷을 거부했습니다: $SourceDatabase"
        }
        if ((Get-Item -LiteralPath $TargetDatabase).Length -ne $sourceLength) {
            throw "SQLite 스냅샷 길이가 원본과 다릅니다: $TargetDatabase"
        }

        $sourceHash = (Get-FileHash -LiteralPath $SourceDatabase -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $TargetDatabase -Algorithm SHA256).Hash
        if (-not [string]::Equals(
                $sourceHash,
                $targetHash,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "SQLite 스냅샷 SHA-256이 원본과 다릅니다: $TargetDatabase"
        }

        Assert-NoSqliteSidecars -DatabasePath $SourceDatabase
        if (
            $sourceLease.Length -ne $sourceLength -or
            [IO.File]::GetLastWriteTimeUtc($SourceDatabase) -ne $sourceLastWriteUtc
        ) {
            throw "해시 검증 중 원본 SQLite DB가 변경되어 스냅샷을 거부했습니다: $SourceDatabase"
        }

        return $targetHash
    }
    finally {
        if ($null -ne $sourceLease) {
            $sourceLease.Dispose()
        }
    }
}

function Copy-OnlineSqliteSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$TargetRoot,
        [Parameter(Mandatory = $true)][string]$SourceDatabase,
        [Parameter(Mandatory = $true)][string]$TargetDatabase
    )

    try {
        $snapshotResult = Invoke-WithProcessEnvironment -Variables @{
            GEORAEPLAN_TEST_MODE = '1'
            GEORAEPLAN_SOURCE_SNAPSHOT_ROOT =
                [IO.Path]::GetFullPath($SourceRoot)
            GEORAEPLAN_TARGET_SNAPSHOT_ROOT =
                [IO.Path]::GetFullPath($TargetRoot)
        } -Action {
            Invoke-DotnetWithOutput `
                -DotnetExe $DotnetExe `
                -Arguments @(
                    'run',
                    '--project',
                    $SyncDiagProject,
                    '--',
                    'snapshot-sqlite',
                    $SourceDatabase,
                    $TargetDatabase
                )
        }
    }
    catch {
        throw (
            'SQLite snapshot child process did not complete. ' +
            'reason_code=child_process_start_failed ' +
            'snapshot_child_output_redacted=True')
    }
    if (
        $snapshotResult.ExitCode -ne 0 -or
        $snapshotResult.Text -notmatch
            '(?m)^snapshot_succeeded=True\s*$' -or
        $snapshotResult.Text -notmatch
            '(?m)^quick_check=ok\s*$' -or
        $snapshotResult.Text -notmatch
            '(?m)^sidecar_count=0\s*$'
    ) {
        throw (
            'SQLite snapshot child process was rejected. ' +
            'reason_code=child_process_failed ' +
            'snapshot_child_output_redacted=True')
    }

    $hashMatch = [regex]::Match(
        $snapshotResult.Text,
        '(?m)^target_sha256=([A-Fa-f0-9]{64})\s*$')
    if (-not $hashMatch.Success) {
        throw 'SQLite 온라인 스냅샷 결과에 SHA-256이 없습니다.'
    }
    $reportedHash = $hashMatch.Groups[1].Value.ToUpperInvariant()
    $actualHash = (
        Get-FileHash `
            -LiteralPath $TargetDatabase `
            -Algorithm SHA256
    ).Hash
    if (-not [string]::Equals(
            $reportedHash,
            $actualHash,
            [StringComparison]::Ordinal)) {
        throw 'SQLite 온라인 스냅샷 SHA-256 검증에 실패했습니다.'
    }
    Assert-NoSqliteSidecars -DatabasePath $TargetDatabase
    return $actualHash
}

function Get-AppSnapshotFileManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "AppData manifest root does not exist: $Root"
    }

    $rootFullPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $rootFullPath + [IO.Path]::DirectorySeparatorChar
    $primaryDatabaseRelativePaths =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in @(
        'data\거래플랜.db',
        'data\거래플랜.db-shm',
        'data\거래플랜.db-wal',
        'data\거래플랜.db-journal'
    )) {
        [void]$primaryDatabaseRelativePaths.Add($relativePath)
    }
    $volatileTopLevelDirectories =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($directoryName in @('logs', 'temp')) {
        [void]$volatileTopLevelDirectories.Add($directoryName)
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($rootFullPath)
    $manifest = @()
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "AppData snapshot tree contains a reparse point: $($item.FullName)"
            }

            if ($item.PSIsContainer) {
                if (
                    [string]::Equals(
                        $currentDirectory,
                        $rootFullPath,
                        [StringComparison]::OrdinalIgnoreCase) -and
                    $volatileTopLevelDirectories.Contains($item.Name)
                ) {
                    continue
                }
                $pendingDirectories.Push($item.FullName)
                continue
            }

            $fullPath = [IO.Path]::GetFullPath($item.FullName)
            if (-not $fullPath.StartsWith(
                    $rootPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "AppData manifest file escaped its root: $fullPath"
            }
            $relativePath =
                $fullPath.Substring($rootPrefix.Length).Replace('/', '\')
            if ($primaryDatabaseRelativePaths.Contains($relativePath)) {
                continue
            }

            $before = Get-Item -LiteralPath $fullPath -Force
            $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
            $after = Get-Item -LiteralPath $fullPath -Force
            if (
                $before.Length -ne $after.Length -or
                $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc
            ) {
                throw "AppData file changed while hashing: $fullPath"
            }

            $manifest += [pscustomobject]@{
                RelativePath = $relativePath
                Length = [long]$after.Length
                LastWriteUtcTicks = [long]$after.LastWriteTimeUtc.Ticks
                Sha256 = $hash
            }
        }
    }

    return @($manifest | Sort-Object RelativePath)
}

function Assert-AppSnapshotFileManifestsEqual {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Expected,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Actual,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $actualByPath =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Actual)) {
        $relativePath = [string]$entry.RelativePath
        if (
            [string]::IsNullOrWhiteSpace($relativePath) -or
            $actualByPath.ContainsKey($relativePath)
        ) {
            throw "$Context contains a missing or duplicate relative path."
        }
        $actualByPath.Add($relativePath, $entry)
    }

    if (@($Expected).Count -ne $actualByPath.Count) {
        throw (
            "$Context file count mismatch. expected=$(@($Expected).Count) " +
            "actual=$($actualByPath.Count)")
    }

    foreach ($expectedEntry in @($Expected)) {
        $relativePath = [string]$expectedEntry.RelativePath
        if (-not $actualByPath.ContainsKey($relativePath)) {
            throw "$Context is missing file: $relativePath"
        }

        $actualEntry = $actualByPath[$relativePath]
        if (
            [long]$expectedEntry.Length -ne [long]$actualEntry.Length -or
            [long]$expectedEntry.LastWriteUtcTicks -ne
                [long]$actualEntry.LastWriteUtcTicks -or
            -not [string]::Equals(
                [string]$expectedEntry.Sha256,
                [string]$actualEntry.Sha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw "$Context content mismatch: $relativePath"
        }
    }
}

function Assert-StagedRetainedIsolatedAppSnapshotExact {
    param(
        [Parameter(Mandatory = $true)][object]$Expected,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $rootFullPath = ConvertTo-NormalizedFullPath -Path $Root
    $databasePath = Join-Path $rootFullPath 'data\거래플랜.db'
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
        throw "$Context is missing the retained AppData database: $databasePath"
    }
    Assert-NoSqliteSidecars -DatabasePath $databasePath
    $databaseSha256 = (
        Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            [string]$Expected.DatabaseSha256,
            $databaseSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Context database content mismatch."
    }

    $actualManifest = @(Get-AppSnapshotFileManifest -Root $rootFullPath)
    Assert-AppSnapshotFileManifestsEqual `
        -Expected @($Expected.ManagedFileManifest) `
        -Actual $actualManifest `
        -Context $Context
}

function Set-TypedIsolatedAppDataSeedRootMarker {
    param(
        [Parameter(Mandatory = $true)][string]$AppDataRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedOldRoot,
        [Parameter(Mandatory = $true)][string]$NewRoot
    )

    $appDataFullPath = ConvertTo-NormalizedFullPath -Path $AppDataRoot
    $expectedOldFullPath = ConvertTo-NormalizedFullPath -Path $ExpectedOldRoot
    $newFullPath = ConvertTo-NormalizedFullPath -Path $NewRoot
    if ([string]::Equals(
            $expectedOldFullPath,
            $newFullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The typed AppData seed-root marker rebase requires distinct roots.'
    }

    $markerRelativePath = '.georaeplan-isolated-seed-root'
    $markerPath = Join-Path $appDataFullPath $markerRelativePath
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "The typed AppData seed-root marker is missing: $markerPath"
    }
    $oldMarkerText = [IO.File]::ReadAllText($markerPath)
    if (
        [string]::IsNullOrWhiteSpace($oldMarkerText) -or
        -not [string]::Equals(
            $oldMarkerText,
            $oldMarkerText.Trim(),
            [StringComparison]::Ordinal)
    ) {
        throw "The typed AppData seed-root marker schema is invalid: $markerPath"
    }
    try {
        $oldMarkerRoot = ConvertTo-NormalizedFullPath -Path $oldMarkerText
    }
    catch {
        throw "The typed AppData seed-root marker path is invalid: $markerPath"
    }
    if (-not [string]::Equals(
            $oldMarkerRoot,
            $expectedOldFullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'The typed AppData seed-root marker has an unexpected old root. ' +
            "marker=$markerPath expected=$expectedOldFullPath actual=$oldMarkerRoot")
    }

    $beforeManifest = @(Get-AppSnapshotFileManifest -Root $appDataFullPath)
    Write-Utf8File -Path $markerPath -Content $newFullPath
    $newMarkerText = [IO.File]::ReadAllText($markerPath)
    if (-not [string]::Equals(
            $newMarkerText,
            $newFullPath,
            [StringComparison]::Ordinal)) {
        throw "The typed AppData seed-root marker rebase was not exact: $markerPath"
    }
    $afterManifest = @(Get-AppSnapshotFileManifest -Root $appDataFullPath)

    $beforeByPath =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    $afterByPath =
        [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $beforeManifest) {
        $beforeByPath.Add([string]$entry.RelativePath, $entry)
    }
    foreach ($entry in $afterManifest) {
        $afterByPath.Add([string]$entry.RelativePath, $entry)
    }
    if (
        $beforeByPath.Count -ne $afterByPath.Count -or
        -not $beforeByPath.ContainsKey($markerRelativePath) -or
        -not $afterByPath.ContainsKey($markerRelativePath)
    ) {
        throw 'The typed AppData seed-root marker rebase changed tree entries.'
    }
    foreach ($relativePath in $beforeByPath.Keys) {
        if (-not $afterByPath.ContainsKey($relativePath)) {
            throw (
                'The typed AppData seed-root marker rebase removed an entry: ' +
                $relativePath)
        }
        $beforeEntry = $beforeByPath[$relativePath]
        $afterEntry = $afterByPath[$relativePath]
        if ([string]::Equals(
                $relativePath,
                $markerRelativePath,
                [StringComparison]::OrdinalIgnoreCase)) {
            if ([string]::Equals(
                    [string]$beforeEntry.Sha256,
                    [string]$afterEntry.Sha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The typed AppData seed-root marker rebase changed no bytes.'
            }
            continue
        }
        if (
            [long]$beforeEntry.Length -ne [long]$afterEntry.Length -or
            [long]$beforeEntry.LastWriteUtcTicks -ne
                [long]$afterEntry.LastWriteUtcTicks -or
            -not [string]::Equals(
                [string]$beforeEntry.Sha256,
                [string]$afterEntry.Sha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'The typed AppData seed-root marker rebase changed another ' +
                "entry: $relativePath")
        }
    }
}

function Set-TypedIsolatedServerRootMarker {
    param(
        [Parameter(Mandatory = $true)][string]$ServerRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedOldRoot,
        [Parameter(Mandatory = $true)][string]$NewRoot
    )

    $serverFullPath = ConvertTo-NormalizedFullPath -Path $ServerRoot
    $expectedOldFullPath = ConvertTo-NormalizedFullPath -Path $ExpectedOldRoot
    $newFullPath = ConvertTo-NormalizedFullPath -Path $NewRoot
    if ([string]::Equals(
            $expectedOldFullPath,
            $newFullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The typed server-root marker rebase requires distinct roots.'
    }

    $markerPath =
        Join-Path $serverFullPath '.georaeplan-isolated-server-root'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "The typed server-root marker is missing: $markerPath"
    }
    $oldMarkerText = [IO.File]::ReadAllText($markerPath)
    if (
        [string]::IsNullOrWhiteSpace($oldMarkerText) -or
        -not [string]::Equals(
            $oldMarkerText,
            $oldMarkerText.Trim(),
            [StringComparison]::Ordinal)
    ) {
        throw "The typed server-root marker schema is invalid: $markerPath"
    }
    try {
        $oldMarkerRoot = ConvertTo-NormalizedFullPath -Path $oldMarkerText
    }
    catch {
        throw "The typed server-root marker path is invalid: $markerPath"
    }
    if (-not [string]::Equals(
            $oldMarkerRoot,
            $expectedOldFullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'The typed server-root marker has an unexpected old root. ' +
            "marker=$markerPath expected=$expectedOldFullPath " +
            "actual=$oldMarkerRoot")
    }

    Write-Utf8File -Path $markerPath -Content $newFullPath
    $newMarkerText = [IO.File]::ReadAllText($markerPath)
    if (-not [string]::Equals(
            $newMarkerText,
            $newFullPath,
            [StringComparison]::Ordinal)) {
        throw "The typed server-root marker rebase was not exact: $markerPath"
    }
}

function Get-AppSnapshotFileManifestDigest {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Manifest
    )

    $lines = @(
        $Manifest |
            Sort-Object RelativePath |
            ForEach-Object {
                '{0}|{1}|{2}|{3}' -f
                    [string]$_.RelativePath,
                    [long]$_.Length,
                    [long]$_.LastWriteUtcTicks,
                    [string]$_.Sha256
            }
    )
    $bytes = [Text.Encoding]::UTF8.GetBytes(
        ($lines -join [Environment]::NewLine))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return (
            [BitConverter]::ToString($sha256.ComputeHash($bytes)).
                Replace('-', ''))
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-RetainedIsolatedAppSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    $rootFullPath = ConvertTo-NormalizedFullPath -Path $Root
    if (-not (Test-Path -LiteralPath $rootFullPath -PathType Container)) {
        throw (
            '-SkipDataCopy requires an existing isolated AppData directory: ' +
            $rootFullPath)
    }

    $databasePath = Join-Path $rootFullPath 'data\거래플랜.db'
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
        throw (
            '-SkipDataCopy requires an existing isolated AppData database: ' +
            $databasePath)
    }

    $markerPath =
        Join-Path $rootFullPath '.georaeplan-isolated-seed-root'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw (
            '-SkipDataCopy requires an existing isolated AppData marker: ' +
            $markerPath)
    }

    $markerValue = [IO.File]::ReadAllText($markerPath).Trim()
    if ([string]::IsNullOrWhiteSpace($markerValue)) {
        throw (
            '-SkipDataCopy found an empty isolated AppData marker: ' +
            $markerPath)
    }

    try {
        $markerRoot = ConvertTo-NormalizedFullPath -Path $markerValue
    }
    catch {
        throw (
            '-SkipDataCopy found an invalid isolated AppData marker: ' +
            $markerPath)
    }
    if (-not [string]::Equals(
            $markerRoot,
            $rootFullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            '-SkipDataCopy isolated AppData marker does not match its root. ' +
            "marker=$markerPath root=$rootFullPath")
    }

    Assert-NoSqliteSidecars -DatabasePath $databasePath
}

function Get-RetainedIsolatedAppSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    Assert-RetainedIsolatedAppSnapshot -Root $Root
    $rootFullPath = ConvertTo-NormalizedFullPath -Path $Root
    $databasePath = Join-Path $rootFullPath 'data\거래플랜.db'
    $managedFileManifest = @(
        Get-AppSnapshotFileManifest -Root $rootFullPath
    )

    return [pscustomobject]@{
        SourceExists = $true
        DatabaseSource = $databasePath
        DatabaseSha256 = (
            Get-FileHash `
                -LiteralPath $databasePath `
                -Algorithm SHA256).Hash
        DatabaseSnapshotMode = 'retained-existing-isolated-snapshot'
        UsedBackupFallback = $false
        ManagedFileCount = $managedFileManifest.Count
        ManagedFileManifest = @($managedFileManifest)
        ManagedFileManifestSha256 =
            Get-AppSnapshotFileManifestDigest -Manifest $managedFileManifest
    }
}

function Assert-RetainedIsolatedAppSnapshotUnchanged {
    param(
        [Parameter(Mandatory = $true)][object]$Expected,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $actual = Get-RetainedIsolatedAppSnapshot -Root $Root
    if (
        -not [string]::Equals(
            [string]$Expected.DatabaseSha256,
            [string]$actual.DatabaseSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        $Expected.ManagedFileCount -ne $actual.ManagedFileCount -or
        -not [string]::Equals(
            [string]$Expected.ManagedFileManifestSha256,
            [string]$actual.ManagedFileManifestSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "The retained isolated AppData snapshot changed $Context."
    }

    return $actual
}

function Get-RuntimeExecutionTreeManifestDigest {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$ExcludedRelativePaths = @()
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Runtime execution tree does not exist: $Root"
    }

    $rootFullPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $rootFullPath + [IO.Path]::DirectorySeparatorChar
    $excluded =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in @($ExcludedRelativePaths)) {
        if (-not [string]::IsNullOrWhiteSpace($relativePath)) {
            [void]$excluded.Add(
                $relativePath.Trim().Replace('/', '\'))
        }
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($rootFullPath)
    $manifestLines = @()
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "Runtime execution tree contains a reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $pendingDirectories.Push($item.FullName)
                continue
            }

            $fullPath = [IO.Path]::GetFullPath($item.FullName)
            if (-not $fullPath.StartsWith(
                    $rootPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Runtime execution file escaped its root: $fullPath"
            }
            $relativePath =
                $fullPath.Substring($rootPrefix.Length).Replace('/', '\')
            if ($excluded.Contains($relativePath)) {
                continue
            }

            $before = Get-Item -LiteralPath $fullPath -Force
            $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
            $after = Get-Item -LiteralPath $fullPath -Force
            if (
                $before.Length -ne $after.Length -or
                $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc
            ) {
                throw "Runtime execution file changed while hashing: $fullPath"
            }
            $manifestLines += (
                '{0}|{1}|{2}' -f
                    $relativePath,
                    [long]$after.Length,
                    $hash)
        }
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes(
        (($manifestLines | Sort-Object) -join [Environment]::NewLine))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return (
            [BitConverter]::ToString($sha256.ComputeHash($bytes)).
                Replace('-', ''))
    }
    finally {
        $sha256.Dispose()
    }
}

function Copy-CurrentAppSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$TargetRoot,
        [string]$DotnetExe,
        [string]$SyncDiagProject
    )

    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw (
            'SourceAppRoot does not exist. Refusing to reuse stale isolated ' +
            "AppData: $SourceRoot")
    }

    $sourceRootIdentityLease =
        Enter-SourceAppRootIdentityLease -Path $SourceRoot
    try {
        Assert-SourceAppRootIdentityLease `
            -Lease $sourceRootIdentityLease
        $sourceFileManifestBefore = @(
            Get-AppSnapshotFileManifest -Root $SourceRoot
        )
        Assert-SourceAppRootIdentityLease `
            -Lease $sourceRootIdentityLease

        Invoke-RobocopyMirror `
            -Source $SourceRoot `
            -Destination $TargetRoot `
            -ExcludeDirectories @(
                (Join-Path $SourceRoot 'logs'),
                (Join-Path $SourceRoot 'temp')
            )
        Assert-SourceAppRootIdentityLease `
            -Lease $sourceRootIdentityLease

    $targetRootFullPath = [IO.Path]::GetFullPath($TargetRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $targetRootPrefix =
        $targetRootFullPath + [IO.Path]::DirectorySeparatorChar
    foreach ($volatileDirectoryName in @('logs', 'temp')) {
        $volatileDirectory = [IO.Path]::GetFullPath(
            (Join-Path $targetRootFullPath $volatileDirectoryName))
        if (-not $volatileDirectory.StartsWith(
                $targetRootPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                'Volatile AppData directory escaped the isolated target: ' +
                $volatileDirectory)
        }
        if (Test-Path -LiteralPath $volatileDirectory) {
            $volatileItem =
                Get-Item -LiteralPath $volatileDirectory -Force
            if (
                -not $volatileItem.PSIsContainer -or
                ($volatileItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw (
                    'Volatile AppData target is not a plain directory: ' +
                    $volatileDirectory)
            }
            Remove-Item `
                -LiteralPath $volatileDirectory `
                -Recurse `
                -Force `
                -ErrorAction Stop
        }
        New-Item `
            -ItemType Directory `
            -Path $volatileDirectory `
            -Force `
            -ErrorAction Stop |
            Out-Null
    }

    $sourceDb = Join-Path $SourceRoot 'data\거래플랜.db'
    $targetDb = Join-Path $TargetRoot 'data\거래플랜.db'
    $databaseSource = $sourceDb
    $databaseSha256 = ''
    $usedBackupFallback = $false

    if (
        $null -ne (
            Get-Command `
                -Name Assert-CopiedSnapshotTargetSafeForRemoval `
                -CommandType Function `
                -ErrorAction SilentlyContinue)
    ) {
        Assert-CopiedSnapshotTargetSafeForRemoval `
            -TargetRoot $TargetRoot `
            -TargetDatabase $targetDb
    }
    else {
        $fallbackTargetRoot =
            [IO.Path]::GetFullPath($TargetRoot).TrimEnd(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar)
        $fallbackTargetDatabase =
            [IO.Path]::GetFullPath($targetDb)
        $fallbackPrefix =
            $fallbackTargetRoot +
            [IO.Path]::DirectorySeparatorChar
        $fallbackItem =
            Get-Item -LiteralPath $fallbackTargetDatabase -Force
        if (
            -not $fallbackTargetDatabase.StartsWith(
                $fallbackPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            $fallbackItem.PSIsContainer -or
            ($fallbackItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw 'The copied snapshot target fallback rejected the file.'
        }
        $fallbackLease = $null
        try {
            $fallbackLease = [IO.File]::Open(
                $fallbackTargetDatabase,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::None)
        }
        finally {
            if ($null -ne $fallbackLease) {
                $fallbackLease.Dispose()
            }
        }
        [IO.File]::Delete($fallbackTargetDatabase)
    }

    foreach ($sqliteSidecar in @(
        (Join-Path $TargetRoot 'data\거래플랜.db-shm'),
        (Join-Path $TargetRoot 'data\거래플랜.db-wal'),
        (Join-Path $TargetRoot 'data\거래플랜.db-journal')
    )) {
        if (Test-Path -LiteralPath $sqliteSidecar) {
            Remove-Item -LiteralPath $sqliteSidecar -Force -ErrorAction Stop
        }
    }

    $databaseSnapshotMode = 'standalone-file-copy'
    if (
        -not [string]::IsNullOrWhiteSpace($DotnetExe) -and
        -not [string]::IsNullOrWhiteSpace($SyncDiagProject)
    ) {
        $databaseSha256 = Copy-OnlineSqliteSnapshot `
            -DotnetExe $DotnetExe `
            -SyncDiagProject $SyncDiagProject `
            -SourceRoot $SourceRoot `
            -TargetRoot $TargetRoot `
            -SourceDatabase $sourceDb `
            -TargetDatabase $targetDb
        $databaseSnapshotMode = 'sqlite-online-backup'
        $usedBackupFallback = $true
    }
    else {
        $databaseSha256 = Copy-StableStandaloneSqliteSnapshot `
            -SourceDatabase $sourceDb `
            -TargetDatabase $targetDb
    }

    $sourceFileManifestAfter = @(
        Get-AppSnapshotFileManifest -Root $SourceRoot
    )
    $targetFileManifest = @(
        Get-AppSnapshotFileManifest -Root $TargetRoot
    )
    Assert-AppSnapshotFileManifestsEqual `
        -Expected $sourceFileManifestBefore `
        -Actual $sourceFileManifestAfter `
        -Context 'Source AppData stability'
    Assert-AppSnapshotFileManifestsEqual `
        -Expected $sourceFileManifestAfter `
        -Actual $targetFileManifest `
        -Context 'AppData source/target copy'

    foreach ($sqliteSidecar in @(
        (Join-Path $TargetRoot 'data\거래플랜.db-shm'),
        (Join-Path $TargetRoot 'data\거래플랜.db-wal'),
        (Join-Path $TargetRoot 'data\거래플랜.db-journal')
    )) {
        if (Test-Path -LiteralPath $sqliteSidecar) {
            throw "SQLite 스냅샷 대상에 예기치 않은 sidecar가 생성되었습니다: $sqliteSidecar"
        }
    }

        return [pscustomobject]@{
            SourceExists = $true
            DatabaseSource = $databaseSource
            DatabaseSha256 = $databaseSha256
            DatabaseSnapshotMode = $databaseSnapshotMode
            UsedBackupFallback = $usedBackupFallback
            ManagedFileCount = $targetFileManifest.Count
            ManagedFileManifestSha256 =
                Get-AppSnapshotFileManifestDigest -Manifest $targetFileManifest
        }
    }
    finally {
        if ($null -ne $sourceRootIdentityLease) {
            $sourceRootIdentityLease.Dispose()
        }
    }
}

function Reset-IsolatedServerStorage {
    param(
        [Parameter(Mandatory = $true)][string]$ServerOutput,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot
    )

    foreach ($path in @(
        (Join-Path $ServerOutput '거래플랜-local.db'),
        (Join-Path $ServerOutput 'salesmaster-local.db'),
        (Join-Path $ServerOutput 'App_Data'),
        $ServerDataRoot
    )) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
        }

        if (Test-Path -LiteralPath $path) {
            throw "격리 테스트 서버의 이전 저장소를 제거하지 못했습니다: $path"
        }
    }
}

function Repair-ProcessPathEnvironmentForChildProcess {
    $pathValue = [Environment]::GetEnvironmentVariable('Path', 'Process')
    if ([string]::IsNullOrWhiteSpace($pathValue)) {
        $pathValue = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    }

    if (-not [string]::IsNullOrWhiteSpace($pathValue)) {
        [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
        [Environment]::SetEnvironmentVariable('Path', $pathValue, 'Process')
    }
}

function New-LocalTestPassword {
    return ('local-test-' + [Guid]::NewGuid().ToString('N'))
}

function Start-IsolatedServerProcess {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$ServerDll,
        [Parameter(Mandatory = $true)][string]$ServerWorkingDirectory,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$FileStorageRoot,
        [Parameter(Mandatory = $true)][string]$UpdatesRoot,
        [Parameter(Mandatory = $true)][string]$AdminPassword,
        [Parameter(Mandatory = $true)][string]$UsenetPassword,
        [bool]$EnableSeedUsers = $false
    )

    $serverUrl = "http://127.0.0.1:$Port"

    $serverEnv = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DOTNET_ENVIRONMENT' = 'Development'
        'Kestrel__Endpoints__Http__Url' = $serverUrl
        'ERP_DB_FALLBACK_SQLITE' = '1'
        'SeedUsers__EnableSeedUsers' = if ($EnableSeedUsers) { 'true' } else { 'false' }
        'SeedUsers__AdminPassword' = $AdminPassword
        'SeedUsers__UserPassword' = (New-LocalTestPassword)
        'SeedUsers__ItwPassword' = (New-LocalTestPassword)
        'SeedUsers__UsenetUsername' = 'usenet'
        'SeedUsers__UsenetPassword' = $UsenetPassword
        'SeedUsers__UpdateExistingUsenetPassword' = 'true'
        'Logging__LogLevel__Default' = 'Warning'
        'Logging__LogLevel__Microsoft' = 'Warning'
        'Logging__LogLevel__Microsoft.AspNetCore' = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore' = 'Warning'
        'FileStorage__RootPath' = $FileStorageRoot
        'Updates__StorageRoot' = $UpdatesRoot
    }

    $previousEnv = @{}
    foreach ($key in $serverEnv.Keys) {
        $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$serverEnv[$key], 'Process')
    }

    try {
        Repair-ProcessPathEnvironmentForChildProcess
        $argumentList = ('"{0}" --environment Development' -f $ServerDll.Replace('"', '""'))
        $process = Start-Process -FilePath $DotnetExe -ArgumentList $argumentList -WorkingDirectory $ServerWorkingDirectory -WindowStyle Hidden -PassThru
    }
    finally {
        foreach ($key in $serverEnv.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnv[$key], 'Process')
        }
    }

    return [pscustomobject]@{
        Process = $process
        ServerUrl = $serverUrl
    }
}

function Stop-IsolatedServerProcess {
    param($State)

    if ($null -eq $State) {
        return
    }

    if ($State.Process) {
        try {
            if (-not $State.Process.HasExited) {
                & taskkill /PID $State.Process.Id /T /F > $null 2>&1
                if (-not $State.Process.WaitForExit(5000)) {
                    $State.Process.Kill()
                    if (-not $State.Process.WaitForExit(5000)) {
                        throw "격리 테스트 서버 프로세스가 종료되지 않았습니다. pid=$($State.Process.Id)"
                    }
                }
            }
        }
        finally {
            $State.Process.Dispose()
        }
    }
}

function Complete-IsolatedServerSqliteSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$ServerWorkingDirectory,
        [Parameter(Mandatory = $true)][string]$SeedLogRoot,
        [Parameter(Mandatory = $true)][string]$LogFileName
    )

    $ServerWorkingDirectory = [IO.Path]::GetFullPath($ServerWorkingDirectory)
    $serverDatabasePath = Join-Path $ServerWorkingDirectory '거래플랜-local.db'
    $finalizeResult = Invoke-WithProcessEnvironment -Variables @{
        GEORAEPLAN_TEST_MODE = '1'
        GEORAEPLAN_TEST_SEED_MODE = '1'
        GEORAEPLAN_TEST_SERVER_ROOT = $ServerWorkingDirectory
    } -Action {
        Invoke-DotnetWithOutput `
            -DotnetExe $DotnetExe `
            -Arguments @(
                'run',
                '--project',
                $SyncDiagProject,
                '--',
                'finalize-test-server-sqlite',
                $serverDatabasePath
            )
    }

    $finalizeLogPath = Join-Path $SeedLogRoot $LogFileName
    Write-Utf8File -Path $finalizeLogPath -Content $finalizeResult.Text
    $serverSqliteFinalized =
        $finalizeResult.ExitCode -eq 0 -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^server_sqlite_finalized=True\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^checkpoint_busy=0\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^journal_mode=delete\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^quick_check=ok\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^sidecar_count=0\s*$')
    if (-not $serverSqliteFinalized) {
        throw "격리 테스트 서버 SQLite 독립 스냅샷 마무리 실패`n$($finalizeResult.Text)"
    }

    return [pscustomobject]@{
        DatabasePath = $serverDatabasePath
        LogPath = $finalizeLogPath
    }
}

function Complete-IsolatedAppSqliteSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$TestAppRoot,
        [Parameter(Mandatory = $true)][string]$SeedLogRoot,
        [Parameter(Mandatory = $true)][string]$LogFileName
    )

    $TestAppRoot = [IO.Path]::GetFullPath($TestAppRoot)
    $appDatabasePath = Join-Path $TestAppRoot 'data\거래플랜.db'
    $finalizeResult = Invoke-WithProcessEnvironment -Variables @{
        GEORAEPLAN_APP_ROOT = $TestAppRoot
        GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
        GEORAEPLAN_TEST_MODE = '1'
        GEORAEPLAN_TEST_SEED_MODE = '1'
        GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot
    } -Action {
        Invoke-DotnetWithOutput `
            -DotnetExe $DotnetExe `
            -Arguments @(
                'run',
                '--project',
                $SyncDiagProject,
                '--',
                'finalize-test-app-sqlite'
            )
    }

    $finalizeLogPath = Join-Path $SeedLogRoot $LogFileName
    Write-Utf8File -Path $finalizeLogPath -Content $finalizeResult.Text
    $appSqliteFinalized =
        $finalizeResult.ExitCode -eq 0 -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^app_sqlite_finalized=True\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^checkpoint_busy=0\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^journal_mode=delete\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^quick_check=ok\s*$') -and
        [regex]::IsMatch($finalizeResult.Text, '(?m)^sidecar_count=0\s*$')
    if (-not $appSqliteFinalized) {
        throw "격리 테스트 앱 SQLite 독립 스냅샷 마무리 실패`n$($finalizeResult.Text)"
    }

    Assert-NoSqliteSidecars -DatabasePath $appDatabasePath
    return [pscustomobject]@{
        DatabasePath = $appDatabasePath
        LogPath = $finalizeLogPath
    }
}

function Stop-IsolatedRuntimeProcesses {
    param([Parameter(Mandatory = $true)][string]$OutputRoot)

    $trimChars = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $fullOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
    $volumeRoot = [IO.Path]::GetPathRoot($fullOutputRoot)
    if ([string]::IsNullOrWhiteSpace($volumeRoot)) {
        throw "OutputRoot must resolve to an absolute path: $OutputRoot"
    }

    $normalizedOutputRoot = $fullOutputRoot.TrimEnd($trimChars)
    $physicalOutputRoot =
        Resolve-PhysicalPathIdentity -Path $normalizedOutputRoot
    $normalizedVolumeRoot = $volumeRoot.TrimEnd($trimChars)
    if ([string]::Equals(
            $normalizedOutputRoot,
            $normalizedVolumeRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputRoot must be below the volume root: $OutputRoot"
    }

    $outputRootPrefix = $normalizedOutputRoot + [IO.Path]::DirectorySeparatorChar

    function Test-PathInsideOutputRoot {
        param([AllowNull()][string]$CandidatePath)

        if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
            return $false
        }

        try {
            $physicalCandidatePath =
                Resolve-PhysicalPathIdentity -Path $CandidatePath.Trim('"')
        }
        catch {
            return $false
        }

        $candidateIsWithinOutputRoot =
            Test-PathSameOrDescendant `
                -CandidatePath $physicalCandidatePath `
                -ParentPath $physicalOutputRoot
        $candidateEqualsOutputRoot = [string]::Equals(
            $physicalCandidatePath,
            $physicalOutputRoot,
            [StringComparison]::OrdinalIgnoreCase)
        return $candidateIsWithinOutputRoot -and -not $candidateEqualsOutputRoot
    }

    function ConvertFrom-IsolatedRuntimeCommandLine {
        param([AllowNull()][string]$CommandLine)

        if ([string]::IsNullOrWhiteSpace($CommandLine)) {
            return @()
        }

        $tokenMatches = [Text.RegularExpressions.Regex]::Matches(
            $CommandLine,
            '"(?<quoted>[^"]*)"|(?<bare>[^\s"]+)')
        return @(
            foreach ($tokenMatch in $tokenMatches) {
                if ($tokenMatch.Groups['quoted'].Success) {
                    $tokenMatch.Groups['quoted'].Value
                }
                else {
                    $tokenMatch.Groups['bare'].Value
                }
            }
        )
    }

    function Test-IsolatedRuntimeHostCommandLine {
        param(
            [AllowNull()][string]$ProcessName,
            [AllowNull()][string]$CommandLine
        )

        $tokens = @(ConvertFrom-IsolatedRuntimeCommandLine -CommandLine $CommandLine)
        if ($tokens.Count -lt 2) {
            return $false
        }

        switch ($ProcessName) {
            { $_ -ieq 'dotnet.exe' } {
                $argumentIndex = 1
                $dotnetOptionsWithValues = @(
                    '--additional-deps',
                    '--additionalprobingpath',
                    '--depsfile',
                    '--fx-version',
                    '--roll-forward',
                    '--runtimeconfig'
                )
                while ($argumentIndex -lt $tokens.Count) {
                    $argument = [string]$tokens[$argumentIndex]
                    if ($argument -ieq 'exec') {
                        $argumentIndex++
                        continue
                    }

                    if (-not $argument.StartsWith('-', [StringComparison]::Ordinal)) {
                        break
                    }

                    $optionName = $argument.Split('=', 2)[0]
                    if (
                        $argument.IndexOf('=', [StringComparison]::Ordinal) -lt 0 -and
                        $dotnetOptionsWithValues -icontains $optionName
                    ) {
                        $argumentIndex += 2
                    }
                    else {
                        $argumentIndex++
                    }
                }

                if ($argumentIndex -ge $tokens.Count) {
                    return $false
                }

                $entryPoint = [string]$tokens[$argumentIndex]
                $extension = [IO.Path]::GetExtension($entryPoint)
                return (
                    ($extension -ieq '.dll' -or $extension -ieq '.exe') -and
                    (Test-PathInsideOutputRoot -CandidatePath $entryPoint)
                )
            }
            { $_ -ieq 'powershell.exe' -or $_ -ieq 'pwsh.exe' } {
                for ($argumentIndex = 1; $argumentIndex -lt ($tokens.Count - 1); $argumentIndex++) {
                    if (
                        $tokens[$argumentIndex] -ieq '-File' -or
                        $tokens[$argumentIndex] -ieq '-f'
                    ) {
                        return Test-PathInsideOutputRoot -CandidatePath (
                            [string]$tokens[$argumentIndex + 1])
                    }
                }

                return $false
            }
            { $_ -ieq 'cmd.exe' } {
                $entryPointMatch = [Text.RegularExpressions.Regex]::Match(
                    $CommandLine,
                    '(?i)(?:^|\s)/(?:c|k)\s+(?:"{1,2})?(?<entry>[^"]+?\.(?:cmd|bat))(?:"|\s|$)')
                if (-not $entryPointMatch.Success) {
                    return $false
                }

                return Test-PathInsideOutputRoot -CandidatePath (
                    $entryPointMatch.Groups['entry'].Value)
            }
            default {
                return $false
            }
        }
    }

    $processes = @(
        Get-CimInstance Win32_Process -ErrorAction Stop
    )
    $processById = @{}
    foreach ($process in $processes) {
        $processById[[int]$process.ProcessId] = $process
    }

    # The preparation command itself contains OutputRoot in its command line.
    # Protect it and every ancestor so cleanup cannot terminate the caller.
    $protectedProcessIds = [Collections.Generic.HashSet[int]]::new()
    $ancestorProcessId = [int]$PID
    while ($ancestorProcessId -gt 0 -and $protectedProcessIds.Add($ancestorProcessId)) {
        if (-not $processById.ContainsKey($ancestorProcessId)) {
            break
        }

        $ancestorProcessId = [int]$processById[$ancestorProcessId].ParentProcessId
    }

    $targets = $processes |
        Where-Object {
            $executablePath = $_.ExecutablePath
            $commandLine = $_.CommandLine
            $executableMatches =
                $executablePath -and
                (Test-PathInsideOutputRoot -CandidatePath $executablePath)
            $commandLineMatches =
                Test-IsolatedRuntimeHostCommandLine `
                    -ProcessName ([string]$_.Name) `
                    -CommandLine $commandLine
            -not $protectedProcessIds.Contains([int]$_.ProcessId) -and (
                $executableMatches -or
                $commandLineMatches
            )
        }

    $terminationFailures = [Collections.Generic.List[string]]::new()
    foreach ($target in $targets) {
        $liveProcess = $null
        try {
            if ($null -eq $target.CreationDate) {
                throw "The process creation time is unavailable."
            }

            $liveProcess = Get-Process `
                -Id ([int]$target.ProcessId) `
                -ErrorAction SilentlyContinue
            if ($null -eq $liveProcess) {
                continue
            }

            $expectedProcessName =
                [IO.Path]::GetFileNameWithoutExtension([string]$target.Name)
            $snapshotStartTimeUtc =
                ([DateTime]$target.CreationDate).ToUniversalTime()
            $liveStartTimeUtc = $liveProcess.StartTime.ToUniversalTime()
            $startTimeDeltaMilliseconds = [Math]::Abs(
                ($liveStartTimeUtc - $snapshotStartTimeUtc).TotalMilliseconds)
            if (
                -not [string]::Equals(
                    $liveProcess.ProcessName,
                    $expectedProcessName,
                    [StringComparison]::OrdinalIgnoreCase) -or
                $startTimeDeltaMilliseconds -gt 1
            ) {
                # The original target already exited and its PID was reused.
                continue
            }

            if ($liveProcess.HasExited) {
                continue
            }

            # Kill through the validated Process handle rather than looking the PID up again.
            $liveProcess.Kill()
            if (-not $liveProcess.WaitForExit(5000)) {
                throw [TimeoutException]::new(
                    "The process did not exit within 5 seconds.")
            }
        }
        catch [InvalidOperationException] {
            # The validated process exited naturally before termination completed.
            continue
        }
        catch {
            $terminationFailures.Add(
                "PID $($target.ProcessId) ($($target.Name)): $($_.Exception.Message)")
        }
        finally {
            if ($null -ne $liveProcess) {
                $liveProcess.Dispose()
            }
        }
    }

    if ($terminationFailures.Count -gt 0) {
        throw (
            "Failed to stop isolated runtime processes:" +
            [Environment]::NewLine +
            (($terminationFailures | ForEach-Object { " - $_" }) -join [Environment]::NewLine)
        )
    }
}

function Write-TestRunScripts {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$DefaultBaseUrl,
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$CertificationId,
        [Parameter(Mandatory = $true)][string]$CertificationMode,
        [Parameter(Mandatory = $true)][int]$PasswordResetCount,
        [switch]$IncludeInternalLockProbe
    )

    $runtimeLogRoot = Join-Path $OutputRoot 'RuntimeLogs'
    New-Item -ItemType Directory -Force -Path $runtimeLogRoot | Out-Null

    $invalidMarkerPath =
        Join-Path $OutputRoot '.georaeplan-runtime-invalid'
    Set-RuntimeInvalidationMarker `
        -Path $invalidMarkerPath `
        -Reason 'test-launcher-replacement'

    $readyMarkerPath = Join-Path $OutputRoot '.georaeplan-runtime-ready'
    if (Test-Path -LiteralPath $readyMarkerPath) {
        Remove-Item `
            -LiteralPath $readyMarkerPath `
            -Force `
            -ErrorAction Stop
        if (Test-Path -LiteralPath $readyMarkerPath) {
            throw (
                'The previous runtime certification could not be removed ' +
                'before replacing test launchers.')
        }
    }

    $runAppContent = @"
@echo off
setlocal EnableExtensions
call "%~dp0Run-All.cmd"
set "RUN_EXIT=%ERRORLEVEL%"
exit /b %RUN_EXIT%
"@

    $hiddenLauncherContent = @'
Option Explicit

Dim shell
Dim fileSystem
Dim scriptDirectory
Dim runAllPath
Dim comSpec
Dim command
Dim exitCode
Dim readyToLaunch
Dim initializationFailed
Dim processEnvironment
Dim environmentEntry
Dim suppressionName
Dim previousSuppressionValue
Dim suppressionWasPresent
Dim suppressionWasApplied

On Error Resume Next
exitCode = 1
readyToLaunch = False
initializationFailed = False
suppressionName = "GEORAEPLAN_SUPPRESS_FAILURE_DIALOG"
previousSuppressionValue = ""
suppressionWasPresent = False
suppressionWasApplied = False
Set shell = CreateObject("WScript.Shell")
If Err.Number <> 0 Then
    initializationFailed = True
    Err.Clear
End If
If Not initializationFailed Then
    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    If Err.Number <> 0 Then
        initializationFailed = True
        Err.Clear
    End If
End If
If Not initializationFailed Then
    Set processEnvironment = shell.Environment("PROCESS")
    If Err.Number <> 0 Then
        initializationFailed = True
        Err.Clear
    End If
End If
If Not initializationFailed Then
    scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
    If Err.Number <> 0 Then
        initializationFailed = True
        Err.Clear
    End If
End If
If Not initializationFailed Then
    runAllPath = fileSystem.BuildPath(scriptDirectory, "Run-All.cmd")
    If Err.Number <> 0 Then
        initializationFailed = True
        Err.Clear
    End If
End If
If Not initializationFailed Then
    comSpec = shell.ExpandEnvironmentStrings("%ComSpec%")
    If Err.Number <> 0 Then
        initializationFailed = True
        Err.Clear
    End If
End If
If Not initializationFailed Then
    readyToLaunch = Len(Trim(comSpec)) > 0
    If readyToLaunch Then
        readyToLaunch = fileSystem.FileExists(comSpec)
    End If
    If Err.Number <> 0 Then
        initializationFailed = True
        readyToLaunch = False
        Err.Clear
    End If
End If
If Not initializationFailed And readyToLaunch Then
    readyToLaunch = fileSystem.FileExists(runAllPath)
    If Err.Number <> 0 Then
        initializationFailed = True
        readyToLaunch = False
        Err.Clear
    End If
End If
If Not initializationFailed And readyToLaunch Then
    For Each environmentEntry In processEnvironment
        If UCase(Left(environmentEntry, Len(suppressionName) + 1)) = _
                UCase(suppressionName & "=") Then
            suppressionWasPresent = True
            previousSuppressionValue = Mid(environmentEntry, Len(suppressionName) + 2)
            Exit For
        End If
    Next
    If Err.Number <> 0 Then
        initializationFailed = True
        Err.Clear
    End If
End If
If Not initializationFailed And readyToLaunch Then
    processEnvironment(suppressionName) = "1"
    If Err.Number <> 0 Then
        initializationFailed = True
        Err.Clear
    Else
        suppressionWasApplied = True
    End If
End If
If Not initializationFailed And readyToLaunch Then
    command = Quote(comSpec) & " /d /c " & Quote(Quote(runAllPath))
    exitCode = shell.Run(command, 0, True)
End If
If Err.Number <> 0 Then
    exitCode = 1
    Err.Clear
End If
If suppressionWasApplied Then
    If suppressionWasPresent Then
        processEnvironment(suppressionName) = previousSuppressionValue
    Else
        processEnvironment.Remove suppressionName
    End If
    If Err.Number <> 0 Then
        exitCode = 1
        Err.Clear
    End If
End If

If exitCode <> 0 Then
    MsgBox BuildErrorMessage(), vbCritical, BuildErrorTitle()
    Err.Clear
End If
On Error GoTo 0

Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function

Function BuildErrorMessage()
    Dim value
    value = ChrW(&HD14C) & ChrW(&HC2A4) & ChrW(&HD2B8) & " "
    value = value & ChrW(&HC571) & " "
    value = value & ChrW(&HC2E4) & ChrW(&HD589) & ChrW(&HC5D0) & " "
    value = value & ChrW(&HC2E4) & ChrW(&HD328) & ChrW(&HD588)
    value = value & ChrW(&HC2B5) & ChrW(&HB2C8) & ChrW(&HB2E4) & ". "
    value = value & ChrW(&HC790) & ChrW(&HC138) & ChrW(&HD55C) & " "
    value = value & ChrW(&HB0B4) & ChrW(&HC6A9) & ChrW(&HC740) & " "
    value = value & "RuntimeLogs" & ChrW(&HC5D0) & ChrW(&HC11C) & " "
    value = value & ChrW(&HD655) & ChrW(&HC778) & ChrW(&HD558)
    value = value & ChrW(&HC138) & ChrW(&HC694) & "."
    BuildErrorMessage = value
End Function

Function BuildErrorTitle()
    Dim value
    value = ChrW(&HAC70) & ChrW(&HB798) & ChrW(&HD50C) & ChrW(&HB79C) & " "
    value = value & ChrW(&HD14C) & ChrW(&HC2A4) & ChrW(&HD2B8) & " "
    value = value & ChrW(&HC2E4) & ChrW(&HD589) & " "
    value = value & ChrW(&HC624) & ChrW(&HB958)
    BuildErrorTitle = value
End Function
'@

    $launcherReadmeContent = @"
거래플랜 격리 테스트 실행 안내

- CMD 창 없이 일반 실행: Launch-Test-App.vbs를 더블클릭합니다.
- 진단 또는 동기식 실행: Run-All.cmd를 실행합니다. 이 방식은 CMD 창과 종료 코드를 유지합니다.
- 실행 실패 알림은 민감정보나 로그 본문을 표시하지 않습니다. 자세한 원인은 RuntimeLogs에서 확인합니다.
- Run-App.cmd는 호환용이며 Run-All.cmd로 위임합니다.
"@

    $runServerContent = @"
@echo off
setlocal EnableExtensions
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Run-IsolatedComponent.ps1" -Mode Server
set "RUN_EXIT=%ERRORLEVEL%"
exit /b %RUN_EXIT%
"@

    $certificationValidationContent = @'
function Initialize-RuntimeFinalPathNativeMethods {
    if ($null -ne ('GeoraePlan.Runtime.FinalPathNativeMethods' -as [type])) {
        return
    }

    $nativeTempBase = 'D:\DevCaches'
    if (Test-Path -LiteralPath $nativeTempBase) {
        Assert-RuntimeRootHasNoReparsePoint -Path $nativeTempBase
    }
    else {
        Assert-RuntimeRootHasNoReparsePoint -Path 'D:\'
        New-Item -ItemType Directory -Path $nativeTempBase -Force |
            Out-Null
    }
    $nativeTempRoot =
        Join-Path $nativeTempBase 'georaeplan-launcher-native'
    if (Test-Path -LiteralPath $nativeTempRoot) {
        Assert-RuntimeRootHasNoReparsePoint -Path $nativeTempRoot
    }
    else {
        New-Item -ItemType Directory -Path $nativeTempRoot -Force |
            Out-Null
        Assert-RuntimeRootHasNoReparsePoint -Path $nativeTempRoot
    }
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    $env:TEMP = $nativeTempRoot
    $env:TMP = $nativeTempRoot
    try {
        Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.Runtime
{
    public static class FinalPathNativeMethods
    {
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathSize,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        public static uint GetLinkCount(string fileName)
        {
            using (SafeFileHandle handle = CreateFileW(
                fileName,
                0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new InvalidOperationException(
                        "Unable to open file for link-count validation.");

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                    throw new InvalidOperationException(
                        "Unable to read file identity.");

                return information.NumberOfLinks;
            }
        }
    }

    public sealed class ChildProcessJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;
        private IntPtr handle;

        public ChildProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Runtime child job creation failed.");

            var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
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
                        "Runtime KILL_ON_JOB_CLOSE configuration failed.");
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

        public void AssignProcess(Process process)
        {
            if (process == null)
                throw new ArgumentNullException("process");
            if (handle == IntPtr.Zero)
                throw new ObjectDisposedException("ChildProcessJob");
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Runtime child process job assignment failed.");
            }

            bool assigned;
            if (!IsProcessInJob(process.Handle, handle, out assigned))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Runtime child process job membership check failed.");
            }
            if (!assigned)
                throw new IOException(
                    "Runtime child process is not in the expected job.");
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
                return;
            IntPtr closingHandle = handle;
            handle = IntPtr.Zero;
            CloseHandle(closingHandle);
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength);

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
        private static extern bool CloseHandle(IntPtr handle);
    }
}
"@
    }
    finally {
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
    }
}

function Assert-RuntimeRootHasNoReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if (
            ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Runtime root ancestry contains a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
    }

    $logicalRoot = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals(
            [IO.Path]::GetPathRoot($logicalRoot),
            'D:\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime root must remain on D:.'
    }
}

function Get-RuntimePhysicalDirectoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-RuntimeRootHasNoReparsePoint -Path $Path
    Initialize-RuntimeFinalPathNativeMethods
    $handle =
        [GeoraePlan.Runtime.FinalPathNativeMethods]::CreateFileW(
            $Path,
            0,
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete,
            [IntPtr]::Zero,
            [GeoraePlan.Runtime.FinalPathNativeMethods]::OPEN_EXISTING,
            [GeoraePlan.Runtime.FinalPathNativeMethods]::FILE_FLAG_BACKUP_SEMANTICS,
            [IntPtr]::Zero)
    if ($handle.IsInvalid) {
        $handle.Dispose()
        throw "Unable to open runtime root for physical-path validation: $Path"
    }

    try {
        $capacity = 32768
        $builder = [Text.StringBuilder]::new($capacity)
        $length =
            [GeoraePlan.Runtime.FinalPathNativeMethods]::
                GetFinalPathNameByHandleW(
                    $handle,
                    $builder,
                    $capacity,
                    0)
        if ($length -eq 0 -or $length -ge $capacity) {
            throw "Unable to resolve runtime physical path: $Path"
        }
        $resolved = $builder.ToString()
        if ($resolved.StartsWith('\\?\UNC\', [StringComparison]::Ordinal)) {
            $resolved = '\\' + $resolved.Substring(8)
        }
        elseif ($resolved.StartsWith('\\?\', [StringComparison]::Ordinal)) {
            $resolved = $resolved.Substring(4)
        }
        return [IO.Path]::GetFullPath($resolved)
    }
    finally {
        $handle.Dispose()
    }
}

function Initialize-IsolatedRuntimeTempEnvironment {
    param([string]$RuntimeRoot = $PSScriptRoot)

    if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
        throw 'Runtime root is required for temp isolation.'
    }
    $runtimePhysicalRoot =
        Get-RuntimePhysicalDirectoryPath -Path $RuntimeRoot
    $runtimeTempRoot = Join-Path $RuntimeRoot 'Temp'
    if (Test-Path -LiteralPath $runtimeTempRoot) {
        if (-not (Test-Path -LiteralPath $runtimeTempRoot -PathType Container)) {
            throw "Runtime temp path is not a directory: $runtimeTempRoot"
        }
        Assert-RuntimeRootHasNoReparsePoint -Path $runtimeTempRoot
    }
    else {
        New-Item -ItemType Directory -Path $runtimeTempRoot -Force |
            Out-Null
        Assert-RuntimeRootHasNoReparsePoint -Path $runtimeTempRoot
    }

    $physicalTempRoot =
        Get-RuntimePhysicalDirectoryPath -Path $runtimeTempRoot
    $trimChars = [char[]]@('\', '/')
    $normalizedRuntimeRoot =
        [IO.Path]::GetFullPath($runtimePhysicalRoot).TrimEnd($trimChars)
    $normalizedTempRoot =
        [IO.Path]::GetFullPath($physicalTempRoot).TrimEnd($trimChars)
    $runtimePrefix =
        $normalizedRuntimeRoot + [IO.Path]::DirectorySeparatorChar
    if (
        -not $normalizedTempRoot.StartsWith(
            $runtimePrefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetPathRoot($normalizedTempRoot),
            'D:\',
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'Runtime temp root must remain directly below the certified D: runtime.'
    }

    [Environment]::SetEnvironmentVariable(
        'GEORAEPLAN_TEMP_ROOT',
        $normalizedTempRoot,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'TEMP',
        $normalizedTempRoot,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'TMP',
        $normalizedTempRoot,
        'Process')
    return $normalizedTempRoot
}

function Assert-RuntimeWritablePathsAreDirectD {
    param(
        [Parameter(Mandatory = $true)][string]$RuntimePhysicalRoot,
        [Parameter(Mandatory = $true)][string]$MarkerPath
    )

    $trimChars = [char[]]@('\', '/')
    $normalizedRuntimeRoot =
        [IO.Path]::GetFullPath($RuntimePhysicalRoot).TrimEnd($trimChars)
    $runtimePrefix =
        $normalizedRuntimeRoot + [IO.Path]::DirectorySeparatorChar
    $appRoot = Join-Path $PSScriptRoot 'App'
    $appSettingsPath = Join-Path $appRoot 'appsettings.json'
    $setApiScriptPath = Join-Path $PSScriptRoot 'Set-ApiBaseUrl.ps1'
    $appDataRoot = Join-Path $PSScriptRoot 'AppData'
    $appDataDirectory = Join-Path $appDataRoot 'data'
    $appDatabasePath =
        Join-Path $appDataDirectory '거래플랜.db'
    $serverRoot = Join-Path $PSScriptRoot 'Server'
    $serverDataRoot = Join-Path $PSScriptRoot 'ServerData'
    $runtimeLogRoot = Join-Path $PSScriptRoot 'RuntimeLogs'
    $serverDatabasePath =
        Join-Path $serverRoot '거래플랜-local.db'
    $requiredPaths = @(
        [pscustomobject]@{
            Path = $appRoot
            Recurse = $true
            RequireSingleLink = $false
        },
        [pscustomobject]@{
            Path = $appSettingsPath
            Recurse = $false
            RequireSingleLink = $true
        },
        [pscustomobject]@{
            Path = $setApiScriptPath
            Recurse = $false
            RequireSingleLink = $true
        },
        [pscustomobject]@{
            Path = $appDataRoot
            Recurse = $true
            RequireSingleLink = $false
        },
        [pscustomobject]@{
            Path = $appDataDirectory
            Recurse = $false
            RequireSingleLink = $false
        },
        [pscustomobject]@{
            Path = $appDatabasePath
            Recurse = $false
            RequireSingleLink = $true
        },
        [pscustomobject]@{
            Path = $serverRoot
            Recurse = $false
            RequireSingleLink = $false
        },
        [pscustomobject]@{
            Path = $serverDataRoot
            Recurse = $true
            RequireSingleLink = $false
        },
        [pscustomobject]@{
            Path = $runtimeLogRoot
            Recurse = $true
            RequireSingleLink = $false
        },
        [pscustomobject]@{
            Path = $serverDatabasePath
            Recurse = $false
            RequireSingleLink = $true
        },
        [pscustomobject]@{
            Path = $MarkerPath
            Recurse = $false
            RequireSingleLink = $true
        }
    )

    foreach ($requiredPath in $requiredPaths) {
        if (-not (Test-Path -LiteralPath $requiredPath.Path)) {
            throw "A required runtime path is missing: $($requiredPath.Path)"
        }
        $physicalPath = (
            Get-RuntimePhysicalDirectoryPath -Path $requiredPath.Path
        ).TrimEnd($trimChars)
        if (
            -not $physicalPath.StartsWith(
                $runtimePrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [IO.Path]::GetPathRoot($physicalPath),
                'D:\',
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw "A writable runtime path escaped its certified D root: $physicalPath"
        }
        if (
            [bool]$requiredPath.RequireSingleLink -and
            [GeoraePlan.Runtime.FinalPathNativeMethods]::GetLinkCount(
                $requiredPath.Path) -ne 1
        ) {
            throw "A writable runtime file has multiple hard links: $($requiredPath.Path)"
        }

        if (-not [bool]$requiredPath.Recurse) {
            continue
        }
        $pendingDirectories = [Collections.Generic.Stack[string]]::new()
        $pendingDirectories.Push([string]$requiredPath.Path)
        while ($pendingDirectories.Count -gt 0) {
            $currentDirectory = $pendingDirectories.Pop()
            foreach ($item in Get-ChildItem -LiteralPath $currentDirectory -Force) {
                if (
                    ($item.Attributes -band
                        [IO.FileAttributes]::ReparsePoint) -ne 0
                ) {
                    throw "Runtime writable data contains a reparse point: $($item.FullName)"
                }
                if ($item.PSIsContainer) {
                    $pendingDirectories.Push($item.FullName)
                }
            }
        }
    }
}

function Get-RuntimeManagedFileManifestDigest {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootFullPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $rootFullPath + [IO.Path]::DirectorySeparatorChar
    $primaryDatabaseRelativePaths =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in @(
        'data\거래플랜.db',
        'data\거래플랜.db-shm',
        'data\거래플랜.db-wal',
        'data\거래플랜.db-journal'
    )) {
        [void]$primaryDatabaseRelativePaths.Add($relativePath)
    }
    $volatileTopLevelDirectories =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($directoryName in @('logs', 'temp')) {
        [void]$volatileTopLevelDirectories.Add($directoryName)
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($rootFullPath)
    $manifestLines = @()
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "Managed runtime data contains a reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                if (
                    [string]::Equals(
                        $currentDirectory,
                        $rootFullPath,
                        [StringComparison]::OrdinalIgnoreCase) -and
                    $volatileTopLevelDirectories.Contains($item.Name)
                ) {
                    continue
                }
                $pendingDirectories.Push($item.FullName)
                continue
            }

            $fullPath = [IO.Path]::GetFullPath($item.FullName)
            if (-not $fullPath.StartsWith(
                    $rootPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Managed runtime file escaped AppData: $fullPath"
            }
            $relativePath =
                $fullPath.Substring($rootPrefix.Length).Replace('/', '\')
            if ($primaryDatabaseRelativePaths.Contains($relativePath)) {
                continue
            }
            $before = Get-Item -LiteralPath $fullPath -Force
            $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
            $after = Get-Item -LiteralPath $fullPath -Force
            if (
                $before.Length -ne $after.Length -or
                $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc
            ) {
                throw "Managed runtime file changed while hashing: $fullPath"
            }
            $manifestLines += (
                '{0}|{1}|{2}|{3}' -f
                    $relativePath,
                    [long]$after.Length,
                    [long]$after.LastWriteTimeUtc.Ticks,
                    $hash)
        }
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes(
        (($manifestLines | Sort-Object) -join [Environment]::NewLine))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return (
            [BitConverter]::ToString($sha256.ComputeHash($bytes)).
                Replace('-', ''))
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-RuntimeExecutionTreeManifestDigest {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string[]]$ExcludedRelativePaths = @()
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Runtime execution tree does not exist: $Root"
    }

    $rootFullPath = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $rootFullPath + [IO.Path]::DirectorySeparatorChar
    $excluded =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in @($ExcludedRelativePaths)) {
        if (-not [string]::IsNullOrWhiteSpace($relativePath)) {
            [void]$excluded.Add(
                $relativePath.Trim().Replace('/', '\'))
        }
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($rootFullPath)
    $manifestLines = @()
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "Runtime execution tree contains a reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $pendingDirectories.Push($item.FullName)
                continue
            }

            $fullPath = [IO.Path]::GetFullPath($item.FullName)
            if (-not $fullPath.StartsWith(
                    $rootPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Runtime execution file escaped its root: $fullPath"
            }
            $relativePath =
                $fullPath.Substring($rootPrefix.Length).Replace('/', '\')
            if ($excluded.Contains($relativePath)) {
                continue
            }

            $before = Get-Item -LiteralPath $fullPath -Force
            $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
            $after = Get-Item -LiteralPath $fullPath -Force
            if (
                $before.Length -ne $after.Length -or
                $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc
            ) {
                throw "Runtime execution file changed while hashing: $fullPath"
            }
            $manifestLines += (
                '{0}|{1}|{2}' -f
                    $relativePath,
                    [long]$after.Length,
                    $hash)
        }
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes(
        (($manifestLines | Sort-Object) -join [Environment]::NewLine))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return (
            [BitConverter]::ToString($sha256.ComputeHash($bytes)).
                Replace('-', ''))
    }
    finally {
        $sha256.Dispose()
    }
}

function Enter-RuntimeCertificationLease {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            return [IO.File]::Open(
                $Path,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw
            }
            Start-Sleep -Milliseconds 50
        }
    } while ($true)
}

function Assert-RuntimeCertification {
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][string]$SelfHashKey
    )

    $runtimeInvalidMarkerPath =
        Join-Path $PSScriptRoot '.georaeplan-runtime-invalid'
    if (Test-Path -LiteralPath $runtimeInvalidMarkerPath) {
        throw (
            'This isolated V1 runtime is explicitly invalidated and ' +
            'must be prepared successfully before launch.')
    }

    $values =
        [Collections.Generic.Dictionary[string, string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $MarkerPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -le 0) {
            throw 'The runtime certification marker is malformed.'
        }
        $key = $line.Substring(0, $separatorIndex).Trim()
        $value = $line.Substring($separatorIndex + 1)
        if ($values.ContainsKey($key)) {
            throw "The runtime certification marker has a duplicate key: $key"
        }
        $values.Add($key, $value)
    }

    $requiredKeys = @(
        'runtime_ready',
        'runtime_state',
        'runtime_root',
        'runtime_physical_root',
        'certification_id',
        'certification_mode',
        'password_reset_count',
        'certified_at_utc',
        'managed_file_manifest_sha256',
        'isolated_app_database_sha256',
        'server_database_sha256',
        'app_executable_sha256',
        'server_dll_sha256',
        'app_execution_tree_sha256',
        'server_execution_tree_sha256',
        'set_api_script_sha256',
        'initial_appsettings_sha256',
        'android_package_state',
        'android_package_file_name',
        'android_package_sha256',
        'android_package_metadata_sha256',
        $SelfHashKey
    )
    foreach ($requiredKey in $requiredKeys) {
        if (
            -not $values.ContainsKey($requiredKey) -or
            [string]::IsNullOrWhiteSpace($values[$requiredKey])
        ) {
            throw "The runtime certification marker is missing: $requiredKey"
        }
    }

    $trimChars = [char[]]@('\', '/')
    $currentRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd($trimChars)
    $certifiedRoot = [IO.Path]::GetFullPath(
        $values['runtime_root']).TrimEnd($trimChars)
    $currentPhysicalRoot = (
        Get-RuntimePhysicalDirectoryPath -Path $PSScriptRoot
    ).TrimEnd($trimChars)
    $certifiedPhysicalRoot = [IO.Path]::GetFullPath(
        $values['runtime_physical_root']).TrimEnd($trimChars)
    $certifiedAt = [DateTimeOffset]::MinValue
    $resetCount = -1
    if (
        -not [string]::Equals(
            $values['runtime_ready'],
            'True',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $currentRoot,
            $certifiedRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $currentPhysicalRoot,
            $certifiedPhysicalRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetPathRoot($currentPhysicalRoot),
            'D:\',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $values['certification_id'],
            '__CERTIFICATION_ID__',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $values['certification_mode'],
            '__CERTIFICATION_MODE__',
            [StringComparison]::Ordinal) -or
        -not [int]::TryParse(
            $values['password_reset_count'],
            [ref]$resetCount) -or
        $resetCount -ne __PASSWORD_RESET_COUNT__ -or
        -not [DateTimeOffset]::TryParse(
            $values['certified_at_utc'],
            [ref]$certifiedAt)
    ) {
        throw 'The runtime certification marker does not match this runtime.'
    }
    Assert-RuntimeWritablePathsAreDirectD `
        -RuntimePhysicalRoot $currentPhysicalRoot `
        -MarkerPath $MarkerPath

    $androidPackageState = $values['android_package_state']
    $androidMobileRoot = Join-Path $PSScriptRoot 'Mobile'
    $androidMetadataPath =
        Join-Path $androidMobileRoot 'android-package.metadata.json'
    if ([string]::Equals(
        $androidPackageState,
        'present',
        [StringComparison]::Ordinal)
    ) {
        $androidFileName = $values['android_package_file_name']
        if (
            [string]::IsNullOrWhiteSpace($androidFileName) -or
            -not [string]::Equals(
                [IO.Path]::GetFileName($androidFileName),
                $androidFileName,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [IO.Path]::GetExtension($androidFileName),
                '.apk',
                [StringComparison]::OrdinalIgnoreCase) -or
            $values['android_package_sha256'] -notmatch
                '^[0-9A-Fa-f]{64}$' -or
            $values['android_package_metadata_sha256'] -notmatch
                '^[0-9A-Fa-f]{64}$'
        ) {
            throw 'The certified Android package identity is malformed.'
        }
        $androidPackagePath = Join-Path $androidMobileRoot $androidFileName
        if (
            -not (Test-Path -LiteralPath $androidPackagePath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $androidMetadataPath -PathType Leaf)
        ) {
            throw 'A certified Android package artifact is missing.'
        }
        $actualAndroidPackageHash = (
            Get-FileHash `
                -LiteralPath $androidPackagePath `
                -Algorithm SHA256
        ).Hash
        $actualAndroidMetadataHash = (
            Get-FileHash `
                -LiteralPath $androidMetadataPath `
                -Algorithm SHA256
        ).Hash
        if (
            -not [string]::Equals(
                $actualAndroidPackageHash,
                $values['android_package_sha256'],
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                $actualAndroidMetadataHash,
                $values['android_package_metadata_sha256'],
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'A certified Android package artifact has changed.'
        }
    }
    elseif ([string]::Equals(
        $androidPackageState,
        'absent',
        [StringComparison]::Ordinal)
    ) {
        if (
            -not [string]::Equals(
                $values['android_package_file_name'],
                'none',
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $values['android_package_sha256'],
                'none',
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $values['android_package_metadata_sha256'],
                'none',
                [StringComparison]::Ordinal) -or
            (Test-Path -LiteralPath $androidMetadataPath -PathType Leaf)
        ) {
            throw 'The certified absent Android package state is inconsistent.'
        }
        if (Test-Path -LiteralPath $androidMobileRoot -PathType Container) {
            $uncertifiedAndroidPackages = @(
                Get-ChildItem `
                    -LiteralPath $androidMobileRoot `
                    -File `
                    -Filter '*.apk' `
                    -ErrorAction Stop
            )
            if ($uncertifiedAndroidPackages.Count -gt 0) {
                throw 'An uncertified Android package was added to the runtime.'
            }
        }
    }
    else {
        throw 'The runtime certification marker has an invalid Android state.'
    }

    $appExecutionRoot = Join-Path $PSScriptRoot 'App'
    $serverExecutionRoot = Join-Path $PSScriptRoot 'Server'
    $appExecutables = @(
        Get-ChildItem `
            -LiteralPath $appExecutionRoot `
            -Filter '*.Desktop.App.exe' `
            -File
    )
    $serverDlls = @(
        Get-ChildItem `
            -LiteralPath $serverExecutionRoot `
            -Filter '*.Server.Api.dll' `
            -File
    )
    if ($appExecutables.Count -ne 1 -or $serverDlls.Count -ne 1) {
        throw 'The certified runtime artifact set is not exact.'
    }

    $actualAppHash =
        (Get-FileHash `
            -LiteralPath $appExecutables[0].FullName `
            -Algorithm SHA256).Hash
    $actualServerHash =
        (Get-FileHash `
            -LiteralPath $serverDlls[0].FullName `
            -Algorithm SHA256).Hash
    $actualAppExecutionTreeHash =
        Get-RuntimeExecutionTreeManifestDigest `
            -Root $appExecutionRoot `
            -ExcludedRelativePaths @('appsettings.json')
    $actualServerExecutionTreeHash =
        Get-RuntimeExecutionTreeManifestDigest `
            -Root $serverExecutionRoot `
            -ExcludedRelativePaths @(
                '거래플랜-local.db',
                '거래플랜-local.db-shm',
                '거래플랜-local.db-wal',
                '거래플랜-local.db-journal'
            )
    $actualSelfHash =
        (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $setApiScriptPath = Join-Path $PSScriptRoot 'Set-ApiBaseUrl.ps1'
    $appSettingsPath = Join-Path $PSScriptRoot 'App\appsettings.json'
    $actualSetApiScriptHash =
        (Get-FileHash `
            -LiteralPath $setApiScriptPath `
            -Algorithm SHA256).Hash
    if (
        -not [string]::Equals(
            $actualAppHash,
            $values['app_executable_sha256'],
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $actualServerHash,
            $values['server_dll_sha256'],
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $actualAppExecutionTreeHash,
            $values['app_execution_tree_sha256'],
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $actualServerExecutionTreeHash,
            $values['server_execution_tree_sha256'],
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $actualSelfHash,
            $values[$SelfHashKey],
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $actualSetApiScriptHash,
            $values['set_api_script_sha256'],
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'A certified runtime artifact has changed.'
    }

    $runtimeState = $values['runtime_state']
    if ([string]::Equals(
            $runtimeState,
            'pristine',
            [StringComparison]::Ordinal)) {
        $appDataRoot = Join-Path $PSScriptRoot 'AppData'
        $appDatabasePath =
            Join-Path $appDataRoot 'data\거래플랜.db'
        $serverDatabasePath =
            Join-Path $PSScriptRoot 'Server\거래플랜-local.db'
        $actualInitialAppSettingsHash = (
            Get-FileHash `
                -LiteralPath $appSettingsPath `
                -Algorithm SHA256).Hash
        foreach ($databasePath in @(
            $appDatabasePath,
            $serverDatabasePath
        )) {
            if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
                throw "A certified runtime database is missing: $databasePath"
            }
            $databaseSidecars = @(
                "$databasePath-wal",
                "$databasePath-shm",
                "$databasePath-journal"
            ) | Where-Object { Test-Path -LiteralPath $_ }
            if ($databaseSidecars.Count -gt 0) {
                throw (
                    'Pristine runtime data contains an uncertified SQLite ' +
                    "sidecar: $($databaseSidecars -join ', ')")
            }
        }

        $actualManagedDigest =
            Get-RuntimeManagedFileManifestDigest -Root $appDataRoot
        $actualAppDatabaseHash = (
            Get-FileHash `
                -LiteralPath $appDatabasePath `
                -Algorithm SHA256).Hash
        $actualServerDatabaseHash = (
            Get-FileHash `
                -LiteralPath $serverDatabasePath `
                -Algorithm SHA256).Hash
        if (
            -not [string]::Equals(
                $actualManagedDigest,
                $values['managed_file_manifest_sha256'],
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                $actualAppDatabaseHash,
                $values['isolated_app_database_sha256'],
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                $actualServerDatabaseHash,
                $values['server_database_sha256'],
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                $actualInitialAppSettingsHash,
                $values['initial_appsettings_sha256'],
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Pristine runtime data has changed since certification.'
        }

        $values['runtime_state'] = 'mutable'
        $values['mutable_since_utc'] =
            [DateTimeOffset]::UtcNow.ToString('O')
        $markerTempPath = Join-Path $PSScriptRoot (
            '.georaeplan-runtime-ready.' +
            [Guid]::NewGuid().ToString('N') +
            '.tmp')
        $markerBackupPath = Join-Path $PSScriptRoot (
            '.georaeplan-runtime-ready.' +
            [Guid]::NewGuid().ToString('N') +
            '.bak')
        try {
            $markerLines = @(
                $values.Keys |
                    Sort-Object |
                    ForEach-Object {
                        "$_=$($values[$_])"
                    }
            )
            [IO.File]::WriteAllLines(
                $markerTempPath,
                $markerLines,
                [Text.UTF8Encoding]::new($true))
            [IO.File]::Replace(
                $markerTempPath,
                $MarkerPath,
                $markerBackupPath)
        }
        finally {
            if (Test-Path -LiteralPath $markerTempPath) {
                Remove-Item `
                    -LiteralPath $markerTempPath `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $markerBackupPath) {
                Remove-Item `
                    -LiteralPath $markerBackupPath `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
        }
    }
    elseif (-not [string]::Equals(
            $runtimeState,
            'mutable',
            [StringComparison]::Ordinal)) {
        throw 'The runtime certification state is invalid.'
    }
}

function Invoke-HiddenSetApiBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string[]]$AppSettingsPaths
    )

    $toPowerShellLiteral = {
        param([Parameter(Mandatory = $true)][string]$Value)

        return "'" + $Value.Replace("'", "''") + "'"
    }
    $scriptLiteral =
        & $toPowerShellLiteral ([IO.Path]::GetFullPath($ScriptPath))
    $baseUrlLiteral = & $toPowerShellLiteral $BaseUrl
    $appSettingsLiterals = @(
        $AppSettingsPaths |
            ForEach-Object {
                & $toPowerShellLiteral ([IO.Path]::GetFullPath($_))
            }
    )
    if ($appSettingsLiterals.Count -eq 0) {
        throw 'At least one appsettings path is required.'
    }

    $command = (
        '$ProgressPreference = ''SilentlyContinue''; ' +
        '$ErrorActionPreference = ''Stop''; & ' +
        $scriptLiteral +
        ' -BaseUrl ' +
        $baseUrlLiteral +
        ' -AppSettingsPaths @(' +
        ($appSettingsLiterals -join ',') +
        ') 3>&1 4>&1 5>&1 6>&1'
    )
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($command))
    $windowsPowerShellPath = Join-Path `
        ([Environment]::SystemDirectory) `
        'WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShellPath -PathType Leaf)) {
        throw 'The absolute Windows PowerShell path was not found.'
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $windowsPowerShellPath
    $startInfo.Arguments = (
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
        "-EncodedCommand $encodedCommand"
    )
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The hidden Set-ApiBaseUrl process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdout
            StandardError = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

function Set-AndVerify-IsolatedApiBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$MarkerPath
    )

    $normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($normalizedBaseUrl)) {
        throw 'The isolated API base URL is empty.'
    }

    $appSettingsPath = Join-Path $PSScriptRoot 'App\appsettings.json'
    $setApiScriptPath = Join-Path $PSScriptRoot 'Set-ApiBaseUrl.ps1'
    foreach ($requiredFile in @($appSettingsPath, $setApiScriptPath)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "A required isolated API configuration file is missing: $requiredFile"
        }
    }

    $runtimePhysicalRoot = Get-RuntimePhysicalDirectoryPath -Path $PSScriptRoot
    Assert-RuntimeWritablePathsAreDirectD `
        -RuntimePhysicalRoot $runtimePhysicalRoot `
        -MarkerPath $MarkerPath

    $setApiResult = Invoke-HiddenSetApiBaseUrl `
        -ScriptPath $setApiScriptPath `
        -BaseUrl $normalizedBaseUrl `
        -AppSettingsPaths @($appSettingsPath)
    $setApiStandardErrorPresent =
        -not [string]::IsNullOrWhiteSpace($setApiResult.StandardError)
    if (
        $setApiResult.ExitCode -ne 0 -or
        $setApiStandardErrorPresent
    ) {
        throw (
            'Failed to update the isolated app API base URL. ' +
            "exitCode=$($setApiResult.ExitCode) " +
            "stderrPresent=$setApiStandardErrorPresent")
    }

    $settings = Get-Content -LiteralPath $appSettingsPath -Raw |
        ConvertFrom-Json
    $actualBaseUrl = [string]$settings.Api.BaseUrl
    if (-not [string]::Equals(
            $actualBaseUrl,
            $normalizedBaseUrl,
            [StringComparison]::Ordinal)) {
        throw (
            'The isolated app API base URL does not match the selected ' +
            "server URL. expected=$normalizedBaseUrl actual=$actualBaseUrl")
    }
}
'@

    $runComponentPsContent = @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('App', 'Server')]
    [string]$Mode__COMPONENT_LOCK_PROBE_PARAMETER__
)

$ErrorActionPreference = 'Stop'
if ($Mode -eq 'App' -and -not __ALLOW_STANDALONE_APP__) {
    throw (
        'Standalone App launch is disabled for isolation safety. ' +
        'Use Run-All.cmd.')
}
$readyMarkerPath = Join-Path $PSScriptRoot '.georaeplan-runtime-ready'
$invalidMarkerPath = Join-Path $PSScriptRoot '.georaeplan-runtime-invalid'
$leasePath = Join-Path $PSScriptRoot '.georaeplan-prepare.lock'
$preparationGateLeasePath =
    Join-Path $PSScriptRoot '.georaeplan-prepare-gate.lock'
$certificationLeasePath =
    Join-Path $PSScriptRoot '.georaeplan-certification.lock'
$componentLeaseName = if ($Mode -eq 'App') {
    '.georaeplan-runtime-app.lock'
}
else {
    '.georaeplan-runtime-server.lock'
}
$componentLeasePath =
    Join-Path $PSScriptRoot $componentLeaseName
$runtimeLease = $null
$startupGateLease = $null
$componentLease = $null
$certificationLease = $null

__CERTIFICATION_VALIDATOR__

function Get-FreePort {
    param([int]$StartingPort = 19080)

    $port = $StartingPort
    while ($true) {
        $listener = $null
        try {
            $listener = [Net.Sockets.TcpListener]::new(
                [Net.IPAddress]::Loopback,
                $port)
            $listener.Start()
            return $port
        }
        catch {
            $port++
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }
}

try {
    try {
        $startupGateLease = [IO.File]::Open(
            $preparationGateLeasePath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
    }
    catch {
        throw 'Preparation is already starting for this isolated runtime root.'
    }
    if (Test-Path -LiteralPath $invalidMarkerPath) {
        throw (
            'This isolated V1 runtime is explicitly invalidated and ' +
            'must be prepared successfully before launch.')
    }
    if (-not (Test-Path -LiteralPath $readyMarkerPath -PathType Leaf)) {
        throw 'This isolated V1 runtime is not certified ready.'
    }
    $startupGateLease.Dispose()
    $startupGateLease = $null
    Initialize-IsolatedRuntimeTempEnvironment | Out-Null

    try {
        $runtimeLease = [IO.File]::Open(
            $leasePath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
    }
    catch {
        throw 'Preparation is already using this isolated runtime root.'
    }

    if (Test-Path -LiteralPath $invalidMarkerPath) {
        throw (
            'This isolated V1 runtime was invalidated while acquiring ' +
            'its preparation lease.')
    }

    try {
        $componentLease = [IO.File]::Open(
            $componentLeasePath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch {
        throw "Another isolated $Mode component is already running."
    }

    if (-not (Test-Path -LiteralPath $readyMarkerPath -PathType Leaf)) {
        throw 'The isolated V1 runtime readiness marker changed while acquiring its lease.'
    }
__COMPONENT_LOCK_ONLY_PROBE_BLOCK__
    $certificationLease =
        Enter-RuntimeCertificationLease `
            -Path $certificationLeasePath
    try {
        Assert-RuntimeCertification `
            -MarkerPath $readyMarkerPath `
            -SelfHashKey 'component_script_sha256'
    }
    finally {
        $certificationLease.Dispose()
        $certificationLease = $null
    }
    if ($Mode -eq 'App') {
        $appRoot = Join-Path $PSScriptRoot 'AppData'
        $appDir = Join-Path $PSScriptRoot 'App'
        $appExecutables = @(
            Get-ChildItem -LiteralPath $appDir -Filter '*.Desktop.App.exe' -File
        )
        if ($appExecutables.Count -ne 1) {
            throw "Expected exactly one desktop executable in $appDir."
        }

        $env:GEORAEPLAN_APP_ROOT = $appRoot
        $env:GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
        $env:GEORAEPLAN_TEST_MODE = '1'
        & $appExecutables[0].FullName
        exit $LASTEXITCODE
    }

    $dotnetExe = '__DOTNET_EXE__'
    $serverDir = Join-Path $PSScriptRoot 'Server'
    $serverDlls = @(
        Get-ChildItem -LiteralPath $serverDir -Filter '*.Server.Api.dll' -File
    )
    if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) {
        throw "dotnet not found: $dotnetExe"
    }
    if ($serverDlls.Count -ne 1) {
        throw "Expected exactly one server DLL in $serverDir."
    }

    $serverDataRoot = Join-Path $PSScriptRoot 'ServerData'
    $serverUrl = "http://127.0.0.1:$(Get-FreePort -StartingPort 19080)"
    $certificationLease =
        Enter-RuntimeCertificationLease `
            -Path $certificationLeasePath
    try {
        Set-AndVerify-IsolatedApiBaseUrl `
            -BaseUrl $serverUrl `
            -MarkerPath $readyMarkerPath
    }
    finally {
        $certificationLease.Dispose()
        $certificationLease = $null
    }

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:Kestrel__Endpoints__Http__Url = $serverUrl
    $env:ERP_DB_FALLBACK_SQLITE = '1'
    $env:SeedUsers__EnableSeedUsers = 'false'
    $env:SeedUsers__AdminPassword = '1234'
    $env:SeedUsers__UserPassword = '1234'
    $env:SeedUsers__ItwPassword = '1234'
    $env:SeedUsers__UsenetUsername = 'usenet'
    $env:SeedUsers__UsenetPassword = '1234'
    $env:SeedUsers__UpdateExistingUsenetPassword = 'false'
    $env:Logging__LogLevel__Default = 'Warning'
    $env:Logging__LogLevel__Microsoft = 'Warning'
    [Environment]::SetEnvironmentVariable(
        'Logging__LogLevel__Microsoft.EntityFrameworkCore',
        'Warning',
        'Process')
    $env:FileStorage__RootPath = Join-Path $serverDataRoot 'FileStore'
    $env:Updates__StorageRoot = Join-Path $serverDataRoot 'updates'

    Push-Location $serverDir
    try {
        Write-Host "[GeoraePlan] Starting isolated test server on $serverUrl"
        & $dotnetExe $serverDlls[0].FullName --environment Development
        exit $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
finally {
    if ($null -ne $startupGateLease) {
        $startupGateLease.Dispose()
    }
    if ($null -ne $certificationLease) {
        $certificationLease.Dispose()
    }
    if ($null -ne $componentLease) {
        $componentLease.Dispose()
    }
    if ($null -ne $runtimeLease) {
        $runtimeLease.Dispose()
    }
}
'@

    $runAllPsContent = @'
[CmdletBinding()]
param(__RUN_ALL_LOCK_PROBE_PARAMETER__)

$ErrorActionPreference = 'Stop'

function Publish-TestFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$TemporaryPath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $targetDirectory = Split-Path -Parent $TargetPath
    $backupPath = Join-Path $targetDirectory (
        ".$([IO.Path]::GetFileName($TargetPath))." +
        [Guid]::NewGuid().ToString('N') +
        '.bak')
    if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
        try {
            [IO.File]::Replace(
                $TemporaryPath,
                $TargetPath,
                $backupPath,
                $true)
            Remove-Item `
                -LiteralPath $backupPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
        catch {
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                Remove-Item `
                    -LiteralPath $backupPath `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
            throw
        }
    }
    else {
        [IO.File]::Move($TemporaryPath, $TargetPath)
    }
}

function Get-FreePort {
    param([int]$StartingPort = 19080)

    $port = $StartingPort
    while ($true) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        }
        catch {
            $port++
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }
}

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$TimeoutSeconds = 40,
        [Diagnostics.Process]$ServerProcess,
        [string]$LogRoot = '',
        [string[]]$LogPaths = @(),
        [ValidateRange(1048576, 1073741824)]
        [long]$MaximumLogBytesPerFile = 67108864
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($null -ne $ServerProcess -and $ServerProcess.HasExited) {
            return $false
        }
        if (
            -not [string]::IsNullOrWhiteSpace($LogRoot) -and
            $LogPaths.Count -gt 0
        ) {
            Assert-RuntimeServerLogsWithinLimit `
                -LogRoot $LogRoot `
                -Paths $LogPaths `
                -MaximumBytesPerFile $MaximumLogBytesPerFile
        }

        try {
            $response = Invoke-WebRequest `
                -Uri $Url `
                -Method Get `
                -UseBasicParsing `
                -TimeoutSec 1
            $healthPayload = $response.Content | ConvertFrom-Json
            if (
                [int]$response.StatusCode -eq 200 -and
                (
                    [string]::Equals(
                        [string]$healthPayload.status,
                        'ok',
                        [StringComparison]::OrdinalIgnoreCase) -or
                    [string]::Equals(
                        [string]$healthPayload.status,
                        'ready',
                        [StringComparison]::OrdinalIgnoreCase)
                )
            ) {
                return $true
            }
        }
        catch {
        }

        if ($null -ne $ServerProcess -and $ServerProcess.HasExited) {
            return $false
        }
        if (
            -not [string]::IsNullOrWhiteSpace($LogRoot) -and
            $LogPaths.Count -gt 0
        ) {
            Assert-RuntimeServerLogsWithinLimit `
                -LogRoot $LogRoot `
                -Paths $LogPaths `
                -MaximumBytesPerFile $MaximumLogBytesPerFile
        }
        Start-Sleep -Milliseconds 100
    }

    return $false
}

function Repair-ProcessPathEnvironmentForChildProcess {
    $pathValue = [Environment]::GetEnvironmentVariable('Path', 'Process')
    if ([string]::IsNullOrWhiteSpace($pathValue)) {
        $pathValue = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    }

    if (-not [string]::IsNullOrWhiteSpace($pathValue)) {
        [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
        [Environment]::SetEnvironmentVariable('Path', $pathValue, 'Process')
    }
}

function New-LocalTestPassword {
    return '1234'
}

function Assert-SafeRuntimeLogFilePath {
    param(
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $normalizedLogRoot = [IO.Path]::GetFullPath($LogRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $normalizedPath = [IO.Path]::GetFullPath($Path)
    $parentPath = [IO.Path]::GetDirectoryName($normalizedPath)
    if (-not [string]::Equals(
            $parentPath,
            $normalizedLogRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime log path escaped its certified log root: $normalizedPath"
    }

    Assert-RuntimeRootHasNoReparsePoint -Path $normalizedLogRoot
    if (-not (Test-Path -LiteralPath $normalizedPath)) {
        return
    }

    $item = Get-Item -LiteralPath $normalizedPath -Force
    if ($item.PSIsContainer) {
        throw "Runtime log path is a directory: $normalizedPath"
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Runtime log file is a reparse point: $normalizedPath"
    }
    if (
        [GeoraePlan.Runtime.FinalPathNativeMethods]::GetLinkCount(
            $normalizedPath) -ne 1
    ) {
        throw "Runtime log file has multiple hard links: $normalizedPath"
    }
}

function Reset-RuntimeLogFile {
    param(
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Content = ''
    )

    Assert-SafeRuntimeLogFilePath -LogRoot $LogRoot -Path $Path
    [IO.File]::WriteAllText(
        $Path,
        $Content,
        [Text.UTF8Encoding]::new($false))
    Assert-SafeRuntimeLogFilePath -LogRoot $LogRoot -Path $Path
}

function Move-RuntimeLogToPrevious {
    param(
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PreviousPath
    )

    Assert-SafeRuntimeLogFilePath -LogRoot $LogRoot -Path $Path
    Assert-SafeRuntimeLogFilePath -LogRoot $LogRoot -Path $PreviousPath
    if (Test-Path -LiteralPath $PreviousPath) {
        Remove-Item -LiteralPath $PreviousPath -Force
    }
    if (Test-Path -LiteralPath $Path) {
        Move-Item -LiteralPath $Path -Destination $PreviousPath
    }
}

function Initialize-RuntimeHealthObservationLog {
    param(
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PreviousPath
    )

    Move-RuntimeLogToPrevious `
        -LogRoot $LogRoot `
        -Path $Path `
        -PreviousPath $PreviousPath
    Reset-RuntimeLogFile `
        -LogRoot $LogRoot `
        -Path $Path `
        -Content (
            'Sequence,ObservedAtUtc,ServerPid,ServerExited,ExitCode,' +
            'HealthOk,HttpStatus,ElapsedMs,ConsecutiveFailures' +
            [Environment]::NewLine)
    $script:healthObservationCount = 0
    $script:healthObservationSequence = 0L
}

function Write-RuntimeHealthObservation {
    param(
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PreviousPath,
        [Parameter(Mandatory = $true)][int]$ServerPid,
        [Parameter(Mandatory = $true)][bool]$ServerExited,
        [string]$ExitCode = '',
        [Parameter(Mandatory = $true)][bool]$HealthOk,
        [string]$HttpStatus = '',
        [Parameter(Mandatory = $true)][long]$ElapsedMilliseconds,
        [Parameter(Mandatory = $true)][int]$ConsecutiveFailures,
        [ValidateRange(1, 100000)]
        [int]$MaximumSamplesPerFile = 1440
    )

    if ($script:healthObservationCount -ge $MaximumSamplesPerFile) {
        Move-RuntimeLogToPrevious `
            -LogRoot $LogRoot `
            -Path $Path `
            -PreviousPath $PreviousPath
        Reset-RuntimeLogFile `
            -LogRoot $LogRoot `
            -Path $Path `
            -Content (
                'Sequence,ObservedAtUtc,ServerPid,ServerExited,ExitCode,' +
                'HealthOk,HttpStatus,ElapsedMs,ConsecutiveFailures' +
                [Environment]::NewLine)
        $script:healthObservationCount = 0
        Write-Log (
            'Runtime health observation log rotated after ' +
            "$MaximumSamplesPerFile samples.")
    }

    Assert-SafeRuntimeLogFilePath -LogRoot $LogRoot -Path $Path
    $script:healthObservationSequence++
    $line = @(
        [string]$script:healthObservationSequence,
        [DateTimeOffset]::UtcNow.ToString('o'),
        [string]$ServerPid,
        [string]$ServerExited,
        [string]$ExitCode,
        [string]$HealthOk,
        [string]$HttpStatus,
        [string]$ElapsedMilliseconds,
        [string]$ConsecutiveFailures
    ) -join ','
    [IO.File]::AppendAllText(
        $Path,
        $line + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $script:healthObservationCount++
}

function Invoke-RuntimeHealthProbe {
    param([Parameter(Mandatory = $true)][string]$Url)

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $healthOk = $false
    $httpStatus = ''
    try {
        $response = Invoke-WebRequest `
            -Uri $Url `
            -Method Get `
            -UseBasicParsing `
            -TimeoutSec 1
        $httpStatus = [string][int]$response.StatusCode
        $payload = $response.Content | ConvertFrom-Json
        $healthOk =
            [int]$response.StatusCode -eq 200 -and
            [string]::Equals(
                [string]$payload.status,
                'ok',
                [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        if ($null -ne $_.Exception.Response) {
            try {
                $httpStatus =
                    [string][int]$_.Exception.Response.StatusCode
            }
            catch {
            }
        }
    }
    finally {
        $stopwatch.Stop()
    }

    return [pscustomobject]@{
        HealthOk = $healthOk
        HttpStatus = $httpStatus
        ElapsedMilliseconds = [long]$stopwatch.ElapsedMilliseconds
    }
}

function Remove-OldRuntimeServerLogs {
    param(
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [ValidateRange(2, 200)][int]$MaximumFileCount = 40,
        [ValidateRange(1048576, 2147483647)]
        [long]$MaximumTotalBytes = 134217728
    )

    $serverLogPattern =
        '^server-\d{8}T\d{9}Z-[0-9a-f]{32}-a\d{2}\.' +
        '(stdout|stderr)\.log$'
    $retainedCount = 0
    $retainedBytes = 0L
    $candidates = @(
        Get-ChildItem -LiteralPath $LogRoot -File -Force |
            Where-Object { $_.Name -match $serverLogPattern } |
            Sort-Object LastWriteTimeUtc -Descending
    )
    foreach ($candidate in $candidates) {
        Assert-SafeRuntimeLogFilePath `
            -LogRoot $LogRoot `
            -Path $candidate.FullName
        $canRetain =
            $retainedCount -lt $MaximumFileCount -and
            ($retainedBytes + [long]$candidate.Length) -le
                $MaximumTotalBytes
        if ($canRetain) {
            $retainedCount++
            $retainedBytes += [long]$candidate.Length
            continue
        }

        Remove-Item -LiteralPath $candidate.FullName -Force
    }
}

function Assert-RuntimeServerLogsWithinLimit {
    param(
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [Parameter(Mandatory = $true)][string[]]$Paths,
        [ValidateRange(1048576, 1073741824)]
        [long]$MaximumBytesPerFile = 67108864,
        [ValidateRange(2097152, 2147483647)]
        [long]$MaximumTotalBytes = 268435456
    )

    foreach ($path in $Paths) {
        Assert-SafeRuntimeLogFilePath -LogRoot $LogRoot -Path $path
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }
        $length = [long](Get-Item -LiteralPath $path -Force).Length
        if ($length -gt $MaximumBytesPerFile) {
            throw (
                'Runtime server log exceeded its safety limit. ' +
                "path=$path bytes=$length limit=$MaximumBytesPerFile")
        }
    }

    $serverLogPattern =
        '^server-\d{8}T\d{9}Z-[0-9a-f]{32}-a\d{2}\.' +
        '(stdout|stderr)\.log$'
    $totalBytes = 0L
    foreach (
        $serverLog in
            Get-ChildItem -LiteralPath $LogRoot -File -Force |
                Where-Object { $_.Name -match $serverLogPattern }
    ) {
        Assert-SafeRuntimeLogFilePath `
            -LogRoot $LogRoot `
            -Path $serverLog.FullName
        $totalBytes += [long]$serverLog.Length
    }
    if ($totalBytes -gt $MaximumTotalBytes) {
        throw (
            'Runtime server logs exceeded their total safety limit. ' +
            "bytes=$totalBytes limit=$MaximumTotalBytes")
    }
}

function Stop-AndDisposeRuntimeProcess {
    param(
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            try {
                $Process.Kill()
            }
            catch [InvalidOperationException] {
                if (-not $Process.HasExited) {
                    throw
                }
            }
        }
        if (-not $Process.WaitForExit(5000)) {
            throw "$Description did not exit within five seconds."
        }
        $Process.WaitForExit()
    }
    finally {
        $Process.Dispose()
    }
}

function Stop-RuntimeAppAfterServerFailure {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process -or $Process.HasExited) {
        return
    }

    $closeRequested = $false
    try {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            $closeRequested = $Process.CloseMainWindow()
        }
    }
    catch {
    }

    if ($closeRequested -and $Process.WaitForExit(10000)) {
        $Process.WaitForExit()
        return
    }

    try {
        $Process.Kill()
    }
    catch [InvalidOperationException] {
        if (-not $Process.HasExited) {
            throw
        }
    }
    if (-not $Process.WaitForExit(5000)) {
        throw 'The desktop app did not exit after the test server failed.'
    }
    $Process.WaitForExit()
}

function Start-HiddenServerProcess {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$ServerDir,
        [Parameter(Mandatory = $true)][string]$ServerDll,
        [Parameter(Mandatory = $true)][string]$ServerUrl,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot,
        [Parameter(Mandatory = $true)][string]$AdminPassword,
        [Parameter(Mandatory = $true)][string]$UsenetPassword,
        [Parameter(Mandatory = $true)][string]$StdoutLogPath,
        [Parameter(Mandatory = $true)][string]$StderrLogPath
    )

    $serverEnv = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DOTNET_ENVIRONMENT' = 'Development'
        'Kestrel__Endpoints__Http__Url' = $ServerUrl
        'ERP_DB_FALLBACK_SQLITE' = '1'
        'SeedUsers__EnableSeedUsers' = 'false'
        'SeedUsers__AdminPassword' = $AdminPassword
        'SeedUsers__UserPassword' = (New-LocalTestPassword)
        'SeedUsers__ItwPassword' = (New-LocalTestPassword)
        'SeedUsers__UsenetUsername' = 'usenet'
        'SeedUsers__UsenetPassword' = $UsenetPassword
        'SeedUsers__UpdateExistingUsenetPassword' = 'false'
        'Logging__LogLevel__Default' = 'Warning'
        'Logging__LogLevel__Microsoft' = 'Warning'
        'Logging__LogLevel__Microsoft.AspNetCore' = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore' = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command' = 'Warning'
        'FileStorage__RootPath' = (Join-Path $ServerDataRoot 'FileStore')
        'Updates__StorageRoot' = (Join-Path $ServerDataRoot 'updates')
    }

    $previousEnv = @{}
    foreach ($key in $serverEnv.Keys) {
        $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$serverEnv[$key], 'Process')
    }

    try {
        Repair-ProcessPathEnvironmentForChildProcess
        $argumentList = ('"{0}" --environment Development' -f $ServerDll.Replace('"', '""'))
        return Start-Process `
            -FilePath $DotnetExe `
            -ArgumentList $argumentList `
            -WorkingDirectory $ServerDir `
            -RedirectStandardOutput $StdoutLogPath `
            -RedirectStandardError $StderrLogPath `
            -WindowStyle Hidden `
            -PassThru
    }
    finally {
        foreach ($key in $serverEnv.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnv[$key], 'Process')
        }
    }
}

function Invoke-IsolatedServerSqliteFinalizer {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$ServerDll,
        [Parameter(Mandatory = $true)][string]$ServerDir,
        [Parameter(Mandatory = $true)][string]$LogRoot,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)]$ProcessJob
    )

    Assert-SafeRuntimeLogFilePath -LogRoot $LogRoot -Path $LogPath
    Reset-RuntimeLogFile -LogRoot $LogRoot -Path $LogPath
    $databasePath = Join-Path $ServerDir '거래플랜-local.db'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $DotnetExe
    $startInfo.Arguments = (
        '"{0}" --finalize-isolated-test-sqlite "{1}"' -f
            $ServerDll.Replace('"', '\"'),
            $databasePath.Replace('"', '\"'))
    $startInfo.WorkingDirectory = $ServerDir
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['GEORAEPLAN_TEST_MODE'] = '1'
    $startInfo.EnvironmentVariables['GEORAEPLAN_TEST_SEED_MODE'] = '1'
    $startInfo.EnvironmentVariables['GEORAEPLAN_TEST_SERVER_ROOT'] =
        $ServerDir

    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw 'The certified server SQLite finalizer did not start.'
        }
        try {
            $ProcessJob.AssignProcess($process)
        }
        catch {
            $assignmentFailure = $_.Exception
            $cleanupFailure = $null
            try {
                if (-not $process.HasExited) {
                    Stop-Process `
                        -Id $process.Id `
                        -Force `
                        -ErrorAction Stop
                }
                if (-not $process.WaitForExit(5000)) {
                    throw (
                        'The uncontained server SQLite finalizer did not ' +
                        'exit within five seconds.')
                }
                $process.WaitForExit()
            }
            catch {
                $cleanupFailure = $_.Exception
            }

            if ($null -ne $cleanupFailure) {
                throw [InvalidOperationException]::new(
                    (
                        'The certified server SQLite finalizer job ' +
                        'assignment failed and process cleanup failed. ' +
                        "assignment=$($assignmentFailure.Message) " +
                        "cleanup=$($cleanupFailure.Message)"
                    ),
                    $assignmentFailure)
            }
            throw [InvalidOperationException]::new(
                (
                    'The certified server SQLite finalizer could not be ' +
                    'assigned to the launcher job.'
                ),
                $assignmentFailure)
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            if (-not $process.WaitForExit(5000)) {
                throw (
                    'The certified server SQLite finalizer timed out and ' +
                    'did not exit within five seconds after termination.')
            }
            $process.WaitForExit()
            throw 'The certified server SQLite finalizer timed out.'
        }
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
        [IO.File]::WriteAllText(
            $LogPath,
            (
                $stdout.TrimEnd() +
                [Environment]::NewLine +
                $stderr.TrimEnd() +
                [Environment]::NewLine),
            [Text.UTF8Encoding]::new($false))

        if ($exitCode -ne 0) {
            throw (
                'The certified server SQLite finalizer failed. ' +
                "exitCode=$exitCode log=$LogPath")
        }

        $expectedKeys = @(
            'server_sqlite_finalized',
            'checkpoint_busy',
            'checkpoint_log_frames',
            'checkpointed_frames',
            'journal_mode',
            'quick_check',
            'sidecar_count',
            'database_length',
            'database_sha256'
        )
        $values =
            [Collections.Generic.Dictionary[string,string]]::new(
                [StringComparer]::Ordinal)
        foreach (
            $line in
                $stdout.Split(
                    [char[]]@("`r", "`n"),
                    [StringSplitOptions]::RemoveEmptyEntries)
        ) {
            $match = [regex]::Match(
                $line,
                '^(?<key>[a-z][a-z0-9_]*)=(?<value>.*)$',
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)
            if (
                -not $match.Success -or
                $expectedKeys -notcontains $match.Groups['key'].Value -or
                $values.ContainsKey($match.Groups['key'].Value)
            ) {
                throw (
                    'The certified server SQLite finalizer returned ' +
                    "unexpected output. log=$LogPath")
            }
            $values.Add(
                $match.Groups['key'].Value,
                $match.Groups['value'].Value)
        }
        if ($values.Count -ne $expectedKeys.Count) {
            throw (
                'The certified server SQLite finalizer output is incomplete. ' +
                "log=$LogPath")
        }

        $checkpointBusy = 0
        $checkpointLogFrames = 0
        $checkpointedFrames = 0
        $sidecarCount = 0
        $databaseLength = 0L
        if (
            $values['server_sqlite_finalized'] -cne 'True' -or
            -not [int]::TryParse(
                $values['checkpoint_busy'],
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$checkpointBusy) -or
            -not [int]::TryParse(
                $values['checkpoint_log_frames'],
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$checkpointLogFrames) -or
            -not [int]::TryParse(
                $values['checkpointed_frames'],
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$checkpointedFrames) -or
            -not [int]::TryParse(
                $values['sidecar_count'],
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$sidecarCount) -or
            -not [long]::TryParse(
                $values['database_length'],
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$databaseLength) -or
            $checkpointBusy -ne 0 -or
            $checkpointLogFrames -ne $checkpointedFrames -or
            $sidecarCount -ne 0 -or
            $databaseLength -le 0 -or
            $values['journal_mode'] -cne 'delete' -or
            $values['quick_check'] -cne 'ok' -or
            $values['database_sha256'] -notmatch '^[0-9A-F]{64}$'
        ) {
            throw (
                'The certified server SQLite finalizer output failed ' +
                "strict validation. log=$LogPath")
        }

        return [pscustomobject]@{
            DatabaseLength = $databaseLength
            DatabaseSha256 = $values['database_sha256']
            SidecarCount = $sidecarCount
        }
    }
    catch {
        if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf) -or
            (Get-Item -LiteralPath $LogPath).Length -eq 0) {
            [IO.File]::WriteAllText(
                $LogPath,
                ("server_sqlite_finalize_error={0}{1}" -f
                    $_.Exception.Message.Replace("`r", ' ').Replace("`n", ' '),
                    [Environment]::NewLine),
                [Text.UTF8Encoding]::new($false))
        }
        throw
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}

$dotnetExe = '__DOTNET_EXE__'
$serverDir = Join-Path $PSScriptRoot 'Server'
$appDir = Join-Path $PSScriptRoot 'App'
$appRoot = Join-Path $PSScriptRoot 'AppData'
$serverDataRoot = Join-Path $PSScriptRoot 'ServerData'
$runtimeLogRoot = Join-Path $PSScriptRoot 'RuntimeLogs'
$healthObservationPath =
    Join-Path $runtimeLogRoot 'health-observation.csv'
$previousHealthObservationPath =
    Join-Path $runtimeLogRoot 'health-observation.previous.csv'
$errorLogPath = Join-Path $runtimeLogRoot 'Run-All-error.log'
$traceLogPath = Join-Path $runtimeLogRoot 'Run-All.log'
$serverSqliteFinalizationLogPath =
    Join-Path $runtimeLogRoot 'server-sqlite-finalize.log'
$readyMarkerPath = Join-Path $PSScriptRoot '.georaeplan-runtime-ready'
$invalidMarkerPath = Join-Path $PSScriptRoot '.georaeplan-runtime-invalid'
$leasePath = Join-Path $PSScriptRoot '.georaeplan-prepare.lock'
$preparationGateLeasePath =
    Join-Path $PSScriptRoot '.georaeplan-prepare-gate.lock'
$certificationLeasePath =
    Join-Path $PSScriptRoot '.georaeplan-certification.lock'
$appLeasePath = Join-Path $PSScriptRoot '.georaeplan-runtime-app.lock'
$serverLeasePath = Join-Path $PSScriptRoot '.georaeplan-runtime-server.lock'
$runtimeLease = $null
$startupGateLease = $null
$appLease = $null
$serverLease = $null
$certificationLease = $null
$childProcessJob = $null
$createdNew = $false
$runtimeMutexIdentity =
    [IO.Path]::GetFullPath($PSScriptRoot).
        TrimEnd([char[]]@('\', '/')).
        ToUpperInvariant()
$runtimeMutexHashAlgorithm =
    [Security.Cryptography.SHA256]::Create()
try {
    $runtimeMutexHash = [BitConverter]::ToString(
        $runtimeMutexHashAlgorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes($runtimeMutexIdentity))).
        Replace('-', '')
}
finally {
    $runtimeMutexHashAlgorithm.Dispose()
}
$runtimeMutexName =
    'Local\GeoraePlan_Test_RunAll_Launcher_{0}' -f
        $runtimeMutexHash
$mutex = New-Object `
    System.Threading.Mutex(
        $true,
        $runtimeMutexName,
        [ref]$createdNew)
$serverProcess = $null
$appProcess = $null
$serverDll = ''
$serverWasStarted = $false
$activeServerStdoutLogPath = ''
$activeServerStderrLogPath = ''
$runtimeRunId =
    [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') +
    '-' +
    [Guid]::NewGuid().ToString('N')
$runtimeFailureMessage = ''
$healthObservationCount = 0
$healthObservationSequence = 0L
$runExitCode = 0
$runScopedAdminPassword = New-LocalTestPassword

__CERTIFICATION_VALIDATOR__

function Write-Log {
    param([string]$Message)
    Assert-SafeRuntimeLogFilePath `
        -LogRoot $runtimeLogRoot `
        -Path $traceLogPath
    [IO.File]::AppendAllText(
        $traceLogPath,
        (
            "[{0}] {1}{2}" -f
                (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
                $Message,
                [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
}

function Stop-RunAllWithEarlyFailure {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateRange(1, 255)][int]$ExitCode = 1
    )

    $writtenErrorLogPath = $errorLogPath
    try {
        Assert-RuntimeRootHasNoReparsePoint -Path $PSScriptRoot
        if (-not (Test-Path -LiteralPath $runtimeLogRoot)) {
            New-Item `
                -ItemType Directory `
                -Path $runtimeLogRoot `
                -ErrorAction Stop | Out-Null
        }
        Assert-RuntimeRootHasNoReparsePoint -Path $runtimeLogRoot
        Initialize-RuntimeFinalPathNativeMethods
        Reset-RuntimeLogFile `
            -LogRoot $runtimeLogRoot `
            -Path $errorLogPath `
            -Content (
                "[{0}] EARLY FAILURE: {1}{2}Trace log: {3}{2}" -f
                    (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
                    $Message,
                    [Environment]::NewLine,
                    $traceLogPath)
    }
    catch {
        $writtenErrorLogPath = $null
    }

    [Console]::Error.WriteLine($Message)
    if (-not [string]::IsNullOrWhiteSpace($writtenErrorLogPath)) {
        [Console]::Error.WriteLine("Error log: $writtenErrorLogPath")
    }
    else {
        [Console]::Error.WriteLine(
            "Error log could not be written. Runtime log directory: $runtimeLogRoot")
    }
    exit $ExitCode
}

function ConvertTo-ValidatedDesktopArchiveEntryPath {
    param(
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    if (
        [string]::IsNullOrWhiteSpace($EntryName) -or
        $EntryName.Length -gt 1024 -or
        [IO.Path]::IsPathRooted($EntryName)
    ) {
        return $null
    }

    $normalized = $EntryName.Replace('\', '/')
    $isDirectory = $normalized.EndsWith(
        '/',
        [StringComparison]::Ordinal)
    if ($isDirectory) {
        $normalized = $normalized.Substring(0, $normalized.Length - 1)
    }
    if (
        [string]::IsNullOrWhiteSpace($normalized) -or
        $normalized.StartsWith('/', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('/', [StringComparison]::Ordinal)
    ) {
        return $null
    }

    $segments = @($normalized -split '/')
    if ($segments.Count -eq 0 -or $segments.Count -gt 64) {
        return $null
    }
    foreach ($segment in $segments) {
        if (
            [string]::IsNullOrWhiteSpace($segment) -or
            $segment.Length -gt 240 -or
            $segment -eq '.' -or
            $segment -eq '..' -or
            $segment.EndsWith('.', [StringComparison]::Ordinal) -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
            $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
        ) {
            return $null
        }

        $deviceStem = @($segment -split '\.', 2)[0]
        if ($deviceStem -match '^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$') {
            return $null
        }
    }

    return [pscustomobject]@{
        Path = $segments -join '/'
        IsDirectory = $isDirectory
    }
}

function Read-BoundedDesktopArchiveTextEntry {
    param(
        [Parameter(Mandatory = $true)][object]$Entry,
        [Parameter(Mandatory = $true)][long]$MaximumBytes
    )

    if (
        $MaximumBytes -le 0 -or
        $Entry.Length -le 0 -or
        $Entry.Length -gt $MaximumBytes
    ) {
        return $null
    }

    $reader = $null
    try {
        $reader = [IO.StreamReader]::new(
            $Entry.Open(),
            [Text.Encoding]::UTF8,
            $true)
        $text = [Text.StringBuilder]::new()
        $buffer = New-Object char[] 4096
        $characterCount = 0L
        while (($read = $reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $characterCount += $read
            if ($characterCount -gt $MaximumBytes) {
                return $null
            }
            [void]$text.Append($buffer, 0, $read)
        }
        return $text.ToString()
    }
    catch {
        return $null
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }
}

function Test-DesktopArchivePortableExecutableEntry {
    param(
        [Parameter(Mandatory = $true)][object]$Entry
    )

    if ($Entry.Length -lt 68 -or $Entry.Length -gt 536870912L) {
        return $false
    }

    $stream = $null
    try {
        $stream = $Entry.Open()
        $dosHeader = New-Object byte[] 64
        $dosBytesRead = 0
        while ($dosBytesRead -lt $dosHeader.Length) {
            $read = $stream.Read(
                $dosHeader,
                $dosBytesRead,
                $dosHeader.Length - $dosBytesRead)
            if ($read -le 0) {
                return $false
            }
            $dosBytesRead += $read
        }
        if ($dosHeader[0] -ne 0x4D -or $dosHeader[1] -ne 0x5A) {
            return $false
        }

        $peOffset = [BitConverter]::ToInt32($dosHeader, 0x3C)
        if ($peOffset -lt 64 -or $peOffset -gt ($Entry.Length - 4)) {
            return $false
        }
        $remaining = [long]$peOffset - 64L
        $skipBuffer = New-Object byte[] 8192
        while ($remaining -gt 0) {
            $requested = [int][Math]::Min(
                [long]$skipBuffer.Length,
                $remaining)
            $read = $stream.Read($skipBuffer, 0, $requested)
            if ($read -le 0) {
                return $false
            }
            $remaining -= $read
        }

        $signature = New-Object byte[] 4
        $signatureBytesRead = 0
        while ($signatureBytesRead -lt $signature.Length) {
            $read = $stream.Read(
                $signature,
                $signatureBytesRead,
                $signature.Length - $signatureBytesRead)
            if ($read -le 0) {
                return $false
            }
            $signatureBytesRead += $read
        }
        return (
            $signature[0] -eq 0x50 -and
            $signature[1] -eq 0x45 -and
            $signature[2] -eq 0 -and
            $signature[3] -eq 0)
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Test-DesktopUpdatePackageContract {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$InspectionRoot
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        return $false
    }
    $packageFile = Get-Item -LiteralPath $PackagePath -Force
    if ($packageFile.Length -le 0 -or $packageFile.Length -gt 536870912L) {
        return $false
    }

    $archive = $null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
        if ($archive.Entries.Count -le 0 -or $archive.Entries.Count -gt 10000) {
            return $false
        }

        $entries = @{}
        $totalUncompressedBytes = 0L
        foreach ($entry in $archive.Entries) {
            $externalAttributes = [int]$entry.ExternalAttributes
            $unixMode = ($externalAttributes -shr 16) -band 0xFFFF
            $validatedPath =
                ConvertTo-ValidatedDesktopArchiveEntryPath `
                    -EntryName $entry.FullName
            if (
                $null -eq $validatedPath -or
                ($externalAttributes -band
                    [int][IO.FileAttributes]::ReparsePoint) -ne 0 -or
                ($unixMode -band 0xF000) -eq 0xA000 -or
                $entries.ContainsKey($validatedPath.Path) -or
                ($validatedPath.IsDirectory -and $entry.Length -ne 0) -or
                $entry.Length -lt 0 -or
                $entry.Length -gt 536870912L -or
                $totalUncompressedBytes -gt (2147483648L - $entry.Length)
            ) {
                return $false
            }
            $totalUncompressedBytes += $entry.Length
            $entries[$validatedPath.Path] = [pscustomobject]@{
                Entry = $entry
                IsDirectory = [bool]$validatedPath.IsDirectory
            }
        }

        foreach ($entryPath in @($entries.Keys)) {
            $segments = @($entryPath -split '/')
            for ($index = 1; $index -lt $segments.Count; $index++) {
                $ancestorPath = @($segments[0..($index - 1)]) -join '/'
                if (
                    $entries.ContainsKey($ancestorPath) -and
                    -not $entries[$ancestorPath].IsDirectory
                ) {
                    return $false
                }
            }
        }

        $desktopExecutableEntries = @(
            $entries.Keys |
                Where-Object {
                    $_ -match '^App/[^/]+\.Desktop\.App\.exe$'
                }
        )
        if (
            $desktopExecutableEntries.Count -ne 1 -or
            -not [string]::Equals(
                $desktopExecutableEntries[0],
                'App/거래플랜.Desktop.App.exe',
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            return $false
        }

        $requiredEntries = @(
            'App/거래플랜.Desktop.App.exe',
            'App/거래플랜.exe',
            'App/appsettings.json',
            'App/앱실행.cmd',
            'App/Updater/거래플랜.Updater.exe',
            'Install-GeoraePlan.ps1',
            '거래플랜-설치.cmd',
            'README.txt'
        )
        foreach ($requiredEntry in $requiredEntries) {
            if (
                -not $entries.ContainsKey($requiredEntry) -or
                $entries[$requiredEntry].IsDirectory -or
                $entries[$requiredEntry].Entry.Length -le 0
            ) {
                return $false
            }
        }

        foreach ($portableExecutableEntry in @(
            'App/거래플랜.Desktop.App.exe',
            'App/거래플랜.exe',
            'App/Updater/거래플랜.Updater.exe'
        )) {
            if (-not (
                Test-DesktopArchivePortableExecutableEntry `
                    -Entry $entries[$portableExecutableEntry].Entry
            )) {
                return $false
            }
        }

        $installScript =
            Read-BoundedDesktopArchiveTextEntry `
                -Entry $entries['Install-GeoraePlan.ps1'].Entry `
                -MaximumBytes 1048576L
        $launchScript =
            Read-BoundedDesktopArchiveTextEntry `
                -Entry $entries['App/앱실행.cmd'].Entry `
                -MaximumBytes 65536L
        $installCommand =
            Read-BoundedDesktopArchiveTextEntry `
                -Entry $entries['거래플랜-설치.cmd'].Entry `
                -MaximumBytes 65536L
        $installTokens = $null
        $installParseErrors = $null
        $installAst = if ([string]::IsNullOrWhiteSpace($installScript)) {
            $null
        }
        else {
            [System.Management.Automation.Language.Parser]::ParseInput(
                $installScript,
                [ref]$installTokens,
                [ref]$installParseErrors)
        }
        $installParameterNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        if ($null -ne $installAst -and $null -ne $installAst.ParamBlock) {
            foreach ($parameter in $installAst.ParamBlock.Parameters) {
                [void]$installParameterNames.Add(
                    [string]$parameter.Name.VariablePath.UserPath)
            }
        }
        $missingInstallParameter = $false
        foreach ($requiredInstallParameter in @(
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
        )) {
            if (-not $installParameterNames.Contains(
                $requiredInstallParameter)) {
                $missingInstallParameter = $true
                break
            }
        }
        if (
            [string]::IsNullOrWhiteSpace($installScript) -or
            [string]::IsNullOrWhiteSpace($launchScript) -or
            [string]::IsNullOrWhiteSpace($installCommand) -or
            $null -eq $installAst -or
            $installParseErrors.Count -ne 0 -or
            $null -eq $installAst.ParamBlock -or
            $missingInstallParameter -or
            -not $installScript.Contains(
                'GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1') -or
            -not $installScript.Contains(
                'GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1') -or
            -not $launchScript.Contains(
                'for %%I in ("%~dp0*.Desktop.App.exe") do if exist "%%~fI"') -or
            -not $launchScript.Contains('start "" "%APP_EXE%"') -or
            $launchScript.Contains('"%~dp0*.exe"') -or
            -not $installCommand.Contains('Install-GeoraePlan.ps1')
        ) {
            return $false
        }

        $inspectionParent = [IO.Path]::GetFullPath($InspectionRoot)
        if (
            -not (Test-Path `
                -LiteralPath $inspectionParent `
                -PathType Container) -or
            ((Get-Item `
                -LiteralPath $inspectionParent `
                -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
        ) {
            return $false
        }
        $packageInspectionRoot = Join-Path `
            $inspectionParent `
            ('georaeplan-test-desktop-package-' +
                [Guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $packageInspectionRoot |
                Out-Null
            if (
                ((Get-Item `
                    -LiteralPath $packageInspectionRoot `
                    -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
            ) {
                return $false
            }
            $extractedAppPath =
                Join-Path `
                    $packageInspectionRoot `
                    '거래플랜.Desktop.App.exe'
            $extractedAliasPath =
                Join-Path `
                    $packageInspectionRoot `
                    '거래플랜.exe'
            [IO.Compression.ZipFileExtensions]::ExtractToFile(
                $entries['App/거래플랜.Desktop.App.exe'].Entry,
                $extractedAppPath,
                $true)
            [IO.Compression.ZipFileExtensions]::ExtractToFile(
                $entries['App/거래플랜.exe'].Entry,
                $extractedAliasPath,
                $true)
            $canonicalHash = (Get-FileHash `
                -LiteralPath $extractedAppPath `
                -Algorithm SHA256).Hash
            $aliasHash = (Get-FileHash `
                -LiteralPath $extractedAliasPath `
                -Algorithm SHA256).Hash
            if (
                -not [string]::Equals(
                    $canonicalHash,
                    $aliasHash,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                return $false
            }
            foreach ($executablePath in @(
                $extractedAppPath,
                $extractedAliasPath
            )) {
                $versionInfo =
                    [Diagnostics.FileVersionInfo]::GetVersionInfo(
                        $executablePath)
                $actualVersion = [string]$versionInfo.ProductVersion
                $actualVersionPrefix =
                    @($actualVersion.Split('+'))[0].Trim()
                if (
                    [string]::IsNullOrWhiteSpace($actualVersionPrefix) -or
                    -not [string]::Equals(
                        $actualVersionPrefix,
                        $ExpectedVersion.Trim(),
                        [StringComparison]::Ordinal)
                ) {
                    return $false
                }
            }
        }
        finally {
            if (Test-Path -LiteralPath $packageInspectionRoot) {
                Remove-Item `
                    -LiteralPath $packageInspectionRoot `
                    -Recurse `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
        }

        return $true
    }
    catch {
        Write-Log (
            "Desktop update package contract validation failed. package={0}; error={1}" -f
            $PackagePath,
            $_.Exception.Message)
        return $false
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }
}

function Initialize-TestUpdateManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ServerDataRoot,
        [string]$RuntimeRoot = $PSScriptRoot
    )

    if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
        throw 'Runtime root is required for test update manifest initialization.'
    }
    $updatesRoot = Join-Path $ServerDataRoot 'updates'
    $manifestRoot = Join-Path $updatesRoot 'manifest'
    $downloadRoot = Join-Path $updatesRoot 'downloads\android'
    New-Item -ItemType Directory -Force -Path $manifestRoot | Out-Null

    $mobileDir = Join-Path $RuntimeRoot 'Mobile'
    $androidMetadataPath =
        Join-Path $mobileDir 'android-package.metadata.json'
    $apkTarget = $null
    $androidManifest = $null
    if (Test-Path -LiteralPath $androidMetadataPath -PathType Leaf) {
        try {
            $androidMetadata =
                Get-Content `
                    -LiteralPath $androidMetadataPath `
                    -Raw `
                    -Encoding UTF8 |
                    ConvertFrom-Json
        }
        catch {
            throw "Android test metadata sidecar is invalid: $androidMetadataPath"
        }

        $schemaVersion = 0
        $versionCode = 0L
        $fileSize = 0L
        $apkTargetName = ([string]$androidMetadata.fileName).Trim()
        $applicationId = ([string]$androidMetadata.applicationId).Trim()
        $versionName = ([string]$androidMetadata.versionName).Trim()
        $sha256 = ([string]$androidMetadata.sha256).Trim()
        if (
            -not [int]::TryParse(
                [string]$androidMetadata.schemaVersion,
                [ref]$schemaVersion) -or
            $schemaVersion -ne 1 -or
            [string]::IsNullOrWhiteSpace($apkTargetName) -or
            -not [string]::Equals(
                [IO.Path]::GetFileName($apkTargetName),
                $apkTargetName,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [IO.Path]::GetExtension($apkTargetName),
                '.apk',
                [StringComparison]::OrdinalIgnoreCase) -or
            $applicationId -notmatch
                '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$' -or
            $versionName -notmatch '^\d+(?:\.\d+)+$' -or
            -not [long]::TryParse(
                [string]$androidMetadata.versionCode,
                [ref]$versionCode) -or
            $versionCode -le 0 -or
            $sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not [long]::TryParse(
                [string]$androidMetadata.fileSize,
                [ref]$fileSize) -or
            $fileSize -le 0
        ) {
            throw 'Android test metadata sidecar contract is invalid.'
        }

        $apkSource = Join-Path $mobileDir $apkTargetName
        if (-not (Test-Path -LiteralPath $apkSource -PathType Leaf)) {
            throw "Android test APK referenced by sidecar is missing: $apkSource"
        }
        $apkSourceFile = Get-Item -LiteralPath $apkSource
        $apkSourceHash = (
            Get-FileHash -LiteralPath $apkSource -Algorithm SHA256
        ).Hash
        if (
            $apkSourceFile.Length -ne $fileSize -or
            -not [string]::Equals(
                $apkSourceHash,
                $sha256,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Android test APK does not match its validated metadata sidecar.'
        }

        New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null
        $apkTarget = Join-Path $downloadRoot $apkTargetName
        $apkTargetTemp = Join-Path $downloadRoot (
            ".$apkTargetName.$([Guid]::NewGuid().ToString('N')).tmp")
        try {
            Copy-Item `
                -LiteralPath $apkSource `
                -Destination $apkTargetTemp `
                -Force
            $apkTargetTempFile = Get-Item -LiteralPath $apkTargetTemp
            $apkTargetTempHash = (
                Get-FileHash -LiteralPath $apkTargetTemp -Algorithm SHA256
            ).Hash
            if (
                $apkTargetTempFile.Length -ne $fileSize -or
                -not [string]::Equals(
                    $apkTargetTempHash,
                    $sha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw 'Android test APK changed while copying to the update root.'
            }
            Publish-TestFileAtomically `
                -TemporaryPath $apkTargetTemp `
                -TargetPath $apkTarget
        }
        finally {
            if (Test-Path -LiteralPath $apkTargetTemp -PathType Leaf) {
                Remove-Item `
                    -LiteralPath $apkTargetTemp `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
        }

        $androidManifest = [ordered]@{
            platform = 'android'
            version = $androidMetadata.versionName
            mandatory = $false
            minimumSupportedVersion = $androidMetadata.versionName
            fileName = $apkTargetName
            packageUrl = ''
            sha256 = $androidMetadata.sha256
            fileSize = [long]$androidMetadata.fileSize
            notes = '테스트 실행환경 모바일 APK입니다.'
            releasedAtUtc = [DateTime]::UtcNow.ToString('O')
        }
    }
    else {
        $unvalidatedApks = @()
        if (Test-Path -LiteralPath $mobileDir -PathType Container) {
            $unvalidatedApks = @(
                Get-ChildItem `
                    -LiteralPath $mobileDir `
                    -File `
                    -Filter '*.apk' `
                    -ErrorAction Stop
            )
        }
        if ($unvalidatedApks.Count -gt 0) {
            throw (
                'Android test APK exists without the validated metadata ' +
                "sidecar: $($unvalidatedApks[0].FullName)")
        }
        Write-Log 'Mobile APK not found. Skipping mobile test update manifest entry only.'
    }

    $testManifestPath = Join-Path $manifestRoot 'test.json'
    $manifest = [ordered]@{
        channel = 'test'
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    }
    if ($null -ne $androidManifest) {
        $manifest.android = $androidManifest
    }

    $desktopDownloadRoot = Join-Path $updatesRoot 'downloads\desktop'
    $desktopPackageCandidate = @(
        Get-ChildItem `
            -LiteralPath $desktopDownloadRoot `
            -File `
            -Filter 'tradeplan-pc-installer-*.zip' `
            -ErrorAction SilentlyContinue |
            ForEach-Object {
                $versionMatch = [regex]::Match(
                    $_.Name,
                    '^tradeplan-pc-installer-v(?<version>\d+(?:\.\d+)+)\.zip$',
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
                [Version]$parsedVersion = $null
                if (
                    $versionMatch.Success -and
                    [Version]::TryParse(
                        $versionMatch.Groups['version'].Value,
                        [ref]$parsedVersion) -and
                    (Test-DesktopUpdatePackageContract `
                        -PackagePath $_.FullName `
                        -ExpectedVersion $versionMatch.Groups['version'].Value `
                        -InspectionRoot (Join-Path $RuntimeRoot 'RuntimeLogs'))
                ) {
                    [pscustomobject]@{
                        Package = $_
                        Version = $parsedVersion
                    }
                }
            }
    ) |
        Sort-Object `
            @{ Expression = { $_.Version }; Descending = $true },
            @{ Expression = { $_.Package.LastWriteTimeUtc }; Descending = $true },
            @{ Expression = { $_.Package.Name }; Descending = $false } |
        Select-Object -First 1
    if ($null -ne $desktopPackageCandidate) {
        $desktopPackage = $desktopPackageCandidate.Package
        $desktopVersion = $desktopPackageCandidate.Version.ToString()
        $desktopHash = Get-FileHash -LiteralPath $desktopPackage.FullName -Algorithm SHA256
        $manifest.desktop = [ordered]@{
            platform = 'desktop'
            version = $desktopVersion
            mandatory = $false
            minimumSupportedVersion = $desktopVersion
            fileName = $desktopPackage.Name
            packageUrl = ''
            sha256 = $desktopHash.Hash
            fileSize = $desktopPackage.Length
            notes = '테스트 실행환경 PC 업데이트 패키지입니다.'
            releasedAtUtc = [DateTime]::UtcNow.ToString('O')
        }
    }
    else {
        Write-Log (
            'Validated desktop installer package was not found. ' +
            'The test manifest will not advertise a non-installable desktop update.')
    }

    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $testManifestPath -Value $manifestJson -Encoding UTF8
    $apkLogValue = if ($null -ne $apkTarget) { [string]$apkTarget } else { 'none' }
    Write-Log ("Test update manifest prepared. channel=test; apk={0}; hasDesktop={1}" -f $apkLogValue, ($null -ne $manifest.desktop))
}

if (-not $createdNew) {
    exit 0
}

try {
    $startupGateLease = [IO.File]::Open(
        $preparationGateLeasePath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
}
catch {
    Stop-RunAllWithEarlyFailure `
        -Message 'Preparation is already starting for this isolated runtime root.'
}

if (Test-Path -LiteralPath $invalidMarkerPath) {
    $startupGateLease.Dispose()
    $startupGateLease = $null
    Stop-RunAllWithEarlyFailure `
        -Message 'This isolated V1 runtime is explicitly invalidated.'
}

if (-not (Test-Path -LiteralPath $readyMarkerPath -PathType Leaf)) {
    $startupGateLease.Dispose()
    $startupGateLease = $null
    Stop-RunAllWithEarlyFailure `
        -Message 'This isolated V1 runtime is not certified ready.'
}

$startupGateLease.Dispose()
$startupGateLease = $null

try {
    $runtimeLease = [IO.File]::Open(
        $leasePath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
}
catch {
    Stop-RunAllWithEarlyFailure `
        -Message 'Preparation is already using this isolated runtime root.'
}

if (Test-Path -LiteralPath $invalidMarkerPath) {
    $runtimeLease.Dispose()
    Stop-RunAllWithEarlyFailure `
        -Message (
            'This isolated V1 runtime was invalidated while acquiring ' +
            'its preparation lease.')
}

try {
    $appLease = [IO.File]::Open(
        $appLeasePath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    $serverLease = [IO.File]::Open(
        $serverLeasePath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
}
catch {
    if ($null -ne $serverLease) {
        $serverLease.Dispose()
    }
    if ($null -ne $appLease) {
        $appLease.Dispose()
    }
    $runtimeLease.Dispose()
    Stop-RunAllWithEarlyFailure `
        -Message 'An isolated App or Server component is already running.'
}

if (-not (Test-Path -LiteralPath $readyMarkerPath -PathType Leaf)) {
    $serverLease.Dispose()
    $appLease.Dispose()
    $runtimeLease.Dispose()
    Stop-RunAllWithEarlyFailure `
        -Message (
            'The isolated V1 runtime readiness marker changed while ' +
            'acquiring its lease.')
}
__RUN_ALL_LOCK_ONLY_PROBE_BLOCK__
try {
    $certificationLease =
        Enter-RuntimeCertificationLease `
            -Path $certificationLeasePath
    try {
        Assert-RuntimeCertification `
            -MarkerPath $readyMarkerPath `
            -SelfHashKey 'run_all_script_sha256'
    }
    finally {
        $certificationLease.Dispose()
        $certificationLease = $null
    }
}
catch {
    if ($null -ne $certificationLease) {
        $certificationLease.Dispose()
        $certificationLease = $null
    }
    $serverLease.Dispose()
    $appLease.Dispose()
    $runtimeLease.Dispose()
    Stop-RunAllWithEarlyFailure -Message $_.Exception.Message
}

try {
    Initialize-IsolatedRuntimeTempEnvironment | Out-Null
    Assert-RuntimeRootHasNoReparsePoint -Path $runtimeLogRoot
    Initialize-RuntimeHealthObservationLog `
        -LogRoot $runtimeLogRoot `
        -Path $healthObservationPath `
        -PreviousPath $previousHealthObservationPath
    Remove-OldRuntimeServerLogs -LogRoot $runtimeLogRoot
    $childProcessJob = [GeoraePlan.Runtime.ChildProcessJob]::new()
    Reset-RuntimeLogFile `
        -LogRoot $runtimeLogRoot `
        -Path $traceLogPath
    Reset-RuntimeLogFile `
        -LogRoot $runtimeLogRoot `
        -Path $errorLogPath
    Write-Log 'Run-All.ps1 started.'
    Write-Log 'Resolving app/server files.'
    $serverDlls = @(
        Get-ChildItem -LiteralPath $serverDir -Filter '*.Server.Api.dll' -File
    )
    $appExecutables = @(
        Get-ChildItem -LiteralPath $appDir -Filter '*.Desktop.App.exe' -File
    )

    if (-not (Test-Path -LiteralPath $dotnetExe)) {
        throw "dotnet not found: $dotnetExe"
    }

    if ($serverDlls.Count -ne 1) {
        throw "Expected exactly one server DLL in $serverDir."
    }

    if ($appExecutables.Count -ne 1) {
        throw "Expected exactly one desktop executable in $appDir."
    }

    $serverDll = $serverDlls[0].FullName
    $appExe = $appExecutables[0].FullName
    Write-Log 'App/server files resolved.'
    Initialize-TestUpdateManifest -ServerDataRoot $serverDataRoot
    $scanPort = 19080
    $serverReady = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Remove-OldRuntimeServerLogs -LogRoot $runtimeLogRoot
        $port = Get-FreePort -StartingPort $scanPort
        $serverUrl = "http://127.0.0.1:$port"

        Write-Log ("Server start attempt #{0} on {1}" -f $attempt, $serverUrl)
        $certificationLease =
            Enter-RuntimeCertificationLease `
                -Path $certificationLeasePath
        try {
            Write-Log 'Updating appsettings Api.BaseUrl.'
            Set-AndVerify-IsolatedApiBaseUrl `
                -BaseUrl $serverUrl `
                -MarkerPath $readyMarkerPath
        }
        finally {
            $certificationLease.Dispose()
            $certificationLease = $null
        }

        Write-Log 'Launching hidden test server process.'
        $attemptTag = '{0:D2}' -f $attempt
        $activeServerStdoutLogPath =
            Join-Path `
                $runtimeLogRoot `
                ("server-{0}-a{1}.stdout.log" -f
                    $runtimeRunId,
                    $attemptTag)
        $activeServerStderrLogPath =
            Join-Path `
                $runtimeLogRoot `
                ("server-{0}-a{1}.stderr.log" -f
                    $runtimeRunId,
                    $attemptTag)
        Reset-RuntimeLogFile `
            -LogRoot $runtimeLogRoot `
            -Path $activeServerStdoutLogPath
        Reset-RuntimeLogFile `
            -LogRoot $runtimeLogRoot `
            -Path $activeServerStderrLogPath
        $serverProcess = Start-HiddenServerProcess `
            -DotnetExe $dotnetExe `
            -ServerDir $serverDir `
            -ServerDll $serverDll `
            -ServerUrl $serverUrl `
            -ServerDataRoot $serverDataRoot `
            -AdminPassword $runScopedAdminPassword `
            -UsenetPassword (New-LocalTestPassword) `
            -StdoutLogPath $activeServerStdoutLogPath `
            -StderrLogPath $activeServerStderrLogPath
        $serverWasStarted = $true
        try {
            $childProcessJob.AssignProcess($serverProcess)
        }
        catch {
            $assignmentError = $_
            try {
                Stop-AndDisposeRuntimeProcess `
                    -Process $serverProcess `
                    -Description 'Isolated test server'
                $serverProcess = $null
            }
            catch {
                $cleanupError = $_
                throw [AggregateException]::new(
                    'Server job assignment and orphan cleanup both failed.',
                    [Exception[]]@(
                        $assignmentError.Exception,
                        $cleanupError.Exception))
            }
            throw $assignmentError
        }
        Write-Log ("Hidden test server process started. pid={0}" -f $serverProcess.Id)
        Write-Log (
            'Server output logs: stdout={0}; stderr={1}' -f
                $activeServerStdoutLogPath,
                $activeServerStderrLogPath)
        Start-Sleep -Milliseconds 300
        if ($serverProcess.HasExited) {
            $earlyExitCode = $serverProcess.ExitCode
            Write-Log (
                "Server exited before health check. exitCode=$earlyExitCode")
            Stop-AndDisposeRuntimeProcess `
                -Process $serverProcess `
                -Description 'Isolated test server'
            $serverProcess = $null
            $scanPort = $port + 1
            continue
        }

        if (
            Wait-HttpReady `
                -Url ($serverUrl + '/readyz') `
                -ServerProcess $serverProcess `
                -LogRoot $runtimeLogRoot `
                -LogPaths @(
                    $activeServerStdoutLogPath,
                    $activeServerStderrLogPath)
        ) {
            Write-Log 'Test server reported database ready.'
            $serverReady = $true
            break
        }

        if ($serverProcess -and $serverProcess.HasExited) {
            Write-Log (
                'Server exited during health check wait. exitCode={0}' -f
                    $serverProcess.ExitCode)
        }
        else {
            Write-Log 'Server health check failed while process was still running. Stopping process and retrying.'
        }

        if ($serverProcess) {
            Stop-AndDisposeRuntimeProcess `
                -Process $serverProcess `
                -Description 'Isolated test server'
        }

        $serverProcess = $null
        $scanPort = $port + 1
    }

    if (-not $serverReady) {
        throw 'Failed to start isolated test server. Use Run-Server.cmd for details.'
    }

    $certificationLease =
        Enter-RuntimeCertificationLease `
            -Path $certificationLeasePath
    try {
        Set-AndVerify-IsolatedApiBaseUrl `
            -BaseUrl $serverUrl `
            -MarkerPath $readyMarkerPath
    }
    finally {
        $certificationLease.Dispose()
        $certificationLease = $null
    }
    Write-Log 'Launching test app.'
    [Environment]::SetEnvironmentVariable('GEORAEPLAN_APP_ROOT', $appRoot, 'Process')
    [Environment]::SetEnvironmentVariable('GEORAEPLAN_DISABLE_LEGACY_MERGE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('GEORAEPLAN_TEST_MODE', '1', 'Process')
    $autoLoginEnvironment = @{
        'GEORAEPLAN_TEST_AUTO_LOGIN' = '1'
        'GEORAEPLAN_TEST_AUTO_LOGIN_USERNAME' = 'admin'
        'GEORAEPLAN_TEST_AUTO_LOGIN_PASSWORD' = $runScopedAdminPassword
    }
    $previousAutoLoginEnvironment = @{}
    foreach ($key in $autoLoginEnvironment.Keys) {
        $previousAutoLoginEnvironment[$key] =
            [Environment]::GetEnvironmentVariable($key, 'Process')
    }
    try {
        foreach ($key in $autoLoginEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable(
                $key,
                [string]$autoLoginEnvironment[$key],
                'Process')
        }
        $appProcess = Start-Process `
            -FilePath $appExe `
            -WorkingDirectory $appDir `
            -PassThru
    }
    finally {
        foreach ($key in $autoLoginEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable(
                $key,
                $previousAutoLoginEnvironment[$key],
                'Process')
        }
    }
    try {
        $childProcessJob.AssignProcess($appProcess)
    }
    catch {
        $assignmentError = $_
        try {
            Stop-AndDisposeRuntimeProcess `
                -Process $appProcess `
                -Description 'Isolated test desktop app'
            $appProcess = $null
        }
        catch {
            $cleanupError = $_
            throw [AggregateException]::new(
                'Desktop job assignment and orphan cleanup both failed.',
                [Exception[]]@(
                    $assignmentError.Exception,
                    $cleanupError.Exception))
        }
        throw $assignmentError
    }
    Write-Log ("Test app started. pid={0}" -f $appProcess.Id)
    $healthProbeIntervalSeconds = 5
    $consecutiveHealthFailures = 0
    $healthDegraded = $false
    $nextHealthProbeUtc = [DateTime]::UtcNow
    while (-not $appProcess.HasExited) {
        try {
            Assert-RuntimeServerLogsWithinLimit `
                -LogRoot $runtimeLogRoot `
                -Paths @(
                    $activeServerStdoutLogPath,
                    $activeServerStderrLogPath)
        }
        catch {
            Stop-RuntimeAppAfterServerFailure -Process $appProcess
            throw
        }

        if ($serverProcess.HasExited) {
            $serverExitCode = $serverProcess.ExitCode
            $consecutiveHealthFailures++
            Write-RuntimeHealthObservation `
                -LogRoot $runtimeLogRoot `
                -Path $healthObservationPath `
                -PreviousPath $previousHealthObservationPath `
                -ServerPid $serverProcess.Id `
                -ServerExited $true `
                -ExitCode ([string]$serverExitCode) `
                -HealthOk $false `
                -ElapsedMilliseconds 0 `
                -ConsecutiveFailures $consecutiveHealthFailures `
                -MaximumSamplesPerFile 17280
            $message =
                'The isolated test server exited while the desktop app was running. ' +
                "pid=$($serverProcess.Id) exitCode=$serverExitCode " +
                "stdout=$activeServerStdoutLogPath " +
                "stderr=$activeServerStderrLogPath"
            Write-Log ("ERROR: {0}" -f $message)
            Stop-RuntimeAppAfterServerFailure -Process $appProcess
            throw $message
        }

        if ([DateTime]::UtcNow -ge $nextHealthProbeUtc) {
            $healthProbe =
                Invoke-RuntimeHealthProbe -Url ($serverUrl + '/healthz')
            try {
                Assert-RuntimeServerLogsWithinLimit `
                    -LogRoot $runtimeLogRoot `
                    -Paths @(
                        $activeServerStdoutLogPath,
                        $activeServerStderrLogPath)
            }
            catch {
                Stop-RuntimeAppAfterServerFailure -Process $appProcess
                throw
            }
            if ([bool]$healthProbe.HealthOk) {
                if ($healthDegraded) {
                    Write-Log (
                        'Runtime server health recovered after {0} consecutive failures.' -f
                            $consecutiveHealthFailures)
                }
                $consecutiveHealthFailures = 0
                $healthDegraded = $false
            }
            else {
                $consecutiveHealthFailures++
            }

            Write-RuntimeHealthObservation `
                -LogRoot $runtimeLogRoot `
                -Path $healthObservationPath `
                -PreviousPath $previousHealthObservationPath `
                -ServerPid $serverProcess.Id `
                -ServerExited $false `
                -HealthOk ([bool]$healthProbe.HealthOk) `
                -HttpStatus ([string]$healthProbe.HttpStatus) `
                -ElapsedMilliseconds (
                    [long]$healthProbe.ElapsedMilliseconds) `
                -ConsecutiveFailures $consecutiveHealthFailures `
                -MaximumSamplesPerFile 17280

            if (
                -not [bool]$healthProbe.HealthOk -and
                $consecutiveHealthFailures -ge 3
            ) {
                $healthDegraded = $true
                $message =
                    'The isolated test server failed three consecutive health checks. ' +
                    "pid=$($serverProcess.Id) " +
                    "stdout=$activeServerStdoutLogPath " +
                    "stderr=$activeServerStderrLogPath"
                Write-Log ("ERROR: {0}" -f $message)
                Stop-RuntimeAppAfterServerFailure -Process $appProcess
                throw $message
            }

            $nextHealthProbeUtc =
                [DateTime]::UtcNow.AddSeconds(
                    $healthProbeIntervalSeconds)
        }

        if ($appProcess.WaitForExit(250)) {
            break
        }
    }

    $appProcess.WaitForExit()
    $appExitCode = $appProcess.ExitCode
    Write-Log (
        "Test app exited. exitCode=$appExitCode. Cleaning up server.")
    if ($appExitCode -ne 0) {
        throw "The isolated test desktop app exited with code $appExitCode."
    }
}
catch {
    $runExitCode = 1
    $message = $_.Exception.ToString()
    $runtimeFailureMessage = $message
    Write-Log ("ERROR: {0}" -f $message)
    Reset-RuntimeLogFile `
        -LogRoot $runtimeLogRoot `
        -Path $errorLogPath `
        -Content $message
}
finally {
    try {
        Write-Log 'Run-All.ps1 entering cleanup.'
    }
    catch {
    }

    if ($null -ne $appProcess) {
        try {
            if (-not $appProcess.HasExited) {
                Stop-RuntimeAppAfterServerFailure -Process $appProcess
            }
            Stop-AndDisposeRuntimeProcess `
                -Process $appProcess `
                -Description 'Isolated test desktop app'
            $appProcess = $null
        }
        catch {
            try {
                Write-Log (
                    "Desktop app cleanup warning: $($_.Exception.Message)")
            }
            catch {
            }
        }
    }

    $serverExitConfirmed = ($null -eq $serverProcess)
    $serverStopFailed = $false
    if ($null -ne $serverProcess) {
        try {
            if (-not $serverProcess.HasExited) {
                try {
                    $serverProcess.Kill()
                }
                catch [InvalidOperationException] {
                    if (-not $serverProcess.HasExited) {
                        throw
                    }
                }
            }
            if (-not $serverProcess.WaitForExit(5000)) {
                throw 'Isolated test server did not exit within five seconds.'
            }
            $serverProcess.WaitForExit()
            $serverExitConfirmed = $true
            $serverProcess.Dispose()
            $serverProcess = $null
            Write-Log 'Hidden test server process stopped.'
        }
        catch {
            $serverStopFailed = $true
            try {
                Write-Log (
                    "Server cleanup warning: $($_.Exception.Message)")
            }
            catch {
            }
        }
    }

    if ($serverStopFailed) {
        $failedChildProcessJob = $childProcessJob
        $childProcessJob = $null
        if ($null -ne $failedChildProcessJob) {
            try {
                $failedChildProcessJob.Dispose()
            }
            catch {
                try {
                    Write-Log 'Server cleanup job disposal warning.'
                }
                catch {
                }
            }
        }

        try {
            if (
                $null -ne $serverProcess -and
                ($serverProcess.HasExited -or $serverProcess.WaitForExit(5000))
            ) {
                $serverProcess.WaitForExit()
                $serverExitConfirmed = $true
                $serverProcess.Dispose()
                $serverProcess = $null
                Write-Log 'Hidden test server exit confirmed after job disposal.'
            }
        }
        catch {
            try {
                Write-Log 'Server exit confirmation warning after job disposal.'
            }
            catch {
            }
        }

        if ($serverExitConfirmed) {
            try {
                $childProcessJob =
                    [GeoraePlan.Runtime.ChildProcessJob]::new()
            }
            catch {
                try {
                    Write-Log 'Finalizer containment recreation warning.'
                }
                catch {
                }
            }
        }
    }

    if ($serverWasStarted -and -not $serverExitConfirmed) {
        $runExitCode = 1
        $serverExitFailure =
            'Server exit could not be confirmed; SQLite finalization was skipped.'
        if ([string]::IsNullOrWhiteSpace($runtimeFailureMessage)) {
            $runtimeFailureMessage = $serverExitFailure
        }
        else {
            $runtimeFailureMessage +=
                [Environment]::NewLine +
                $serverExitFailure
        }
        try {
            Write-Log ("ERROR: {0}" -f $serverExitFailure)
            Reset-RuntimeLogFile `
                -LogRoot $runtimeLogRoot `
                -Path $errorLogPath `
                -Content $runtimeFailureMessage
        }
        catch {
        }
    }

    if (
        $serverWasStarted -and
        $serverExitConfirmed -and
        $null -eq $childProcessJob
    ) {
        $runExitCode = 1
        $finalizerContainmentFailure =
            'SQLite finalization was skipped because process containment was unavailable.'
        if ([string]::IsNullOrWhiteSpace($runtimeFailureMessage)) {
            $runtimeFailureMessage = $finalizerContainmentFailure
        }
        else {
            $runtimeFailureMessage +=
                [Environment]::NewLine +
                $finalizerContainmentFailure
        }
        try {
            Write-Log ("ERROR: {0}" -f $finalizerContainmentFailure)
            Reset-RuntimeLogFile `
                -LogRoot $runtimeLogRoot `
                -Path $errorLogPath `
                -Content $runtimeFailureMessage
        }
        catch {
        }
    }

    if (
        $serverWasStarted -and
        $serverExitConfirmed -and
        $null -ne $childProcessJob
    ) {
        try {
            $finalization =
                Invoke-IsolatedServerSqliteFinalizer `
                    -DotnetExe $dotnetExe `
                    -ServerDll $serverDll `
                    -ServerDir $serverDir `
                    -LogRoot $runtimeLogRoot `
                    -LogPath $serverSqliteFinalizationLogPath `
                    -ProcessJob $childProcessJob
            $finalizationMessage =
                (
                    'Server SQLite finalized. sidecar_count={0}; ' +
                    'database_length={1}; database_sha256={2}'
                ) -f
                    $finalization.SidecarCount,
                    $finalization.DatabaseLength,
                    $finalization.DatabaseSha256
            Write-Log $finalizationMessage
        }
        catch {
            $runExitCode = 1
            $finalizationFailure =
                'Server SQLite finalization failed after runtime shutdown. ' +
                $_.Exception.Message
            if ([string]::IsNullOrWhiteSpace($runtimeFailureMessage)) {
                $runtimeFailureMessage = $finalizationFailure
            }
            else {
                $runtimeFailureMessage +=
                    [Environment]::NewLine +
                    $finalizationFailure
            }
            try {
                Write-Log ("ERROR: {0}" -f $finalizationFailure)
                Reset-RuntimeLogFile `
                    -LogRoot $runtimeLogRoot `
                    -Path $errorLogPath `
                    -Content $runtimeFailureMessage
            }
            catch {
            }
        }
    }

    if ($null -ne $childProcessJob) {
        $childProcessJob.Dispose()
    }

    foreach ($remainingProcess in @($appProcess, $serverProcess)) {
        if ($null -eq $remainingProcess) {
            continue
        }
        try {
            if ($remainingProcess.WaitForExit(5000)) {
                $remainingProcess.WaitForExit()
            }
            $remainingProcess.Dispose()
        }
        catch {
        }
    }

    if ($mutex) {
        try {
            if ($createdNew) {
                $mutex.ReleaseMutex()
            }
        }
        catch {
        }

        $mutex.Dispose()
    }

    if ($null -ne $startupGateLease) {
        $startupGateLease.Dispose()
    }
    if ($null -ne $certificationLease) {
        $certificationLease.Dispose()
    }
    if ($null -ne $serverLease) {
        $serverLease.Dispose()
    }
    if ($null -ne $appLease) {
        $appLease.Dispose()
    }
    if ($null -ne $runtimeLease) {
        $runtimeLease.Dispose()
    }
}

if (-not [string]::IsNullOrWhiteSpace($runtimeFailureMessage)) {
    [Console]::Error.WriteLine(
        "Test execution failed. Error log: $errorLogPath")
}

if (
    -not [string]::IsNullOrWhiteSpace($runtimeFailureMessage) -and
    [string]::Equals(
        $env:GEORAEPLAN_SHOW_FAILURE_DIALOG,
        '1',
        [StringComparison]::Ordinal) -and
    -not [string]::Equals(
        $env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG,
        '1',
        [StringComparison]::Ordinal)
) {
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            (
                "테스트 실행에 실패했습니다.`r`n`r`n" +
                "앱과 서버 정리를 완료했습니다.`r`n" +
                "런타임 로그: $runtimeLogRoot`r`n" +
                "오류 로그: $errorLogPath"),
            '거래플랜 테스트 실행 오류') | Out-Null
    }
    catch {
    }
}

exit $runExitCode
'@

    $runAllContent = @"
@echo off
setlocal EnableExtensions
set "RUN_ALL_PS=%~dp0Run-All.ps1"
set "READY_MARKER=%~dp0.georaeplan-runtime-ready"
set "INVALID_MARKER=%~dp0.georaeplan-runtime-invalid"
if exist "%INVALID_MARKER%" (
  echo [GeoraePlan] This isolated V1 runtime is explicitly invalidated. 1>&2
  exit /b 1
)
if not exist "%READY_MARKER%" (
  echo [GeoraePlan] This isolated V1 runtime is not certified ready. 1>&2
  exit /b 1
)
if not exist "%RUN_ALL_PS%" (
  echo [GeoraePlan] Run-All.ps1 not found: %RUN_ALL_PS% 1>&2
  exit /b 1
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "%RUN_ALL_PS%"
set "RUN_EXIT=%ERRORLEVEL%"
exit /b %RUN_EXIT%
"@

    $componentLockProbeParameter = ''
    $runAllLockProbeParameter = ''
    $componentLockOnlyProbeBlock = ''
    $runAllLockOnlyProbeBlock = ''
    $allowStandaloneApp = '$false'
    if ($IncludeInternalLockProbe) {
        $allowStandaloneApp = '$true'
        $componentLockProbeParameter = @'
,
    [ValidateRange(0, 30000)]
    [int]$LeaseProbeMilliseconds = 0
'@
        $runAllLockProbeParameter = @'

    [ValidateRange(0, 30000)]
    [int]$LeaseProbeMilliseconds = 0

'@
        $componentLockOnlyProbeBlock = @'
    if ($LeaseProbeMilliseconds -gt 0) {
        Start-Sleep -Milliseconds $LeaseProbeMilliseconds
        exit 0
    }
'@
        $runAllLockOnlyProbeBlock = @'
if ($LeaseProbeMilliseconds -gt 0) {
    try {
        Start-Sleep -Milliseconds $LeaseProbeMilliseconds
    }
    finally {
        $serverLease.Dispose()
        $appLease.Dispose()
        $runtimeLease.Dispose()
        if ($mutex) {
            try {
                if ($createdNew) {
                    $mutex.ReleaseMutex()
                }
            }
            catch {
            }
            $mutex.Dispose()
        }
    }
    exit 0
}
'@
    }

    $runComponentPsContent = $runComponentPsContent.
        Replace(
            '__COMPONENT_LOCK_PROBE_PARAMETER__',
            $componentLockProbeParameter).
        Replace(
            '__COMPONENT_LOCK_ONLY_PROBE_BLOCK__',
            $componentLockOnlyProbeBlock).
        Replace(
            '__ALLOW_STANDALONE_APP__',
            $allowStandaloneApp).
        Replace(
            '__CERTIFICATION_VALIDATOR__',
            $certificationValidationContent).
        Replace('__DOTNET_EXE__', $DotnetExe).
        Replace('__CERTIFICATION_ID__', $CertificationId).
        Replace('__CERTIFICATION_MODE__', $CertificationMode).
        Replace('__PASSWORD_RESET_COUNT__', [string]$PasswordResetCount)
    $runAllPsContent = $runAllPsContent.
        Replace(
            '__RUN_ALL_LOCK_PROBE_PARAMETER__',
            $runAllLockProbeParameter).
        Replace(
            '__RUN_ALL_LOCK_ONLY_PROBE_BLOCK__',
            $runAllLockOnlyProbeBlock).
        Replace(
            '__CERTIFICATION_VALIDATOR__',
            $certificationValidationContent).
        Replace('__DOTNET_EXE__', $DotnetExe).
        Replace('__CERTIFICATION_ID__', $CertificationId).
        Replace('__CERTIFICATION_MODE__', $CertificationMode).
        Replace('__PASSWORD_RESET_COUNT__', [string]$PasswordResetCount)

    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-App.cmd') -Content $runAppContent.Trim()
    Write-Utf8File -Path (Join-Path $OutputRoot 'Launch-Test-App.vbs') -Content $hiddenLauncherContent.Trim()
    Write-Utf8File -Path (Join-Path $OutputRoot 'Launcher-README.txt') -Content $launcherReadmeContent.Trim()
    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-Server.cmd') -Content $runServerContent.Trim()
    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-IsolatedComponent.ps1') -Content $runComponentPsContent -WithBom
    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-All.ps1') -Content $runAllPsContent -WithBom
    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-All.cmd') -Content $runAllContent.Trim()
}

function Assert-LegacyInvoiceCanonicalizationReportProfile {
    param(
        [Parameter(Mandatory = $true)][object]$Report,
        [Parameter(Mandatory = $true)][string]$ReportJson,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceDatabaseSha256
    )

    $expectedTopLevelProperties = @(
        'schemaVersion',
        'succeeded',
        'sourceDatabaseSha256',
        'seedScope',
        'changedGroupCount',
        'changedInvoiceCount',
        'excludedDeletedInvoiceCount',
        'beforeMetadataSha256',
        'afterMetadataSha256',
        'activeInvoiceIdsSha256',
        'latestInvoiceBusinessSha256',
        'dependencyReferencesSha256',
        'groups'
    )
    $actualTopLevelProperties = @(
        $Report.PSObject.Properties.Name)
    if (
        @(Compare-Object `
            -ReferenceObject $expectedTopLevelProperties `
            -DifferenceObject $actualTopLevelProperties).Count -ne 0
    ) {
        throw 'Canonicalization report properties do not match schema 2.'
    }

    $expectedBeforeMetadataSha256 =
        '8A324FC2831CF3C8F996D8D6EA6B7AD01EDBFB7E793C5CB0548ED534F960904D'
    $expectedAfterMetadataSha256 =
        '3EE8A9B5E52A2AD014AB9FFD65574D70A562E867B0C12256CA7BB7168AE1230B'
    $expectedActiveInvoiceIdsSha256 =
        '0D2CCBFEDEDA9540F4C5898187BAA7BFC3418D6272112C01772C7CE834AB076E'
    $originalApprovedSourceDatabaseSha256 =
        '795B5A6CA153B788C6272222D778D714DB10873541775493AB7B36EA091E2FBE'
    $currentApprovedSourceDatabaseSha256 =
        'E98DF3E657205319F595AE61089F50E1B87F0BD272C650827AA123B4A8616916'
    $latestApprovedSourceDatabaseSha256 =
        '719380E811BB04DC364FB6D2E0BD4C4E04B3D3C12F4D56207233D600F80B9A5C'
    if ([string]::Equals(
            $ExpectedSourceDatabaseSha256,
            $originalApprovedSourceDatabaseSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        $expectedLatestInvoiceBusinessSha256 =
            'C80296708B5E84B5401D1D393CFA5FD2D117708C4B3F611BD3156330469D01EA'
        $expectedDependencyReferencesSha256 =
            '6F7DA4EFEE728601EF5AADBC60F0AB08C59DA70A3A7D49D7B74BBA652DD1ECB9'
    }
    elseif ([string]::Equals(
            $ExpectedSourceDatabaseSha256,
            $currentApprovedSourceDatabaseSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        $expectedLatestInvoiceBusinessSha256 =
            'EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8'
        $expectedDependencyReferencesSha256 =
            'D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7'
    }
    elseif ([string]::Equals(
            $ExpectedSourceDatabaseSha256,
            $latestApprovedSourceDatabaseSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        $expectedLatestInvoiceBusinessSha256 =
            'EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8'
        $expectedDependencyReferencesSha256 =
            'D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7'
    }
    else {
        throw 'Canonicalization report source snapshot is not approved.'
    }
    if (
        ($Report.schemaVersion -isnot [int] -and
            $Report.schemaVersion -isnot [long]) -or
        [int]$Report.schemaVersion -ne 2 -or
        $Report.succeeded -isnot [bool] -or
        -not [bool]$Report.succeeded -or
        ($Report.changedGroupCount -isnot [int] -and
            $Report.changedGroupCount -isnot [long]) -or
        ($Report.changedInvoiceCount -isnot [int] -and
            $Report.changedInvoiceCount -isnot [long]) -or
        ($Report.excludedDeletedInvoiceCount -isnot [int] -and
            $Report.excludedDeletedInvoiceCount -isnot [long]) -or
        [string]$Report.seedScope -cne
            'active_operational_seed_only_not_deleted_history_migration' -or
        -not [string]::Equals(
            [string]$Report.sourceDatabaseSha256,
            $ExpectedSourceDatabaseSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        [int]$Report.changedGroupCount -ne 5 -or
        [int]$Report.changedInvoiceCount -ne 5 -or
        [int]$Report.excludedDeletedInvoiceCount -ne 2 -or
        [string]$Report.beforeMetadataSha256 -cne
            $expectedBeforeMetadataSha256 -or
        [string]$Report.afterMetadataSha256 -cne
            $expectedAfterMetadataSha256 -or
        [string]$Report.activeInvoiceIdsSha256 -cne
            $expectedActiveInvoiceIdsSha256 -or
        [string]$Report.latestInvoiceBusinessSha256 -cne
            $expectedLatestInvoiceBusinessSha256 -or
        [string]$Report.dependencyReferencesSha256 -cne
            $expectedDependencyReferencesSha256
    ) {
        throw 'Canonicalization report does not match the approved profile.'
    }

    $groups = @($Report.groups)
    if ($groups.Count -ne 5) {
        throw 'Canonicalization report must contain exactly five groups.'
    }
    if (
        [regex]::IsMatch(
            $ReportJson,
            '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b')
    ) {
        throw 'Canonicalization report contains a raw identifier.'
    }

    $expectedGroupProperties = @(
        'groupOrdinal',
        'groupFingerprintSha256',
        'mode',
        'activeInvoiceCount',
        'excludedDeletedInvoiceCount',
        'changedMetadataFields',
        'beforeMetadataSha256',
        'afterMetadataSha256'
    )
    $expectedModeCounts = @{
        'deleted_predecessor_active_chain_reroot' = 2
        'duplicate_sibling_linearize' = 2
        'historical_responsible_office_align' = 1
    }
    $actualModeCounts = @{}
    $sanitizedGroups = @()
    for ($index = 0; $index -lt $groups.Count; $index++) {
        $group = $groups[$index]
        $actualGroupProperties = @(
            $group.PSObject.Properties.Name)
        if (
            @(Compare-Object `
                -ReferenceObject $expectedGroupProperties `
                -DifferenceObject $actualGroupProperties).Count -ne 0
        ) {
            throw 'Canonicalization group properties are not approved.'
        }
        if (
            ($group.groupOrdinal -isnot [int] -and
                $group.groupOrdinal -isnot [long]) -or
            ($group.activeInvoiceCount -isnot [int] -and
                $group.activeInvoiceCount -isnot [long]) -or
            ($group.excludedDeletedInvoiceCount -isnot [int] -and
                $group.excludedDeletedInvoiceCount -isnot [long]) -or
            [int]$group.groupOrdinal -ne ($index + 1)
        ) {
            throw 'Canonicalization group ordinals are not exact.'
        }
        foreach ($hashProperty in @(
            'groupFingerprintSha256',
            'beforeMetadataSha256',
            'afterMetadataSha256'
        )) {
            if (
                [string]$group.$hashProperty -cnotmatch
                    '^[A-F0-9]{64}$'
            ) {
                throw 'Canonicalization group hash is invalid.'
            }
        }

        $mode = [string]$group.mode
        if (-not $expectedModeCounts.ContainsKey($mode)) {
            throw 'Canonicalization report contains an unexpected mode.'
        }
        if (-not $actualModeCounts.ContainsKey($mode)) {
            $actualModeCounts[$mode] = 0
        }
        $actualModeCounts[$mode] = [int]$actualModeCounts[$mode] + 1

        $sanitizedGroups += [ordered]@{
            groupOrdinal = [int]$group.groupOrdinal
            groupFingerprintSha256 =
                [string]$group.groupFingerprintSha256
            mode = $mode
            activeInvoiceCount = [int]$group.activeInvoiceCount
            excludedDeletedInvoiceCount =
                [int]$group.excludedDeletedInvoiceCount
            beforeMetadataSha256 =
                [string]$group.beforeMetadataSha256
            afterMetadataSha256 =
                [string]$group.afterMetadataSha256
        }
    }
    foreach ($mode in $expectedModeCounts.Keys) {
        if (
            -not $actualModeCounts.ContainsKey($mode) -or
            [int]$actualModeCounts[$mode] -ne
                [int]$expectedModeCounts[$mode]
        ) {
            throw 'Canonicalization mode counts do not match the approved profile.'
        }
    }

    return [pscustomobject][ordered]@{
        schemaVersion = 2
        succeeded = $true
        sourceDatabaseSha256 =
            ([string]$Report.sourceDatabaseSha256).ToUpperInvariant()
        seedScope =
            'active_operational_seed_only_not_deleted_history_migration'
        changedGroupCount = 5
        changedInvoiceCount = 5
        excludedDeletedInvoiceCount = 2
        beforeMetadataSha256 = $expectedBeforeMetadataSha256
        afterMetadataSha256 = $expectedAfterMetadataSha256
        activeInvoiceIdsSha256 = $expectedActiveInvoiceIdsSha256
        latestInvoiceBusinessSha256 =
            $expectedLatestInvoiceBusinessSha256
        dependencyReferencesSha256 =
            $expectedDependencyReferencesSha256
        groups = $sanitizedGroups
    }
}

function Initialize-IsolatedServerData {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$TestAppRoot,
        [Parameter(Mandatory = $true)][string]$ServerDll,
        [Parameter(Mandatory = $true)][string]$ServerWorkingDirectory,
        [Parameter(Mandatory = $true)][string]$SeedLogRoot,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot,
        [Parameter(Mandatory = $true)][string]$SourceApiBaseUrl,
        [switch]$CanonicalizeLegacyInvoiceSeed,
        [string]$CanonicalizeLegacyInvoiceSeedSourceDatabaseSha256,
        [AllowNull()][object]$SourceUsersSnapshot,
        [switch]$ResetAllUserPasswords
    )

    if (-not (Test-Path -LiteralPath (Join-Path $TestAppRoot 'data\거래플랜.db'))) {
        throw "테스트 앱 데이터베이스가 없어 테스트 서버 시드를 만들 수 없습니다: $(Join-Path $TestAppRoot 'data\거래플랜.db')"
    }

    $TestAppRoot = [IO.Path]::GetFullPath($TestAppRoot)
    $ServerWorkingDirectory = [IO.Path]::GetFullPath($ServerWorkingDirectory)
    if (-not (Test-Path -LiteralPath $ServerWorkingDirectory -PathType Container)) {
        throw "격리 테스트 서버 작업 경로를 찾을 수 없습니다: $ServerWorkingDirectory"
    }

    Write-Utf8File `
        -Path (Join-Path $TestAppRoot '.georaeplan-isolated-seed-root') `
        -Content $TestAppRoot
    Write-Utf8File `
        -Path (Join-Path $ServerWorkingDirectory '.georaeplan-isolated-server-root') `
        -Content $ServerWorkingDirectory
    New-Item -ItemType Directory -Force -Path $SeedLogRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $ServerDataRoot | Out-Null
    $adminPassword = New-LocalTestPassword
    $usenetPassword = New-LocalTestPassword

    $prepareResult = Invoke-WithProcessEnvironment -Variables @{
        GEORAEPLAN_APP_ROOT = $TestAppRoot
        GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
        GEORAEPLAN_TEST_MODE = '1'
        GEORAEPLAN_TEST_SEED_MODE = '1'
        GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot
    } -Action {
        Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'prepare-test-seed')
    }

    Write-Utf8File -Path (Join-Path $SeedLogRoot 'prepare-test-seed.log') -Content $prepareResult.Text
    if ($prepareResult.ExitCode -ne 0) {
        throw "테스트 앱 데이터 시드 준비 실패`n$($prepareResult.Text)"
    }

    $canonicalizationReport = $null
    if ($CanonicalizeLegacyInvoiceSeed) {
        if (
            [string]::IsNullOrWhiteSpace(
                $CanonicalizeLegacyInvoiceSeedSourceDatabaseSha256) -or
            $CanonicalizeLegacyInvoiceSeedSourceDatabaseSha256 -cnotmatch
                '^[A-Fa-f0-9]{64}$'
        ) {
            throw '격리 레거시 청구서 정규화에 검증된 원본 DB SHA-256이 필요합니다.'
        }

        $canonicalizationLogPath =
            Join-Path `
                $SeedLogRoot `
                'canonicalize-legacy-invoice-test-seed.log'
        try {
            $canonicalizationResult =
                Invoke-WithProcessEnvironment -Variables @{
                    GEORAEPLAN_APP_ROOT = $TestAppRoot
                    GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
                    GEORAEPLAN_TEST_MODE = '1'
                    GEORAEPLAN_TEST_SEED_MODE = '1'
                    GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot
                    GEORAEPLAN_TEST_SEED_CANONICALIZE_LEGACY_INVOICES = '1'
                    GEORAEPLAN_TEST_SEED_SOURCE_DATABASE_SHA256 =
                        $CanonicalizeLegacyInvoiceSeedSourceDatabaseSha256
                } -Action {
                    Invoke-DotnetWithOutput `
                        -DotnetExe $DotnetExe `
                        -Arguments @(
                            'run',
                            '--project',
                            $SyncDiagProject,
                            '--',
                            'canonicalize-legacy-invoice-test-seed'
                        )
                }
        }
        catch {
            Write-Utf8File `
                -Path $canonicalizationLogPath `
                -Content (
                    @(
                        'canonicalization_succeeded=False',
                        'reason_code=child_process_start_failed',
                        'exit_code=unavailable',
                        'child_output_redacted=True'
                    ) -join [Environment]::NewLine)
            throw '격리 레거시 청구서 시드 정규화 자식 프로세스를 완료하지 못했습니다.'
        }
        if ($canonicalizationResult.ExitCode -ne 0) {
            Write-Utf8File `
                -Path $canonicalizationLogPath `
                -Content (
                    @(
                        'canonicalization_succeeded=False',
                        'reason_code=child_process_failed',
                        "exit_code=$($canonicalizationResult.ExitCode)",
                        'child_output_redacted=True'
                    ) -join [Environment]::NewLine)
            throw '격리 레거시 청구서 시드 정규화 자식 프로세스가 실패했습니다.'
        }

        try {
            if (
                $canonicalizationResult.Text -notmatch
                    '(?m)^legacy_invoice_seed_canonicalization_succeeded=True\s*$' -or
                $canonicalizationResult.Text -notmatch
                    '(?m)^legacy_invoice_seed_scope=active_operational_seed_only_not_deleted_history_migration\s*$'
            ) {
                throw 'Canonicalization success envelope is invalid.'
            }

            $canonicalizationHashMatch = [regex]::Match(
                $canonicalizationResult.Text,
                '(?m)^legacy_invoice_seed_canonicalization_report_sha256=([A-Fa-f0-9]{64})\s*$')
            $canonicalizationJsonMatch = [regex]::Match(
                $canonicalizationResult.Text,
                '(?m)^legacy_invoice_seed_canonicalization_json=(\{.+\})\s*$')
            if (
                -not $canonicalizationHashMatch.Success -or
                -not $canonicalizationJsonMatch.Success
            ) {
                throw 'Canonicalization result evidence is incomplete.'
            }

            $canonicalizationJson =
                $canonicalizationJsonMatch.Groups[1].Value
            $reportedCanonicalizationHash =
                $canonicalizationHashMatch.Groups[1].Value.
                    ToUpperInvariant()
            $actualCanonicalizationHash =
                (Get-Utf8TextSha256 -Text $canonicalizationJson).
                    ToUpperInvariant()
            if (-not [string]::Equals(
                    $reportedCanonicalizationHash,
                    $actualCanonicalizationHash,
                    [StringComparison]::Ordinal)) {
                throw 'Canonicalization report hash mismatch.'
            }

            $canonicalizationReport =
                $canonicalizationJson | ConvertFrom-Json
            $canonicalizationReport =
                Assert-LegacyInvoiceCanonicalizationReportProfile `
                    -Report $canonicalizationReport `
                    -ReportJson $canonicalizationJson `
                    -ExpectedSourceDatabaseSha256 `
                        $CanonicalizeLegacyInvoiceSeedSourceDatabaseSha256
            $canonicalizationEvidenceJson =
                $canonicalizationReport |
                    ConvertTo-Json -Depth 5 -Compress
            if (
                [regex]::IsMatch(
                    $canonicalizationEvidenceJson,
                    '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b')
            ) {
                throw 'Sanitized canonicalization evidence contains a raw identifier.'
            }
        }
        catch {
            Write-Utf8File `
                -Path $canonicalizationLogPath `
                -Content (
                    @(
                        'canonicalization_succeeded=False',
                        'reason_code=success_report_validation_failed',
                        "exit_code=$($canonicalizationResult.ExitCode)",
                        'child_output_redacted=True'
                    ) -join [Environment]::NewLine)
            throw '격리 레거시 청구서 시드 정규화 성공 보고서 검증이 실패했습니다.'
        }

        Write-Utf8File `
            -Path $canonicalizationLogPath `
            -Content (
                @(
                    'canonicalization_succeeded=True',
                    "report_sha256=$reportedCanonicalizationHash",
                    'child_output_redacted=True'
                ) -join [Environment]::NewLine)
        Write-Utf8File `
            -Path (
                Join-Path `
                    $SeedLogRoot `
                    'legacy-invoice-seed-canonicalization.json') `
            -Content $canonicalizationEvidenceJson
        Write-Utf8File `
            -Path (
                Join-Path `
                    $SeedLogRoot `
                    'legacy-invoice-seed-canonicalization.success') `
            -Content (
                @(
                    'canonicalization_succeeded=True',
                    "report_sha256=$reportedCanonicalizationHash",
                    'seed_scope=active_operational_seed_only_not_deleted_history_migration'
                ) -join [Environment]::NewLine)
    }

    $seedPort = Get-FreeTcpPort -StartingPort 19080
    $serverState = Start-IsolatedServerProcess -DotnetExe $DotnetExe -ServerDll $ServerDll -ServerWorkingDirectory $ServerWorkingDirectory -Port $seedPort -FileStorageRoot (Join-Path $ServerDataRoot 'FileStore') -UpdatesRoot (Join-Path $ServerDataRoot 'updates') -AdminPassword $adminPassword -UsenetPassword $usenetPassword -EnableSeedUsers $true
    $seedSummary = $null

    try {
        if (-not (Wait-HttpReady -Url ($serverState.ServerUrl + '/readyz') -TimeoutSeconds 50)) {
            throw "테스트 서버 기동 확인 실패. url=$($serverState.ServerUrl) dll=$ServerDll"
        }

        $preSyncResult = Invoke-WithProcessEnvironment -Variables @{
            GEORAEPLAN_APP_ROOT = $TestAppRoot
            GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
            GEORAEPLAN_TEST_MODE = '1'
            GEORAEPLAN_TEST_SEED_MODE = '1'
            GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot
            GEORAEPLAN_TEST_SERVER_ROOT = $ServerWorkingDirectory
            GEORAEPLAN_TEST_SERVER_BASEURL = ($serverState.ServerUrl + '/')
            GEORAEPLAN_SYNC_USERNAME = 'admin'
            GEORAEPLAN_SYNC_PASSWORD = $adminPassword
            GEORAEPLAN_SYNC_BASEURL = ($serverState.ServerUrl + '/')
        } -Action {
            Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'preseed-sync')
        }

        Write-Utf8File -Path (Join-Path $SeedLogRoot 'pre-seed-sync.log') -Content $preSyncResult.Text
        $preSeedSyncSucceeded = $preSyncResult.ExitCode -eq 0 -and $preSyncResult.Text -match 'sync_ok=(True|true)'
        if (-not $preSeedSyncSucceeded) {
            throw "테스트 서버 기본 데이터 동기화 준비 실패`n$($preSyncResult.Text)"
        }

        $markResult = Invoke-WithProcessEnvironment -Variables @{
            GEORAEPLAN_APP_ROOT = $TestAppRoot
            GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
            GEORAEPLAN_TEST_MODE = '1'
            GEORAEPLAN_TEST_SEED_MODE = '1'
            GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot
        } -Action {
            Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'mark-all-dirty')
        }

        Write-Utf8File -Path (Join-Path $SeedLogRoot 'mark-all-dirty.log') -Content $markResult.Text
        if ($markResult.ExitCode -ne 0) {
            $markDirtyWarning = @(
                '테스트 앱 데이터 전체 dirty 표시가 완료되지 않았습니다.',
                '첨부파일 보호 트리거 또는 기존 테스트 데이터 제약으로 일부 행만 표시될 수 있습니다.',
                '',
                $markResult.Text
            ) -join [Environment]::NewLine
            Write-Utf8File -Path (Join-Path $SeedLogRoot 'mark-all-dirty-warning.log') -Content $markDirtyWarning

            throw $markDirtyWarning
        }

        $maxSeedSyncAttempts = 3
        $seedSyncAttemptOutputs = [Collections.Generic.List[string]]::new()
        $seedSyncAttemptCount = 0
        $seedSyncSucceeded = $false
        $syncResult = $null
        for ($seedSyncAttempt = 1; $seedSyncAttempt -le $maxSeedSyncAttempts; $seedSyncAttempt++) {
            $seedSyncAttemptCount = $seedSyncAttempt
            $syncResult = Invoke-WithProcessEnvironment -Variables @{
                GEORAEPLAN_APP_ROOT = $TestAppRoot
                GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
                GEORAEPLAN_TEST_MODE = '1'
                GEORAEPLAN_TEST_SEED_MODE = '1'
                GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot
                GEORAEPLAN_TEST_SERVER_ROOT = $ServerWorkingDirectory
                GEORAEPLAN_TEST_SERVER_BASEURL = ($serverState.ServerUrl + '/')
                GEORAEPLAN_SYNC_USERNAME = 'admin'
                GEORAEPLAN_SYNC_PASSWORD = $adminPassword
                GEORAEPLAN_SYNC_BASEURL = ($serverState.ServerUrl + '/')
            } -Action {
                Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'sync')
            }

            $seedSyncAttemptLog = @(
                "attempt=$seedSyncAttempt",
                "exit_code=$($syncResult.ExitCode)",
                $syncResult.Text
            ) -join [Environment]::NewLine
            $seedSyncAttemptOutputs.Add($seedSyncAttemptLog)
            Write-Utf8File `
                -Path (Join-Path $SeedLogRoot ("seed-sync-attempt-{0}.log" -f $seedSyncAttempt)) `
                -Content $seedSyncAttemptLog

            $seedSyncSucceeded =
                $syncResult.ExitCode -eq 0 -and
                $syncResult.Text -match 'sync_ok=(True|true)' -and
                [regex]::IsMatch($syncResult.Text, '(?m)^dirty_count=0\s*$') -and
                [regex]::IsMatch(
                    $syncResult.Text,
                    '(?m)^non_acknowledged_outbox_count=0\s*$')
            if ($seedSyncSucceeded) {
                break
            }

            if ($seedSyncAttempt -lt $maxSeedSyncAttempts) {
                if (-not (Wait-HttpReady -Url ($serverState.ServerUrl + '/readyz') -TimeoutSeconds 10)) {
                    throw "테스트 서버가 시드 동기화 재시도 전에 종료되었습니다. url=$($serverState.ServerUrl)"
                }

                $retryPreparationResult = Invoke-WithProcessEnvironment -Variables @{
                    GEORAEPLAN_APP_ROOT = $TestAppRoot
                    GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
                    GEORAEPLAN_TEST_MODE = '1'
                    GEORAEPLAN_TEST_SEED_MODE = '1'
                    GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot
                } -Action {
                    Invoke-DotnetWithOutput `
                        -DotnetExe $DotnetExe `
                        -Arguments @('run', '--project', $SyncDiagProject, '--', 'prepare-test-seed-retry')
                }
                Write-Utf8File `
                    -Path (Join-Path $SeedLogRoot ("seed-sync-retry-preparation-{0}.log" -f $seedSyncAttempt)) `
                    -Content $retryPreparationResult.Text
                if ($retryPreparationResult.ExitCode -ne 0) {
                    throw "테스트 서버 데이터 시드 재시도 준비 실패`n$($retryPreparationResult.Text)"
                }

                Start-Sleep -Milliseconds 250
            }
        }

        Write-Utf8File `
            -Path (Join-Path $SeedLogRoot 'seed-sync.log') `
            -Content ($seedSyncAttemptOutputs -join ([Environment]::NewLine + [Environment]::NewLine))
        if (-not $seedSyncSucceeded) {
            $seedSyncWarning = @(
                "테스트 서버 데이터 시드 동기화가 ${seedSyncAttemptCount}회 시도 후에도 완료되지 않았습니다.",
                '로컬 AppData에 미동기화/충돌 데이터가 있을 때 발생할 수 있습니다.',
                '미동기화 데이터가 남은 테스트 서버는 완성된 실행환경으로 만들 수 없습니다.',
                '',
                $syncResult.Text
            ) -join [Environment]::NewLine
            Write-Utf8File -Path (Join-Path $SeedLogRoot 'seed-sync-warning.log') -Content $seedSyncWarning
            $failedSeedSummary = @(
                "seed_server_url=$($serverState.ServerUrl)",
                "seed_sync_log=$(Join-Path $SeedLogRoot 'seed-sync.log')",
                "seed_sync_attempts=$seedSyncAttemptCount",
                'seed_sync_succeeded=False',
                "source_api_base_url_configured=$($null -eq $SourceUsersSnapshot)",
                "source_users_snapshot_file_used=$($null -ne $SourceUsersSnapshot)"
            ) -join [Environment]::NewLine
            Write-Utf8File -Path (Join-Path $SeedLogRoot 'seed-summary.txt') -Content $failedSeedSummary
            throw $seedSyncWarning
        }

        $storedCredentials = if ($ResetAllUserPasswords) {
            @()
        }
        else {
            @(
                Get-StoredSyncCredentialsFromLocalState `
                    -DotnetExe $DotnetExe `
                    -SyncDiagProject $SyncDiagProject `
                    -AppRoot $TestAppRoot `
                    -LogPath (
                        Join-Path `
                            $SeedLogRoot `
                            'stored-sync-credentials.log')
            )
        }

        $resolvedSourceUsersSnapshot = Resolve-SourceUsersSnapshot `
            -FileSnapshot $SourceUsersSnapshot `
            -BaseUrl $SourceApiBaseUrl `
            -StoredCredentials $storedCredentials `
            -LogPath (Join-Path $SeedLogRoot 'source-users.json')
        $sourceUsersRestored =
            $null -ne $resolvedSourceUsersSnapshot -and
            [bool]$resolvedSourceUsersSnapshot.IsComplete

        $sourceUsers = @(
            Resolve-IsolatedSourceUsers `
                -SourceUsersSnapshot $resolvedSourceUsersSnapshot `
                -StoredCredentials $storedCredentials `
                -AllowFallback:$AllowFallbackOperationalUsers
        )

        $resolvedUsers = Resolve-IsolatedUserDefinitions `
            -SourceUsers $sourceUsers `
            -StoredCredentials $storedCredentials `
            -ResetUnresolvedPasswords:$ResetUnresolvedUserPasswordsForIsolatedTest `
            -ResetAllPasswords:$ResetAllUserPasswords
        $resolvedUsersSanitized = @(
            $resolvedUsers | ForEach-Object {
                [pscustomobject]@{
                    Username = [string]$_.Username
                    Role = [string]$_.Role
                    OfficeCode = [string]$_.OfficeCode
                    TenantCode = [string]$_.TenantCode
                    ScopeType = [string]$_.ScopeType
                    IsActive = [bool]$_.IsActive
                    Permissions = @($_.Permissions)
                    PasswordResolved = -not [string]::IsNullOrWhiteSpace([string]$_.Password)
                    PasswordWasReset = [bool]$_.PasswordWasReset
                }
            }
        )
        Write-Utf8File -Path (Join-Path $SeedLogRoot 'resolved-users.json') -Content ($resolvedUsersSanitized | ConvertTo-Json -Depth 20)
        $resetPasswordUserCount = @(
            $resolvedUsers |
                Where-Object { [bool]$_.PasswordWasReset }
        ).Count

        if ($ResetAllUserPasswords) {
            Assert-IsolatedAllUserPasswordResetResult `
                -SourceUsers $sourceUsers `
                -ResolvedUsers $resolvedUsers
        }

        Sync-IsolatedServerUsers `
            -TargetBaseUrl $serverState.ServerUrl `
            -AdminPassword $adminPassword `
            -Users $resolvedUsers `
            -LogPath (Join-Path $SeedLogRoot 'user-bootstrap.json')

        $seedSummary = @(
            "seed_server_url=$($serverState.ServerUrl)",
            "seed_sync_log=$(Join-Path $SeedLogRoot 'seed-sync.log')",
            "seed_sync_attempts=$seedSyncAttemptCount",
            "seed_sync_succeeded=$seedSyncSucceeded",
            "source_api_base_url_configured=$($null -eq $SourceUsersSnapshot)",
            "source_users_snapshot_file_used=$($null -ne $SourceUsersSnapshot)",
            "source_users_snapshot_sha256=$(if ($null -ne $SourceUsersSnapshot) { [string]$SourceUsersSnapshot.SnapshotSha256 } else { 'none' })",
            "source_users_canonical_sha256=$(if ($null -ne $SourceUsersSnapshot) { [string]$SourceUsersSnapshot.CanonicalSha256 } else { 'none' })",
            "source_users_restored=$sourceUsersRestored",
            "isolated_test_password_reset_count=$resetPasswordUserCount",
            "legacy_invoice_seed_canonicalization_enabled=$([bool]$CanonicalizeLegacyInvoiceSeed)",
            "legacy_invoice_seed_canonicalization_succeeded=$([bool]($null -ne $canonicalizationReport))",
            "legacy_invoice_seed_canonicalization_changed_groups=$(if ($null -ne $canonicalizationReport) { [int]$canonicalizationReport.changedGroupCount } else { 0 })",
            "legacy_invoice_seed_canonicalization_excluded_deleted_invoices=$(if ($null -ne $canonicalizationReport) { [int]$canonicalizationReport.excludedDeletedInvoiceCount } else { 0 })",
            'legacy_invoice_seed_scope=active_operational_seed_only_not_deleted_history_migration',
            "user_bootstrap_log=$(Join-Path $SeedLogRoot 'user-bootstrap.json')"
        ) -join [Environment]::NewLine
    }
    finally {
        Stop-IsolatedServerProcess -State $serverState
    }

    $initialFinalization = Complete-IsolatedServerSqliteSnapshot `
        -DotnetExe $DotnetExe `
        -SyncDiagProject $SyncDiagProject `
        -ServerWorkingDirectory $ServerWorkingDirectory `
        -SeedLogRoot $SeedLogRoot `
        -LogFileName 'server-sqlite-finalize-before-smoke.log'

    $restartSmokePort = Get-FreeTcpPort -StartingPort 19080
    $restartSmokeState = $null
    $restartSmokeLog = [Collections.Generic.List[string]]::new()
    $restartSmokeLog.Add("restart_smoke_port=$restartSmokePort")
    try {
        $restartSmokeState = Start-IsolatedServerProcess `
            -DotnetExe $DotnetExe `
            -ServerDll $ServerDll `
            -ServerWorkingDirectory $ServerWorkingDirectory `
            -Port $restartSmokePort `
            -FileStorageRoot (Join-Path $ServerDataRoot 'FileStore') `
            -UpdatesRoot (Join-Path $ServerDataRoot 'updates') `
            -AdminPassword $adminPassword `
            -UsenetPassword $usenetPassword `
            -EnableSeedUsers $false
        $restartSmokeLog.Add("restart_smoke_pid=$($restartSmokeState.Process.Id)")

        $restartSmokeReady = Wait-HttpReady `
            -Url ($restartSmokeState.ServerUrl + '/readyz') `
            -TimeoutSeconds 50
        $restartSmokeLog.Add("restart_smoke_health_ready=$restartSmokeReady")
        if (-not $restartSmokeReady) {
            throw "마무리된 격리 SQLite DB로 테스트 서버를 재기동할 수 없습니다. url=$($restartSmokeState.ServerUrl)"
        }

        Assert-IsolatedServerUserState `
            -TargetBaseUrl $restartSmokeState.ServerUrl `
            -AdminPassword $adminPassword `
            -Users $resolvedUsers `
            -LogPath (Join-Path $SeedLogRoot 'user-bootstrap-after-restart.json')
        $restartSmokeLog.Add('restart_smoke_user_state_verified=True')
    }
    finally {
        Stop-IsolatedServerProcess -State $restartSmokeState
        $restartSmokeLog.Add('restart_smoke_process_stopped=True')
        Write-Utf8File `
            -Path (Join-Path $SeedLogRoot 'server-sqlite-restart-smoke.log') `
            -Content ($restartSmokeLog -join [Environment]::NewLine)
    }

    $finalFinalization = Complete-IsolatedServerSqliteSnapshot `
        -DotnetExe $DotnetExe `
        -SyncDiagProject $SyncDiagProject `
        -ServerWorkingDirectory $ServerWorkingDirectory `
        -SeedLogRoot $SeedLogRoot `
        -LogFileName 'server-sqlite-finalize-after-smoke.log'
    $appFinalization = Complete-IsolatedAppSqliteSnapshot `
        -DotnetExe $DotnetExe `
        -SyncDiagProject $SyncDiagProject `
        -TestAppRoot $TestAppRoot `
        -SeedLogRoot $SeedLogRoot `
        -LogFileName 'app-sqlite-finalize.log'

    $seedSummary = @(
        $seedSummary,
        'server_sqlite_finalized=True',
        "server_sqlite_finalize_before_smoke_log=$($initialFinalization.LogPath)",
        'server_sqlite_restart_smoke=True',
        "server_sqlite_restart_smoke_log=$(Join-Path $SeedLogRoot 'server-sqlite-restart-smoke.log')",
        'server_user_state_after_restart_verified=True',
        "server_user_state_after_restart_log=$(Join-Path $SeedLogRoot 'user-bootstrap-after-restart.json')",
        "server_sqlite_finalize_after_smoke_log=$($finalFinalization.LogPath)",
        'app_sqlite_finalized=True',
        "app_sqlite_finalize_log=$($appFinalization.LogPath)"
    ) -join [Environment]::NewLine
    Write-Utf8File -Path (Join-Path $SeedLogRoot 'seed-summary.txt') -Content $seedSummary
}

function Invoke-TestEnvironmentPreparationFaultPoint {
    param(
        [Parameter(Mandatory = $true)][string]$Point,
        [switch]$Rollback
    )

    $environmentName = if ($Rollback) {
        'GEORAEPLAN_PREPARATION_ROLLBACK_FAULT_POINT'
    }
    else {
        'GEORAEPLAN_PREPARATION_FAULT_POINT'
    }
    $requestedPoint = [Environment]::GetEnvironmentVariable(
        $environmentName,
        'Process')
    if (
        -not [string]::IsNullOrWhiteSpace($requestedPoint) -and
        [string]::Equals(
            $requestedPoint.Trim(),
            $Point,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Deterministic preparation fault: $Point"
    }
}

function New-IsolatedRuntimePromotionWorkspace {
    param([Parameter(Mandatory = $true)][string]$OutputRoot)

    $normalizedOutputRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if (-not [string]::Equals(
            [IO.Path]::GetPathRoot($normalizedOutputRoot),
            'D:\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The isolated runtime promotion workspace must remain on D:.'
    }
    $parent = Split-Path -Parent $normalizedOutputRoot
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw "The OutputRoot parent does not exist: $parent"
    }
    $parentItem = Get-Item -LiteralPath $parent -Force -ErrorAction Stop
    if (
        -not $parentItem.PSIsContainer -or
        ($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "The OutputRoot parent must be a plain directory: $parent"
    }

    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $parentRootLease = $nativeType::CreateFileW(
        $parent,
        $nativeType::FileReadAttributes,
        ($nativeType::FileShareRead -bor $nativeType::FileShareWrite),
        [IntPtr]::Zero,
        $nativeType::OpenExisting,
        ($nativeType::FileFlagBackupSemantics -bor
         $nativeType::FileFlagOpenReparsePoint),
        [IntPtr]::Zero)
    if ($parentRootLease.IsInvalid) {
        $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $parentRootLease.Dispose()
        throw (New-Object ComponentModel.Win32Exception(
            $error,
            "Unable to lease the OutputRoot parent: $parent"))
    }
    $parentInformation = $nativeType::GetFileInformation($parentRootLease)
    $parentFinalPath = ConvertTo-NormalizedFullPath `
        -Path ($nativeType::GetFinalPath($parentRootLease))
    if (-not [string]::Equals(
            $parentFinalPath,
            (ConvertTo-NormalizedFullPath -Path $parent),
            [StringComparison]::OrdinalIgnoreCase)) {
        $parentRootLease.Dispose()
        throw 'The OutputRoot parent handle resolved to an unexpected path.'
    }
    $parent = $parentFinalPath
    $outputRootLease = $nativeType::CreateFileW(
        $normalizedOutputRoot,
        $nativeType::FileReadAttributes,
        ($nativeType::FileShareRead -bor $nativeType::FileShareWrite),
        [IntPtr]::Zero,
        $nativeType::OpenExisting,
        ($nativeType::FileFlagBackupSemantics -bor
         $nativeType::FileFlagOpenReparsePoint),
        [IntPtr]::Zero)
    if ($outputRootLease.IsInvalid) {
        $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $outputRootLease.Dispose()
        $parentRootLease.Dispose()
        throw (New-Object ComponentModel.Win32Exception(
            $error,
            "Unable to lease OutputRoot: $normalizedOutputRoot"))
    }
    $outputRootInformation =
        $nativeType::GetFileInformation($outputRootLease)

    if ([string]::Equals(
            $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS,
            '1',
            [StringComparison]::Ordinal)) {
        $swapSource =
            $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_SOURCE
        $swapProtected =
            $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_PROTECTED
        if (
            -not [string]::IsNullOrWhiteSpace($swapSource) -and
            -not [string]::IsNullOrWhiteSpace($swapProtected) -and
            [string]::Equals(
                (ConvertTo-NormalizedFullPath -Path $swapSource),
                $parent,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            $retainedParent = $parent + '.test-retained'
            try {
                [IO.Directory]::Move($parent, $retainedParent)
                $junctionOutput = & cmd.exe /d /c mklink /J `
                    $parent `
                    (ConvertTo-NormalizedFullPath -Path $swapProtected) 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "Unable to create deterministic parent junction: $junctionOutput"
                }
                $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_RESULT = 'swapped'
                throw 'The OutputRoot parent substitution unexpectedly succeeded.'
            }
            catch [System.IO.IOException] {
                $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_RESULT = 'blocked'
            }
            catch [System.UnauthorizedAccessException] {
                $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_RESULT = 'blocked'
            }
        }
    }

    $transactionId = [Guid]::NewGuid().ToString('N')
    $outputLeaf = Split-Path -Leaf $normalizedOutputRoot
    $stageRoot = Join-Path `
        $parent `
        ('.georaeplan-stage-{0}-{1}' -f $outputLeaf, $transactionId)
    $backupRoot = Join-Path `
        $parent `
        ('.georaeplan-backup-{0}-{1}' -f $outputLeaf, $transactionId)
    $quarantineRoot = Join-Path `
        $parent `
        ('.georaeplan-quarantine-{0}-{1}' -f $outputLeaf, $transactionId)
    foreach ($privateRoot in @($stageRoot, $backupRoot, $quarantineRoot)) {
        if (Test-Path -LiteralPath $privateRoot) {
            throw "Private promotion path already exists: $privateRoot"
        }
        if (-not [string]::Equals(
                [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($privateRoot)),
                [IO.Path]::GetPathRoot($normalizedOutputRoot),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Private promotion paths must remain on the OutputRoot volume.'
        }
    }

    $privateDefinitions = @(
        @('Stage', $stageRoot),
        @('Backup', $backupRoot),
        @('Quarantine', $quarantineRoot)
    )
    $privateLeases = @{}
    try {
    foreach ($privateDefinition in $privateDefinitions) {
        $privateName = [string]$privateDefinition[0]
        $privateRoot = [string]$privateDefinition[1]
        $rootHandle = $nativeType::CreatePrivateDirectoryUnderHeldParent(
            $parentRootLease,
            $parent,
            (Split-Path -Leaf $privateRoot))

        $sentinelPath = Join-Path `
            $privateRoot `
            ('.georaeplan-private-root-{0}.sentinel' -f $transactionId)
        $sentinelContent = @(
            'schema=georaeplan-private-runtime-root-v1',
            "transaction_id=$transactionId",
            "kind=$($privateName.ToLowerInvariant())"
        ) -join [Environment]::NewLine
        try {
            $sentinelBytes = (New-Utf8NoBomEncoding).GetBytes(
                $sentinelContent)
            $sentinelHandle =
                $nativeType::CreateNewHeldFileUnderDirectory(
                    $rootHandle,
                    $privateRoot,
                    (Split-Path -Leaf $sentinelPath),
                    $sentinelBytes,
                    'SENTINEL_PRECREATE')
            $rootInformation = $nativeType::GetFileInformation($rootHandle)
            $sentinelInformation =
                $nativeType::GetFileInformation($sentinelHandle)
            $privateLeases[$privateName] = [pscustomobject]@{
                RootHandle = $rootHandle
                RootPath = $privateRoot
                RootFinalPath = $nativeType::GetFinalPath($rootHandle)
                RootVolumeSerialNumber =
                    [uint32]$rootInformation.VolumeSerialNumber
                RootFileIndexHigh = [uint32]$rootInformation.FileIndexHigh
                RootFileIndexLow = [uint32]$rootInformation.FileIndexLow
                SentinelHandle = $sentinelHandle
                SentinelPath = $sentinelPath
                SentinelContent = $sentinelContent
                SentinelFinalPath = $nativeType::GetFinalPath($sentinelHandle)
                SentinelVolumeSerialNumber =
                    [uint32]$sentinelInformation.VolumeSerialNumber
                SentinelFileIndexHigh =
                    [uint32]$sentinelInformation.FileIndexHigh
                SentinelFileIndexLow =
                    [uint32]$sentinelInformation.FileIndexLow
            }
        }
        catch {
            try {
                $nativeType::DeletePrivatePromotionTreeAndRoot(
                    $rootHandle,
                    $privateRoot)
            }
            finally {
                $rootHandle.Dispose()
            }
            throw
        }
    }
    $workspace = [pscustomobject]@{
        TransactionId = $transactionId
        OutputRoot = $normalizedOutputRoot
        OutputRootLease = $outputRootLease
        OutputRootVolumeSerialNumber =
            [uint32]$outputRootInformation.VolumeSerialNumber
        OutputRootFileIndexHigh =
            [uint32]$outputRootInformation.FileIndexHigh
        OutputRootFileIndexLow =
            [uint32]$outputRootInformation.FileIndexLow
        Parent = $parent
        StageRoot = $stageRoot
        BackupRoot = $backupRoot
        QuarantineRoot = $quarantineRoot
        PhysicalParent = Resolve-PhysicalPathIdentity -Path $parent
        ParentRootLease = $parentRootLease
        ParentFinalPath = $parentFinalPath
        ParentVolumeSerialNumber =
            [uint32]$parentInformation.VolumeSerialNumber
        ParentFileIndexHigh = [uint32]$parentInformation.FileIndexHigh
        ParentFileIndexLow = [uint32]$parentInformation.FileIndexLow
        PhysicalStageRoot = Resolve-PhysicalPathIdentity -Path $stageRoot
        PhysicalBackupRoot = Resolve-PhysicalPathIdentity -Path $backupRoot
        PhysicalQuarantineRoot =
            Resolve-PhysicalPathIdentity -Path $quarantineRoot
        StageRootLease = $privateLeases.Stage.RootHandle
        StageSentinelLease = $privateLeases.Stage.SentinelHandle
        StageSentinelPath = $privateLeases.Stage.SentinelPath
        StageIdentity = $privateLeases.Stage
        BackupRootLease = $privateLeases.Backup.RootHandle
        BackupSentinelLease = $privateLeases.Backup.SentinelHandle
        BackupSentinelPath = $privateLeases.Backup.SentinelPath
        BackupIdentity = $privateLeases.Backup
        QuarantineRootLease = $privateLeases.Quarantine.RootHandle
        QuarantineSentinelLease = $privateLeases.Quarantine.SentinelHandle
        QuarantineSentinelPath = $privateLeases.Quarantine.SentinelPath
        QuarantineIdentity = $privateLeases.Quarantine
    }
    Assert-IsolatedRuntimePromotionWorkspace -Workspace $workspace
    return $workspace
    }
    catch {
        foreach ($privateName in @('Quarantine', 'Backup', 'Stage')) {
            $identity = $privateLeases[$privateName]
            if ($null -eq $identity) {
                continue
            }
            try {
                if (
                    $null -ne $identity.SentinelHandle -and
                    -not $identity.SentinelHandle.IsClosed
                ) {
                    $nativeType::DeleteHeldExactSingleLinkRegularFile(
                        $identity.SentinelHandle,
                        [string]$identity.SentinelPath)
                    $identity.SentinelHandle.Dispose()
                }
                if (
                    $null -ne $identity.RootHandle -and
                    -not $identity.RootHandle.IsClosed
                ) {
                    $nativeType::DeletePrivatePromotionTreeAndRoot(
                        $identity.RootHandle,
                        [string]$identity.RootPath)
                    $identity.RootHandle.Dispose()
                }
            }
            catch {
                # Retain any identity that could not be safely removed.
            }
        }
        if (-not $parentRootLease.IsClosed) {
            $parentRootLease.Dispose()
        }
        if (-not $outputRootLease.IsClosed) {
            $outputRootLease.Dispose()
        }
        throw
    }
}

function Assert-IsolatedRuntimePromotionWorkspace {
    param([Parameter(Mandatory = $true)][object]$Workspace)

    $expectedParent = [IO.Path]::GetFullPath(
        [string]$Workspace.Parent).TrimEnd('\')
    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $parentRootLease = $Workspace.ParentRootLease
    if (
        $null -eq $parentRootLease -or
        $parentRootLease.IsInvalid -or
        $parentRootLease.IsClosed
    ) {
        throw 'The private runtime promotion parent lease is closed.'
    }
    $parentInformation = $nativeType::GetFileInformation($parentRootLease)
    if (
        [uint32]$parentInformation.VolumeSerialNumber -ne
            [uint32]$Workspace.ParentVolumeSerialNumber -or
        [uint32]$parentInformation.FileIndexHigh -ne
            [uint32]$Workspace.ParentFileIndexHigh -or
        [uint32]$parentInformation.FileIndexLow -ne
            [uint32]$Workspace.ParentFileIndexLow -or
        -not [string]::Equals(
            (ConvertTo-NormalizedFullPath `
                -Path ($nativeType::GetFinalPath($parentRootLease))),
            $expectedParent,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'The private runtime promotion parent identity changed.'
    }
    $outputRootLease = $Workspace.OutputRootLease
    if (
        $null -eq $outputRootLease -or
        $outputRootLease.IsInvalid -or
        $outputRootLease.IsClosed
    ) {
        throw 'The final runtime root lease is closed.'
    }
    $outputRootInformation = $nativeType::GetFileInformation($outputRootLease)
    if (
        [uint32]$outputRootInformation.VolumeSerialNumber -ne
            [uint32]$Workspace.OutputRootVolumeSerialNumber -or
        [uint32]$outputRootInformation.FileIndexHigh -ne
            [uint32]$Workspace.OutputRootFileIndexHigh -or
        [uint32]$outputRootInformation.FileIndexLow -ne
            [uint32]$Workspace.OutputRootFileIndexLow -or
        -not [string]::Equals(
            (ConvertTo-NormalizedFullPath `
                -Path ($nativeType::GetFinalPath($outputRootLease))),
            (ConvertTo-NormalizedFullPath -Path $Workspace.OutputRoot),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw 'The final runtime root identity changed.'
    }
    $physicalDefinitions = @(
        @('StageRoot', 'PhysicalStageRoot', 'StageIdentity'),
        @('BackupRoot', 'PhysicalBackupRoot', 'BackupIdentity'),
        @('QuarantineRoot', 'PhysicalQuarantineRoot', 'QuarantineIdentity')
    )
    foreach ($definition in $physicalDefinitions) {
        $logicalPath = [IO.Path]::GetFullPath(
            [string]$Workspace.($definition[0])).TrimEnd('\')
        if (-not [string]::Equals(
                (Split-Path -Parent $logicalPath),
                $expectedParent,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A private runtime promotion path escaped its sibling parent.'
        }
        $item = Get-Item -LiteralPath $logicalPath -Force -ErrorAction Stop
        if (
            -not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "A private runtime promotion root is not plain: $logicalPath"
        }
        $actualPhysicalPath = Resolve-PhysicalPathIdentity -Path $logicalPath
        $expectedPhysicalPath = [string]$Workspace.($definition[1])
        if (-not [string]::Equals(
                $actualPhysicalPath,
                $expectedPhysicalPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "A private runtime promotion root changed identity: $logicalPath"
        }
        $identity = $Workspace.($definition[2])
        if ($null -eq $identity) {
            throw "A private runtime promotion root lease is missing: $logicalPath"
        }
        $rootHandle = $identity.RootHandle
        $sentinelHandle = $identity.SentinelHandle
        if (
            $null -eq $rootHandle -or $rootHandle.IsInvalid -or
            $rootHandle.IsClosed -or
            $null -eq $sentinelHandle -or $sentinelHandle.IsInvalid -or
            $sentinelHandle.IsClosed
        ) {
            throw "A private runtime promotion identity lease is closed: $logicalPath"
        }
        $rootInformation = $nativeType::GetFileInformation($rootHandle)
        $sentinelInformation =
            $nativeType::GetFileInformation($sentinelHandle)
        if (
            [uint32]$rootInformation.VolumeSerialNumber -ne
                [uint32]$identity.RootVolumeSerialNumber -or
            [uint32]$rootInformation.FileIndexHigh -ne
                [uint32]$identity.RootFileIndexHigh -or
            [uint32]$rootInformation.FileIndexLow -ne
                [uint32]$identity.RootFileIndexLow -or
            [uint32]$sentinelInformation.VolumeSerialNumber -ne
                [uint32]$identity.SentinelVolumeSerialNumber -or
            [uint32]$sentinelInformation.FileIndexHigh -ne
                [uint32]$identity.SentinelFileIndexHigh -or
            [uint32]$sentinelInformation.FileIndexLow -ne
                [uint32]$identity.SentinelFileIndexLow -or
            -not [string]::Equals(
                (ConvertTo-NormalizedFullPath `
                    -Path ($nativeType::GetFinalPath($rootHandle))),
                $logicalPath,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                (ConvertTo-NormalizedFullPath `
                    -Path ($nativeType::GetFinalPath($sentinelHandle))),
                (ConvertTo-NormalizedFullPath -Path $identity.SentinelPath),
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw "A private runtime promotion volume/file identity changed: $logicalPath"
        }
        $nativeType::AssertPrivateDirectoryAcl($logicalPath)
        $sentinelText = $nativeType::ReadHeldExactSingleLinkUtf8File(
            $sentinelHandle,
            [string]$identity.SentinelPath,
            $logicalPath)
        if (-not [string]::Equals(
                $sentinelText,
                [string]$identity.SentinelContent,
                [StringComparison]::Ordinal)) {
            throw "A private runtime promotion sentinel changed: $logicalPath"
        }
    }
    $actualPhysicalParent = Resolve-PhysicalPathIdentity -Path $expectedParent
    if (-not [string]::Equals(
            $actualPhysicalParent,
            [string]$Workspace.PhysicalParent,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The private runtime promotion parent changed physical identity.'
    }
}

function Close-IsolatedRuntimePromotionWorkspaceLeases {
    param([AllowNull()][object]$Workspace)

    if ($null -eq $Workspace) {
        return
    }
    foreach ($identityName in @(
        'StageIdentity', 'BackupIdentity', 'QuarantineIdentity'
    )) {
        $identity = $Workspace.$identityName
        if ($null -eq $identity) {
            continue
        }
        foreach ($handleName in @('SentinelHandle', 'RootHandle')) {
            $handle = $identity.$handleName
            if ($null -ne $handle -and -not $handle.IsClosed) {
                $handle.Dispose()
            }
        }
    }
    $parentRootLease = $Workspace.ParentRootLease
    $outputRootLease = $Workspace.OutputRootLease
    if ($null -ne $outputRootLease -and -not $outputRootLease.IsClosed) {
        $outputRootLease.Dispose()
    }
    if ($null -ne $parentRootLease -and -not $parentRootLease.IsClosed) {
        $parentRootLease.Dispose()
    }
}

function New-StagedRuntimePreparationLease {
    param([Parameter(Mandatory = $true)][object]$Workspace)

    Assert-IsolatedRuntimePromotionWorkspace -Workspace $Workspace
    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $stageRoot = [string]$Workspace.StageRoot
    $leasePath = Join-Path $stageRoot '.georaeplan-prepare.lock'
    $handle = $null
    try {
        $handle = $nativeType::CreateNewHeldPreparationLeaseUnderDirectory(
            $Workspace.StageRootLease,
            $stageRoot,
            '.georaeplan-prepare.lock',
            'PREPARATION_LEASE_PRECREATE')
        $information = $nativeType::GetFileInformation($handle)
        return [pscustomobject]@{
            Path = $leasePath
            Handle = $handle
            ParentHandle = $Workspace.StageRootLease
            ParentPath = $stageRoot
            Leaf = '.georaeplan-prepare.lock'
            VolumeSerialNumber = [uint32]$information.VolumeSerialNumber
            FileIndexHigh = [uint32]$information.FileIndexHigh
            FileIndexLow = [uint32]$information.FileIndexLow
            Deleted = $false
        }
    }
    catch {
        if ($null -ne $handle -and -not $handle.IsClosed) {
            $handle.Dispose()
        }
        throw
    }
}

function Close-StagedRuntimePreparationLease {
    param([AllowNull()][object]$Lease)

    if ($null -eq $Lease -or [bool]$Lease.Deleted) {
        return
    }
    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $handle = $Lease.Handle
    if ($null -ne $handle) {
        try {
            if (-not $handle.IsClosed) {
                $handle.Dispose()
            }
        }
        finally {
            $Lease.Handle = $null
        }
    }

    $deletionHandle = $null
    $deletionHandle =
        $nativeType::ReopenHeldPreparationLeaseForDeletion(
            $Lease.ParentHandle,
            [string]$Lease.ParentPath,
            [string]$Lease.Leaf,
            [uint32]$Lease.VolumeSerialNumber,
            [uint32]$Lease.FileIndexHigh,
            [uint32]$Lease.FileIndexLow)
    try {
        $nativeType::DeleteHeldExactSingleLinkRegularFile(
            $deletionHandle,
            [string]$Lease.Path)
    }
    finally {
        if (
            $null -ne $deletionHandle -and
            -not $deletionHandle.IsClosed
        ) {
            $deletionHandle.Dispose()
        }
    }
    $Lease.Deleted = $true
    if (Test-Path -LiteralPath ([string]$Lease.Path)) {
        throw 'The exact staged preparation lifetime lease was not deleted.'
    }
}

function Get-IsolatedRuntimeManagedComponents {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$StageRoot,
        [Parameter(Mandatory = $true)][string]$BackupRoot,
        [switch]$ReplaceAppData,
        [switch]$RequireLaunchers
    )

    $definitions = @(
        @('App', 'App', 'Directory', $true),
        @('Server', 'Server', 'Directory', $true)
    )
    if ($ReplaceAppData) {
        $definitions += ,@('AppData', 'AppData', 'Directory', $true)
    }
    $definitions += @(
        @('ServerData', 'ServerData', 'Directory', $true),
        @('Mobile', 'Mobile', 'Directory', $true),
        @('Set-ApiBaseUrl.ps1', 'Set-ApiBaseUrl.ps1', 'File', $true),
        @('Run-App.cmd', 'Run-App.cmd', 'File', [bool]$RequireLaunchers),
        @('Launch-Test-App.vbs', 'Launch-Test-App.vbs', 'File', [bool]$RequireLaunchers),
        @('Launcher-README.txt', 'Launcher-README.txt', 'File', [bool]$RequireLaunchers),
        @('Run-Server.cmd', 'Run-Server.cmd', 'File', [bool]$RequireLaunchers),
        @('Run-IsolatedComponent.ps1', 'Run-IsolatedComponent.ps1', 'File', [bool]$RequireLaunchers),
        @('Run-All.ps1', 'Run-All.ps1', 'File', [bool]$RequireLaunchers),
        @('Run-All.cmd', 'Run-All.cmd', 'File', [bool]$RequireLaunchers)
    )

    return @(
        foreach ($definition in $definitions) {
            [pscustomobject]@{
                Name = [string]$definition[0]
                RelativePath = [string]$definition[1]
                Kind = [string]$definition[2]
                RequiredStage = [bool]$definition[3]
                FinalPath = Join-Path $OutputRoot ([string]$definition[1])
                StagePath = Join-Path $StageRoot ([string]$definition[1])
                BackupPath = Join-Path $BackupRoot ([string]$definition[1])
            }
        }
    )
}

function Get-RuntimeMarkerByteSnapshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $exists = Test-Path -LiteralPath $Path -PathType Leaf
    return [pscustomobject]@{
        Path = [IO.Path]::GetFullPath($Path)
        Exists = $exists
        Bytes = if ($exists) { [IO.File]::ReadAllBytes($Path) } else { $null }
    }
}

function Restore-RuntimeMarkerByteSnapshot {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ([bool]$Snapshot.Exists) {
        [IO.File]::WriteAllBytes(
            [string]$Snapshot.Path,
            [byte[]]$Snapshot.Bytes)
    }
    elseif (Test-Path -LiteralPath ([string]$Snapshot.Path)) {
        Remove-Item `
            -LiteralPath ([string]$Snapshot.Path) `
            -Force `
            -ErrorAction Stop
    }
}

function New-RuntimePreparationTransaction {
    param(
        [Parameter(Mandatory = $true)][object]$Workspace,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()][object[]]$Components,
        [Parameter(Mandatory = $true)][object]$ReadyMarkerSnapshot,
        [Parameter(Mandatory = $true)][object]$InvalidMarkerSnapshot
    )

    return [pscustomobject]@{
        Workspace = $Workspace
        Components = @($Components)
        ReadyMarkerSnapshot = $ReadyMarkerSnapshot
        InvalidMarkerSnapshot = $InvalidMarkerSnapshot
        InvalidMarkerLease = $null
        PromotionRecords = [Collections.Generic.List[object]]::new()
        PromotionStarted = $false
        Committed = $false
        RollbackFailed = $false
        RestoreMarkerSnapshots = $true
        MarkerRollbackFailureReason = ''
    }
}

function Set-RuntimePreparationRollbackMarkersFailClosed {
    param(
        [Parameter(Mandatory = $true)][object]$Transaction,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $Transaction.RestoreMarkerSnapshots = $false
    $Transaction.MarkerRollbackFailureReason = $Reason
}

function Move-IsolatedRuntimePromotionPath {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][ValidateSet('Directory', 'File')]
        [string]$Kind
    )

    $destinationParent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        throw "The handle-bound move parent is missing: $destinationParent"
    }
    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    $nativeType::MoveExactPathByHandle(
        $Source,
        $Destination,
        ($Kind -ceq 'Directory'))
}

function Invoke-IsolatedRuntimeComponentPromotion {
    param([Parameter(Mandatory = $true)][object]$Transaction)

    if ($Transaction.Committed -or $Transaction.PromotionStarted) {
        throw 'The isolated runtime promotion transaction cannot be reused.'
    }
    $Transaction.PromotionStarted = $true
    if (-not (Test-Path `
            -LiteralPath $Transaction.Workspace.BackupRoot `
            -PathType Container)) {
        throw 'The private runtime backup root disappeared before promotion.'
    }

    foreach ($component in @($Transaction.Components)) {
        $stageExists = Test-Path -LiteralPath $component.StagePath
        if ([bool]$component.RequiredStage -and -not $stageExists) {
            throw "Required staged runtime component is missing: $($component.Name)"
        }
        if ($stageExists) {
            $stageItem = Get-Item -LiteralPath $component.StagePath -Force
            $stageKind = if ($stageItem.PSIsContainer) { 'Directory' } else { 'File' }
            if ($stageKind -cne [string]$component.Kind) {
                throw "Staged runtime component kind is invalid: $($component.Name)"
            }
            if (($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Staged runtime component is a reparse point: $($component.Name)"
            }
        }

        $record = [pscustomobject]@{
            Component = $component
            FinalMovedToBackup = $false
            StageMovedToFinal = $false
        }
        $Transaction.PromotionRecords.Add($record)
        if (Test-Path -LiteralPath $component.FinalPath) {
            Move-IsolatedRuntimePromotionPath `
                -Source $component.FinalPath `
                -Destination $component.BackupPath `
                -Kind $component.Kind
            $record.FinalMovedToBackup = $true
        }

        $faultPrefix = if ($component.Kind -ceq 'File') {
            'root-file'
        }
        else {
            'component'
        }
        Invoke-TestEnvironmentPreparationFaultPoint `
            -Point ($faultPrefix + ':' + [string]$component.Name)

        if ($stageExists) {
            Move-IsolatedRuntimePromotionPath `
                -Source $component.StagePath `
                -Destination $component.FinalPath `
                -Kind $component.Kind
            $record.StageMovedToFinal = $true
        }
    }
}

function Write-RuntimePromotionRollbackEvidence {
    param(
        [Parameter(Mandatory = $true)][object]$Transaction,
        [Parameter(Mandatory = $true)][object[]]$Failures
    )

    $quarantineRoot = [string]$Transaction.Workspace.QuarantineRoot
    New-Item `
        -ItemType Directory `
        -Path $quarantineRoot `
        -Force `
        -ErrorAction Stop |
        Out-Null
    $evidencePath = Join-Path $quarantineRoot 'rollback-failure.txt'
    Write-Utf8File `
        -Path $evidencePath `
        -Content (@(
            "transaction_id=$($Transaction.Workspace.TransactionId)",
            "output_root=$($Transaction.Workspace.OutputRoot)",
            "stage_root=$($Transaction.Workspace.StageRoot)",
            "backup_root=$($Transaction.Workspace.BackupRoot)",
            "recorded_at_utc=$([DateTime]::UtcNow.ToString('O'))",
            'failures=',
            @($Failures | ForEach-Object { ' - ' + [string]$_ })
        ) -join [Environment]::NewLine) `
        -WithBom
}

function Set-RuntimePromotionRollbackFailedClosed {
    param(
        [Parameter(Mandatory = $true)][object]$Transaction,
        [Parameter(Mandatory = $true)][object[]]$Failures
    )

    $Transaction.RollbackFailed = $true
    $readyPath = [string]$Transaction.ReadyMarkerSnapshot.Path
    $invalidPath = [string]$Transaction.InvalidMarkerSnapshot.Path
    $failClosedFailures = [Collections.Generic.List[string]]::new()
    try {
        if (Test-Path -LiteralPath $readyPath) {
            Remove-Item -LiteralPath $readyPath -Force -ErrorAction Stop
        }
    }
    catch {
        $failClosedFailures.Add("ready-marker: $($_.Exception.Message)")
    }
    try {
        if (
            $null -eq $Transaction.InvalidMarkerLease -or
            $Transaction.InvalidMarkerLease.IsClosed
        ) {
            Set-RuntimeInvalidationMarker `
                -Path $invalidPath `
                -Reason 'promotion-rollback-failed'
        }
    }
    catch {
        $failClosedFailures.Add("invalid-marker: $($_.Exception.Message)")
    }
    try {
        Write-RuntimePromotionRollbackEvidence `
            -Transaction $Transaction `
            -Failures @($Failures + $failClosedFailures)
    }
    catch {
        $failClosedFailures.Add("quarantine-evidence: $($_.Exception.Message)")
    }

    if ($failClosedFailures.Count -gt 0) {
        throw (
            'Runtime promotion rollback failed and fail-closed evidence was ' +
            'not fully writable: ' +
            ($failClosedFailures -join '; '))
    }
}

function Restore-IsolatedRuntimePromotionTransaction {
    param([Parameter(Mandatory = $true)][object]$Transaction)

    if ($Transaction.Committed) {
        throw 'A committed runtime promotion transaction cannot be rolled back.'
    }
    $rollbackFailures = [Collections.Generic.List[string]]::new()
    for ($index = $Transaction.PromotionRecords.Count - 1; $index -ge 0; $index--) {
        $record = $Transaction.PromotionRecords[$index]
        $component = $record.Component
        try {
            Invoke-TestEnvironmentPreparationFaultPoint `
                -Point ('rollback:' + [string]$component.Name) `
                -Rollback
            if ([bool]$record.StageMovedToFinal) {
                if (-not (Test-Path -LiteralPath $component.FinalPath)) {
                    throw 'Promoted final component disappeared before rollback.'
                }
                Move-IsolatedRuntimePromotionPath `
                    -Source $component.FinalPath `
                    -Destination $component.StagePath `
                    -Kind $component.Kind
            }
            if ([bool]$record.FinalMovedToBackup) {
                if (-not (Test-Path -LiteralPath $component.BackupPath)) {
                    throw 'Backed-up component disappeared before rollback.'
                }
                Move-IsolatedRuntimePromotionPath `
                    -Source $component.BackupPath `
                    -Destination $component.FinalPath `
                    -Kind $component.Kind
            }
        }
        catch {
            $rollbackFailures.Add(
                "$($component.Name): $($_.Exception.Message)")
        }
    }

    if ([bool]$Transaction.RestoreMarkerSnapshots) {
        try {
            Restore-RuntimeMarkerByteSnapshot `
                -Snapshot $Transaction.ReadyMarkerSnapshot
        }
        catch {
            $rollbackFailures.Add("ready-marker: $($_.Exception.Message)")
        }
        try {
            if (
                $null -ne $Transaction.InvalidMarkerLease -and
                -not $Transaction.InvalidMarkerLease.IsClosed
            ) {
                Restore-HeldRuntimeInvalidationMarkerSnapshot `
                    -Snapshot $Transaction.InvalidMarkerSnapshot `
                    -Lease $Transaction.InvalidMarkerLease
                $Transaction.InvalidMarkerLease.Dispose()
                $Transaction.InvalidMarkerLease = $null
            }
            else {
                Restore-RuntimeMarkerByteSnapshot `
                    -Snapshot $Transaction.InvalidMarkerSnapshot
            }
        }
        catch {
            $rollbackFailures.Add("invalid-marker: $($_.Exception.Message)")
        }
    }
    else {
        try {
            if (Test-Path -LiteralPath $Transaction.ReadyMarkerSnapshot.Path) {
                Remove-Item `
                    -LiteralPath $Transaction.ReadyMarkerSnapshot.Path `
                    -Force `
                    -ErrorAction Stop
            }
        }
        catch {
            $rollbackFailures.Add("ready-marker: $($_.Exception.Message)")
        }
        try {
            if (
                $null -eq $Transaction.InvalidMarkerLease -or
                $Transaction.InvalidMarkerLease.IsClosed
            ) {
                Set-RuntimeInvalidationMarker `
                    -Path $Transaction.InvalidMarkerSnapshot.Path `
                    -Reason $Transaction.MarkerRollbackFailureReason
            }
        }
        catch {
            $rollbackFailures.Add("invalid-marker: $($_.Exception.Message)")
        }
    }

    if ($rollbackFailures.Count -gt 0) {
        Set-RuntimePromotionRollbackFailedClosed `
            -Transaction $Transaction `
            -Failures @($rollbackFailures)
        throw (
            'The previous isolated runtime could not be completely restored. ' +
            'The runtime remains invalid and private evidence was retained. ' +
            ($rollbackFailures -join '; '))
    }
}

function Convert-StagedRuntimeRootMarkers {
    param(
        [Parameter(Mandatory = $true)][string]$StageRoot,
        [Parameter(Mandatory = $true)][string]$FinalRoot,
        [string[]]$ExcludedFullPaths = @()
    )

    $stageFullPath = [IO.Path]::GetFullPath($StageRoot).TrimEnd('\')
    $finalFullPath = [IO.Path]::GetFullPath($FinalRoot).TrimEnd('\')
    $textExtensions = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(
        '.cmd', '.ps1', '.vbs', '.txt', '.json', '.config', '.xml', '.md'
    )) {
        [void]$textExtensions.Add($extension)
    }
    $excludedPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($excludedPath in @($ExcludedFullPaths)) {
        [void]$excludedPaths.Add(
            (ConvertTo-NormalizedFullPath -Path $excludedPath))
    }
    foreach ($file in Get-ChildItem -LiteralPath $stageFullPath -File -Recurse -Force) {
        if ($excludedPaths.Contains(
                (ConvertTo-NormalizedFullPath -Path $file.FullName))) {
            continue
        }
        if (-not $textExtensions.Contains($file.Extension)) {
            continue
        }
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $hasUtf8Bom = (
            $bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and
            $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF)
        $offset = if ($hasUtf8Bom) { 3 } else { 0 }
        $content = [Text.Encoding]::UTF8.GetString(
            $bytes,
            $offset,
            $bytes.Length - $offset)
        if ($content.IndexOf(
                $stageFullPath,
                [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            continue
        }
        $rewritten = [Text.RegularExpressions.Regex]::Replace(
            $content,
            [Text.RegularExpressions.Regex]::Escape($stageFullPath),
            { param($match) $finalFullPath },
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($hasUtf8Bom) {
            Write-Utf8File -Path $file.FullName -Content $rewritten -WithBom
        }
        else {
            Write-Utf8File -Path $file.FullName -Content $rewritten
        }
    }
}

function Complete-IsolatedRuntimePromotionTransaction {
    param([Parameter(Mandatory = $true)][object]$Transaction)

    $Transaction.Committed = $true
    Assert-IsolatedRuntimePromotionWorkspace `
        -Workspace $Transaction.Workspace
    Initialize-TestEnvironmentFinalPathNativeMethods
    $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
    foreach ($identityName in @(
        'StageIdentity', 'BackupIdentity', 'QuarantineIdentity'
    )) {
        $identity = $Transaction.Workspace.$identityName
        $privateRoot = [string]$identity.RootPath
        $privateLeaf = Split-Path -Leaf $privateRoot
        if (
            $privateLeaf -notmatch
                ('^\.georaeplan-(stage|backup|quarantine)-.+-' +
                 [Text.RegularExpressions.Regex]::Escape(
                     [string]$Transaction.Workspace.TransactionId) + '$')
        ) {
            throw "Unsafe private promotion cleanup path: $privateRoot"
        }
        $nativeType::DeleteHeldExactSingleLinkRegularFile(
            $identity.SentinelHandle,
            [string]$identity.SentinelPath)
        $identity.SentinelHandle.Dispose()
        if (Test-Path -LiteralPath $identity.SentinelPath) {
            throw "The exact private promotion sentinel was not deleted: $privateRoot"
        }
        $nativeType::DeletePrivatePromotionTreeAndRoot(
            $identity.RootHandle,
            $privateRoot)
        $identity.RootHandle.Dispose()
        if (Test-Path -LiteralPath $privateRoot) {
            throw "The exact private promotion root was not deleted: $privateRoot"
        }
    }
    if (-not $Transaction.Workspace.OutputRootLease.IsClosed) {
        $Transaction.Workspace.OutputRootLease.Dispose()
    }
    if (-not $Transaction.Workspace.ParentRootLease.IsClosed) {
        $Transaction.Workspace.ParentRootLease.Dispose()
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot '실행환경'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ([string]::IsNullOrWhiteSpace($SourceAppRoot)) {
    $SourceAppRoot = Join-Path $env:LOCALAPPDATA '거래플랜'
}
$SourceAppRoot = [IO.Path]::GetFullPath($SourceAppRoot)
if (
    -not $SkipDataCopy -and
    -not (Test-Path -LiteralPath $SourceAppRoot -PathType Container)
) {
    throw (
        'SourceAppRoot does not exist. Refusing to prepare an original-data ' +
        "restore: $SourceAppRoot")
}

$solutionPath = (Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName)
if ([string]::IsNullOrWhiteSpace($solutionPath)) {
    throw "솔루션 파일을 찾지 못했습니다: $ProjectRoot"
}

$desktopProject = Find-FirstFile -Root (Join-Path $ProjectRoot 'Desktop') -Filter '*.Desktop.App.csproj'
$serverProject = Find-FirstFile -Root (Join-Path $ProjectRoot 'Server') -Filter '*.Server.Api.csproj'
$mobileProject =
    Join-Path `
        $ProjectRoot `
        'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'
Assert-SafeTestEnvironmentOutputRoot `
    -ProjectRoot $ProjectRoot `
    -ScriptRoot $scriptRoot `
    -OutputRoot $OutputRoot `
    -SourceAppRoot $SourceAppRoot `
    -DesktopSourceRoot (Split-Path -Parent $desktopProject) `
    -ServerSourceRoot (Split-Path -Parent $serverProject)
if (-not $SkipDataCopy) {
    $sourceAppRootPreflightLease =
        Enter-SourceAppRootIdentityLease -Path $SourceAppRoot
    try {
        Assert-SourceAppRootIdentityLease `
            -Lease $sourceAppRootPreflightLease
    }
    finally {
        $sourceAppRootPreflightLease.Dispose()
    }
}
$syncDiagProject = Join-Path $ProjectRoot 'tools\SyncDiag\SyncDiag.csproj'
$androidMetadataHelper =
    Join-Path $ProjectRoot 'tools\mobile\AndroidApkMetadata.ps1'
$deploymentRoot = Find-DeploymentRoot -ProjectRoot $ProjectRoot
$templatePath = Join-Path $scriptRoot '검증 체크리스트 템플릿.md'
$recordsRoot = Join-Path $scriptRoot '기록'
$changedFilesPath = Join-Path $scriptRoot '최근 수정 파일.md'
$currentChecklistPath = Join-Path $scriptRoot '검증 체크리스트.md'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$sessionRoot = Join-Path $recordsRoot $timestamp
$finalAppOutput = Join-Path $OutputRoot 'App'
$finalServerOutput = Join-Path $OutputRoot 'Server'
$finalIsolatedAppRoot = Join-Path $OutputRoot 'AppData'
$finalServerDataRoot = Join-Path $OutputRoot 'ServerData'
$appOutput = $finalAppOutput
$serverOutput = $finalServerOutput
$isolatedAppRoot = $finalIsolatedAppRoot
$serverDataRoot = $finalServerDataRoot
$runtimeReadyMarkerPath = Join-Path $OutputRoot '.georaeplan-runtime-ready'
$runtimeInvalidMarkerPath =
    Join-Path $OutputRoot '.georaeplan-runtime-invalid'
$defaultBaseUrl = 'http://127.0.0.1:19080'
$setApiSource = Join-Path $deploymentRoot 'Set-ApiBaseUrl.ps1'
$serverDll = Join-Path $serverOutput '거래플랜.Server.Api.dll'
$desktopAppSettingsPath = Join-Path (Split-Path -Parent $desktopProject) 'appsettings.json'
foreach ($requiredPath in @(
    $desktopProject,
    $serverProject,
    $mobileProject,
    $syncDiagProject,
    $androidMetadataHelper,
    $templatePath,
    $setApiSource
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "필수 경로를 찾지 못했습니다: $requiredPath"
    }
}
if ($SkipDataCopy) {
    Assert-RetainedIsolatedAppSnapshot -Root $finalIsolatedAppRoot
}
. $androidMetadataHelper

if ([string]::IsNullOrWhiteSpace($SourceApiBaseUrl)) {
    $SourceApiBaseUrl = 'https://api.example.invalid'
    if (Test-Path -LiteralPath $desktopAppSettingsPath) {
        try {
            $desktopAppSettings = Get-Content -LiteralPath $desktopAppSettingsPath -Raw | ConvertFrom-Json
            $configuredBaseUrl = [string]$desktopAppSettings.Api.BaseUrl
            if (-not [string]::IsNullOrWhiteSpace($configuredBaseUrl)) {
                $SourceApiBaseUrl = $configuredBaseUrl
            }
        }
        catch {
        }
    }
}

$sourceUsersSnapshotAllowedRoot = Join-Path `
    ([IO.Path]::GetPathRoot($ProjectRoot)) `
    'DevCaches\georaeplan-v1-user-snapshots'
$sourceUsersSnapshotFromFile = $null
if (
    [string]::IsNullOrWhiteSpace($SourceUsersSnapshotPath) -and
    -not [string]::IsNullOrWhiteSpace($SourceUsersSnapshotSha256)
) {
    throw (
        '-SourceUsersSnapshotSha256 cannot be used without ' +
        '-SourceUsersSnapshotPath.')
}
if (-not [string]::IsNullOrWhiteSpace($SourceUsersSnapshotPath)) {
    if ($SkipServerSeed) {
        throw '-SourceUsersSnapshotPath requires server seed preparation.'
    }
    if ($AllowFallbackOperationalUsers) {
        throw (
            '-SourceUsersSnapshotPath cannot be combined with ' +
            '-AllowFallbackOperationalUsers.')
    }
    if (
        [string]::IsNullOrWhiteSpace($SourceUsersSnapshotSha256) -or
        $SourceUsersSnapshotSha256.Trim() -cnotmatch
            '^[A-Fa-f0-9]{64}$'
    ) {
        throw (
            '-SourceUsersSnapshotPath requires an explicit valid ' +
            '-SourceUsersSnapshotSha256.')
    }

    $sourceUsersSnapshotFullPath =
        ConvertTo-NormalizedFullPath -Path $SourceUsersSnapshotPath
    $outputRootPrefix =
        $OutputRoot.TrimEnd([char[]]@('\', '/')) +
        [IO.Path]::DirectorySeparatorChar
    if ($sourceUsersSnapshotFullPath.StartsWith(
            $outputRootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source users snapshot cannot be located inside OutputRoot.'
    }

    $sourceUsersSnapshotFromFile = Import-SourceUsersSnapshot `
        -Path $SourceUsersSnapshotPath `
        -AllowedRoot $sourceUsersSnapshotAllowedRoot `
        -ExpectedSha256 $SourceUsersSnapshotSha256.Trim() `
        -RequireProtectedAcl
}

$sourceApiBaseUrl = $SourceApiBaseUrl.Trim()
if (-not $SkipServerSeed -and $null -eq $sourceUsersSnapshotFromFile) {
    $sourceApiBaseUrl = Assert-SafeSourceApiBaseUrl `
        -BaseUrl $sourceApiBaseUrl `
        -AllowRemote:$AllowRemoteSourceApi
}

if ($AllowDirtySeedFailure) {
    throw '-AllowDirtySeedFailure is no longer supported because an incomplete seed cannot be certified as restored.'
}
if ($CanonicalizeLegacyInvoiceSeed) {
    if ($SkipDataCopy) {
        throw (
            '-CanonicalizeLegacyInvoiceSeed requires a fresh source snapshot; ' +
            '-SkipDataCopy is not allowed.')
    }
    if ($SkipServerSeed) {
        throw (
            '-CanonicalizeLegacyInvoiceSeed is an active operational server ' +
            'seed option and cannot be combined with -SkipServerSeed.')
    }
    if (
        [string]::IsNullOrWhiteSpace(
            $CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256) -or
        $CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256 -cnotmatch
            '^[A-Fa-f0-9]{64}$'
    ) {
        throw (
            '-CanonicalizeLegacyInvoiceSeed requires ' +
            '-CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256.')
    }
    $approvedLegacyInvoiceSeedSourceDatabaseSha256Values = @(
        '795B5A6CA153B788C6272222D778D714DB10873541775493AB7B36EA091E2FBE',
        'E98DF3E657205319F595AE61089F50E1B87F0BD272C650827AA123B4A8616916',
        '719380E811BB04DC364FB6D2E0BD4C4E04B3D3C12F4D56207233D600F80B9A5C'
    )
    $requestedLegacyInvoiceSeedSourceDatabaseSha256 =
        $CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256.Trim()
    $isApprovedLegacyInvoiceSeedSourceDatabaseSha256 = @(
        $approvedLegacyInvoiceSeedSourceDatabaseSha256Values |
            Where-Object {
                [string]::Equals(
                    $requestedLegacyInvoiceSeedSourceDatabaseSha256,
                    $_,
                    [StringComparison]::OrdinalIgnoreCase)
            }
    ).Count -eq 1
    if (-not $isApprovedLegacyInvoiceSeedSourceDatabaseSha256) {
        throw (
            '-CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256 ' +
            'is not the approved isolated legacy invoice seed snapshot.')
    }
}
elseif (-not [string]::IsNullOrWhiteSpace(
            $CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256)) {
    throw (
        '-CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256 ' +
        'requires -CanonicalizeLegacyInvoiceSeed.')
}

$buildEnvironmentPaths = Get-IsolatedBuildEnvironmentPaths
$buildEnvironmentPreflightLease =
    Enter-IsolatedBuildEnvironmentPreflightLease `
        -EnvironmentPaths $buildEnvironmentPaths `
        -ProjectRoot $ProjectRoot `
        -ScriptRoot $scriptRoot `
        -OutputRoot $OutputRoot `
        -SourceAppRoot $SourceAppRoot `
        -DesktopSourceRoot (Split-Path -Parent $desktopProject) `
        -ServerSourceRoot (Split-Path -Parent $serverProject) `
        -SourceUsersSnapshotAllowedRoot $sourceUsersSnapshotAllowedRoot `
        -SourceUsersSnapshotPath $SourceUsersSnapshotPath
try {
    Initialize-IsolatedBuildEnvironmentOnD `
        -EnvironmentPaths $buildEnvironmentPaths
    Assert-IsolatedBuildEnvironmentInitialized `
        -EnvironmentPaths $buildEnvironmentPaths `
        -PreflightLease $buildEnvironmentPreflightLease
    $dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
    $env:DOTNET_EXE = $dotnetExe

    if (-not $SkipBuild) {
        Assert-IsolatedBuildEnvironmentPreflightLease `
            -EnvironmentPaths $buildEnvironmentPaths `
            -PreflightLease $buildEnvironmentPreflightLease
        Invoke-Dotnet `
            -DotnetExe $dotnetExe `
            -Arguments @(
                'build',
                $solutionPath,
                '-c',
                $Configuration,
                '-nodeReuse:false',
                '/p:UseSharedCompilation=false'
            )
    }
}
finally {
    $buildEnvironmentPreflightLease.Dispose()
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Assert-SafeTestEnvironmentOutputRoot `
    -ProjectRoot $ProjectRoot `
    -ScriptRoot $scriptRoot `
    -OutputRoot $OutputRoot `
    -SourceAppRoot $SourceAppRoot `
    -DesktopSourceRoot (Split-Path -Parent $desktopProject) `
    -ServerSourceRoot (Split-Path -Parent $serverProject)
$preparationLeasePath = Join-Path $OutputRoot '.georaeplan-prepare.lock'
$preparationGateLeasePath =
    Join-Path $OutputRoot '.georaeplan-prepare-gate.lock'
$testAndroidPackageInspection = $null
$launchAfterPreparation = $false
$runtimeOutputMutationStarted = $false
$preparationGateLease = $null
$preparationLease = $null
$runtimePromotionWorkspace = $null
$runtimePreparationTransaction = $null
$stagePreparationLease = $null
try {
    $runtimePromotionWorkspace =
        New-IsolatedRuntimePromotionWorkspace -OutputRoot $OutputRoot
    Assert-IsolatedRuntimePromotionWorkspace `
        -Workspace $runtimePromotionWorkspace
    $stageRoot = [string]$runtimePromotionWorkspace.StageRoot
    $stagePreparationLease =
        New-StagedRuntimePreparationLease `
            -Workspace $runtimePromotionWorkspace
    $appOutput = Join-Path $stageRoot 'App'
    $serverOutput = Join-Path $stageRoot 'Server'
    $isolatedAppRoot = Join-Path $stageRoot 'AppData'
    $serverDataRoot = Join-Path $stageRoot 'ServerData'
    $serverDll = Join-Path $serverOutput '거래플랜.Server.Api.dll'
    New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null

    Initialize-TestAndroidPackageMetadata `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -MobileProject $mobileProject `
        -AndroidPackagePath $AndroidPackagePath `
        -ApkAnalyzerPath $ApkAnalyzerPath `
        -JavaSdkDirectory $JavaSdkDirectory `
        -InspectOnly `
        -SnapshotReference ([ref]$testAndroidPackageInspection)
    Initialize-TestAndroidPackageMetadata `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -MobileProject $mobileProject `
        -AndroidPackagePath $AndroidPackagePath `
        -ApkAnalyzerPath $ApkAnalyzerPath `
        -JavaSdkDirectory $JavaSdkDirectory `
        -ValidatedSnapshot $testAndroidPackageInspection `
        -InspectOnly `
        -SnapshotReference ([ref]$testAndroidPackageInspection)
    $testAndroidPackageState = if ($null -eq $testAndroidPackageInspection) {
        [pscustomobject]@{
            State = 'absent'
            FileName = 'none'
            Sha256 = 'none'
            MetadataSha256 = 'none'
        }
    }
    else {
        $null
    }

    $publishCacheLease =
        Enter-IsolatedBuildEnvironmentPreflightLease `
            -EnvironmentPaths $buildEnvironmentPaths `
            -ProjectRoot $ProjectRoot `
            -ScriptRoot $scriptRoot `
            -OutputRoot $OutputRoot `
            -SourceAppRoot $SourceAppRoot `
            -DesktopSourceRoot (Split-Path -Parent $desktopProject) `
            -ServerSourceRoot (Split-Path -Parent $serverProject) `
            -SourceUsersSnapshotAllowedRoot $sourceUsersSnapshotAllowedRoot `
            -SourceUsersSnapshotPath $SourceUsersSnapshotPath
    try {
        Assert-IsolatedBuildEnvironmentInitialized `
            -EnvironmentPaths $buildEnvironmentPaths `
            -PreflightLease $publishCacheLease
        Assert-IsolatedBuildEnvironmentPreflightLease `
            -EnvironmentPaths $buildEnvironmentPaths `
            -PreflightLease $publishCacheLease
        Invoke-Dotnet `
            -DotnetExe $dotnetExe `
            -Arguments @(
                'publish', $desktopProject, '-c', $Configuration, '-o', $appOutput)
        Invoke-TestEnvironmentPreparationFaultPoint -Point 'publish:App'
        Assert-IsolatedBuildEnvironmentPreflightLease `
            -EnvironmentPaths $buildEnvironmentPaths `
            -PreflightLease $publishCacheLease
        Invoke-Dotnet `
            -DotnetExe $dotnetExe `
            -Arguments @(
                'publish', $serverProject, '-c', $Configuration, '-o', $serverOutput)
        Invoke-TestEnvironmentPreparationFaultPoint -Point 'publish:Server'
        Assert-IsolatedBuildEnvironmentPreflightLease `
            -EnvironmentPaths $buildEnvironmentPaths `
            -PreflightLease $publishCacheLease
    }
    finally {
        $publishCacheLease.Dispose()
    }
    if (-not (Test-Path -LiteralPath $serverDll -PathType Leaf)) {
        throw "테스트 서버 DLL을 찾지 못했습니다: $serverOutput"
    }

    $stagedSetApiPath = Join-Path $stageRoot 'Set-ApiBaseUrl.ps1'
    Copy-Item `
        -LiteralPath $setApiSource `
        -Destination $stagedSetApiPath `
        -Force `
        -ErrorAction Stop
    $initialSetApiResult = Invoke-HiddenSetApiBaseUrl `
        -ScriptPath $stagedSetApiPath `
        -BaseUrl $defaultBaseUrl `
        -AppSettingsPaths @((Join-Path $appOutput 'appsettings.json'))
    $initialSetApiStandardErrorPresent =
        -not [string]::IsNullOrWhiteSpace(
            $initialSetApiResult.StandardError)
    if (
        $initialSetApiResult.ExitCode -ne 0 -or
        $initialSetApiStandardErrorPresent
    ) {
        throw (
            '로컬 테스트용 Api.BaseUrl 설정에 실패했습니다. ' +
            "exitCode=$($initialSetApiResult.ExitCode) " +
            "stderrPresent=$initialSetApiStandardErrorPresent")
    }
    Invoke-TestEnvironmentPreparationFaultPoint `
        -Point 'stage-root-file:Set-ApiBaseUrl.ps1'

    New-Item `
        -ItemType Directory `
        -Path (Join-Path $stageRoot 'Mobile') `
        -Force `
        -ErrorAction Stop |
        Out-Null
    $testAndroidPackageState = if ($null -eq $testAndroidPackageInspection) {
        $testAndroidPackageState
    }
    else {
        Initialize-TestAndroidPackageMetadata `
            -ProjectRoot $ProjectRoot `
            -OutputRoot $stageRoot `
            -MobileProject $mobileProject `
            -ApkAnalyzerPath $ApkAnalyzerPath `
            -JavaSdkDirectory $JavaSdkDirectory `
            -ValidatedSnapshot $testAndroidPackageInspection
    }
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'stage-component:Mobile'

    $preparationGateLease =
        Enter-PreparationGateLease -Path $preparationGateLeasePath
    $readyMarkerSnapshot =
        Get-RuntimeMarkerByteSnapshot -Path $runtimeReadyMarkerPath
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'invalid:set:before'
    $runtimeOutputMutationStarted = $true
    $invalidMarkerState =
        Enter-RuntimeInvalidationMarkerTransactionState `
            -OutputRootLease $runtimePromotionWorkspace.OutputRootLease `
            -OutputRoot $OutputRoot `
            -Path $runtimeInvalidMarkerPath `
            -Reason 'preparation-started'
    $invalidMarkerSnapshot = $invalidMarkerState.Snapshot
    $runtimePreparationTransaction =
        New-RuntimePreparationTransaction `
            -Workspace $runtimePromotionWorkspace `
            -Components @() `
            -ReadyMarkerSnapshot $readyMarkerSnapshot `
            -InvalidMarkerSnapshot $invalidMarkerSnapshot
    $runtimePreparationTransaction.InvalidMarkerLease =
        $invalidMarkerState.Lease
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'invalid:set:after'
    Assert-PreparationExclusionLease `
        -Lease $preparationGateLease `
        -InvalidMarkerPath $runtimeInvalidMarkerPath
    Assert-SafeTestEnvironmentOutputRoot `
        -ProjectRoot $ProjectRoot `
        -ScriptRoot $scriptRoot `
        -OutputRoot $OutputRoot `
        -SourceAppRoot $SourceAppRoot `
        -DesktopSourceRoot (Split-Path -Parent $desktopProject) `
        -ServerSourceRoot (Split-Path -Parent $serverProject)

    Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot
    try {
        $preparationLease = [IO.File]::Open(
            $preparationLeasePath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
    }
    catch {
        throw (
            '격리 런타임이 종료 후에도 lifetime lease를 해제하지 ' +
            "않았습니다: $OutputRoot")
    }
    Assert-PreparationExclusionLease `
        -Lease $preparationLease `
        -InvalidMarkerPath $runtimeInvalidMarkerPath

    $retainedAppSnapshotResult = $null
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'data:before'
    if ($SkipDataCopy) {
        $retainedAppSnapshotResult =
            Get-RetainedIsolatedAppSnapshot -Root $finalIsolatedAppRoot
        Invoke-RobocopyMirror `
            -Source $finalIsolatedAppRoot `
            -Destination $isolatedAppRoot
        Assert-StagedRetainedIsolatedAppSnapshotExact `
            -Expected $retainedAppSnapshotResult `
            -Root $isolatedAppRoot `
            -Context 'while staging retained AppData'
        Set-TypedIsolatedAppDataSeedRootMarker `
            -AppDataRoot $isolatedAppRoot `
            -ExpectedOldRoot $finalIsolatedAppRoot `
            -NewRoot $isolatedAppRoot
        $dataSnapshotResult = $retainedAppSnapshotResult
    }
    else {
        $dataSnapshotResult = Copy-CurrentAppSnapshot `
            -SourceRoot $SourceAppRoot `
            -TargetRoot $isolatedAppRoot `
            -DotnetExe $dotnetExe `
            -SyncDiagProject $syncDiagProject
    }
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'data:after'

    if (
        $SkipDataCopy -and
        $null -ne $sourceUsersSnapshotFromFile -and
        -not $ResetUnresolvedUserPasswordsForIsolatedTest -and
        -not $ResetAllUserPasswordsForIsolatedTest
    ) {
        $credentialPreflightParent =
            'D:\DevCaches\georaeplan-v1-prepare\user-snapshot-preflight'
        $credentialPreflightWorkDirectory =
            New-SecureIsolatedWorkDirectory `
                -Parent $credentialPreflightParent
        $credentialPreflightRoot =
            $credentialPreflightWorkDirectory.Root
        try {
            $preflightStoredCredentials = @(
                Get-StoredSyncCredentialsFromLocalState `
                    -DotnetExe $dotnetExe `
                    -SyncDiagProject $syncDiagProject `
                    -AppRoot $isolatedAppRoot `
                    -LogPath (
                        Join-Path `
                            $credentialPreflightRoot `
                            'stored-sync-credentials.log')
            )
            $preflightSourceUsers = @(
                Resolve-IsolatedSourceUsers `
                    -SourceUsersSnapshot $sourceUsersSnapshotFromFile `
                    -StoredCredentials $preflightStoredCredentials
            )
            [void](
                Resolve-IsolatedUserDefinitions `
                    -SourceUsers $preflightSourceUsers `
                    -StoredCredentials $preflightStoredCredentials
            )
        }
        finally {
            Remove-SecureIsolatedWorkDirectory `
                -WorkDirectory $credentialPreflightWorkDirectory
        }
    }

if ($CanonicalizeLegacyInvoiceSeed) {
    $expectedCanonicalizationSourceHash = (
        [string]$CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256
    ).ToUpperInvariant()
    $actualCanonicalizationSourceHash =
        ([string]$dataSnapshotResult.DatabaseSha256).ToUpperInvariant()
    if (-not [string]::Equals(
            $expectedCanonicalizationSourceHash,
            $actualCanonicalizationSourceHash,
            [StringComparison]::Ordinal)) {
        throw (
            '격리 레거시 청구서 정규화 원본 DB SHA-256이 검증된 ' +
            'fresh snapshot과 일치하지 않습니다. ' +
            "expected=$expectedCanonicalizationSourceHash " +
            "actual=$actualCanonicalizationSourceHash")
    }

    $canonicalizationSourceAttestation = [ordered]@{
        schemaVersion = 1
        databaseSha256 = $actualCanonicalizationSourceHash
    }
    Write-Utf8File `
        -Path (
            Join-Path `
                $isolatedAppRoot `
                '.georaeplan-isolated-seed-source-attestation.json') `
        -Content (
            $canonicalizationSourceAttestation |
                ConvertTo-Json -Compress)
}

    Invoke-TestEnvironmentPreparationFaultPoint -Point 'server-data:before'
    Reset-IsolatedServerStorage `
        -ServerOutput $serverOutput `
        -ServerDataRoot $serverDataRoot
    New-Item `
        -ItemType Directory `
        -Path $serverDataRoot `
        -Force `
        -ErrorAction Stop |
        Out-Null
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'server-data:after'

$seedSucceeded = $false
$seedSkippedReason = ''
$seedLogRoot = Join-Path $sessionRoot 'server-seed'
if (-not $SkipServerSeed) {
    $initializeServerDataParameters = @{
        DotnetExe = $dotnetExe
        SyncDiagProject = $syncDiagProject
        TestAppRoot = $isolatedAppRoot
        ServerDll = $serverDll
        ServerWorkingDirectory = $serverOutput
        SeedLogRoot = $seedLogRoot
        ServerDataRoot = $serverDataRoot
        SourceApiBaseUrl = $sourceApiBaseUrl
        SourceUsersSnapshot = $sourceUsersSnapshotFromFile
        ResetAllUserPasswords =
            [bool]$ResetAllUserPasswordsForIsolatedTest
    }
    if ($CanonicalizeLegacyInvoiceSeed) {
        $initializeServerDataParameters[
            'CanonicalizeLegacyInvoiceSeed'] = $true
        $initializeServerDataParameters[
            'CanonicalizeLegacyInvoiceSeedSourceDatabaseSha256'] =
                $CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256
    }
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'seed:before'
    Initialize-IsolatedServerData @initializeServerDataParameters
    Invoke-TestEnvironmentPreparationFaultPoint -Point 'seed:after'
    $seedSucceeded = $true
}
else {
    $seedSkippedReason = '사용자 옵션으로 서버 시드를 건너뜀'
}

$runtimeReady = $false
$runtimeCertificationId = ''
$runtimeCertificationMode = ''
$runtimePasswordResetCount = 0
if ($seedSucceeded) {
    $resolvedUsersPath = Join-Path $seedLogRoot 'resolved-users.json'
    if (-not (Test-Path -LiteralPath $resolvedUsersPath -PathType Leaf)) {
        throw "격리 사용자 복원 결과를 찾지 못했습니다: $resolvedUsersPath"
    }
    $resolvedUserResults = @(
        Get-Content -LiteralPath $resolvedUsersPath -Raw |
            ConvertFrom-Json |
            ForEach-Object { $_ }
    )
    if (
        $resolvedUserResults.Count -eq 0 -or
        @(
            $resolvedUserResults |
                Where-Object { -not [bool]$_.PasswordResolved }
        ).Count -gt 0
    ) {
        throw '격리 사용자 비밀번호 검증 상태가 완전하지 않아 runtime을 인증할 수 없습니다.'
    }
    $runtimePasswordResetCount = @(
        $resolvedUserResults |
            Where-Object { [bool]$_.PasswordWasReset }
    ).Count
    $runtimeCertificationMode = if ($runtimePasswordResetCount -gt 0) {
        'isolated-original-data-test-password-resets'
    }
    elseif ($null -ne $sourceUsersSnapshotFromFile) {
        'isolated-original-data-cached-auth-unverified'
    }
    else {
        'isolated-original-data-source-admin-plus-cached-auth'
    }
    $runtimeCertificationId = [Guid]::NewGuid().ToString('N')

    Write-TestRunScripts `
        -OutputRoot $stageRoot `
        -DefaultBaseUrl $defaultBaseUrl `
        -DotnetExe $dotnetExe `
        -CertificationId $runtimeCertificationId `
        -CertificationMode $runtimeCertificationMode `
        -PasswordResetCount $runtimePasswordResetCount
}

    Close-StagedRuntimePreparationLease `
        -Lease $stagePreparationLease
    $stagePreparationLease = $null
    $stagedAppDataSeedRootMarker =
        Join-Path $isolatedAppRoot '.georaeplan-isolated-seed-root'
    $stagedServerRootMarker =
        Join-Path $serverOutput '.georaeplan-isolated-server-root'
    Convert-StagedRuntimeRootMarkers `
        -StageRoot $stageRoot `
        -FinalRoot $OutputRoot `
        -ExcludedFullPaths @(
            $stagedAppDataSeedRootMarker,
            $stagedServerRootMarker)
    Set-TypedIsolatedAppDataSeedRootMarker `
        -AppDataRoot $isolatedAppRoot `
        -ExpectedOldRoot $isolatedAppRoot `
        -NewRoot $finalIsolatedAppRoot
    Set-TypedIsolatedServerRootMarker `
        -ServerRoot $serverOutput `
        -ExpectedOldRoot $serverOutput `
        -NewRoot $finalServerOutput
    if ($SkipDataCopy) {
        try {
            [void](
                Assert-RetainedIsolatedAppSnapshotUnchanged `
                    -Expected $retainedAppSnapshotResult `
                    -Root $finalIsolatedAppRoot `
                    -Context 'before managed component promotion')
        }
        catch {
            Set-RuntimePreparationRollbackMarkersFailClosed `
                -Transaction $runtimePreparationTransaction `
                -Reason 'retained-appdata-changed-before-promotion'
            throw
        }
    }
    Assert-IsolatedRuntimePromotionWorkspace `
        -Workspace $runtimePromotionWorkspace
    Assert-PreparationExclusionLease `
        -Lease $preparationGateLease `
        -InvalidMarkerPath $runtimeInvalidMarkerPath
    Assert-PreparationExclusionLease `
        -Lease $preparationLease `
        -InvalidMarkerPath $runtimeInvalidMarkerPath
    Assert-SafeTestEnvironmentOutputRoot `
        -ProjectRoot $ProjectRoot `
        -ScriptRoot $scriptRoot `
        -OutputRoot $OutputRoot `
        -SourceAppRoot $SourceAppRoot `
        -DesktopSourceRoot (Split-Path -Parent $desktopProject) `
        -ServerSourceRoot (Split-Path -Parent $serverProject)
    $runtimePreparationTransaction.Components = @(
        Get-IsolatedRuntimeManagedComponents `
            -OutputRoot $OutputRoot `
            -StageRoot $stageRoot `
            -BackupRoot $runtimePromotionWorkspace.BackupRoot `
            -ReplaceAppData:(-not $SkipDataCopy) `
            -RequireLaunchers:$seedSucceeded
    )
    Invoke-IsolatedRuntimeComponentPromotion `
        -Transaction $runtimePreparationTransaction

    $appOutput = $finalAppOutput
    $serverOutput = $finalServerOutput
    $isolatedAppRoot = $finalIsolatedAppRoot
    $serverDataRoot = $finalServerDataRoot
    $serverDll = Join-Path $serverOutput '거래플랜.Server.Api.dll'
    if ($SkipDataCopy) {
        try {
            $dataSnapshotResult =
                Assert-RetainedIsolatedAppSnapshotUnchanged `
                    -Expected $retainedAppSnapshotResult `
                    -Root $isolatedAppRoot `
                    -Context 'after managed component promotion'
        }
        catch {
            Set-RuntimePreparationRollbackMarkersFailClosed `
                -Transaction $runtimePreparationTransaction `
                -Reason 'retained-appdata-changed-during-promotion'
            throw
        }
    }

$generatedAt = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$branch = (Get-GitOutput -ProjectRoot $ProjectRoot -Arguments @('rev-parse', '--abbrev-ref', 'HEAD') -AllowFailure).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) { $branch = 'unknown' }
$commit = (Get-GitOutput -ProjectRoot $ProjectRoot -Arguments @('rev-parse', 'HEAD') -AllowFailure).Trim()
if ([string]::IsNullOrWhiteSpace($commit)) { $commit = 'unknown' }

$changedFilesContent = Build-ChangedFilesMarkdown -ProjectRoot $ProjectRoot -GeneratedAt $generatedAt -Branch $branch -Commit $commit
Write-Utf8File -Path $changedFilesPath -Content $changedFilesContent -WithBom
Write-Utf8File -Path (Join-Path $sessionRoot '최근 수정 파일.md') -Content $changedFilesContent -WithBom

$checklistTokens = @{
    GENERATED_AT = $generatedAt
    BRANCH = $branch
    COMMIT = $commit
    TEST_ROOT = $scriptRoot
    RUNTIME_ROOT = $OutputRoot
    APP_DATA_ROOT = $isolatedAppRoot
    SERVER_DB_PATH = (Join-Path $serverOutput '거래플랜-local.db')
}
$checklistContent = Build-ChecklistContent -TemplatePath $templatePath -Tokens $checklistTokens
Write-Utf8File -Path $currentChecklistPath -Content $checklistContent -WithBom
Write-Utf8File -Path (Join-Path $sessionRoot '검증 체크리스트.md') -Content $checklistContent -WithBom

$prepareLog = @(
    "generated_at=$generatedAt",
    "configuration=$Configuration",
    "project_root=$ProjectRoot",
    "runtime_root=$OutputRoot",
    "branch=$branch",
    "commit=$commit",
    "api_base_url=$defaultBaseUrl",
    "source_api_base_url_configured=$($null -eq $sourceUsersSnapshotFromFile -and -not [string]::IsNullOrWhiteSpace($sourceApiBaseUrl))",
    "source_users_snapshot_file_used=$($null -ne $sourceUsersSnapshotFromFile)",
    "source_users_snapshot_sha256=$(if ($null -ne $sourceUsersSnapshotFromFile) { [string]$sourceUsersSnapshotFromFile.SnapshotSha256 } else { 'none' })",
    "source_users_canonical_sha256=$(if ($null -ne $sourceUsersSnapshotFromFile) { [string]$sourceUsersSnapshotFromFile.CanonicalSha256 } else { 'none' })",
    "dotnet=$dotnetExe",
    "source_app_root=$SourceAppRoot",
    "isolated_app_root=$isolatedAppRoot",
    "isolated_server_db=$(Join-Path $serverOutput '거래플랜-local.db')",
    "isolated_server_data_root=$serverDataRoot",
    "data_snapshot_source_exists=$($dataSnapshotResult.SourceExists)",
    "data_snapshot_database_source=$($dataSnapshotResult.DatabaseSource)",
    "data_snapshot_database_sha256=$($dataSnapshotResult.DatabaseSha256)",
    "data_snapshot_database_mode=$($dataSnapshotResult.DatabaseSnapshotMode)",
    "data_snapshot_used_backup_fallback=$($dataSnapshotResult.UsedBackupFallback)",
    "data_snapshot_managed_file_count=$($dataSnapshotResult.ManagedFileCount)",
    "data_snapshot_managed_file_manifest_sha256=$($dataSnapshotResult.ManagedFileManifestSha256)",
    "legacy_invoice_seed_canonicalization_enabled=$([bool]$CanonicalizeLegacyInvoiceSeed)",
    "legacy_invoice_seed_canonicalization_source_database_sha256=$(if ($CanonicalizeLegacyInvoiceSeed) { $CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256.ToUpperInvariant() } else { 'none' })",
    'legacy_invoice_seed_scope=active_operational_seed_only_not_deleted_history_migration',
    "server_seed_enabled=$([bool](-not $SkipServerSeed))",
    "server_seed_succeeded=$seedSucceeded",
    "server_seed_skip_reason=$seedSkippedReason",
    "runtime_certification_id=$runtimeCertificationId",
    "runtime_certification_mode=$runtimeCertificationMode",
    "runtime_password_reset_count=$runtimePasswordResetCount",
    "runtime_ready=$runtimeReady",
    "runtime_ready_marker=$runtimeReadyMarkerPath"
) -join [Environment]::NewLine
Write-Utf8File -Path (Join-Path $sessionRoot '준비 로그.txt') -Content $prepareLog -WithBom

if ($seedSucceeded) {
    $certifiedAppExecutables = @(
        Get-ChildItem `
            -LiteralPath $appOutput `
            -Filter '*.Desktop.App.exe' `
            -File
    )
    $certifiedServerDlls = @(
        Get-ChildItem `
            -LiteralPath $serverOutput `
            -Filter '*.Server.Api.dll' `
            -File
    )
    if (
        $certifiedAppExecutables.Count -ne 1 -or
        $certifiedServerDlls.Count -ne 1
    ) {
        throw 'runtime 인증 대상 App/Server artifact 집합이 정확하지 않습니다.'
    }
    $componentScriptPath =
        Join-Path $OutputRoot 'Run-IsolatedComponent.ps1'
    $runAllScriptPath = Join-Path $OutputRoot 'Run-All.ps1'
    $serverDatabasePath =
        Join-Path $serverOutput '거래플랜-local.db'
    $isolatedAppDatabasePath =
        Join-Path $isolatedAppRoot 'data\거래플랜.db'
    foreach ($certificationPath in @(
        $componentScriptPath,
        $runAllScriptPath,
        $isolatedAppDatabasePath,
        $serverDatabasePath
    )) {
        if (-not (Test-Path -LiteralPath $certificationPath -PathType Leaf)) {
            throw "runtime 인증 대상 파일을 찾지 못했습니다: $certificationPath"
        }
    }
    Assert-NoSqliteSidecars -DatabasePath $isolatedAppDatabasePath
    Assert-NoSqliteSidecars -DatabasePath $serverDatabasePath

    $appExecutableSha256 = (
        Get-FileHash `
            -LiteralPath $certifiedAppExecutables[0].FullName `
            -Algorithm SHA256).Hash
    $serverDllSha256 = (
        Get-FileHash `
            -LiteralPath $certifiedServerDlls[0].FullName `
            -Algorithm SHA256).Hash
    $appExecutionTreeSha256 =
        Get-RuntimeExecutionTreeManifestDigest `
            -Root $appOutput `
            -ExcludedRelativePaths @('appsettings.json')
    $serverExecutionTreeSha256 =
        Get-RuntimeExecutionTreeManifestDigest `
            -Root $serverOutput `
            -ExcludedRelativePaths @(
                '거래플랜-local.db',
                '거래플랜-local.db-shm',
                '거래플랜-local.db-wal',
                '거래플랜-local.db-journal'
            )
    $componentScriptSha256 = (
        Get-FileHash `
            -LiteralPath $componentScriptPath `
            -Algorithm SHA256).Hash
    $runAllScriptSha256 = (
        Get-FileHash `
            -LiteralPath $runAllScriptPath `
            -Algorithm SHA256).Hash
    $setApiScriptSha256 = (
        Get-FileHash `
            -LiteralPath (Join-Path $OutputRoot 'Set-ApiBaseUrl.ps1') `
            -Algorithm SHA256).Hash
    $initialAppSettingsSha256 = (
        Get-FileHash `
            -LiteralPath (Join-Path $appOutput 'appsettings.json') `
            -Algorithm SHA256).Hash
    $serverDatabaseSha256 = (
        Get-FileHash `
            -LiteralPath $serverDatabasePath `
            -Algorithm SHA256).Hash
    $isolatedAppDatabaseSha256 = (
        Get-FileHash `
            -LiteralPath $isolatedAppDatabasePath `
            -Algorithm SHA256).Hash
    $runtimePhysicalRoot =
        Resolve-PhysicalPathIdentity -Path $OutputRoot
    $certifiedManagedManifest = @(
        Get-AppSnapshotFileManifest -Root $isolatedAppRoot
    )
    $managedManifestSha256 =
        Get-AppSnapshotFileManifestDigest `
            -Manifest $certifiedManagedManifest
    if ([string]::IsNullOrWhiteSpace($managedManifestSha256)) {
        $managedManifestSha256 = 'none'
    }

    $readyMarkerTempPath = Join-Path $OutputRoot (
        '.georaeplan-runtime-ready.' +
        [Guid]::NewGuid().ToString('N') +
        '.tmp')
    try {
        Invoke-TestEnvironmentPreparationFaultPoint -Point 'ready:write:before'
        Write-Utf8File `
            -Path $readyMarkerTempPath `
            -Content (@(
                'runtime_ready=True',
                'runtime_state=pristine',
                "runtime_root=$OutputRoot",
                "runtime_physical_root=$runtimePhysicalRoot",
                "certification_id=$runtimeCertificationId",
                "certification_mode=$runtimeCertificationMode",
                "password_reset_count=$runtimePasswordResetCount",
                "source_users_mode=$(if ($null -ne $sourceUsersSnapshotFromFile) { 'validated-file-snapshot' } else { 'authenticated-source-api' })",
                "source_users_snapshot_sha256=$(if ($null -ne $sourceUsersSnapshotFromFile) { [string]$sourceUsersSnapshotFromFile.SnapshotSha256 } else { 'none' })",
                "source_users_canonical_sha256=$(if ($null -ne $sourceUsersSnapshotFromFile) { [string]$sourceUsersSnapshotFromFile.CanonicalSha256 } else { 'none' })",
                "managed_file_count=$($certifiedManagedManifest.Count)",
                "managed_file_manifest_sha256=$managedManifestSha256",
                "source_database_sha256=$($dataSnapshotResult.DatabaseSha256)",
                "source_database_snapshot_mode=$($dataSnapshotResult.DatabaseSnapshotMode)",
                "isolated_app_database_sha256=$isolatedAppDatabaseSha256",
                "server_database_sha256=$serverDatabaseSha256",
                "app_executable_sha256=$appExecutableSha256",
                "server_dll_sha256=$serverDllSha256",
                "app_execution_tree_sha256=$appExecutionTreeSha256",
                "server_execution_tree_sha256=$serverExecutionTreeSha256",
                "set_api_script_sha256=$setApiScriptSha256",
                "initial_appsettings_sha256=$initialAppSettingsSha256",
                "component_script_sha256=$componentScriptSha256",
                "run_all_script_sha256=$runAllScriptSha256",
                "android_package_state=$($testAndroidPackageState.State)",
                "android_package_file_name=$($testAndroidPackageState.FileName)",
                "android_package_sha256=$($testAndroidPackageState.Sha256)",
                "android_package_metadata_sha256=$($testAndroidPackageState.MetadataSha256)",
                "certified_at_utc=$([DateTime]::UtcNow.ToString('O'))"
            ) -join [Environment]::NewLine) `
            -WithBom
        Invoke-TestEnvironmentPreparationFaultPoint -Point 'ready:write:after'
        Publish-TestFileAtomically `
            -TemporaryPath $readyMarkerTempPath `
            -TargetPath $runtimeReadyMarkerPath
        Invoke-TestEnvironmentPreparationFaultPoint -Point 'ready:publish:after'
        Complete-IsolatedRuntimePromotionTransaction `
            -Transaction $runtimePreparationTransaction
        try {
            Invoke-TestEnvironmentPreparationFaultPoint `
                -Point 'invalid:clear:before'
            Initialize-TestEnvironmentFinalPathNativeMethods
            $nativeType = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
            $nativeType::DeleteHeldExactSingleLinkRegularFile(
                $runtimePreparationTransaction.InvalidMarkerLease,
                $runtimeInvalidMarkerPath)
            $runtimePreparationTransaction.InvalidMarkerLease.Dispose()
            $runtimePreparationTransaction.InvalidMarkerLease = $null
            if (Test-Path -LiteralPath $runtimeInvalidMarkerPath) {
                throw 'The exact runtime invalidation marker still exists.'
            }
        }
        catch {
            throw [InvalidOperationException]::new(
                'The runtime invalidation marker could not be cleared after ' +
                'commit; the new runtime remains blocked.',
                $_.Exception)
        }
        $runtimeReady = $true
        $runtimeOutputMutationStarted = $false
    }
    finally {
        if (Test-Path -LiteralPath $readyMarkerTempPath) {
            Remove-Item `
                -LiteralPath $readyMarkerTempPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
}
elseif (
    $null -ne $runtimePreparationTransaction -and
    -not $runtimePreparationTransaction.Committed
) {
    Complete-IsolatedRuntimePromotionTransaction `
        -Transaction $runtimePreparationTransaction
    $runtimeOutputMutationStarted = $false
}

$prepareLog = $prepareLog -replace `
    '(?m)^runtime_ready=.*$', `
    "runtime_ready=$runtimeReady"
Write-Utf8File `
    -Path (Join-Path $sessionRoot '준비 로그.txt') `
    -Content $prepareLog `
    -WithBom

Write-Host '테스트 환경 준비 완료'
Write-Host "- 현재 로컬 데이터 스냅샷: $SourceAppRoot"
Write-Host "- 테스트 앱 데이터 루트: $isolatedAppRoot"
Write-Host "- 테스트 서버 DB: $(Join-Path $serverOutput '거래플랜-local.db')"
Write-Host "- 최근 수정 파일: $changedFilesPath"
Write-Host "- 검증 체크리스트: $currentChecklistPath"
Write-Host "- 실행 환경: $OutputRoot"
if ($runtimeReady) {
    Write-Host "- 실행 명령: $(Join-Path $OutputRoot 'Run-All.cmd')"
    Write-Host "- CMD 창 없는 수동 실행: $(Join-Path $OutputRoot 'Launch-Test-App.vbs')"
}
else {
    Write-Host '- 실행 명령: 서버 시드가 생략되어 생성하지 않음'
}

if ($Launch) {
    if (-not $runtimeReady) {
        throw '-Launch requires a fully seeded and certified isolated runtime.'
    }

    $launchAfterPreparation = $true
}
}
catch {
    $preparationFailure = $_
    if (
        $runtimeOutputMutationStarted -and
        $null -ne $runtimePreparationTransaction -and
        -not $runtimePreparationTransaction.Committed
    ) {
        try {
            Restore-IsolatedRuntimePromotionTransaction `
                -Transaction $runtimePreparationTransaction
            Close-StagedRuntimePreparationLease `
                -Lease $stagePreparationLease
            $stagePreparationLease = $null
            Complete-IsolatedRuntimePromotionTransaction `
                -Transaction $runtimePreparationTransaction
            $runtimeOutputMutationStarted = $false
        }
        catch {
            throw [InvalidOperationException]::new(
                'Runtime preparation failed and rollback or private workspace ' +
                'cleanup also failed. ' +
                'The final runtime remains fail-closed; private stage, backup, ' +
                'and quarantine evidence were retained. ' +
                "preparation=$($preparationFailure.Exception.Message); " +
                "rollback=$($_.Exception.Message)",
                $_.Exception)
        }
    }

    throw $preparationFailure
}
finally {
    try {
        Close-StagedRuntimePreparationLease `
            -Lease $stagePreparationLease
    }
    finally {
        Remove-GeoraePlanAndroidApkSnapshot `
            -Snapshot $testAndroidPackageInspection
        if ($null -ne $preparationLease) {
            $preparationLease.Dispose()
        }
        if ($null -ne $preparationGateLease) {
            $preparationGateLease.Dispose()
        }
        if (
            $null -ne $runtimePreparationTransaction -and
            $null -ne $runtimePreparationTransaction.InvalidMarkerLease -and
            -not $runtimePreparationTransaction.InvalidMarkerLease.IsClosed
        ) {
            $runtimePreparationTransaction.InvalidMarkerLease.Dispose()
            $runtimePreparationTransaction.InvalidMarkerLease = $null
        }
        Close-IsolatedRuntimePromotionWorkspaceLeases `
            -Workspace $runtimePromotionWorkspace
    }
}

if ($launchAfterPreparation) {
    $runAllProcess = $null
    $previousFailureDialogSuppression =
        $env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG
    try {
        $env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG = '1'
        $runAllProcess = Start-Process `
            -FilePath (Join-Path $OutputRoot 'Run-All.cmd') `
            -WorkingDirectory $OutputRoot `
            -WindowStyle Hidden `
            -PassThru
    }
    finally {
        $env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG =
            $previousFailureDialogSuppression
    }

    try {
        if ($runAllProcess.WaitForExit(1500)) {
            if ($runAllProcess.ExitCode -ne 0) {
                throw (
                    '로컬 테스트 실행 프로세스가 조기 종료되었습니다. ' +
                    'RuntimeLogs에서 실행 상태를 확인하세요.')
            }
            Write-Host (
                '로컬 테스트 실행 프로세스가 조기 종료되었습니다. ' +
                '최종 서버/앱 상태는 RuntimeLogs에서 확인하세요.')
        }
        else {
            Write-Host (
                '로컬 테스트 실행 프로세스 시작만 확인했습니다. ' +
                '최종 서버/앱 상태는 RuntimeLogs에서 확인하세요.')
        }
    }
    finally {
        if ($null -ne $runAllProcess) {
            $runAllProcess.Dispose()
        }
    }
}
