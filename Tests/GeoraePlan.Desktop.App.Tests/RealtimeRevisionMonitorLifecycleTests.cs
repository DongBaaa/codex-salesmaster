using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RealtimeRevisionMonitorLifecycleTests
{
    [Fact]
    public async Task IsolatedRevisionLookup_CompletesWhileMainScopeDbContextIsOccupied()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-realtime-revision-{Guid.NewGuid():N}.db");
        var blocker = new MainScopeCommandBlocker();
        var services = new ServiceCollection();
        services.AddSingleton(blocker);
        services.AddSingleton<OfficeAccessService>();
        services.AddSingleton<SyncRequestDispatcher>();
        services.AddSingleton<SessionState>();
        services.AddSingleton<DesktopDataChangeNotifier>();
        services.AddDbContext<LocalDbContext>((provider, options) =>
            options
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .AddInterceptors(provider.GetRequiredService<MainScopeCommandBlocker>()));
        services.AddScoped<LocalStateService>();

        try
        {
            await using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

            await using (var initializationScope = provider.CreateAsyncScope())
            {
                var db = initializationScope.ServiceProvider.GetRequiredService<LocalDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "314"
                });
                await db.SaveChangesAsync();
            }

            await using var mainScope = provider.CreateAsyncScope();
            var mainDb = mainScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            var mainLocal = mainScope.ServiceProvider.GetRequiredService<LocalStateService>();
            Assert.Same(mainDb, GetLocalDbContext(mainLocal));
            blocker.Block(mainDb);

            var mainRead = mainLocal.GetSettingAsync("LastSyncRevision");
            await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            LocalStateService? isolatedLocal = null;
            LocalDbContext? isolatedDb = null;
            try
            {
                var isolatedValue = await MainWindow.RunIsolatedLocalStateOperationAsync(
                        provider.GetRequiredService<IServiceScopeFactory>(),
                        async local =>
                        {
                            isolatedLocal = local;
                            isolatedDb = GetLocalDbContext(local);
                            return await local.GetSettingAsync("LastSyncRevision");
                        })
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal("314", isolatedValue);
                Assert.NotNull(isolatedLocal);
                Assert.NotSame(mainLocal, isolatedLocal);
                Assert.NotNull(isolatedDb);
                Assert.NotSame(mainDb, isolatedDb);
                Assert.False(mainRead.IsCompleted);
            }
            finally
            {
                blocker.Release();
            }

            Assert.Equal("314", await mainRead.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }
    }

    [Fact]
    public async Task CancelledIsolatedRevisionLookup_StopsPromptlyAndDisposesScope()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-realtime-revision-cancel-{Guid.NewGuid():N}.db");
        var blocker = new MainScopeCommandBlocker();
        var services = new ServiceCollection();
        services.AddSingleton(blocker);
        services.AddSingleton<OfficeAccessService>();
        services.AddSingleton<SyncRequestDispatcher>();
        services.AddSingleton<SessionState>();
        services.AddSingleton<DesktopDataChangeNotifier>();
        services.AddDbContext<LocalDbContext>((provider, options) =>
            options
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .AddInterceptors(provider.GetRequiredService<MainScopeCommandBlocker>()));
        services.AddScoped<LocalStateService>();

        try
        {
            await using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

            await using (var initializationScope = provider.CreateAsyncScope())
            {
                var db = initializationScope.ServiceProvider.GetRequiredService<LocalDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "2718"
                });
                await db.SaveChangesAsync();
            }

            blocker.BlockNextSettingsRead();
            using var cancellation = new CancellationTokenSource();
            var lookup = MainWindow.ResolveLocalLastSyncRevisionAsync(
                provider.GetRequiredService<IServiceScopeFactory>(),
                cancellation.Token);
            await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lookup.IsCompleted);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await lookup.WaitAsync(TimeSpan.FromSeconds(5)));

            var isolatedDb = Assert.IsType<LocalDbContext>(blocker.BlockedContext);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await isolatedDb.Settings.CountAsync());
        }
        finally
        {
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }
    }

    [Fact]
    public async Task RapidStopAndRestart_KeepTokensOwnedAndDisposeAfterEachTaskCompletes()
    {
        var oldCts = new CancellationTokenSource();
        var oldEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;

        var oldTask = MainWindow.StartRealtimeRevisionMonitorTask(
            async token =>
            {
                oldToken = token;
                oldEntered.TrySetResult();
                await releaseOld.Task;
                token.ThrowIfCancellationRequested();
            },
            oldCts);
        await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        oldCts.Cancel();
        var oldDrain = MainWindow.ObserveAndDisposeRealtimeRevisionMonitorAsync(
            oldTask,
            oldCts);
        Assert.False(oldDrain.IsCompleted);
        Assert.True(oldCts.Token.IsCancellationRequested);

        var newCts = new CancellationTokenSource();
        var newEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNew = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken newToken = default;
        var newTask = MainWindow.StartRealtimeRevisionMonitorTask(
            async token =>
            {
                newToken = token;
                newEntered.TrySetResult();
                await releaseNew.Task;
                token.ThrowIfCancellationRequested();
            },
            newCts);
        await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(oldToken, newToken);
        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(newToken.IsCancellationRequested);

        releaseOld.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await oldDrain);
        Assert.Throws<ObjectDisposedException>(() => _ = oldCts.Token);

        newCts.Cancel();
        var newDrain = MainWindow.ObserveAndDisposeRealtimeRevisionMonitorAsync(
            newTask,
            newCts);
        releaseNew.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await newDrain);
        Assert.Throws<ObjectDisposedException>(() => _ = newCts.Token);
    }

    private static LocalDbContext GetLocalDbContext(LocalStateService local)
    {
        var field = typeof(LocalStateService).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<LocalDbContext>(field.GetValue(local));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a unique test-only temporary database.
        }
    }

    private sealed class MainScopeCommandBlocker : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid? _blockedContextId;
        private bool _blockAnyContext;
        private int _blocked;

        public Task Entered => _entered.Task;
        public DbContext? BlockedContext { get; private set; }

        public void Block(LocalDbContext db)
            => _blockedContextId = db.ContextId.InstanceId;

        public void BlockNextSettingsRead()
            => _blockAnyContext = true;

        public void Release()
            => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if ((_blockAnyContext ||
                 _blockedContextId == eventData.Context?.ContextId.InstanceId) &&
                command.CommandText.Contains("Settings", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                BlockedContext = eventData.Context;
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
