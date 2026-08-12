using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.Tools.SyncDiag;

public sealed class IsolatedPreparationDatabaseLease : IDisposable
{
    public const string PreparationLockFileName = ".georaeplan-prepare.lock";
    public const string IsolatedSeedMarkerFileName =
        ".georaeplan-isolated-seed-root";
    public const string LocalDatabaseFileName = "\uac70\ub798\ud50c\ub79c.db";

    private readonly List<GuardedPathHandle> _handles;
    private bool _disposed;

    private IsolatedPreparationDatabaseLease(
        string guardedRoot,
        string? databasePath,
        List<GuardedPathHandle> handles)
    {
        GuardedRoot = guardedRoot;
        DatabasePath = databasePath;
        _handles = handles;
    }

    public string GuardedRoot { get; }

    public string? DatabasePath { get; }

    public static IsolatedPreparationDatabaseLease AcquireForAppData(
        string appRoot,
        string expectedRoot)
    {
        var normalizedAppRoot = NormalizeDirectoryPath(appRoot);
        var normalizedExpectedRoot = NormalizeDirectoryPath(expectedRoot);
        if (!string.Equals(
                normalizedAppRoot,
                normalizedExpectedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated AppData root does not match the expected seed root.");
        }

        AssertDVolume(normalizedAppRoot, "The isolated AppData root");
        AssertBelowVolumeRoot(
            normalizedAppRoot,
            "The isolated AppData root");
        AssertOutsideNormalApplicationData(normalizedAppRoot);

        var preparationRoot = GetDirectPreparationRoot(
            normalizedAppRoot,
            expectedChildName: "AppData");
        var dataDirectory = Path.Combine(normalizedAppRoot, "data");
        var databasePath = Path.Combine(
            dataDirectory,
            LocalDatabaseFileName);
        var markerPath = Path.Combine(
            normalizedAppRoot,
            IsolatedSeedMarkerFileName);

        var handles = new List<GuardedPathHandle>();
        try
        {
            handles.Add(OpenCanonicalDirectory(preparationRoot));
            handles.Add(AcquirePreparationLock(preparationRoot));
            handles.Add(OpenCanonicalDirectory(normalizedAppRoot));
            handles.Add(OpenCanonicalDirectory(dataDirectory));
            handles.Add(OpenCanonicalFile(
                markerPath,
                requireSingleLink: true));
            handles.Add(OpenCanonicalFile(
                databasePath,
                requireSingleLink: true));

            var markerRoot = NormalizeDirectoryPath(
                File.ReadAllText(markerPath).Trim());
            if (!string.Equals(
                    markerRoot,
                    normalizedAppRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The isolated seed marker does not match the guarded AppData root.");
            }

            AssertAllHandlesStable(handles);
            return new IsolatedPreparationDatabaseLease(
                normalizedAppRoot,
                databasePath,
                handles);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    public static IsolatedPreparationDatabaseLease AcquireForServerRoot(
        string serverRoot)
    {
        var normalizedServerRoot = NormalizeDirectoryPath(serverRoot);
        AssertDVolume(normalizedServerRoot, "The isolated server root");
        AssertBelowVolumeRoot(
            normalizedServerRoot,
            "The isolated server root");
        AssertOutsideNormalApplicationData(normalizedServerRoot);

        var preparationRoot = GetDirectPreparationRoot(
            normalizedServerRoot,
            expectedChildName: "Server");
        var handles = new List<GuardedPathHandle>();
        try
        {
            handles.Add(OpenCanonicalDirectory(preparationRoot));
            handles.Add(AcquirePreparationLock(preparationRoot));
            handles.Add(OpenCanonicalDirectory(normalizedServerRoot));
            AssertAllHandlesStable(handles);
            return new IsolatedPreparationDatabaseLease(
                normalizedServerRoot,
                databasePath: null,
                handles);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    public static IsolatedPreparationDatabaseLease AcquireReadOnlyDatabase(
        string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException(
                "A database path is required.",
                nameof(databasePath));
        }

        var normalizedDatabasePath = Path.GetFullPath(databasePath);
        AssertDVolume(normalizedDatabasePath, "The read-only database");
        AssertOutsideNormalApplicationData(normalizedDatabasePath);

        var databaseDirectory = Path.GetDirectoryName(normalizedDatabasePath);
        if (string.IsNullOrWhiteSpace(databaseDirectory))
        {
            throw new InvalidOperationException(
                "The read-only database must be below a directory.");
        }

        var normalizedDatabaseDirectory =
            NormalizeDirectoryPath(databaseDirectory);
        AssertBelowVolumeRoot(
            normalizedDatabaseDirectory,
            "The read-only database directory");

        var handles = new List<GuardedPathHandle>();
        try
        {
            foreach (var directory in EnumerateDirectoriesFromVolume(
                         normalizedDatabaseDirectory))
            {
                handles.Add(OpenCanonicalDirectory(directory));
            }

            handles.Add(OpenCanonicalFile(
                normalizedDatabasePath,
                requireSingleLink: true));
            AssertAllHandlesStable(handles);
            return new IsolatedPreparationDatabaseLease(
                normalizedDatabaseDirectory,
                normalizedDatabasePath,
                handles);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    public void AssertStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertAllHandlesStable(_handles);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeHandles(_handles);
    }

    private static GuardedPathHandle AcquirePreparationLock(
        string preparationRoot)
    {
        var lockPath = Path.Combine(
            preparationRoot,
            PreparationLockFileName);
        AssertDVolume(lockPath, "The preparation lock");

        var exclusiveProbe = NativeMethods.CreateFileW(
            lockPath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            shareMode: 0,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!exclusiveProbe.IsInvalid)
        {
            exclusiveProbe.Dispose();
            throw new InvalidOperationException(
                "The parent preparation lease is not held for the isolated runtime root.");
        }

        var exclusiveError = Marshal.GetLastWin32Error();
        exclusiveProbe.Dispose();
        if (exclusiveError != NativeMethods.ErrorSharingViolation)
        {
            throw new Win32Exception(
                exclusiveError,
                $"Could not verify the parent preparation lease: {lockPath}");
        }

        var childLease = GuardedPathHandle.Open(
            lockPath,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            NativeMethods.FileFlagOpenReparsePoint);
        try
        {
            childLease.AssertCanonicalFile(
                lockPath,
                requireSingleLink: true);

            var parentPresenceProbe = NativeMethods.CreateFileW(
                lockPath,
                NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!parentPresenceProbe.IsInvalid)
            {
                parentPresenceProbe.Dispose();
                throw new InvalidOperationException(
                    "The parent preparation lease was released before the child guard was acquired.");
            }

            var presenceError = Marshal.GetLastWin32Error();
            parentPresenceProbe.Dispose();
            if (presenceError != NativeMethods.ErrorSharingViolation)
            {
                throw new Win32Exception(
                    presenceError,
                    $"Could not confirm the parent preparation lease: {lockPath}");
            }

            return childLease;
        }
        catch
        {
            childLease.Dispose();
            throw;
        }
    }

    private static GuardedPathHandle OpenCanonicalDirectory(string path)
    {
        var handle = GuardedPathHandle.Open(
            path,
            NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            NativeMethods.FileFlagBackupSemantics |
            NativeMethods.FileFlagOpenReparsePoint);
        try
        {
            handle.AssertCanonicalDirectory(path);
            AssertDVolume(handle.FinalPath, "A guarded directory");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static GuardedPathHandle OpenCanonicalFile(
        string path,
        bool requireSingleLink)
    {
        var handle = GuardedPathHandle.Open(
            path,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            NativeMethods.FileFlagOpenReparsePoint);
        try
        {
            handle.AssertCanonicalFile(path, requireSingleLink);
            AssertDVolume(handle.FinalPath, "A guarded file");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string GetDirectPreparationRoot(
        string childRoot,
        string expectedChildName)
    {
        if (!string.Equals(
                Path.GetFileName(childRoot),
                expectedChildName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The isolated root must be the direct {expectedChildName} child of its preparation root.");
        }

        var parent = Directory.GetParent(childRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException(
                "The isolated root does not have a preparation root.");
        }

        var normalizedParent = NormalizeDirectoryPath(parent);
        AssertDVolume(normalizedParent, "The preparation root");
        AssertBelowVolumeRoot(normalizedParent, "The preparation root");
        return normalizedParent;
    }

    private static IEnumerable<string> EnumerateDirectoriesFromVolume(
        string leafDirectory)
    {
        var volumeRoot = NormalizeDirectoryPath(
            Path.GetPathRoot(leafDirectory)
            ?? throw new InvalidOperationException(
                "The guarded path must be absolute."));
        var directories = new Stack<string>();
        var current = new DirectoryInfo(leafDirectory);
        while (current is not null &&
               !string.Equals(
                   NormalizeDirectoryPath(current.FullName),
                   volumeRoot,
                   StringComparison.OrdinalIgnoreCase))
        {
            directories.Push(NormalizeDirectoryPath(current.FullName));
            current = current.Parent;
        }

        while (directories.Count > 0)
            yield return directories.Pop();
    }

    private static void AssertAllHandlesStable(
        IEnumerable<GuardedPathHandle> handles)
    {
        foreach (var handle in handles)
            handle.AssertStable();
    }

    private static void DisposeHandles(
        IEnumerable<GuardedPathHandle> handles)
    {
        foreach (var handle in handles.Reverse())
            handle.Dispose();
    }

    private static void AssertOutsideNormalApplicationData(string path)
    {
        var localAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The normal local application data root could not be resolved.");
        }

        var productionRoot = NormalizeDirectoryPath(
            Path.Combine(localAppData, "\uac70\ub798\ud50c\ub79c"));
        var normalizedPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        if (PathsOverlap(normalizedPath, productionRoot))
        {
            throw new InvalidOperationException(
                "The guarded database path cannot use or contain the normal V1 application data root.");
        }
    }

    private static void AssertDVolume(string path, string description)
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
        if (!string.Equals(
                volumeRoot,
                @"D:\",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{description} must be on the D: volume.");
        }
    }

    private static void AssertBelowVolumeRoot(
        string path,
        string description)
    {
        var normalizedPath = NormalizeDirectoryPath(path);
        var volumeRoot = NormalizeDirectoryPath(
            Path.GetPathRoot(normalizedPath)
            ?? throw new InvalidOperationException(
                $"{description} must be absolute."));
        if (string.Equals(
                normalizedPath,
                volumeRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{description} must be below the volume root.");
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        var leftPrefix = left + Path.DirectorySeparatorChar;
        var rightPrefix = right + Path.DirectorySeparatorChar;
        return left.StartsWith(
                   rightPrefix,
                   StringComparison.OrdinalIgnoreCase) ||
               right.StartsWith(
                   leftPrefix,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private readonly record struct NativeFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private sealed class GuardedPathHandle : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _openedPath;
        private bool _disposed;

        private GuardedPathHandle(
            SafeFileHandle handle,
            string openedPath,
            NativeMethods.ByHandleFileInformation information,
            string finalPath)
        {
            _handle = handle;
            _openedPath = openedPath;
            Identity = ToIdentity(information);
            Attributes = (FileAttributes)information.FileAttributes;
            NumberOfLinks = information.NumberOfLinks;
            FinalPath = finalPath;
        }

        public NativeFileIdentity Identity { get; }

        public FileAttributes Attributes { get; }

        public uint NumberOfLinks { get; }

        public string FinalPath { get; }

        public static GuardedPathHandle Open(
            string path,
            uint desiredAccess,
            uint shareMode,
            uint flagsAndAttributes)
        {
            var fullPath = Path.GetFullPath(path);
            var handle = NativeMethods.CreateFileW(
                fullPath,
                desiredAccess,
                shareMode,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                flagsAndAttributes,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(
                    error,
                    $"Could not acquire a guarded filesystem handle: {fullPath}. Win32Error={error}.");
            }

            try
            {
                var information = ReadInformation(handle, fullPath);
                var finalPath = ReadFinalPath(handle, fullPath);
                return new GuardedPathHandle(
                    handle,
                    fullPath,
                    information,
                    finalPath);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void AssertCanonicalDirectory(string expectedPath)
        {
            AssertStable();
            if ((Attributes & FileAttributes.Directory) == 0 ||
                (Attributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(
                    NormalizeDirectoryPath(FinalPath),
                    NormalizeDirectoryPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A guarded directory is a reparse point or resolves outside its canonical path: {expectedPath}");
            }
        }

        public void AssertCanonicalFile(
            string expectedPath,
            bool requireSingleLink)
        {
            AssertStable();
            if ((Attributes & FileAttributes.Directory) != 0 ||
                (Attributes & FileAttributes.ReparsePoint) != 0 ||
                (requireSingleLink && NumberOfLinks != 1) ||
                !string.Equals(
                    Path.GetFullPath(FinalPath),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A guarded file is a reparse point, hard link, or resolves outside its canonical path: {expectedPath}");
            }
        }

        public void AssertStable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var information = ReadInformation(_handle, _openedPath);
            var finalPath = ReadFinalPath(_handle, _openedPath);
            if (ToIdentity(information) != Identity ||
                (FileAttributes)information.FileAttributes != Attributes ||
                information.NumberOfLinks != NumberOfLinks ||
                !string.Equals(
                    finalPath,
                    FinalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A guarded filesystem identity changed: {_openedPath}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _handle.Dispose();
        }

        private static NativeMethods.ByHandleFileInformation ReadInformation(
            SafeFileHandle handle,
            string path)
        {
            if (!NativeMethods.GetFileInformationByHandle(
                    handle,
                    out var information))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not read guarded filesystem identity: {path}. Win32Error={error}.");
            }

            return information;
        }

        private static string ReadFinalPath(
            SafeFileHandle handle,
            string path)
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
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        error,
                        $"Could not resolve guarded filesystem path: {path}. Win32Error={error}.");
                }

                if (length < buffer.Capacity)
                    return NormalizeNativeFinalPath(buffer.ToString());

                capacity = checked((int)length + 1);
            }
        }

        private static NativeFileIdentity ToIdentity(
            NativeMethods.ByHandleFileInformation information)
            => new(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow);

        private static string NormalizeNativeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            var normalized = path.StartsWith(
                    uncPrefix,
                    StringComparison.OrdinalIgnoreCase)
                ? @"\\" + path[uncPrefix.Length..]
                : path.StartsWith(
                    devicePrefix,
                    StringComparison.OrdinalIgnoreCase)
                    ? path[devicePrefix.Length..]
                    : path;
            return Path.GetFullPath(normalized);
        }
    }

    private static class NativeMethods
    {
        public const int ErrorSharingViolation = 32;
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileReadAttributes = 0x00000080;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileFlagOpenReparsePoint = 0x00200000;
        public const uint FileFlagBackupSemantics = 0x02000000;

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
        public struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
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
    }
}
