using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed class ImmutableSqliteInspectionGuard : IDisposable
{
    private static readonly string[] SidecarSuffixes =
        ["-wal", "-shm", "-journal"];

    private readonly FileStream _databaseHandle;
    private readonly DatabaseFileIdentity _databaseIdentity;
    private readonly string _finalDatabasePath;
    private readonly long _initialLength;
    private readonly DateTime _initialLastWriteTimeUtc;
    private readonly string _initialSha256;
    private bool _disposed;

    private ImmutableSqliteInspectionGuard(
        string databasePath,
        FileStream databaseHandle)
    {
        DatabasePath = databasePath;
        _databaseHandle = databaseHandle;
        var fileInformation = ReadFileInformation(
            databaseHandle.SafeFileHandle);
        _databaseIdentity = ToIdentity(fileInformation);
        _finalDatabasePath = ReadFinalPath(
            databaseHandle.SafeFileHandle);
        AssertCanonicalFileInformation(
            fileInformation,
            _finalDatabasePath);
        _initialLength = databaseHandle.Length;
        _initialLastWriteTimeUtc =
            File.GetLastWriteTimeUtc(databasePath);
        _initialSha256 = ComputeSha256(databaseHandle);
        AssertStableSidecarFree();
    }

    public string DatabasePath { get; }

    public static ImmutableSqliteInspectionGuard Acquire(
        string databasePath)
    {
        var normalizedDatabasePath =
            Path.GetFullPath(databasePath);
        if (!File.Exists(normalizedDatabasePath))
        {
            throw new FileNotFoundException(
                "The SQLite inspection source does not exist.",
                normalizedDatabasePath);
        }

        AssertNoReparsePath(normalizedDatabasePath);

        FileStream? databaseHandle = null;
        try
        {
            databaseHandle = File.Open(
                normalizedDatabasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            AssertNoReparsePath(normalizedDatabasePath);
            return new ImmutableSqliteInspectionGuard(
                normalizedDatabasePath,
                databaseHandle);
        }
        catch (IOException ex)
        {
            databaseHandle?.Dispose();
            throw new InvalidOperationException(
                "SQLite inspection requires a finalized database with no " +
                "active writer. Close the isolated app and finalize its " +
                "database before inspection.",
                ex);
        }
        catch
        {
            databaseHandle?.Dispose();
            throw;
        }
    }

    public void AssertStableSidecarFree()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertNoReparsePath(DatabasePath);
        AssertNoSidecars(DatabasePath);

        var handleInformation = ReadFileInformation(
            _databaseHandle.SafeFileHandle);
        AssertCanonicalFileInformation(
            handleInformation,
            ReadFinalPath(_databaseHandle.SafeFileHandle));

        using (var pathProbe = new FileStream(
                   DatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var pathInformation = ReadFileInformation(
                pathProbe.SafeFileHandle);
            var pathFinalPath = ReadFinalPath(
                pathProbe.SafeFileHandle);
            if (ToIdentity(pathInformation) != _databaseIdentity ||
                pathInformation.NumberOfLinks != 1 ||
                !string.Equals(
                    pathFinalPath,
                    _finalDatabasePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The SQLite inspection source path identity changed " +
                    "while the read-only guard was held.");
            }
        }

        if (_databaseHandle.Length != _initialLength ||
            File.GetLastWriteTimeUtc(DatabasePath) !=
            _initialLastWriteTimeUtc ||
            !string.Equals(
                ComputeSha256(_databaseHandle),
                _initialSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The SQLite inspection source changed while the read-only " +
                "inspection guard was held.");
        }

        AssertNoSidecars(DatabasePath);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _databaseHandle.Dispose();
    }

    private void AssertCanonicalFileInformation(
        NativeMethods.ByHandleFileInformation information,
        string finalPath)
    {
        if (ToIdentity(information) != _databaseIdentity ||
            information.NumberOfLinks != 1 ||
            ((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !string.Equals(
                finalPath,
                _finalDatabasePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                finalPath,
                DatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The SQLite inspection source must be a canonical, " +
                "single-link, non-reparse file.");
        }
    }

    private static void AssertNoReparsePath(string databasePath)
    {
        var fileAttributes = File.GetAttributes(databasePath);
        if ((fileAttributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "The SQLite inspection source cannot be a directory or " +
                "reparse point.");
        }

        var directory = new DirectoryInfo(
            Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException(
                "The SQLite inspection source must have a parent directory."));
        while (directory is not null)
        {
            if (!directory.Exists ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The SQLite inspection source path cannot traverse a " +
                    "reparse directory.");
            }

            directory = directory.Parent;
        }
    }

    private static DatabaseFileIdentity ToIdentity(
        NativeMethods.ByHandleFileInformation information)
        => new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow);

    private static NativeMethods.ByHandleFileInformation ReadFileInformation(
        SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(
                handle,
                out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not inspect the SQLite source file identity.");
        }

        return information;
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = NativeMethods.GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                flags: 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not resolve the SQLite source file path.");
            }

            if (length < buffer.Capacity)
            {
                var path = buffer.ToString();
                return path.StartsWith(
                        @"\\?\UNC\",
                        StringComparison.OrdinalIgnoreCase)
                    ? @"\\" + path[8..]
                    : path.StartsWith(
                        @"\\?\",
                        StringComparison.OrdinalIgnoreCase)
                        ? path[4..]
                        : path;
            }

            capacity = checked((int)length + 1);
        }
    }

    private static void AssertNoSidecars(string databasePath)
    {
        var sidecars = SidecarSuffixes
            .Select(suffix => databasePath + suffix)
            .Where(File.Exists)
            .ToList();
        if (sidecars.Count > 0)
        {
            throw new InvalidOperationException(
                "SQLite inspection requires a finalized sidecar-free " +
                "database. Finalize the isolated app database before " +
                $"inspection. Found: {string.Join(
                    ", ",
                    sidecars.Select(Path.GetFileName))}");
        }
    }

    private static string ComputeSha256(FileStream databaseHandle)
    {
        databaseHandle.Position = 0;
        try
        {
            return Convert.ToHexString(
                SHA256.HashData(databaseHandle));
        }
        finally
        {
            databaseHandle.Position = 0;
        }
    }

    private readonly record struct DatabaseFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

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
    }
}
