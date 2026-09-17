using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalSettingRetryPersistenceTests
{
    [Theory]
    [InlineData(false, false, 5)]
    [InlineData(false, true, 5)]
    [InlineData(true, false, 5)]
    [InlineData(true, true, 5)]
    [InlineData(false, true, 6)]
    [InlineData(true, true, 6)]
    public async Task TransientLock_PersistsValueInIndependentConnection(bool wrapped, bool existing, int code)
    {
        await using var fixture = await Fixture.CreateAsync(existing);
        var fault = new SaveFault(wrapped, code, failures: 1);
        await using var db = fixture.Open(fault);
        await Local(db).SetSettingAsync("Target", "new");
        Assert.Equal(2, fault.Attempts);
        Assert.Equal("new", await fixture.ReadAsync());
        Assert.Equal(1, await db.Settings.CountAsync(row => row.Key == "Target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentLock_ThrowsAndLeavesOriginalValue(bool wrapped)
    {
        await using var fixture = await Fixture.CreateAsync(existing: true);
        var fault = new SaveFault(wrapped, 5, failures: int.MaxValue);
        await using var db = fixture.Open(fault);
        await Assert.ThrowsAnyAsync<Exception>(() => Local(db).SetSettingAsync("Target", "new"));
        Assert.Equal(4, fault.Attempts);
        Assert.Equal("old", await fixture.ReadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonLockFailure_IsNotRetried(bool wrapped)
    {
        await using var fixture = await Fixture.CreateAsync(existing: true);
        var fault = new SaveFault(wrapped, 19, failures: int.MaxValue);
        await using var db = fixture.Open(fault);
        await Assert.ThrowsAnyAsync<Exception>(() => Local(db).SetSettingAsync("Target", "new"));
        Assert.Equal(1, fault.Attempts);
        Assert.Equal("old", await fixture.ReadAsync());
    }

    [Fact]
    public async Task CancellationDuringRetry_DoesNotReportSuccess()
    {
        await using var fixture = await Fixture.CreateAsync(existing: true);
        using var cancellation = new CancellationTokenSource();
        var fault = new SaveFault(false, 5, 1, cancellation.Cancel);
        await using var db = fixture.Open(fault);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Local(db).SetSettingAsync("Target", "new", cancellation.Token));
        Assert.Equal(1, fault.Attempts);
        Assert.Equal("old", await fixture.ReadAsync());
    }

    [Fact]
    public async Task UnchangedPersistedValue_DoesNotSaveUnrelatedPendingSetting()
    {
        await using var fixture = await Fixture.CreateAsync(existing: true);
        var fault = new SaveFault(false, 5, int.MaxValue);
        await using var db = fixture.Open(fault);
        db.Settings.Add(new LocalSetting { Key = "Unrelated", Value = "pending" });
        await Local(db).SetSettingAsync("Target", "old");
        Assert.Equal(0, fault.Attempts);
        await using var verifier = fixture.Open();
        Assert.False(await verifier.Settings.AnyAsync(row => row.Key == "Unrelated"));
    }

    [Fact]
    public async Task ActualTransactionStartLock_RecoversAfterWriterReleases()
    {
        await using var fixture = await Fixture.CreateAsync(existing: true);
        await using var blocker = new SqliteConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction();
        await using var db = fixture.Open();
        db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        var write = Task.Run(() => Local(db).SetSettingAsync("Target", "new"));
        await Task.Delay(1400);
        await transaction.CommitAsync();
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new", await fixture.ReadAsync());
    }

    private static LocalStateService Local(LocalDbContext db)
        => new(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());

    private sealed class SaveFault(bool wrapped, int code, int failures, Action? onFailure = null) : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= failures)
            {
                onFailure?.Invoke();
                var error = new SqliteException("Isolated setting write fault", code);
                if (wrapped) throw new DbUpdateException("Isolated save failure", error);
                throw error;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture(string root) : IAsyncDisposable
    {
        public string ConnectionString => $"Data Source={Path.Combine(root, "test.db")};Default Timeout=1;Pooling=False";
        public LocalDbContext Open(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new LocalDbContext(options.Options);
        }
        public static async Task<Fixture> CreateAsync(bool existing)
        {
            var root = Path.Combine(Path.GetTempPath(), "trade-setting-persistence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root);
            await using var db = fixture.Open();
            await db.Database.EnsureCreatedAsync();
            if (existing)
            {
                db.Settings.Add(new LocalSetting { Key = "Target", Value = "old" });
                await db.SaveChangesAsync();
            }
            return fixture;
        }
        public async Task<string?> ReadAsync()
        {
            await using var db = Open();
            return await db.Settings.AsNoTracking().Where(row => row.Key == "Target").Select(row => row.Value).SingleOrDefaultAsync();
        }
        public ValueTask DisposeAsync()
        {
            Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
