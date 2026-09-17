using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryCategoryRefreshBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EmptyInventory_DefaultsDoNotCreateChanges_AndDraftSurvivesReload(bool hasDraft)
        => RunOnStaAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"georaeplan-empty-inventory-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption { Id = Guid.NewGuid(), Name = "수입/지출", IsActive = true });
            await db.SaveChangesAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "empty-inventory-admin", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.Itworld, OfficeCode = OfficeCodeCatalog.Itworld,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vm = new InventoryViewModel(local, session);
            try
            {
                var selector = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Name", DataContext = vm };
                selector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.ItemCategoryOptions)));
                selector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                    new Binding(nameof(vm.EditCategoryName)) { Mode = BindingMode.TwoWay });
                await vm.LoadAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Empty(vm.FilteredItems);
                Assert.Equal("수입/지출", vm.EditCategoryName);
                Assert.False(vm.HasPendingChanges);
                if (hasDraft) vm.EditSimpleMemo = "작성 중인 메모";
                await vm.LoadAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(hasDraft, vm.HasPendingChanges);
                Assert.Equal(hasDraft ? "작성 중인 메모" : string.Empty, vm.EditSimpleMemo);
                if (!hasDraft) Assert.True(await vm.TryAutoSaveOnCloseAsync());
                Assert.Empty(await db.Items.ToListAsync());
                Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                BindingOperations.ClearAllBindings(selector);
            }
            finally { await vm.CancelPendingBackgroundWorkAsync(); }
        });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public Task ReloadCategories_WithBoundSelector_PreservesCategoryAndEditState(bool hasDraft, bool deactivateCategory, bool bindBeforeLoad)
        => RunOnStaAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"georaeplan-category-binding-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            const string category = "렌탈료";
            var item = new LocalItem
            {
                Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, NameOriginal = "분류 조회 검증",
                NameMatchKey = "분류조회검증", CategoryName = category,
                TrackingType = ItemTrackingTypes.NonStock, ItemKind = ItemKinds.Product,
                Revision = 100, IsDirty = false
            };
            db.Items.Add(item);
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(), Name = category, IsActive = true, IsDirty = false
            });
            await db.SaveChangesAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "category-binding-admin", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vm = new InventoryViewModel(local, session);
            try
            {
                if (!bindBeforeLoad) await vm.LoadAndSelectItemAsync(item.Id);
                var selector = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Name", DataContext = vm };
                selector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.ItemCategoryOptions)));
                selector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                    new Binding(nameof(vm.EditCategoryName)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                if (bindBeforeLoad) await vm.LoadAndSelectItemAsync(item.Id);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(category, selector.SelectedValue);
                Assert.False(vm.HasPendingChanges);
                if (hasDraft) vm.EditSimpleMemo = "저장 전 메모";
                var outboxBefore = await db.SyncOutboxEntries.CountAsync();
                if (deactivateCategory)
                {
                    var storedCategory = await db.ItemCategoryOptions.SingleAsync();
                    storedCategory.IsActive = false;
                    await db.SaveChangesAsync();
                }

                await vm.ReloadItemCategoryOptionsAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                Assert.Equal(category, vm.EditCategoryName);
                if (deactivateCategory)
                {
                    Assert.Empty(vm.ItemCategoryOptions);
                    Assert.Null(selector.SelectedItem);
                }
                Assert.Equal(category, selector.SelectedValue);
                Assert.Equal(hasDraft, vm.HasPendingChanges);
                Assert.Equal(hasDraft ? "저장 전 메모" : string.Empty, vm.EditSimpleMemo);
                Assert.Equal(outboxBefore, await db.SyncOutboxEntries.CountAsync());
                if (!hasDraft)
                {
                    Assert.True(await vm.TryAutoSaveOnCloseAsync());
                    db.ChangeTracker.Clear();
                    var stored = await db.Items.AsNoTracking().SingleAsync(x => x.Id == item.Id);
                    Assert.Equal(category, stored.CategoryName);
                    Assert.False(stored.IsDirty);
                    Assert.Equal(outboxBefore, await db.SyncOutboxEntries.CountAsync());
                    Assert.Empty(await db.ItemPriceGrades.ToListAsync());
                }
                BindingOperations.ClearAllBindings(selector);
            }
            finally
            {
                await vm.CancelPendingBackgroundWorkAsync();
            }
        });

    private static Task RunOnStaAsync(Func<Task> action)
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
