using System.Data.Common;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Fact]
    public Task CustomerListLifecycle_InitializationFailureDetachesSharedNotifier()
        => OnCustomerListSta(async () =>
        {
            PrepareAppRoot("customer-list-init-failure");
            try
            {
                await using var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
                await db.Database.EnsureCreatedAsync();
                var notifier = new DesktopDataChangeNotifier();
                var session = CreateCustomerScopeSession();
                var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session, notifier);
                using var vm = new CustomerManagementViewModel(local, session);
                await db.Database.ExecuteSqlRawAsync("DROP TABLE Offices");
                await Assert.ThrowsAsync<SqliteException>(() => vm.InitializeAsync());
                var handlers = (Delegate?)typeof(DesktopDataChangeNotifier)
                    .GetField(nameof(DesktopDataChangeNotifier.CustomerStateChanged), BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(notifier);
                Assert.DoesNotContain(handlers?.GetInvocationList() ?? [], handler => ReferenceEquals(handler.Target, vm));
                Assert.False(vm.IsBusy);
                local.TryPublishCustomerStateChanged();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Empty(vm.Customers);
            }
            finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
        });

    [Fact]
    public Task CustomerListLifecycle_OwnerChangeClearsContractSummaryAndNotifiesBindings()
        => OnCustomerListSta(async () =>
        {
            PrepareAppRoot("customer-list-summary-owner");
            try
            {
                await using var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
                await db.Database.EnsureCreatedAsync();
                var id = Guid.NewGuid();
                db.Customers.Add(CustomerScopeFixture(id, 5, false));
                foreach (var expiry in new[] { DateOnly.FromDateTime(DateTime.Today).AddDays(-1), DateOnly.FromDateTime(DateTime.Today).AddDays(1) })
                    db.CustomerContracts.Add(new LocalCustomerContract { Id = Guid.NewGuid(), CustomerId = id, FileName = "fixture.pdf", FileSize = 3, FileContent = [1, 2, 3], ExpireDate = expiry, Revision = 5, IsDirty = false });
                await db.SaveChangesAsync(); db.ChangeTracker.Clear();
                var notifier = new DesktopDataChangeNotifier();
                var session = CreateCustomerScopeSession();
                var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session, notifier);
                using var vm = new CustomerManagementViewModel(local, session);
                await vm.InitializeAsync();
                Assert.Equal(1, vm.CustomersWithContractsCount);
                Assert.Equal(1, vm.ExpiredContractCount);
                Assert.Equal(1, vm.ExpiringSoonContractCount);
                Assert.True(vm.HasContractAlerts);
                var oldSummary = vm.ContractAlertSummary;
                var bindingNotifications = new List<string?>();
                vm.PropertyChanged += (_, args) => bindingNotifications.Add(args.PropertyName);
                session.SetSession("next-user", CreateCustomerScopeSession().User!, DateTime.UtcNow.AddHours(1));
                local.TryPublishCustomerStateChanged();
                await WaitForCustomerListAsync(() => vm.Customers.Count == 0);
                Assert.Empty(vm.Customers);
                Assert.Empty(vm.ContractAlerts);
                Assert.False(vm.HasContractAlerts);
                Assert.Equal(0, vm.CustomersWithContractsCount);
                Assert.Equal(0, vm.ExpiredContractCount);
                Assert.Equal(0, vm.ExpiringSoonContractCount);
                Assert.NotEqual(oldSummary, vm.ContractAlertSummary);
                Assert.Contains(nameof(vm.HasContractAlerts), bindingNotifications);
                Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                Assert.Equal(2, await db.CustomerContracts.CountAsync());
            }
            finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CustomerListLifecycle_MainAndManagerQueriesSerializeOnSharedContext(bool mainFirst)
        => OnCustomerListSta(async () =>
        {
            PrepareAppRoot("customer-list-main-query-overlap");
            var queryGate = new CustomerListReadGate();
            try
            {
                await using var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).AddInterceptors(queryGate).Options);
                await db.Database.EnsureCreatedAsync();
                db.Customers.Add(CustomerScopeFixture(Guid.NewGuid(), 5, false));
                await db.SaveChangesAsync(); db.ChangeTracker.Clear();
                var session = CreateCustomerScopeSession();
                var dispatcher = new SyncRequestDispatcher();
                var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
                var rental = new RentalStateService(db, local);
                var diagnostics = new SyncDiagnosticsService(session);
                using var http = new HttpClient(new CustomerScopePullHandler(new SyncPullResponse(), () => { })) { BaseAddress = new Uri("http://fixture.invalid/") };
                var api = new ErpApiClient(http, session);
                using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
                var main = new MainViewModel(local, sync, new BackupService(), rental, diagnostics, api, session);
                using var manager = new CustomerManagementViewModel(local, session);
                await manager.InitializeAsync();
                var mainReload = typeof(MainViewModel).GetMethod("ReloadCustomerAndInvoiceDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Task ReloadMain() => (Task)mainReload.Invoke(main, [CancellationToken.None])!;
                Task? first = null, second = null;
                try
                {
                    queryGate.Arm();
                    first = mainFirst ? ReloadMain() : manager.ReloadCommand.ExecuteAsync(null);
                    await queryGate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    second = mainFirst ? manager.ReloadCommand.ExecuteAsync(null) : ReloadMain();
                    local.TryPublishCustomerStateChanged();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(60);
                    Assert.False(second.IsCompleted);
                    queryGate.Release();
                    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
                    await manager.ReloadCommand.ExecuteAsync(null);
                    await WaitForCustomerListAsync(() => !manager.IsBusy);
                    Assert.Single(manager.Customers);
                    Assert.False(await db.Customers.AnyAsync(x => x.IsDirty));
                    Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                }
                finally
                {
                    queryGate.Release();
                    foreach (var pending in new[] { first, second })
                        if (pending is not null) try { await pending; } catch { }
                    await main.DrainPendingBackgroundWorkForShutdownAsync();
                }
            }
            finally { queryGate.Release(); Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
        });

    private sealed class CustomerListReadGate : DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _released.TrySetResult();
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Customers\"", StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Blocked.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
