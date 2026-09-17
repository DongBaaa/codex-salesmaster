using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerOfficeRefreshBindingTests
{
    [Theory]
    [InlineData("전체", 3)]
    [InlineData("USENET", 1)]
    [InlineData("ITWORLD", 1)]
    [InlineData("YEONSU", 1)]
    public Task Reload_WithLoadedOfficeCells_PreservesFilterAndDoesNotEditRows(string officeFilter, int expectedRows)
        => OnSta(async () =>
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            foreach (var office in new[] { "USENET", "ITWORLD", "YEONSU" })
                db.Customers.Add(new LocalCustomer
                {
                    Id = Guid.NewGuid(), NameOriginal = "담당지점 검증 " + office, NameMatchKey = office,
                    TenantCode = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
                    OfficeCode = office == "YEONSU" ? "USENET" : office, ResponsibleOfficeCode = office, IsDirty = false
                });
            await db.SaveChangesAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "office-binding-test", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = "USENET", ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            using var vm = new CustomerManagementViewModel(local, session);
            await vm.InitializeAsync();
            vm.SelectedOfficeFilter = officeFilter;
            vm.SearchCommand.Execute(null);
            var oldRows = vm.Customers.ToArray();
            var originalOffices = oldRows.ToDictionary(row => row.Id, row => row.ResponsibleOfficeCode);
            var grid = new DataGrid { DataContext = vm, AutoGenerateColumns = false, CanUserAddRows = false };
            grid.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Customers)));
            grid.Columns.Add(new DataGridTemplateColumn { CellTemplate = ReadActualOfficeTemplate(), Width = 180 });
            var filter = new ComboBox { DataContext = vm };
            filter.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.OfficeFilters)));
            filter.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(vm.SelectedOfficeFilter)));
            var panel = new StackPanel();
            panel.Children.Add(filter);
            panel.Children.Add(grid);
            var window = new Window { Content = panel, Width = 360, Height = 250, ShowActivated = false, ShowInTaskbar = false, Title = "담당지점 조회 회귀 검증" };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(expectedRows, Descendants<ComboBox>(grid).Count(c => c.IsLoaded && c.DataContext is EnvironmentCustomerRow));
                await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
                await vm.ReloadCommand.ExecuteAsync(null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                var manuallyReloadedRows = vm.Customers.ToArray();
                await Task.Run(() => local.TryPublishCustomerStateChanged());
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (ReferenceEquals(manuallyReloadedRows[0], vm.Customers.FirstOrDefault()) && DateTime.UtcNow < deadline)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(10);
                }
                Assert.NotSame(manuallyReloadedRows[0], vm.Customers.FirstOrDefault());
                Assert.Equal(officeFilter, vm.SelectedOfficeFilter);
                Assert.Equal(officeFilter, filter.SelectedItem);
                Assert.Equal(expectedRows, vm.Customers.Count);
                Assert.All(oldRows, row =>
                {
                    Assert.Equal(originalOffices[row.Id], row.ResponsibleOfficeCode);
                    Assert.False(row.IsModified);
                });
                Assert.False(await db.Customers.AnyAsync(row => row.IsDirty));
                Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
            }
            finally { window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        });

    private static DataTemplate ReadActualOfficeTemplate()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Desktop"))) current = current.Parent;
        Assert.NotNull(current);
        var document = XDocument.Load(Path.Combine(current.FullName, "Desktop", "거래플랜.Desktop.App", "Views", "CustomerManagementWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var combo = document.Descendants(xaml + "ComboBox").Single(c => ((string?)c.Attribute("SelectedItem"))?.Contains("ResponsibleOfficeCode") == true);
        var template = new XElement(combo.Parent!);
        // This test exercises the production bindings and loaded-row lifecycle without invoking writes.
        template.Descendants(xaml + "ComboBox").Single().Attribute("SelectionChanged")!.Remove();
        return (DataTemplate)XamlReader.Parse(template.ToString());
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static Task OnSta(Func<Task> action)
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
