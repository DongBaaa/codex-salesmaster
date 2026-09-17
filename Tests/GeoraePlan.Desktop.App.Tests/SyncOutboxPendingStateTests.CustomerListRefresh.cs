using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Fact]
    public Task CustomerListRefresh_CommittedScopeChangeRefreshesOpenListAndPreservesOtherDraft()
        => OnCustomerListSta(async () =>
        {
            PrepareAppRoot("customer-list-refresh");
            try
            {
                await using var db = new LocalDbContext();
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                var session = CreateCustomerScopeSession();
                var revokedId = Guid.NewGuid();
                var retainedId = Guid.NewGuid();
                db.Customers.AddRange(CustomerScopeFixture(revokedId, 5, false), CustomerScopeFixture(retainedId, 5, false));
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                var notifier = new DesktopDataChangeNotifier();
                await using var uiDb = new LocalDbContext();
                var local = new LocalStateService(uiDb, new OfficeAccessService(), new SyncRequestDispatcher(), session, notifier);
                using var vm = new CustomerManagementViewModel(local, session);
                await vm.InitializeAsync();
                vm.SelectedOfficeFilter = "전체";
                vm.SearchCommand.Execute(null);
                var draft = vm.Customers.Single(x => x.Id == retainedId);
                draft.ResponsibleOfficeCode = "USENET";
                Assert.True(draft.IsModified);
                vm.SelectedCustomer = draft;
                using var sync = CreateSyncService(db, session, handler: null, notifier: notifier);
                await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
                {
                    CurrentServerRevision = 6,
                    CustomerScopeSnapshot = CustomerScopeSnapshotFor(session, retainedId)
                }, 5);
                await WaitForCustomerListAsync(() => vm.Customers.All(x => x.Id != revokedId));
                Assert.DoesNotContain(vm.Customers, x => x.Id == revokedId);
                Assert.Same(draft, vm.Customers.Single());
                Assert.Same(draft, vm.SelectedCustomer);
                Assert.True(draft.IsModified);
                Assert.Equal("USENET", draft.ResponsibleOfficeCode);
                Assert.Equal("전체", vm.SelectedOfficeFilter);
                Assert.False(await uiDb.Customers.AnyAsync(x => x.IsDirty || x.IsDeleted));
                Assert.Empty(await uiDb.SyncOutboxEntries.ToListAsync());
                await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
                {
                    CurrentServerRevision = 6,
                    CustomerScopeSnapshot = CustomerScopeSnapshotFor(session, retainedId, revokedId)
                }, 6);
                await WaitForCustomerListAsync(() => vm.Customers.Count == 2);
                Assert.Equal(2, vm.Customers.Count);
                Assert.Same(draft, vm.Customers.Single(x => x.Id == retainedId));
            }
            finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
        });

    [Fact]
    public async Task CustomerListRefresh_NotificationIsPostCommitAndAbsentOnRollbackOrRepeatedScope()
    {
        PrepareAppRoot("customer-change-notification");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            var id = Guid.NewGuid();
            db.Customers.Add(CustomerScopeFixture(id, 5, false));
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var notifier = new DesktopDataChangeNotifier();
            var notifications = 0;
            var observedUncommitted = false;
            notifier.CustomerStateChanged += (_, _) =>
            {
                notifications++;
                observedUncommitted |= db.Database.CurrentTransaction is not null;
            };
            using var sync = CreateSyncService(db, session, handler: null, notifier: notifier);
            var pull = new SyncPullResponse { CurrentServerRevision = 6, CustomerScopeSnapshot = CustomerScopeSnapshotFor(session) };
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_cursor BEFORE INSERT ON Settings WHEN NEW.Key='LastSyncRevision' BEGIN SELECT RAISE(ABORT, 'test rollback'); END");
            await Assert.ThrowsAnyAsync<Exception>(() => InvokeApplyPullAndUpdateRevisionAsync(sync, pull, 5));
            Assert.Equal(0, notifications);
            Assert.False(await db.Settings.AnyAsync(x => x.Key.StartsWith("Sync.CustomerScopeExclusion.")));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_cursor");
            await InvokeApplyPullAndUpdateRevisionAsync(sync, pull, 5);
            Assert.Equal(1, notifications);
            Assert.False(observedUncommitted);
            await InvokeApplyPullAndUpdateRevisionAsync(sync, pull, 6);
            Assert.Equal(1, notifications);
            pull.CustomerScopeSnapshot.VisibleCustomerIds.Add(id);
            await InvokeApplyPullAndUpdateRevisionAsync(sync, pull, 6);
            Assert.Equal(2, notifications);
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("owner-change")]
    [InlineData("coalesced")]
    public Task CustomerListRefresh_QueuedNotificationRespectsLifecycleAndUpdatesCustomerAndContract(string mode)
        => OnCustomerListSta(async () =>
        {
            PrepareAppRoot("customer-list-lifecycle");
            try
            {
                await using var db = new LocalDbContext();
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                var session = CreateCustomerScopeSession();
                var id = Guid.NewGuid();
                db.Customers.Add(CustomerScopeFixture(id, 5, false));
                await db.SaveChangesAsync(); db.ChangeTracker.Clear();
                var notifier = new DesktopDataChangeNotifier();
                await using var uiDb = new LocalDbContext();
                var local = new LocalStateService(uiDb, new OfficeAccessService(), new SyncRequestDispatcher(), session, notifier);
                using var vm = new CustomerManagementViewModel(local, session);
                await vm.InitializeAsync();
                var original = vm.Customers.Single();
                using var sync = CreateSyncService(db, session, handler: null, notifier: notifier);
                await local.OwnerScopeDataGate.WaitAsync();
                try
                {
                    var updated = CustomerScopeFixture(id, 6, false);
                    updated.NameOriginal = "갱신된 거래처";
                    await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
                    {
                        CurrentServerRevision = 6,
                        Customers = [LocalMappings.ToDto(updated)],
                        CustomerContracts = [new CustomerContractDto { Id = Guid.NewGuid(), CustomerId = id, Revision = 6, Description = "동기화된 계약 초안" }]
                    }, 5);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if (mode == "disposed") vm.Dispose();
                    else if (mode == "owner-change") session.SetSession("next-user", CreateCustomerScopeSession().User!, DateTime.UtcNow.AddHours(1));
                    else
                    {
                        updated.Revision = 7; updated.NameOriginal = "마지막 변경";
                        await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
                        {
                            CurrentServerRevision = 7, Customers = [LocalMappings.ToDto(updated)]
                        }, 6);
                    }
                }
                finally { local.OwnerScopeDataGate.Release(); }
                if (mode == "disposed")
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Same(original, vm.Customers.Single());
                }
                else if (mode == "owner-change")
                {
                    await WaitForCustomerListAsync(() => vm.Customers.Count == 0);
                    Assert.Empty(vm.Customers);
                    Assert.Contains("다시 열어", vm.StatusMessage);
                }
                else
                {
                    await WaitForCustomerListAsync(() => vm.Customers.SingleOrDefault()?.NameOriginal == "마지막 변경" && !vm.IsBusy);
                    Assert.Equal("마지막 변경", vm.Customers.Single().NameOriginal);
                    Assert.Equal(1, vm.Customers.Single().ContractCount);
                }
                Assert.Empty(await uiDb.SyncOutboxEntries.ToListAsync());
                Assert.False(await uiDb.Customers.AnyAsync(x => x.IsDirty));
            }
            finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomerListRefresh_HttpPullOnlyNotifiesCurrentOwner(bool changeOwner)
    {
        PrepareAppRoot("customer-list-http-owner");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            db.Customers.Add(CustomerScopeFixture(Guid.NewGuid(), 5, false));
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var response = new SyncPullResponse { CurrentServerRevision = 5, CustomerScopeSnapshot = CustomerScopeSnapshotFor(session) };
            var handler = new CustomerScopePullHandler(response, () =>
            {
                if (changeOwner) session.SetSession("next-token", CreateCustomerScopeSession().User!, DateTime.UtcNow.AddHours(1));
            });
            var notifier = new DesktopDataChangeNotifier();
            var notifications = 0;
            notifier.CustomerStateChanged += (_, _) => notifications++;
            using var sync = CreateSyncService(db, session, handler, notifier);
            Assert.Equal(!changeOwner, await InvokePullNewCoreAsync(sync, false));
            Assert.Equal(changeOwner ? 0 : 1, notifications);
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task CustomerListRefresh_FullBusinessCacheReplacementNotifiesForRemovedRows()
    {
        PrepareAppRoot("customer-list-full-cache");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            db.Customers.Add(CustomerScopeFixture(Guid.NewGuid(), 5, false));
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var notifier = new DesktopDataChangeNotifier();
            var notifications = 0;
            notifier.CustomerStateChanged += (_, _) => notifications++;
            using var sync = CreateSyncService(db, session, new CacheReplacementDirtyHandler(() => Task.CompletedTask), notifier);
            Assert.True(await sync.ReplaceCurrentBusinessScopeCacheFromServerAsync());
            Assert.Equal(1, notifications);
            Assert.Empty(await db.Customers.AsNoTracking().ToListAsync());
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }

    private static async Task WaitForCustomerListAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(10);
        }
    }

    private static Task OnCustomerListSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
