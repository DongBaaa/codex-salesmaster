using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace 거래플랜.Server.Api.Services;

internal sealed class StoredFileDeletionLease : IDisposable
{
    internal const string LockFileName = ".georaeplan-backup-delete.lock";
    internal const string ProtocolVersion = "shared-flock-v1";

    private const int LockShared = 1;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private const int LinuxWouldBlock = 11;
    private const int MacOsWouldBlock = 35;

    private readonly FileStream? _stream;
    private bool _locked;

    private StoredFileDeletionLease(FileStream? stream, bool locked)
    {
        _stream = stream;
        _locked = locked;
    }

    public static StoredFileDeletionLease? TryAcquireShared(string? storageRoot)
    {
        if (!OperatingSystem.IsLinux())
            return new StoredFileDeletionLease(stream: null, locked: false);

        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new InvalidOperationException("The central file storage root is unavailable.");

        var rootPath = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(rootPath);
        var lockPath = Path.Combine(rootPath, LockFileName);
        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException ex) when (IsWouldBlock(ex.HResult))
        {
            // On Linux, FileStream can surface the competing flock as an open
            // failure before the explicit P/Invoke below. Treat it as the same
            // expected backup contention rather than an operational error.
            return null;
        }

        var fileDescriptor = GetFileDescriptor(stream.SafeFileHandle);
        if (Flock(fileDescriptor, LockShared | LockNonBlocking) == 0)
            return new StoredFileDeletionLease(stream, locked: true);

        var error = Marshal.GetLastPInvokeError();
        stream.Dispose();
        if (error is LinuxWouldBlock or MacOsWouldBlock)
            return null;

        throw new IOException(
            $"Unable to acquire the stored-file deletion lease. errno={error}");
    }

    public void Dispose()
    {
        if (_stream is null)
            return;

        try
        {
            if (_locked)
            {
                var fileDescriptor = GetFileDescriptor(_stream.SafeFileHandle);
                _ = Flock(fileDescriptor, LockUnlock);
                _locked = false;
            }
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private static int GetFileDescriptor(SafeFileHandle safeFileHandle)
        => checked((int)safeFileHandle.DangerousGetHandle().ToInt64());

    private static bool IsWouldBlock(int errorCode)
        => errorCode is LinuxWouldBlock or MacOsWouldBlock ||
           (errorCode & 0xffff) is LinuxWouldBlock or MacOsWouldBlock;

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}
