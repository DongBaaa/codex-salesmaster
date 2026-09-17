using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerCategoryRefreshBindingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Reload_WithBoundCategorySelector_PreservesExistingFilterOrFallsBackWhenRemoved(
        bool bindBeforeLoad, bool removeCategory)
        => RunOnStaAsync(async () =>
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var category = new LocalCustomerCategory { Id = Guid.NewGuid(), Name = "필터 검증", IsDirty = false };
            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(), NameOriginal = "분류 대상 거래처", NameMatchKey = "CATEGORYCUSTOMER",
                CategoryId = category.Id, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                IsDirty = false
            };
            db.CustomerCategories.Add(category);
            db.Customers.AddRange(customer, new LocalCustomer
            {
                Id = Guid.NewGuid(), NameOriginal = "다른 거래처", NameMatchKey = "OTHERCUSTOMER",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, IsDirty = false
            });
            await db.SaveChangesAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "customer-filter-test", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            using var vm = new CustomerManagementViewModel(local, session);
            if (!bindBeforeLoad) await vm.InitializeAsync();
            var selector = new ComboBox { DataContext = vm };
            selector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.CategoryFilters)));
            selector.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(vm.SelectedCategoryFilter)));
            try
            {
                if (bindBeforeLoad) await vm.InitializeAsync();
                vm.SelectedCategoryFilter = category.Name;
                vm.SearchCommand.Execute(null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(category.Name, selector.SelectedItem);
                Assert.Equal(customer.Id, Assert.Single(vm.Customers).Id);
                if (removeCategory)
                    await db.CustomerCategories.Where(x => x.Id == category.Id).ExecuteDeleteAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");

                await vm.ReloadCommand.ExecuteAsync(null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                Assert.Equal(removeCategory ? "전체" : category.Name, vm.SelectedCategoryFilter);
                Assert.Equal(vm.SelectedCategoryFilter, selector.SelectedItem);
                Assert.Equal(OfficeCodeCatalog.Usenet, vm.SelectedOfficeFilter);
                Assert.Equal(removeCategory ? 2 : 1, vm.Customers.Count);
                if (!removeCategory) Assert.Equal(customer.Id, vm.SelectedCustomer?.Id);
                Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                Assert.False(await db.Customers.AnyAsync(x => x.IsDirty));
            }
            finally { BindingOperations.ClearAllBindings(selector); }
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
