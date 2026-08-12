using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncServiceStopAndDrainTests
{
    [Fact]
    public async Task StopAndDrainAsync_IsIdempotentAndAwaitsEveryObservedTask()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        using var httpClient = new HttpClient(new NoOpHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var sync = CreateSyncService(db, httpClient);
        var firstWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveForTesting(sync, firstWork.Task);
        ObserveForTesting(sync, secondWork.Task);

        var firstDrain = sync.StopAndDrainAsync();
        var secondDrain = sync.StopAndDrainAsync();

        Assert.Same(firstDrain, secondDrain);
        Assert.False(firstDrain.IsCompleted);
        Assert.False(await sync.TrySyncAsync());

        firstWork.SetResult();
        await Task.Yield();
        Assert.False(firstDrain.IsCompleted);

        secondWork.SetResult();
        await firstDrain.WaitAsync(TimeSpan.FromSeconds(5));
        sync.Dispose();
    }

    [Fact]
    public async Task StopAndDrainAsync_ObservesThrowingCancellationCallback_AndStillCompletesDrain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        using var httpClient = new HttpClient(new NoOpHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var sync = CreateSyncService(db, httpClient);
        var work = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveForTesting(sync, work.Task);

        using var registration = ReadRuntimeStopToken(sync).Register(
            static () => throw new InvalidOperationException(
                "synthetic cancellation callback failure"));

        var drain = sync.StopAndDrainAsync();

        Assert.Same(drain, sync.StopAndDrainAsync());
        Assert.False(drain.IsCompleted);
        Assert.False(await sync.TrySyncAsync());

        work.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(drain.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Dispose_ReturnsWithoutBlocking_AndReleasesOnlyAfterDrain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        using var httpClient = new HttpClient(new NoOpHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var sync = CreateSyncService(db, httpClient);
        var work = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveForTesting(sync, work.Task);

        var stopwatch = Stopwatch.StartNew();
        sync.Dispose();
        stopwatch.Stop();

        var drain = sync.StopAndDrainAsync();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(drain.IsCompleted);
        Assert.False(ReadDisposed(sync));

        work.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ReadDisposed(sync));
    }

    [Fact]
    public void Dispose_SourceDoesNotSynchronouslyWait()
    {
        var source = File.ReadAllText(FindSyncServiceSource());
        var disposeStart = source.IndexOf(
            "public void Dispose()",
            StringComparison.Ordinal);
        var releaseStart = source.IndexOf(
            "private void ReleaseResourcesAfterDrain()",
            disposeStart,
            StringComparison.Ordinal);
        Assert.True(disposeStart >= 0 && releaseStart > disposeStart);
        var disposeSource = source[disposeStart..releaseStart];

        Assert.Contains("StopAndDrainAsync()", disposeSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", disposeSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", disposeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter", disposeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetResult", disposeSource, StringComparison.Ordinal);
        Assert.Contains(
            "CancelForStop(_runtimeStopCts, stopErrors)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TaskCreationOptions.RunContinuationsAsynchronously",
            source,
            StringComparison.Ordinal);
        Assert.Contains("await timer.DisposeAsync()", source, StringComparison.Ordinal);
        Assert.Contains("_observedBackgroundTasks", source, StringComparison.Ordinal);
    }

    private static SyncService CreateSyncService(
        LocalDbContext db,
        HttpClient httpClient)
    {
        var session = new SessionState();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(
            db,
            new OfficeAccessService(),
            dispatcher,
            session);
        return new SyncService(
            db,
            local,
            new RentalStateService(db),
            new ErpApiClient(httpClient, session),
            session,
            dispatcher,
            new SyncDiagnosticsService(session));
    }

    private static LocalDbContext CreateDbContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options);

    private static void ObserveForTesting(SyncService sync, Task task)
    {
        var method = typeof(SyncService).GetMethod(
            "ObserveBackgroundTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(sync, [task, "shutdown drain test"]);
    }

    private static bool ReadDisposed(SyncService sync)
    {
        var field = typeof(SyncService).GetField(
            "_disposed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<bool>(field.GetValue(sync));
    }

    private static CancellationToken ReadRuntimeStopToken(SyncService sync)
    {
        var field = typeof(SyncService).GetField(
            "_runtimeStopCts",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<CancellationTokenSource>(field.GetValue(sync)).Token;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeoutAt)
            await Task.Delay(10);

        Assert.True(predicate());
    }

    private static string FindSyncServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Desktop",
                "거래플랜.Desktop.App",
                "Services",
                "SyncService.cs");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("SyncService.cs was not found.");
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.OK));
    }
}
