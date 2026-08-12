using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace \uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string AppRootOverrideEnvironmentKey = "GEORAEPLAN_APP_ROOT";
    private Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static bool TryAcquireForCurrentAppRoot(out SingleInstanceGuard? guard)
        => TryAcquire(ResolveCurrentAppRoot(), out guard);

    public static bool TryAcquireForCurrentAppRoot(
        out SingleInstanceGuard? guard,
        out string appRootIdentity)
        => TryAcquire(ResolveCurrentAppRoot(), out guard, out appRootIdentity);

    public static bool TryAcquire(string appRoot, out SingleInstanceGuard? guard)
        => TryAcquire(appRoot, out guard, out _);

    public static bool TryAcquire(
        string appRoot,
        out SingleInstanceGuard? guard,
        out string appRootIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);

        var rootHash = BuildAppRootHash(appRoot);
        appRootIdentity = $"sha256:{rootHash}";
        var mutex = new Mutex(
            initiallyOwned: true,
            BuildMutexNameFromHash(rootHash),
            out var createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public static string BuildMutexName(string appRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);

        return BuildMutexNameFromHash(BuildAppRootHash(appRoot));
    }

    public static string BuildAppRootIdentity(string appRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);

        return $"sha256:{BuildAppRootHash(appRoot)}";
    }

    private static string BuildAppRootHash(string appRoot)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appRoot))
            .ToUpperInvariant();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)));
    }

    private static string BuildMutexNameFromHash(string rootHash)
    {
        // Global prevents the same Windows user from opening one local database
        // concurrently through separate console/RDP logon sessions.
        return $@"Global\GeoraePlan.Desktop.{rootHash[..32]}";
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
            return;

        if (_ownsMutex)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already terminating or ownership was lost.
            }
            finally
            {
                _ownsMutex = false;
            }
        }

        mutex.Dispose();
    }

    private static string ResolveCurrentAppRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable(AppRootOverrideEnvironmentKey);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "\uac70\ub798\ud50c\ub79c");
    }
}
