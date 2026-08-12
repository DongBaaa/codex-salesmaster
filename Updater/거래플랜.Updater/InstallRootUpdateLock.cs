using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using 거래플랜.Shared.Contracts;

[assembly: InternalsVisibleTo("GeoraePlan.Desktop.App.Tests")]

namespace 거래플랜.Updater;

internal sealed class InstallRootUpdateLock : IDisposable
{
    private const string MutexNamePrefix = @"Global\GeoraePlan.Updater.InstallRoot.";
    private const string OperationLeaseMutexNamePrefix =
        @"Global\GeoraePlan.Updater.InstallRootLease.";
    private const string WorkerLeaseMutexNamePrefix =
        @"Global\GeoraePlan.Updater.InstallRootWorkerLease.";

    private readonly string[] _mutexNames;
    private readonly ManualResetEventSlim _acquisitionCompleted = new(initialState: false);
    private readonly ManualResetEventSlim _releaseRequested = new(initialState: false);
    private readonly Thread _ownerThread;
    private readonly TimeSpan _waitTimeout;
    private Exception? _acquisitionError;
    private bool _acquired;
    private int _disposed;

    private InstallRootUpdateLock(
        IEnumerable<string> mutexNames,
        TimeSpan waitTimeout)
    {
        _mutexNames = mutexNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (_mutexNames.Length == 0)
            throw new ArgumentException("설치 잠금 대상이 비어 있습니다.", nameof(mutexNames));

        _waitTimeout = waitTimeout;
        _ownerThread = new Thread(OwnMutex)
        {
            IsBackground = true,
            Name = "GeoraePlan updater install lock"
        };
    }

    public static InstallRootUpdateLock Acquire(string installRoot)
        => Acquire(installRoot, TimeSpan.Zero);

    internal static InstallRootUpdateLock AcquireForDesktopHandoff(
        string installRoot,
        TimeSpan waitTimeout)
        => Acquire(installRoot, waitTimeout);

    internal static InstallRootUpdateLock AcquireForDesktopHandoff(
        IEnumerable<string> installRoots,
        TimeSpan waitTimeout)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        if (waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        var mutexNames = installRoots
            .Select(CreateMutexName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var updateLock = new InstallRootUpdateLock(mutexNames, waitTimeout);
        return Acquire(updateLock);
    }

    internal static InstallRootUpdateLock AcquireOperationLeasesForDesktopHandoff(
        IEnumerable<string> installRoots,
        TimeSpan waitTimeout)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        if (waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        var mutexNames = installRoots
            .Select(CreateOperationLeaseMutexName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var updateLock = new InstallRootUpdateLock(mutexNames, waitTimeout);
        return Acquire(updateLock);
    }

    internal static InstallRootUpdateLock AcquireWorkerLeasesForDesktopHandoff(
        IEnumerable<string> installRoots,
        TimeSpan waitTimeout)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        if (waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        var mutexNames = installRoots
            .Select(CreateWorkerLeaseMutexName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var updateLock = new InstallRootUpdateLock(mutexNames, waitTimeout);
        return Acquire(updateLock);
    }

    private static InstallRootUpdateLock Acquire(string installRoot, TimeSpan waitTimeout)
    {
        if (waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        var updateLock = new InstallRootUpdateLock(
            [CreateMutexName(installRoot)],
            waitTimeout);
        return Acquire(updateLock);
    }

    private static InstallRootUpdateLock Acquire(
        InstallRootUpdateLock updateLock)
    {
        updateLock._ownerThread.Start();
        updateLock._acquisitionCompleted.Wait();

        if (updateLock._acquired)
            return updateLock;

        var acquisitionError = updateLock._acquisitionError;
        updateLock.Dispose();

        if (acquisitionError is not null)
        {
            throw new InvalidOperationException(
                "업데이트 설치 잠금을 만들지 못했습니다. 잠시 후 다시 시도하세요.",
                acquisitionError);
        }

        throw new InvalidOperationException(
            "동일한 설치 위치에 대한 다른 거래플랜 업데이트가 이미 진행 중입니다. 기존 업데이트가 끝난 뒤 다시 시도하세요.");
    }

    internal static string CreateMutexName(string installRoot)
        => CreateMutexName(installRoot, MutexNamePrefix);

    internal static string CreateOperationLeaseMutexName(string installRoot)
        => CreateMutexName(installRoot, OperationLeaseMutexNamePrefix);

    internal static string CreateWorkerLeaseMutexName(string installRoot)
        => CreateMutexName(installRoot, WorkerLeaseMutexNamePrefix);

    private static string CreateMutexName(
        string installRoot,
        string mutexNamePrefix)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentException("설치 경로가 비어 있습니다.", nameof(installRoot));

        var normalizedRoot = InstallRootPathIdentity.Resolve(installRoot)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var rootHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot));
        return mutexNamePrefix + Convert.ToHexString(rootHash);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _releaseRequested.Set();
        if (_ownerThread.IsAlive && Thread.CurrentThread != _ownerThread)
            _ownerThread.Join();

        _acquisitionCompleted.Dispose();
        _releaseRequested.Dispose();
    }

    private void OwnMutex()
    {
        var mutexes = new List<Mutex>(_mutexNames.Length);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            foreach (var mutexName in _mutexNames)
            {
                var remaining = _waitTimeout == TimeSpan.Zero
                    ? TimeSpan.Zero
                    : _waitTimeout - stopwatch.Elapsed;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;

                var mutex = new Mutex(initiallyOwned: false, mutexName);
                var ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = mutex.WaitOne(remaining);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        mutex.Dispose();
                        return;
                    }

                    mutexes.Add(mutex);
                }
                catch
                {
                    if (ownsMutex)
                        mutex.ReleaseMutex();
                    mutex.Dispose();
                    throw;
                }
            }

            _acquired = true;
            _acquisitionCompleted.Set();
            _releaseRequested.Wait();
        }
        catch (Exception ex)
        {
            _acquisitionError = ex;
        }
        finally
        {
            for (var index = mutexes.Count - 1; index >= 0; index--)
            {
                try
                {
                    mutexes[index].ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The owning thread is already leaving; disposal remains safe.
                }
                mutexes[index].Dispose();
            }

            _acquisitionCompleted.Set();
        }
    }
}
