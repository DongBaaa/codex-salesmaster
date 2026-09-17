using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class NavigationScopeReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassiveSyncMetadataRead_WaitsForWorkerBillingQuery(bool readRevision)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var queryGate = new PausedQuery("RentalBillingProfiles");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).AddInterceptors(queryGate).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        await local.SetSettingAsync("LastSyncRevision", "731");
        using var pipeline = new SelectionPipelineCoordinator(local.OwnerScopeDataGate);
        queryGate.Enabled = true;
        var child = pipeline.RunExclusiveAsync(ct => Task.Run(
            () => db.RentalBillingProfiles.AsNoTracking().ToListAsync(ct), ct), CancellationToken.None);
        await queryGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        async Task<string> ReadMetadata(CancellationToken ct) => readRevision
            ? (await local.GetSettingAsync("LastSyncRevision", ct))!
            : (await local.HasPendingSyncChangesAsync(session, ct)).ToString();
        Task<string>? metadata = null;
        try
        {
            // The actual passive-sync reads must not overlap the worker query.
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReadMetadata(CancellationToken.None));
            metadata = MainWindow.ReadOwnerScopeNavigationStateAsync(local, ReadMetadata, CancellationToken.None);
            Assert.False(metadata.IsCompleted);
        }
        finally
        {
            queryGate.Release.TrySetResult();
            await child.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(readRevision ? "731" : "False", await metadata!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, local.OwnerScopeDataGate.CurrentCount);
    }

    [Fact]
    public async Task NavigationRead_WaitsForChildQuery_OnTheSameDbContext()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var queryGate = new PausedQuery();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).AddInterceptors(queryGate).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
        using var pipeline = new SelectionPipelineCoordinator(local.OwnerScopeDataGate);
        queryGate.Enabled = true;
        var child = pipeline.RunExclusiveAsync(async ct =>
        {
            await db.Invoices.AsNoTracking().CountAsync(ct);
        }, CancellationToken.None);
        await queryGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<int>? navigation = null;
        try
        {
            // The previous unguarded navigation query reproduces the EF failure.
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Customers.CountAsync());
            navigation = MainWindow.ReadOwnerScopeNavigationStateAsync(
                local, ct => db.Customers.CountAsync(ct), CancellationToken.None);
            Assert.False(navigation.IsCompleted);
        }
        finally
        {
            queryGate.Release.TrySetResult();
            await child.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.NotNull(navigation);
        Assert.Equal(0, await navigation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, local.OwnerScopeDataGate.CurrentCount);
    }

    [Fact]
    public async Task NavigationRead_CanceledWaitDoesNotReleaseAnotherOperationsGate()
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
        await local.OwnerScopeDataGate.WaitAsync();
        using var cts = new CancellationTokenSource();
        var entered = false;
        var waiting = MainWindow.ReadOwnerScopeNavigationStateAsync(local, _ =>
        {
            entered = true;
            return Task.FromResult(1);
        }, cts.Token);
        cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(entered);
            Assert.Equal(0, local.OwnerScopeDataGate.CurrentCount);
        }
        finally { local.OwnerScopeDataGate.Release(); }
    }

    [Fact]
    public async Task NavigationRead_FailedReadReleasesGateForNextOperation()
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MainWindow.ReadOwnerScopeNavigationStateAsync<int>(local,
                _ => throw new InvalidOperationException("read failed"), CancellationToken.None));
        Assert.Equal(7, await MainWindow.ReadOwnerScopeNavigationStateAsync(
            local, _ => Task.FromResult(7), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class PausedQuery : DbCommandInterceptor
    {
        private readonly string _table;
        internal PausedQuery(string table = "Invoices") => _table = table;
        internal bool Enabled;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains($"FROM \"{_table}\"", StringComparison.Ordinal))
            {
                Enabled = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
