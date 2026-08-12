[CmdletBinding()]
param(
    [switch]$Apply,

    [Parameter(DontShow = $true)]
    [ValidateSet(
        'None',
        'AfterJournal',
        'JournalShortWrite',
        'JournalBeforeFlush',
        'JournalAfterFlushBeforePublish',
        'JournalProcessKillAfterFlush',
        'OwnerShortWrite',
        'OwnerOneByteWrite',
        'OwnerTailShortWrite',
        'OwnerBeforeFlush',
        'OwnerAfterFlushBeforePublish',
        'OwnerProcessKillAfterFlush',
        'AfterRoot',
        'AfterOwner',
        'AfterFirstSentinel')]
    [string]$TestFaultInjection = 'None'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$cacheRoot = 'D:\DevCaches\georaeplan-v1-prepare'
$ownerFileName = '.georaeplan-build-cache-owner.json'
$coordinatorFileName = '.georaeplan-build-cache-provision.lease'
$sentinelFileName = '.georaeplan-build-cache-lease'
$rootProvisioningEaName = 'GEORAEPLAN.BUILDCACHE.PROVISIONING'
$expectedOwner = 'georaeplan-build-cache'
$emptyFileSha256 =
    'E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855'
$comparison = [StringComparison]::OrdinalIgnoreCase
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testExecutionDirectoryName = -join @(
    53580, 49828, 53944, 32, 49884, 54665 | ForEach-Object { [char]$_ }
)
$currentRuntimeDirectoryName = -join @(
    49892, 54665, 54872, 44221 | ForEach-Object { [char]$_ }
)
$protectedRuntimeSnapshotPattern = -join @(
    49892, 54665, 54872, 44221, 45, 50896, 48376, 49828, 45253, 49399,
    45, 42 | ForEach-Object { [char]$_ }
)
$leafRelativePaths = @(
    'temp',
    'nuget\packages',
    'nuget\http-cache',
    'nuget\plugins-cache',
    'dotnet-home'
)

function Initialize-BuildCacheProvisionerNativeType {
    if ($null -ne ('GeoraePlan.BuildCacheProvisioner.NativeEntry' -as [type])) {
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

namespace GeoraePlan.BuildCacheProvisioner
{
    public sealed class NativeEntry : IDisposable
    {
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileReadData = 0x00000001;
        private const uint FileAddFile = 0x00000002;
        private const uint FileAddSubdirectory = 0x00000004;
        private const uint FileReadEa = 0x00000008;
        private const uint FileTraverse = 0x00000020;
        private const uint DeleteAccess = 0x00010000;
        private const uint ReadControl = 0x00020000;
        private const uint WriteDac = 0x00040000;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint DirectoryAttribute = 0x00000010;
        private const uint ReparsePointAttribute = 0x00000400;
        private const uint DuplicateSameAccess = 0x00000002;
        private const uint SynchronizeAccess = 0x00100000;
        private const uint ObjectCaseInsensitive = 0x00000040;
        private const uint NtFileOpen = 1;
        private const uint NtFileCreate = 2;
        private const uint NtFileDirectory = 0x00000001;
        private const uint NtFileSynchronousIoNonAlert = 0x00000020;
        private const uint NtFileNonDirectory = 0x00000040;
        private const uint NtFileOpenReparsePoint = 0x00200000;
        private const uint NtFileRenameInformationEx = 65;
        private const int FileRenamePosixSemantics = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(SafeFileHandle file);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileSizeEx(
            SafeFileHandle file,
            out long fileSize);

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

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(
            out SafeFileHandle file,
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            SafeFileHandle file,
            byte[] buffer,
            uint bytesToWrite,
            out uint bytesWritten,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(
            SafeFileHandle file,
            long distance,
            out long newPosition,
            uint moveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEndOfFile(SafeFileHandle file);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(
            SafeFileHandle file,
            out IoStatusBlock ioStatusBlock,
            IntPtr information,
            uint informationLength,
            uint informationClass);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryEaFile(
            SafeFileHandle file,
            out IoStatusBlock ioStatusBlock,
            IntPtr buffer,
            uint length,
            [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
            IntPtr eaList,
            uint eaListLength,
            IntPtr eaIndex,
            [MarshalAs(UnmanagedType.U1)] bool restartScan);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);

        private SafeFileHandle handle;
        private ByHandleFileInformation information;

        private NativeEntry(
            string logicalPath,
            string finalPath,
            SafeFileHandle handle,
            ByHandleFileInformation information)
        {
            LogicalPath = logicalPath;
            FinalPath = NormalizeFinalPath(finalPath);
            this.handle = handle;
            this.information = information;
        }

        public string LogicalPath { get; private set; }
        public string FinalPath { get; private set; }
        public bool IsDirectory
        {
            get { return (information.FileAttributes & DirectoryAttribute) != 0; }
        }
        public bool IsReparsePoint
        {
            get { return (information.FileAttributes & ReparsePointAttribute) != 0; }
        }
        public uint NumberOfLinks { get { return information.NumberOfLinks; } }
        public string VolumeSerialNumber
        {
            get { return information.VolumeSerialNumber.ToString("X8"); }
        }
        public string FileId
        {
            get
            {
                ulong value = ((ulong)information.FileIndexHigh << 32) |
                    information.FileIndexLow;
                return value.ToString("X16");
            }
        }

        public static NativeEntry OpenLease(string path, bool isDirectory)
        {
            return Open(path, isDirectory, false, false, false);
        }

        public static NativeEntry OpenPublishParentLease(string path)
        {
            return Open(path, true, false, false, true);
        }

        public static NativeEntry OpenStableFileLease(string path)
        {
            return Open(path, false, false, true, false);
        }

        public static NativeEntry CreateDirectoryChild(
            NativeEntry parent,
            string childName,
            byte[] securityDescriptor)
        {
            return OpenRelative(parent, childName, true, true, false, null,
                securityDescriptor, false, -1, true, null, null);
        }

        public static NativeEntry CreateProvisionedDirectoryChild(
            NativeEntry parent,
            string childName,
            byte[] securityDescriptor,
            string eaName,
            byte[] eaValue)
        {
            if (String.IsNullOrWhiteSpace(eaName) || eaValue == null ||
                eaValue.Length == 0)
            {
                throw new IOException("Provisioning root EA is invalid.");
            }
            return OpenRelative(parent, childName, true, true, false, null,
                securityDescriptor, false, -1, true, eaName, eaValue);
        }

        public static NativeEntry OpenDirectoryChild(
            NativeEntry parent,
            string childName)
        {
            return OpenRelative(parent, childName, true, false, false, null,
                null, false, -1, true, null, null);
        }

        public static NativeEntry CreateStableFileChild(
            NativeEntry parent,
            string childName,
            byte[] content,
            byte[] securityDescriptor)
        {
            return OpenRelative(parent, childName, false, true, false, content,
                securityDescriptor, false, -1, true, null, null);
        }

        public static NativeEntry CreatePublishFileChild(
            NativeEntry parent,
            string childName,
            byte[] content,
            byte[] securityDescriptor,
            int writeLength,
            bool flush)
        {
            return OpenRelative(parent, childName, false, true, false, content,
                securityDescriptor, true, writeLength, flush, null, null);
        }

        public static NativeEntry OpenPublishFileChild(
            NativeEntry parent,
            string childName)
        {
            return OpenRelative(parent, childName, false, false, false, null,
                null, true, -1, true, null, null);
        }

        public static NativeEntry OpenStableFileChild(
            NativeEntry parent,
            string childName)
        {
            return OpenRelative(parent, childName, false, false, false, null,
                null, false, -1, true, null, null);
        }

        public static NativeEntry CreateExclusiveFileChild(
            NativeEntry parent,
            string childName,
            byte[] securityDescriptor)
        {
            return OpenRelative(
                parent,
                childName,
                false,
                true,
                true,
                new byte[0],
                securityDescriptor,
                false,
                -1,
                true,
                null,
                null);
        }

        public static NativeEntry OpenExclusiveFileChild(
            NativeEntry parent,
            string childName)
        {
            return OpenRelative(
                parent,
                childName,
                false,
                false,
                true,
                null,
                null,
                false,
                -1,
                true,
                null,
                null);
        }

        private static NativeEntry OpenRelative(
            NativeEntry parent,
            string childName,
            bool isDirectory,
            bool create,
            bool exclusive,
            byte[] content,
            byte[] securityDescriptor,
            bool publish,
            int writeLength,
            bool flush,
            string eaName,
            byte[] eaValue)
        {
            if (parent == null || !parent.IsDirectory)
            {
                throw new IOException("The relative-create parent lease is invalid.");
            }
            if (String.IsNullOrWhiteSpace(childName) ||
                !String.Equals(
                    Path.GetFileName(childName),
                    childName,
                    StringComparison.Ordinal))
            {
                throw new IOException("The relative-create leaf name is invalid.");
            }

            string expectedPath = Path.GetFullPath(Path.Combine(
                parent.FinalPath,
                childName));
            IntPtr nameBuffer = Marshal.StringToHGlobalUni(childName);
            IntPtr unicodeBuffer = IntPtr.Zero;
            bool parentHandleAdded = false;
            SafeFileHandle opened = null;
            try
            {
                UnicodeString unicode = new UnicodeString();
                unicode.Length = checked((ushort)(childName.Length * 2));
                unicode.MaximumLength = unicode.Length;
                unicode.Buffer = nameBuffer;
                unicodeBuffer = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(unicode, unicodeBuffer, false);

                parent.handle.DangerousAddRef(ref parentHandleAdded);
                ObjectAttributes attributes = new ObjectAttributes();
                attributes.Length = Marshal.SizeOf(typeof(ObjectAttributes));
                attributes.RootDirectory = parent.handle.DangerousGetHandle();
                attributes.ObjectName = unicodeBuffer;
                attributes.Attributes = ObjectCaseInsensitive;
                GCHandle descriptorHandle = new GCHandle();
                GCHandle eaHandle = new GCHandle();
                bool descriptorPinned = false;
                bool eaPinned = false;
                try
                {
                    if (create && (securityDescriptor == null ||
                        securityDescriptor.Length == 0))
                    {
                        throw new IOException(
                            "Relative create requires an exact security descriptor.");
                    }
                    if (securityDescriptor != null)
                    {
                        descriptorHandle = GCHandle.Alloc(
                            securityDescriptor,
                            GCHandleType.Pinned);
                        descriptorPinned = true;
                        attributes.SecurityDescriptor =
                            descriptorHandle.AddrOfPinnedObject();
                    }
                byte[] eaBuffer = null;
                if (eaName != null || eaValue != null)
                {
                    if (!create || String.IsNullOrWhiteSpace(eaName) ||
                        eaValue == null || eaValue.Length == 0)
                    {
                        throw new IOException("Relative create EA is invalid.");
                    }
                    eaBuffer = BuildEaBuffer(eaName, eaValue);
                    eaHandle = GCHandle.Alloc(eaBuffer, GCHandleType.Pinned);
                    eaPinned = true;
                }
                IoStatusBlock ioStatusBlock;
                uint access = FileReadAttributes | SynchronizeAccess |
                    (isDirectory
                        ? (ReadControl | WriteDac |
                            FileAddFile | FileAddSubdirectory | FileReadEa |
                            FileTraverse |
                            (publish ? DeleteAccess : 0))
                        : (create || publish
                            ? (GenericRead | GenericWrite | DeleteAccess)
                            : FileReadData));
                uint options = NtFileSynchronousIoNonAlert |
                    NtFileOpenReparsePoint |
                    (isDirectory ? NtFileDirectory : NtFileNonDirectory);
                int status = NtCreateFile(
                    out opened,
                    access,
                    ref attributes,
                    out ioStatusBlock,
                    IntPtr.Zero,
                    0,
                    exclusive ? 0 : (ShareRead |
                        (isDirectory ? ShareWrite : 0) |
                        (publish && isDirectory ? ShareDelete : 0)),
                    create ? NtFileCreate : NtFileOpen,
                    options,
                    eaPinned ? eaHandle.AddrOfPinnedObject() : IntPtr.Zero,
                    eaBuffer == null ? 0U : (uint)eaBuffer.Length);
                if (status != 0 || opened == null || opened.IsInvalid)
                {
                    if (opened != null)
                    {
                        opened.Dispose();
                    }
                    throw new IOException(
                        "Handle-relative build-cache entry cannot be opened. " +
                        "NTSTATUS=0x" + status.ToString("X8") +
                        " Path=" + expectedPath);
                }

                ByHandleFileInformation identity;
                if (!GetFileInformationByHandle(opened, out identity))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Relative build-cache identity cannot be read.");
                }
                bool actualDirectory =
                    (identity.FileAttributes & DirectoryAttribute) != 0;
                if ((identity.FileAttributes & ReparsePointAttribute) != 0)
                {
                    throw new IOException(
                        "Handle-relative build-cache entry is a reparse point: " +
                        expectedPath);
                }
                if (actualDirectory != isDirectory ||
                    (!actualDirectory && identity.NumberOfLinks != 1))
                {
                    throw new IOException(
                        "Relative build-cache entry is nonconforming: " + expectedPath);
                }
                NativeEntry result = new NativeEntry(
                    expectedPath,
                    ReadFinalPath(opened),
                    opened,
                    identity);
                opened = null;
                result.AssertIdentityAt(expectedPath);
                if (create && !isDirectory)
                {
                    byte[] bytes = content ?? new byte[0];
                    int count = writeLength < 0 ? bytes.Length : writeLength;
                    if (count < 0 || count > bytes.Length)
                    {
                        result.Dispose();
                        throw new IOException("Relative write length is invalid.");
                    }
                    uint written;
                    if (count > 0 && (!WriteFile(
                        result.handle,
                        bytes,
                        (uint)count,
                        out written,
                        IntPtr.Zero) || written != count))
                    {
                        result.Dispose();
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Relative build-cache content cannot be written.");
                    }
                    if (flush)
                    {
                        result.Flush();
                    }
                }
                return result;
                }
                finally
                {
                    if (descriptorPinned)
                    {
                        descriptorHandle.Free();
                    }
                    if (eaPinned)
                    {
                        eaHandle.Free();
                    }
                }
            }
            finally
            {
                if (opened != null)
                {
                    opened.Dispose();
                }
                if (parentHandleAdded)
                {
                    parent.handle.DangerousRelease();
                }
                if (unicodeBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(unicodeBuffer);
                }
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static byte[] BuildEaBuffer(string name, byte[] value)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            if (nameBytes.Length == 0 || nameBytes.Length > 255 ||
                value.Length > UInt16.MaxValue)
            {
                throw new IOException("Provisioning root EA is outside bounds.");
            }
            for (int index = 0; index < nameBytes.Length; index++)
            {
                byte current = nameBytes[index];
                if (current < 0x21 || current > 0x7E || current == (byte)'=')
                {
                    throw new IOException("Provisioning root EA name is invalid.");
                }
            }
            byte[] buffer = new byte[8 + nameBytes.Length + 1 + value.Length];
            buffer[4] = 0;
            buffer[5] = (byte)nameBytes.Length;
            buffer[6] = (byte)(value.Length & 0xFF);
            buffer[7] = (byte)((value.Length >> 8) & 0xFF);
            Buffer.BlockCopy(nameBytes, 0, buffer, 8, nameBytes.Length);
            Buffer.BlockCopy(value, 0, buffer, 9 + nameBytes.Length, value.Length);
            return buffer;
        }

        public byte[] ReadExtendedAttribute(string name)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            if (nameBytes.Length == 0 || nameBytes.Length > 255)
            {
                throw new IOException("Provisioning root EA name is invalid.");
            }
            IntPtr output = Marshal.AllocHGlobal(65536);
            try
            {
                IoStatusBlock ioStatusBlock;
                int status = NtQueryEaFile(
                    handle,
                    out ioStatusBlock,
                    output,
                    65536,
                    false,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    true);
                if (status != 0)
                {
                    throw new IOException(
                        "Provisioning root EA cannot be read. NTSTATUS=0x" +
                        status.ToString("X8"));
                }
                int actualNameLength = Marshal.ReadByte(output, 5);
                int valueLength = (ushort)Marshal.ReadInt16(output, 6);
                if (Marshal.ReadInt32(output, 0) != 0 ||
                    actualNameLength != nameBytes.Length ||
                    valueLength <= 0 || valueLength > 65535)
                {
                    throw new IOException("Provisioning root EA is nonconforming.");
                }
                byte[] actualName = new byte[actualNameLength];
                Marshal.Copy(IntPtr.Add(output, 8), actualName, 0, actualName.Length);
                int difference = 0;
                for (int index = 0; index < actualName.Length; index++)
                {
                    difference |= actualName[index] ^ nameBytes[index];
                }
                if (difference != 0)
                {
                    throw new IOException("Provisioning root EA name changed.");
                }
                byte[] value = new byte[valueLength];
                Marshal.Copy(
                    IntPtr.Add(output, 9 + actualNameLength),
                    value,
                    0,
                    value.Length);
                return value;
            }
            finally
            {
                Marshal.FreeHGlobal(output);
            }
        }

        public void RewriteAndFlush(byte[] content)
        {
            if (IsDirectory)
            {
                throw new IOException("A directory cannot be rewritten.");
            }
            long ignored;
            if (!SetFilePointerEx(handle, 0, out ignored, 0) ||
                !SetEndOfFile(handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Build-cache staging file cannot be truncated.");
            }
            uint written;
            if (content.Length > 0 && (!WriteFile(
                handle,
                content,
                (uint)content.Length,
                out written,
                IntPtr.Zero) || written != content.Length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Build-cache staging file cannot be rewritten.");
            }
            Flush();
        }

        public void RenameRelativeNoReplace(
            NativeEntry parent,
            string childName,
            string expectedPath)
        {
            if (parent == null || !parent.IsDirectory ||
                String.IsNullOrWhiteSpace(childName) ||
                !String.Equals(Path.GetFileName(childName), childName,
                    StringComparison.Ordinal))
            {
                throw new IOException("Relative publish target is invalid.");
            }
            byte[] name = Encoding.Unicode.GetBytes(childName);
            int rootOffset = IntPtr.Size == 8 ? 8 : 4;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + 4;
            int bufferLength = (nameOffset + name.Length + 7) & ~7;
            IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
            bool parentHandleAdded = false;
            try
            {
                for (int index = 0; index < bufferLength; index++)
                {
                    Marshal.WriteByte(buffer, index, 0);
                }
                parent.handle.DangerousAddRef(ref parentHandleAdded);
                Marshal.WriteInt32(buffer, 0, FileRenamePosixSemantics);
                Marshal.WriteIntPtr(buffer, rootOffset,
                    parent.handle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                IoStatusBlock ioStatusBlock;
                int status = NtSetInformationFile(
                    handle,
                    out ioStatusBlock,
                    buffer,
                    (uint)bufferLength,
                    NtFileRenameInformationEx);
                if (status != 0)
                {
                    int error = unchecked((int)RtlNtStatusToDosError(status));
                    throw new Win32Exception(
                        error,
                        "Build-cache staging publish failed. NTSTATUS=0x" +
                        status.ToString("X8"));
                }
                AssertIdentityAt(expectedPath);
            }
            finally
            {
                if (parentHandleAdded)
                {
                    parent.handle.DangerousRelease();
                }
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static NativeEntry Open(
            string path,
            bool isDirectory,
            bool shareDelete,
            bool stableFile,
            bool publishParent)
        {
            string fullPath = Path.GetFullPath(path);
            SafeFileHandle opened = CreateFileW(
                fullPath,
                FileReadAttributes |
                    (shareDelete && isDirectory ? DeleteAccess : 0) |
                    (publishParent
                        ? (FileAddFile | FileAddSubdirectory | FileTraverse)
                        : 0) |
                    (stableFile ? FileReadData : 0),
                ShareRead | (stableFile ? 0 : ShareWrite) |
                    (shareDelete ? ShareDelete : 0),
                IntPtr.Zero,
                OpenExisting,
                OpenReparsePoint | (isDirectory ? BackupSemantics : 0),
                IntPtr.Zero);
            if (opened.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                opened.Dispose();
                throw new Win32Exception(
                    error,
                    "Build-cache identity lease cannot be acquired: " + fullPath);
            }

            try
            {
                ByHandleFileInformation identity;
                if (!GetFileInformationByHandle(opened, out identity))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Build-cache identity cannot be read: " + fullPath);
                }
                bool actualDirectory =
                    (identity.FileAttributes & DirectoryAttribute) != 0;
                if (
                    actualDirectory != isDirectory ||
                    (identity.FileAttributes & ReparsePointAttribute) != 0 ||
                    (!actualDirectory && identity.NumberOfLinks != 1))
                {
                    throw new IOException(
                        "Build-cache entry is not an exact regular entry: " +
                        fullPath);
                }

                StringBuilder builder = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(
                    opened,
                    builder,
                    (uint)builder.Capacity,
                    0);
                if (length == 0 || length >= builder.Capacity)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Build-cache final path cannot be read: " + fullPath);
                }

                NativeEntry result = new NativeEntry(
                    fullPath,
                    builder.ToString(),
                    opened,
                    identity);
                if (!String.Equals(
                    Path.GetFullPath(result.FinalPath),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "Build-cache entry changed physical path: " + fullPath);
                }
                return result;
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        }

        public void Flush()
        {
            if (!FlushFileBuffers(handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Build-cache file buffers cannot be flushed.");
            }
        }

        public long GetLength()
        {
            long length;
            if (!GetFileSizeEx(handle, out length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Build-cache file length cannot be read.");
            }
            return length;
        }

        public string ComputeSha256()
        {
            if (IsDirectory)
            {
                throw new IOException("A directory cannot be hashed.");
            }
            SafeFileHandle duplicate;
            IntPtr process = GetCurrentProcess();
            if (!DuplicateHandle(
                process,
                handle,
                process,
                out duplicate,
                0,
                false,
                DuplicateSameAccess))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Build-cache stable file handle cannot be duplicated.");
            }
            using (duplicate)
            using (FileStream stream = new FileStream(
                duplicate,
                FileAccess.Read,
                4096,
                false))
            using (SHA256 sha = SHA256.Create())
            {
                stream.Position = 0;
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", String.Empty);
            }
        }

        public void AssertIdentityAt(string expectedPath)
        {
            AssertIdentity(false);
            string currentFinalPath = ReadFinalPath(handle);
            string expected = Path.GetFullPath(expectedPath);
            if (!String.Equals(
                Path.GetFullPath(currentFinalPath),
                expected,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Build-cache identity is not at the expected path: " +
                    expected);
            }
            FinalPath = currentFinalPath;
            LogicalPath = expected;
        }

        public void AssertUnchanged()
        {
            AssertIdentity(true);
        }

        private void AssertIdentity(bool requireOriginalPath)
        {
            if (handle == null || handle.IsClosed || handle.IsInvalid)
            {
                throw new ObjectDisposedException("Build-cache identity lease");
            }
            ByHandleFileInformation current;
            if (!GetFileInformationByHandle(handle, out current))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Build-cache identity lease cannot be refreshed.");
            }
            if (
                current.VolumeSerialNumber != information.VolumeSerialNumber ||
                current.FileIndexHigh != information.FileIndexHigh ||
                current.FileIndexLow != information.FileIndexLow ||
                current.FileAttributes != information.FileAttributes ||
                current.NumberOfLinks != information.NumberOfLinks)
            {
                throw new IOException("Build-cache identity changed while leased.");
            }
            if (requireOriginalPath && !String.Equals(
                Path.GetFullPath(ReadFinalPath(handle)),
                Path.GetFullPath(FinalPath),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Build-cache physical path changed while leased.");
            }
        }

        private static string ReadFinalPath(SafeFileHandle opened)
        {
            StringBuilder builder = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(
                opened,
                builder,
                (uint)builder.Capacity,
                0);
            if (length == 0 || length >= builder.Capacity)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Build-cache final path cannot be refreshed.");
            }
            return NormalizeFinalPath(builder.ToString());
        }

        private static string NormalizeFinalPath(string path)
        {
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            {
                return "\\\\" + path.Substring(8);
            }
            if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(4);
            }
            return path;
        }

        public void Dispose()
        {
            if (handle != null)
            {
                handle.Dispose();
                handle = null;
            }
        }
    }
}
'@
}

function ConvertTo-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    }
    catch {
        throw [ArgumentException]::new(
            "Path cannot be normalized. Path=[$Path]",
            $_.Exception)
    }
}

function Test-PathsOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    $leftPath = (ConvertTo-NormalizedFullPath -Path $Left) + '\'
    $rightPath = (ConvertTo-NormalizedFullPath -Path $Right) + '\'
    return $leftPath.StartsWith($rightPath, $comparison) -or
        $rightPath.StartsWith($leftPath, $comparison)
}

function Assert-PlainExistingAncestorChain {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    $segments = [Collections.Generic.List[string]]::new()
    $current = $fullPath
    while ($true) {
        $segments.Add($current)
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            break
        }
        $current = $parent.FullName
    }

    foreach ($entryPath in @($segments | Sort-Object { $_.Length })) {
        if (-not (Test-Path -LiteralPath $entryPath)) {
            continue
        }
        $item = Get-Item -LiteralPath $entryPath -Force -ErrorAction Stop
        if (
            -not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "Unsafe build-cache path: a reparse point or non-directory ancestor was found. Path=$entryPath"
        }
    }
}

function Resolve-PhysicalPathForCandidate {
    param([Parameter(Mandatory = $true)][string]$Path)

    Initialize-BuildCacheProvisionerNativeType
    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    Assert-PlainExistingAncestorChain -Path $fullPath
    $missing = New-Object 'Collections.Generic.Stack[string]'
    $existing = $fullPath
    while (-not (Test-Path -LiteralPath $existing)) {
        $parent = [IO.Directory]::GetParent($existing)
        if ($null -eq $parent) {
            throw "No existing ancestor was found. Path=$Path"
        }
        $missing.Push([IO.Path]::GetFileName($existing))
        $existing = $parent.FullName
    }

    $lease = [GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenLease(
        $existing,
        $true)
    try {
        $physical = $lease.FinalPath
        while ($missing.Count -gt 0) {
            $physical = Join-Path $physical $missing.Pop()
        }
        return ConvertTo-NormalizedFullPath -Path $physical
    }
    finally {
        $lease.Dispose()
    }
}

function Get-ProtectedPaths {
    $paths = [Collections.Generic.List[string]]::new()
    [void]$paths.Add($projectRoot)
    $testExecutionRoot = Join-Path $projectRoot $testExecutionDirectoryName
    [void]$paths.Add((Join-Path `
        $testExecutionRoot `
        $currentRuntimeDirectoryName))
    [void]$paths.Add('D:\DevCaches\georaeplan-v1-user-snapshots')

    foreach ($folderName in @('ApplicationData', 'LocalApplicationData')) {
        $folder = [Environment]::GetFolderPath($folderName)
        if (-not [string]::IsNullOrWhiteSpace($folder)) {
            [void]$paths.Add($folder)
        }
    }

    if (Test-Path -LiteralPath $testExecutionRoot) {
        foreach ($snapshot in @(Get-ChildItem `
            -LiteralPath $testExecutionRoot `
            -Directory `
            -Force `
            -Filter $protectedRuntimeSnapshotPattern `
            -ErrorAction Stop)) {
            [void]$paths.Add($snapshot.FullName)
        }
    }
    return @($paths | Sort-Object -Unique)
}

function Assert-CacheRootSafety {
    $logicalRoot = ConvertTo-NormalizedFullPath -Path $cacheRoot
    if (-not [string]::Equals(
        [IO.Path]::GetPathRoot($logicalRoot),
        'D:\',
        $comparison)) {
        throw "Build-cache root must remain on D:. Path=$logicalRoot"
    }
    Assert-PlainExistingAncestorChain -Path $logicalRoot
    $physicalRoot = Resolve-PhysicalPathForCandidate -Path $logicalRoot

    foreach ($protectedPath in @(Get-ProtectedPaths)) {
        $logicalProtected = ConvertTo-NormalizedFullPath -Path $protectedPath
        $physicalProtected = Resolve-PhysicalPathForCandidate -Path $logicalProtected
        if (
            (Test-PathsOverlap -Left $logicalRoot -Right $logicalProtected) -or
            (Test-PathsOverlap -Left $physicalRoot -Right $physicalProtected)
        ) {
            throw "Build-cache root overlaps a protected path. ProtectedPath=$logicalProtected"
        }
    }

    return [pscustomobject]@{
        LogicalPath = $logicalRoot
        PhysicalPath = $physicalRoot
    }
}

function Assert-RegularEmptyFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is nonconforming because it is not a file. Path=$Path"
    }
    $lease =
        [GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenStableFileLease(
            $Path)
    try {
        $lease.AssertIdentityAt($Path)
        if (
            $lease.GetLength() -ne 0 -or
            $lease.ComputeSha256() -cne $emptyFileSha256
        ) {
            throw "$Label is nonconforming because it is not empty. Path=$Path"
        }
        $lease | Add-Member `
            -MemberType NoteProperty `
            -Name ExpectedLength `
            -Value ([long]0)
        $lease | Add-Member `
            -MemberType NoteProperty `
            -Name ExpectedSha256 `
            -Value $emptyFileSha256
        return $lease
    }
    catch {
        $lease.Dispose()
        throw
    }
}

function Get-ExpectedDirectorySecurity {
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw 'The current Windows owner SID is unavailable.'
    }
    $security = New-Object Security.AccessControl.DirectorySecurity
    [void]$security.SetOwner($currentSid)
    [void]$security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $fullControl = [Security.AccessControl.FileSystemRights]::FullControl
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sid in @(
        $currentSid,
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::LocalSystemSid,
            $null)),
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
            $null))
    )) {
        [void]$security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            $fullControl,
            $inheritance,
            $propagation,
            $allow)))
    }
    return ,$security
}

function Get-ExpectedFileSecurity {
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) {
        throw 'The current Windows owner SID is unavailable.'
    }
    $security = New-Object Security.AccessControl.FileSecurity
    [void]$security.SetOwner($currentSid)
    [void]$security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @(
        $currentSid,
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::LocalSystemSid,
            $null)),
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
            $null))
    )) {
        [void]$security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)))
    }
    return ,$security
}

function Assert-ExactDirectorySecurity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $actual = Get-Acl -LiteralPath $Path
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (-not $actual.AreAccessRulesProtected -or
        $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne
            $currentSid) {
        throw "Build-cache directory ACL/owner contract is invalid. Path=$Path"
    }
    $expectedSids = @(
        $currentSid,
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::LocalSystemSid,
            $null)).Value,
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
            $null)).Value)
    $rules = @($actual.GetAccessRules(
        $true, $false, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne $expectedSids.Count) {
        throw "Build-cache directory ACL has unexpected rules. Path=$Path"
    }
    foreach ($sid in $expectedSids) {
        $matches = @($rules | Where-Object {
            $_.IdentityReference.Value -ceq $sid -and
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $_.FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl -and
            $_.InheritanceFlags -eq (
                [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                [Security.AccessControl.InheritanceFlags]::ObjectInherit) -and
            $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None
        })
        if ($matches.Count -ne 1) {
            throw "Build-cache directory ACL is nonconforming. Path=$Path SID=$sid"
        }
    }
}

function Assert-ExactFileSecurity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $actual = Get-Acl -LiteralPath $Path
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (-not $actual.AreAccessRulesProtected -or
        $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne
            $currentSid) {
        throw "Build-cache file ACL/owner contract is invalid. Path=$Path Owner=$($actual.GetOwner([Security.Principal.SecurityIdentifier]).Value) ExpectedOwner=$currentSid Protected=$($actual.AreAccessRulesProtected) Sddl=$($actual.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::All))"
    }
    $expectedSids = @(
        $currentSid,
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::LocalSystemSid,
            $null)).Value,
        (New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
            $null)).Value)
    $rules = @($actual.GetAccessRules(
        $true, $false, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne $expectedSids.Count) {
        throw "Build-cache file ACL has unexpected rules. Path=$Path"
    }
    foreach ($sid in $expectedSids) {
        $matches = @($rules | Where-Object {
            $_.IdentityReference.Value -ceq $sid -and
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $_.FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl -and
            $_.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and
            $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None
        })
        if ($matches.Count -ne 1) {
            throw "Build-cache file ACL is nonconforming. Path=$Path SID=$sid"
        }
    }
}

function ConvertTo-OwnerMetadataJson {
    param(
        [Parameter(Mandatory = $true)]$RootIdentity,
        [Parameter(Mandatory = $true)][string]$ExpectedPhysicalPath,
        [Parameter(Mandatory = $true)][string]$CreatedAtUtc
    )

    $metadata = [ordered]@{
        schemaVersion = 1
        owner = $expectedOwner
        cacheRootPath = ConvertTo-NormalizedFullPath -Path $cacheRoot
        cacheRootPhysicalPath = ConvertTo-NormalizedFullPath `
            -Path $ExpectedPhysicalPath
        volumeSerialNumber = $RootIdentity.VolumeSerialNumber
        fileId = $RootIdentity.FileId
        ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        leafRelativePaths = @($leafRelativePaths | ForEach-Object {
            $_.Replace('\', '/')
        })
        createdAtUtc = $CreatedAtUtc
    }
    return ($metadata | ConvertTo-Json -Depth 4 -Compress) +
        [Environment]::NewLine
}

function Assert-OwnerMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$RootIdentity,
        [Parameter(Mandatory = $true)][string]$ExpectedCreatedAtUtc,
        [AllowNull()]$LeaseSink
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }
    $lease =
        [GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenStableFileLease(
            $Path)
    try {
        $lease.AssertIdentityAt($Path)
        $metadataLength = $lease.GetLength()
        if ($metadataLength -le 0 -or $metadataLength -gt 65536) {
            throw 'Build-cache owner metadata length is nonconforming.'
        }
        $metadata = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $propertyNames = @($metadata.PSObject.Properties.Name)
        $expectedPropertyNames = @(
            'schemaVersion',
            'owner',
            'cacheRootPath',
            'cacheRootPhysicalPath',
            'volumeSerialNumber',
            'fileId',
            'ownerSid',
            'leafRelativePaths',
            'createdAtUtc'
        )
        if (
            @(Compare-Object `
                $propertyNames `
                $expectedPropertyNames).Count -ne 0 -or
            [int]$metadata.schemaVersion -ne 1 -or
            [string]$metadata.owner -cne $expectedOwner -or
            -not [string]::Equals(
                [string]$metadata.cacheRootPath,
                (ConvertTo-NormalizedFullPath -Path $cacheRoot),
                $comparison) -or
            -not [string]::Equals(
                [string]$metadata.cacheRootPhysicalPath,
                $RootIdentity.FinalPath,
                $comparison) -or
            [string]$metadata.volumeSerialNumber -cne
                $RootIdentity.VolumeSerialNumber -or
            [string]$metadata.fileId -cne $RootIdentity.FileId -or
            [string]$metadata.ownerSid -cne
                [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
            [string]$metadata.createdAtUtc -cne $ExpectedCreatedAtUtc
        ) {
            throw 'Build-cache owner metadata is nonconforming.'
        }
        $actualLeaves = @($metadata.leafRelativePaths | ForEach-Object {
            [string]$_
        })
        $expectedLeaves = @($leafRelativePaths | ForEach-Object {
            $_.Replace('\', '/')
        })
        if (
            $actualLeaves.Count -ne $expectedLeaves.Count -or
            @(Compare-Object `
                $actualLeaves `
                $expectedLeaves `
                -SyncWindow 0).Count -ne 0
        ) {
            throw 'Build-cache owner leaf metadata is nonconforming.'
        }
        $createdAt = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParseExact(
            [string]$metadata.createdAtUtc,
            'o',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$createdAt)) {
            throw 'Build-cache owner timestamp is nonconforming.'
        }
        if ($null -ne $LeaseSink) {
            $lease | Add-Member `
                -MemberType NoteProperty `
                -Name ExpectedLength `
                -Value $metadataLength
            $lease | Add-Member `
                -MemberType NoteProperty `
                -Name ExpectedSha256 `
                -Value $lease.ComputeSha256()
            [void]$LeaseSink.Add($lease)
            $lease = $null
        }
        return $true
    }
    finally {
        if ($null -ne $lease) {
            $lease.Dispose()
        }
    }
}

function Assert-StableFileContract {
    param(
        [Parameter(Mandatory = $true)]$Lease,
        [Parameter(Mandatory = $true)][string]$ExpectedPath
    )

    $Lease.AssertIdentityAt($ExpectedPath)
    if (
        $Lease.GetLength() -ne [long]$Lease.ExpectedLength -or
        $Lease.ComputeSha256() -cne [string]$Lease.ExpectedSha256
    ) {
        throw "Stable build-cache file bytes changed. Path=$ExpectedPath"
    }
}

function Get-JournalBytes {
    param([Parameter(Mandatory = $true)]$Root,[Parameter(Mandatory = $true)][string]$StageName,[Parameter(Mandatory = $true)][string]$ProvisioningId)
    $x=[ordered]@{schemaVersion=3;owner=$expectedOwner;cacheRootPath=(ConvertTo-NormalizedFullPath $cacheRoot);ownerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;provisioningId=$ProvisioningId;stageName=$StageName;volumeSerialNumber=$Root.VolumeSerialNumber;fileId=$Root.FileId;leafRelativePaths=@($leafRelativePaths|%{$_.Replace('\','/')})}
    $s=($x|ConvertTo-Json -Depth 4 -Compress)+[Environment]::NewLine
    return (New-Object Text.UTF8Encoding($false)).GetBytes($s)
}
function Get-ProvisioningId {
    $value=(ConvertTo-NormalizedFullPath $cacheRoot)+'|'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $bytes=(New-Object Text.UTF8Encoding($false)).GetBytes($value)
    return (Get-Hash $bytes).Substring(0,32).ToLowerInvariant()
}
function Get-RootProvisioningTokenBytes([string]$ProvisioningId,[string]$CreatedAtUtc) {
    $normalizedRoot=ConvertTo-NormalizedFullPath $cacheRoot
    $pathBytes=(New-Object Text.UTF8Encoding($false)).GetBytes($normalizedRoot)
    $created=[DateTimeOffset]::MinValue
    if(-not[DateTimeOffset]::TryParseExact($CreatedAtUtc,'o',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$created)){throw 'Build-cache root provisioning timestamp is nonconforming.'}
    $x=[ordered]@{schemaVersion=1;owner=$expectedOwner;cacheRootPath=$normalizedRoot;cacheRootPathSha256=(Get-Hash $pathBytes);ownerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;provisioningId=$ProvisioningId;createdAtUtc=$CreatedAtUtc}
    $s=($x|ConvertTo-Json -Compress)+[Environment]::NewLine
    return (New-Object Text.UTF8Encoding($false)).GetBytes($s)
}
function Read-RootProvisioningToken($Root,[string]$ProvisioningId) {
    $actual=$Root.ReadExtendedAttribute($rootProvisioningEaName)
    if($actual.Length-le0-or$actual.Length-gt4096){throw 'Build-cache root provisioning token length is nonconforming.'}
    try{
        $raw=(New-Object Text.UTF8Encoding($false,$true)).GetString($actual)
        $metadata=$raw|ConvertFrom-Json -ErrorAction Stop
        $properties=@($metadata.PSObject.Properties.Name)
        $expectedProperties=@('schemaVersion','owner','cacheRootPath','cacheRootPathSha256','ownerSid','provisioningId','createdAtUtc')
        if(@(Compare-Object $properties $expectedProperties -SyncWindow 0).Count-ne0-or[int]$metadata.schemaVersion-ne1-or[string]$metadata.owner-cne$expectedOwner-or[string]$metadata.cacheRootPath-cne(ConvertTo-NormalizedFullPath $cacheRoot)-or[string]$metadata.ownerSid-cne[Security.Principal.WindowsIdentity]::GetCurrent().User.Value-or[string]$metadata.provisioningId-cne$ProvisioningId){throw 'fields mismatch'}
        $expected=Get-RootProvisioningTokenBytes $ProvisioningId ([string]$metadata.createdAtUtc)
        if($expected.Length-ne$actual.Length){throw 'length mismatch'}
        $difference=0
        for($i=0;$i-lt$actual.Length;$i++){$difference=$difference-bor($actual[$i]-bxor$expected[$i])}
        if($difference-ne0){throw 'bytes mismatch'}
        return [pscustomobject]@{Bytes=$actual;CreatedAtUtc=[string]$metadata.createdAtUtc}
    }catch{throw 'Build-cache root provisioning token is nonconforming.'}
}
function Assert-RootProvisioningToken($Root,[byte[]]$Expected) {
    $actual=$Root.ReadExtendedAttribute($rootProvisioningEaName)
    if($actual.Length-ne$Expected.Length){throw 'Build-cache root provisioning token length is nonconforming.'}
    $difference=0
    for($i=0;$i-lt$actual.Length;$i++){$difference=$difference-bor($actual[$i]-bxor$Expected[$i])}
    if($difference-ne0){throw 'Build-cache root provisioning token is nonconforming.'}
}
function Get-Hash([byte[]]$Bytes) {
    $h=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($h.ComputeHash($Bytes)).Replace('-','')}finally{$h.Dispose()}
}
function Set-StableExpected($Lease,[long]$Length,[string]$Hash) {
    $Lease|Add-Member NoteProperty ExpectedLength $Length -Force
    $Lease|Add-Member NoteProperty ExpectedSha256 $Hash -Force
}
function Assert-Bytes($Lease,[string]$Path,[byte[]]$Bytes,[string]$Label) {
    $h=Get-Hash $Bytes
    if($Lease.GetLength()-ne $Bytes.Length-or $Lease.ComputeSha256()-cne $h){throw "$Label bytes are nonconforming. Path=$Path"}
    Set-StableExpected $Lease $Bytes.Length $h
    Assert-StableFileContract $Lease $Path
}
function Read-PinnedFileBytes($Lease,[string]$Path,[long]$MaximumLength) {
    $length=$Lease.GetLength()
    if($length-lt0-or$length-gt$MaximumLength){throw "Pending metadata length is outside its bounded contract. Path=$Path"}
    $bytes=New-Object byte[] ([int]$length)
    $stream=New-Object IO.FileStream($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
    try{$offset=0;while($offset-lt$bytes.Length){$read=$stream.Read($bytes,$offset,$bytes.Length-$offset);if($read-eq0){throw "Pending metadata had a short read. Path=$Path"};$offset+=$read}}finally{$stream.Dispose()}
    $Lease.AssertIdentityAt($Path)
    return ,$bytes
}
function Assert-RecoverablePendingBytes($Lease,[string]$Path,[byte[]]$Expected,[string]$Name) {
    $actual=Read-PinnedFileBytes $Lease $Path 65536
    if($actual.Length-gt$Expected.Length){throw "Pending metadata is longer than its exact destination. Path=$Path"}
    $prefixMatches=$true
    for($i=0;$i-lt$actual.Length;$i++){if($actual[$i]-ne$Expected[$i]){$prefixMatches=$false;break}}
    if(-not$prefixMatches){throw "Pending metadata bytes are not an exact interrupted prefix. Path=$Path"}
}
function Publish-AtomicStableFileChild {
    param(
        [Parameter(Mandatory = $true)]$Parent,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][byte[]]$FileSecurityDescriptor,
        [Parameter(Mandatory = $true)][string]$PendingId,
        [string]$Fault = 'None')
    $path=Join-Path $Parent.LogicalPath $Name
    $pendingName=$Name+'.pending-'+$PendingId
    $pendingPath=Join-Path $Parent.LogicalPath $pendingName
    $pendingMatches=@(Get-ChildItem -LiteralPath $Parent.LogicalPath -Force -ErrorAction Stop | Where-Object {$_.Name.StartsWith($Name+'.pending-',[StringComparison]::Ordinal)})
    foreach($entry in $pendingMatches){if($entry.Name-cne$pendingName){throw "Published metadata has an unknown pending sibling. Path=$($entry.FullName)"}}
    if(Test-Path -LiteralPath $path){
        if(Test-Path -LiteralPath $pendingPath){throw "Published metadata has an unexpected pending sibling. Path=$pendingPath"}
        $existing=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenStableFileChild($Parent,$Name)
        Assert-ExactFileSecurity $path
        Assert-Bytes $existing $path $Bytes 'Published metadata'
        return $existing
    }
    $pending=$null
    try {
        if(Test-Path -LiteralPath $pendingPath){
            $pending=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenPublishFileChild($Parent,$pendingName)
            Assert-ExactFileSecurity $pendingPath
            Assert-RecoverablePendingBytes $pending $pendingPath $Bytes $Name
            $pending.RewriteAndFlush($Bytes)
        } else {
            $faultPrefix=if($Name-ceq$journalName){'Journal'}elseif($Name-ceq$ownerFileName){'Owner'}else{''}
            $isShortWrite=$Fault-ceq($faultPrefix+'ShortWrite')-and$faultPrefix.Length-gt0
            $isOneByteWrite=$Fault-ceq($faultPrefix+'OneByteWrite')-and$faultPrefix.Length-gt0
            $isTailShortWrite=$Fault-ceq($faultPrefix+'TailShortWrite')-and$faultPrefix.Length-gt0
            $isBeforeFlush=$Fault-ceq($faultPrefix+'BeforeFlush')-and$faultPrefix.Length-gt0
            $isAfterFlush=$Fault-ceq($faultPrefix+'AfterFlushBeforePublish')-and$faultPrefix.Length-gt0
            $isProcessKill=$Fault-ceq($faultPrefix+'ProcessKillAfterFlush')-and$faultPrefix.Length-gt0
            $writeLength=if($isOneByteWrite){1}elseif($isTailShortWrite){[Math]::Max(1,$Bytes.Length-1)}elseif($isShortWrite){[Math]::Max(1,[int]($Bytes.Length/2))}else{-1}
            $flush=-not$isBeforeFlush
            $pending=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::CreatePublishFileChild($Parent,$pendingName,$Bytes,$FileSecurityDescriptor,$writeLength,$flush)
            Assert-ExactFileSecurity $pendingPath
            if($isShortWrite-or$isOneByteWrite-or$isTailShortWrite){throw 'Injected metadata short write.'}
            if($isBeforeFlush){throw 'Injected failure before metadata flush.'}
            if($isAfterFlush){throw 'Injected failure after metadata staging flush.'}
            if($isProcessKill){[Diagnostics.Process]::GetCurrentProcess().Kill()}
        }
        Assert-Bytes $pending $pendingPath $Bytes 'Pending metadata'
        $pendingFileId=$pending.FileId
        $pendingSddl=(Get-Acl -LiteralPath $pendingPath).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::All)
        $pending.RenameRelativeNoReplace($Parent,$Name,$path)
        Assert-ExactFileSecurity $path
        $publishedSddl=(Get-Acl -LiteralPath $path).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::All)
        if($pending.FileId-cne$pendingFileId-or$publishedSddl-cne$pendingSddl){throw "Published metadata changed file identity or exact security descriptor. Path=$path"}
        Assert-Bytes $pending $path $Bytes 'Published metadata'
        # TEST-HOOK: AfterMetadataPublishBeforeLeaseReturn
        return $pending
    } catch {
        if($pending){$pending.Dispose()}
        throw
    }
}
function Assert-PartialNames([string]$Root) {
    $top=@($journalName,($journalName+'.pending-'+$provisioningId),$ownerFileName,($ownerFileName+'.pending-'+$provisioningId),$coordinatorFileName,'temp','nuget','dotnet-home')
    foreach($e in @(Get-ChildItem -LiteralPath $Root -Force)){if($top-notcontains$e.Name){throw "Unknown partial root entry. Path=$($e.FullName)"}}
    $n=Join-Path $Root 'nuget'
    if(Test-Path -LiteralPath $n){
        if(-not(Test-Path -LiteralPath $n -PathType Container)){throw 'Partial nuget entry is not a directory.'}
        foreach($e in @(Get-ChildItem -LiteralPath $n -Force)){if(@('packages','http-cache','plugins-cache')-notcontains$e.Name){throw "Unknown partial nuget entry. Path=$($e.FullName)"}}
    }
}
function Assert-NoReparseStructure($RootLease,[string]$RootPath) {
    foreach($name in @('temp','nuget','dotnet-home')) {
        $path=Join-Path $RootPath $name
        if(Test-Path -LiteralPath $path -PathType Container) {
            $lease=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenDirectoryChild($RootLease,$name)
            try{$lease.AssertIdentityAt($path)}finally{$lease.Dispose()}
        }
    }
    $nugetPath=Join-Path $RootPath 'nuget'
    if(Test-Path -LiteralPath $nugetPath -PathType Container) {
        $nuget=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenDirectoryChild($RootLease,'nuget')
        try {
            foreach($name in @('packages','http-cache','plugins-cache')) {
                $path=Join-Path $nugetPath $name
                if(Test-Path -LiteralPath $path -PathType Container) {
                    $lease=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenDirectoryChild($nuget,$name)
                    try{$lease.AssertIdentityAt($path)}finally{$lease.Dispose()}
                }
            }
        }
        finally{$nuget.Dispose()}
    }
}

Initialize-BuildCacheProvisionerNativeType
$rootPlan=Assert-CacheRootSafety
$cacheParent=Split-Path -Parent $rootPlan.LogicalPath
$rootName=[IO.Path]::GetFileName($rootPlan.LogicalPath)
$journalName='.georaeplan-build-cache-provisioning.json'
$provisioningId=Get-ProvisioningId
$rootTokenBytes=$null
$rootCreatedAtUtc=$null
$utf8=New-Object Text.UTF8Encoding($false)
$directoryDescriptor=(Get-ExpectedDirectorySecurity).GetSecurityDescriptorBinaryForm()
$fileDescriptor=(Get-ExpectedFileSecurity).GetSecurityDescriptorBinaryForm()
if(-not(Test-Path -LiteralPath $cacheParent -PathType Container)){throw "Build-cache parent must be pre-provisioned. Path=$cacheParent"}
$parent=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenPublishParentLease($cacheParent)
$root=$null;$journal=$null;$coordinator=$null
$dirs=[Collections.Generic.List[object]]::new()
$meta=[Collections.Generic.List[object]]::new()
$sentinels=[Collections.Generic.List[object]]::new()
$missing=[Collections.Generic.List[object]]::new()
try{
    $parent.AssertIdentityAt($cacheParent)
    $hasRoot=Test-Path -LiteralPath $rootPlan.LogicalPath -PathType Container
    if((Test-Path -LiteralPath $rootPlan.LogicalPath)-and-not$hasRoot){throw 'Build-cache root is not a directory.'}
    Write-Output "Mode=$(if($Apply){'Apply'}else{'DryRun'})"
    Write-Output 'EnvironmentPathCount=6'
    Write-Output 'UniqueLeafCount=5'
    Write-Output 'EnvironmentAliases=TEMP,TMP->temp'
    Write-Output "CacheRoot=$($rootPlan.LogicalPath)"
    if(-not$hasRoot-and-not$Apply){
        Write-Output "WouldCreateJournal=$(Join-Path $rootPlan.LogicalPath $journalName)"
        Write-Output "WouldCreateDirectory=$($rootPlan.LogicalPath)"
        foreach($r in $leafRelativePaths){Write-Output "WouldCreateDirectory=$(Join-Path $rootPlan.LogicalPath $r)"}
        Write-Output "WouldCreateOwnerMarker=$(Join-Path $rootPlan.LogicalPath $ownerFileName)"
        Write-Output "WouldCreateCoordinator=$(Join-Path $rootPlan.LogicalPath $coordinatorFileName)"
        Write-Output 'ProvisioningComplete=False';exit 0
    }
    if($hasRoot){
        $root=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenDirectoryChild($parent,$rootName)
        Assert-NoReparseStructure $root $rootPlan.LogicalPath
        try{
            Assert-ExactDirectorySecurity $rootPlan.LogicalPath
            $rootToken=Read-RootProvisioningToken $root $provisioningId
            $rootTokenBytes=$rootToken.Bytes
            $rootCreatedAtUtc=$rootToken.CreatedAtUtc
        }catch{throw "An unowned or unbound canonical root cannot be adopted. $($_.Exception.Message)"}
    } else {
        if(-not$Apply){Write-Output "WouldCreateDirectory=$($rootPlan.LogicalPath)";Write-Output 'ProvisioningComplete=False';exit 0}
        # TEST-HOOK: BeforeRootCreate
        $parent.AssertIdentityAt($cacheParent)
        $rootCreatedAtUtc=[DateTime]::UtcNow.ToString('o')
        $rootTokenBytes=Get-RootProvisioningTokenBytes $provisioningId $rootCreatedAtUtc
        $root=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::CreateProvisionedDirectoryChild($parent,$rootName,$directoryDescriptor,$rootProvisioningEaName,$rootTokenBytes)
        if($root.VolumeSerialNumber-cne$parent.VolumeSerialNumber){throw 'Handle-relative root crossed a volume.'}
        Assert-ExactDirectorySecurity $rootPlan.LogicalPath
        Assert-RootProvisioningToken $root $rootTokenBytes
        $hasRoot=$true
    }
    Assert-PartialNames $rootPlan.LogicalPath
    $journalPath=Join-Path $rootPlan.LogicalPath $journalName
    $journalBytes=Get-JournalBytes $root $rootName $provisioningId
    $hasJournal=Test-Path -LiteralPath $journalPath -PathType Leaf
    if((Test-Path -LiteralPath $journalPath)-and-not$hasJournal){throw 'Provisioning journal is not a regular file.'}
    if($hasJournal){
        $journal=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenStableFileChild($root,$journalName)
        Assert-ExactFileSecurity $journalPath
        Assert-Bytes $journal $journalPath $journalBytes 'Provisioning journal'
    }elseif($Apply){
        $journal=Publish-AtomicStableFileChild $root $journalName $journalBytes $fileDescriptor $provisioningId $TestFaultInjection
        $hasJournal=$true
    }else{
        Write-Output "WouldCreateJournal=$journalPath";Write-Output 'ProvisioningComplete=False';exit 0
    }
    if($TestFaultInjection-ceq'AfterJournal'){throw 'Injected failure after journal.'}
    if($TestFaultInjection-ceq'AfterRoot'){throw 'Injected failure after root.'}
    $root.AssertIdentityAt($rootPlan.LogicalPath)
    Assert-NoReparseStructure $root $rootPlan.LogicalPath
    $ownerPath=Join-Path $rootPlan.LogicalPath $ownerFileName
    $hasOwner=Test-Path -LiteralPath $ownerPath -PathType Leaf
    if((Test-Path -LiteralPath $ownerPath)-and-not$hasOwner){throw 'Owner marker is not a regular file.'}
    $hasJournal=$null-ne$journal
    if(-not$hasOwner-and-not$hasJournal){throw 'An unowned partial canonical root cannot be adopted.'}
    if($hasJournal){Assert-PartialNames $rootPlan.LogicalPath}
    if($hasOwner){
        $o=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenStableFileChild($root,$ownerFileName)
        Assert-ExactFileSecurity $ownerPath
        Set-StableExpected $o $o.GetLength() $o.ComputeSha256()
        [void](Assert-OwnerMetadata $ownerPath $root $rootCreatedAtUtc);[void]$meta.Add($o)
    }elseif($Apply){
        # TEST-HOOK: BeforeOwnerMarkerPublish
        $b=$utf8.GetBytes((ConvertTo-OwnerMetadataJson $root $rootPlan.PhysicalPath $rootCreatedAtUtc))
        $o=Publish-AtomicStableFileChild $root $ownerFileName $b $fileDescriptor $provisioningId $TestFaultInjection
        Set-StableExpected $o $b.Length (Get-Hash $b)
        [void]$meta.Add($o);$hasOwner=$true
        if($TestFaultInjection-ceq'AfterOwner'){throw 'Injected failure after owner.'}
    }
    if(-not$Apply-and-not$hasOwner){Write-Output "WouldCreateOwnerMarker=$ownerPath";Write-Output 'ProvisioningComplete=False';exit 0}
    [void]$dirs.Add([pscustomobject]@{Path=$rootPlan.LogicalPath;Lease=$root})
    $nugetPath=Join-Path $rootPlan.LogicalPath 'nuget'
    if(Test-Path -LiteralPath $nugetPath -PathType Container){$nuget=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenDirectoryChild($root,'nuget');Assert-ExactDirectorySecurity $nugetPath}
    elseif($hasJournal-and$Apply){$nuget=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::CreateDirectoryChild($root,'nuget',$directoryDescriptor);Assert-ExactDirectorySecurity $nugetPath}
    else{throw 'Required nuget directory is missing.'}
    [void]$dirs.Add([pscustomobject]@{Path=$nugetPath;Lease=$nuget})
    foreach($rel in $leafRelativePaths){
        $p=$rel.Split([char]'\');$par=if($p.Count-eq1){$root}else{$nuget};$name=$p[-1];$path=Join-Path $rootPlan.LogicalPath $rel
        if(Test-Path -LiteralPath $path -PathType Container){$l=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenDirectoryChild($par,$name);Assert-ExactDirectorySecurity $path}
        elseif($hasJournal-and$Apply){$l=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::CreateDirectoryChild($par,$name,$directoryDescriptor);Assert-ExactDirectorySecurity $path}
        else{throw "Required leaf is missing. Path=$path"}
        [void]$dirs.Add([pscustomobject]@{Path=$path;Lease=$l})
    }
    $coordPath=Join-Path $rootPlan.LogicalPath $coordinatorFileName
    $hasCoord=Test-Path -LiteralPath $coordPath -PathType Leaf
    if((Test-Path -LiteralPath $coordPath)-and-not$hasCoord){throw 'Coordinator is not a regular file.'}
    foreach($d in @($dirs|Select-Object -Skip 2)){
        $sp=Join-Path $d.Path $sentinelFileName
        if(Test-Path -LiteralPath $sp -PathType Leaf){
            $s=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenStableFileChild($d.Lease,$sentinelFileName)
            Assert-ExactFileSecurity $sp
            Assert-Bytes $s $sp ([byte[]]@()) 'Build-cache sentinel';[void]$sentinels.Add($s)
        }elseif(Test-Path -LiteralPath $sp){throw "Build-cache sentinel is nonconforming. Path=$sp"}
        else{[void]$missing.Add([pscustomobject]@{Path=$sp;Parent=$d.Lease})}
    }
    $already=$hasOwner-and$hasCoord-and$missing.Count-eq0
    if(-not$Apply){
        if(-not$hasCoord){Write-Output "WouldCreateCoordinator=$coordPath"}
        foreach($x in $missing){Write-Output "WouldCreateSentinel=$($x.Path)"}
        Write-Output 'ProvisioningComplete=False';exit 0
    }
    # TEST-HOOK: BeforeCoordinatorOpenOrCreate
    try {
        if($hasCoord){$coordinator=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenExclusiveFileChild($root,$coordinatorFileName);Assert-ExactFileSecurity $coordPath}
        else{$coordinator=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::CreateExclusiveFileChild($root,$coordinatorFileName,$fileDescriptor);Assert-ExactFileSecurity $coordPath}
    }
    catch {
        throw [InvalidOperationException]::new(
            "Build-cache coordinator lease cannot be acquired. Path=$coordPath",
            $_.Exception)
    }
    Assert-Bytes $coordinator $coordPath ([byte[]]@()) 'Coordinator'
    foreach($d in $dirs){$directoryPath=$d.Path;# TEST-HOOK: BeforeAclMutation
        $d.Lease.AssertIdentityAt($directoryPath);Assert-ExactDirectorySecurity $directoryPath}
    $made=0
    foreach($x in $missing){
        foreach($d in $dirs){$d.Lease.AssertIdentityAt($d.Path)}
        $sentinelPath=$x.Path
        # TEST-HOOK: BeforeSentinelPublish
        $s=[GeoraePlan.BuildCacheProvisioner.NativeEntry]::CreateStableFileChild($x.Parent,$sentinelFileName,[byte[]]@(),$fileDescriptor)
        Assert-ExactFileSecurity $sentinelPath
        Assert-Bytes $s $sentinelPath ([byte[]]@()) 'Build-cache sentinel';[void]$sentinels.Add($s);$made++
        if($TestFaultInjection-ceq'AfterFirstSentinel'-and$made-eq1){throw 'Injected failure after first sentinel.'}
    }
    foreach($l in $meta){Assert-StableFileContract $l $l.LogicalPath}
    foreach($l in $sentinels){Assert-StableFileContract $l $l.LogicalPath}
    if($journal){Assert-StableFileContract $journal $journalPath}
    Assert-RootProvisioningToken $root $rootTokenBytes
    Write-Output "AlreadyProvisioned=$already"
    Write-Output 'ProvisioningComplete=True'
}finally{
    if($coordinator){$coordinator.Dispose()}
    foreach($l in $sentinels){if($l){$l.Dispose()}}
    foreach($l in $meta){if($l){$l.Dispose()}}
    foreach($d in $dirs){if($d.Lease-and$d.Lease-ne$root){$d.Lease.Dispose()}}
    if($root){$root.Dispose()};if($journal){$journal.Dispose()};$parent.Dispose()
}
