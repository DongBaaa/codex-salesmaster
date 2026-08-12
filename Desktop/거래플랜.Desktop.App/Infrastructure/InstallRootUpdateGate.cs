using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using 거래플랜.Shared.Contracts;

namespace \uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure;

internal sealed class InstallRootUpdateGate : IDisposable
{
    private const string MutexNamePrefix = @"Global\GeoraePlan.Updater.InstallRoot.";
    private const string OperationLeaseMutexNamePrefix =
        @"Global\GeoraePlan.Updater.InstallRootLease.";
    private const string WorkerLeaseMutexNamePrefix =
        @"Global\GeoraePlan.Updater.InstallRootWorkerLease.";

    private Mutex[]? _ownedMutexes;

    private InstallRootUpdateGate(Mutex[] ownedMutexes)
    {
        _ownedMutexes = ownedMutexes;
    }

    internal static bool TryAcquire(string installRoot, out InstallRootUpdateGate? gate)
        => TryAcquireMany([installRoot], out gate);

    internal static bool TryAcquire(
        string installRoot,
        out InstallRootUpdateGate? gate,
        IEnumerable<string> additionalInstallRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(additionalInstallRoots);

        return TryAcquireMany(
            additionalInstallRoots.Prepend(installRoot),
            out gate);
    }

    internal static bool TryAcquireMany(
        IEnumerable<string> installRoots,
        out InstallRootUpdateGate? gate)
    {
        ArgumentNullException.ThrowIfNull(installRoots);

        var orderedMutexNames = GetOrderedGateMutexNames(installRoots);
        if (orderedMutexNames.Count == 0)
            throw new ArgumentException("At least one install root is required.", nameof(installRoots));

        var acquired = new List<Mutex>(orderedMutexNames.Count);
        try
        {
            foreach (var mutexName in orderedMutexNames)
            {
                var mutex = new Mutex(
                    initiallyOwned: true,
                    mutexName,
                    out var createdNew);

                if (!createdNew)
                {
                    mutex.Dispose();
                    ReleaseOwnedMutexes(acquired);
                    gate = null;
                    return false;
                }

                acquired.Add(mutex);
            }

            gate = new InstallRootUpdateGate(acquired.ToArray());
            return true;
        }
        catch
        {
            ReleaseOwnedMutexes(acquired);
            throw;
        }
    }

    internal static string BuildMutexName(string installRoot)
        => MutexNamePrefix + BuildInstallRootHash(installRoot);

    internal static string BuildOperationLeaseMutexName(string installRoot)
        => OperationLeaseMutexNamePrefix + BuildInstallRootHash(installRoot);

    internal static string BuildWorkerLeaseMutexName(string installRoot)
        => WorkerLeaseMutexNamePrefix + BuildInstallRootHash(installRoot);

    private static string BuildInstallRootHash(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var normalizedRoot = InstallRootPathIdentity.Resolve(installRoot)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var rootHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot));
        return Convert.ToHexString(rootHash);
    }

    internal static IReadOnlyList<string> GetOrderedMutexNames(
        IEnumerable<string> installRoots)
    {
        ArgumentNullException.ThrowIfNull(installRoots);

        return installRoots
            .Select(root =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(root);
                return BuildMutexName(root);
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> GetOrderedOperationLeaseMutexNames(
        IEnumerable<string> installRoots)
    {
        ArgumentNullException.ThrowIfNull(installRoots);

        return installRoots
            .Select(root =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(root);
                return BuildOperationLeaseMutexName(root);
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> GetOrderedGateMutexNames(
        IEnumerable<string> installRoots)
    {
        ArgumentNullException.ThrowIfNull(installRoots);

        var roots = installRoots.ToArray();
        return GetOrderedMutexNames(roots)
            .Concat(GetOrderedOperationLeaseMutexNames(roots))
            .Concat(GetOrderedWorkerLeaseMutexNames(roots))
            .ToArray();
    }

    internal static IReadOnlyList<string> GetOrderedWorkerLeaseMutexNames(
        IEnumerable<string> installRoots)
    {
        ArgumentNullException.ThrowIfNull(installRoots);

        return installRoots
            .Select(root =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(root);
                return BuildWorkerLeaseMutexName(root);
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        var ownedMutexes = Interlocked.Exchange(ref _ownedMutexes, null);
        if (ownedMutexes is null)
            return;

        ReleaseOwnedMutexes(ownedMutexes);
    }

    private static void ReleaseOwnedMutexes(IReadOnlyList<Mutex> mutexes)
    {
        for (var index = mutexes.Count - 1; index >= 0; index--)
        {
            var mutex = mutexes[index];
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Process shutdown can race with release.
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}
