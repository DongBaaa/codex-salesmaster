using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SalesCatalogAmountBindingTests
{
    public static IEnumerable<object[]> HiddenCases =>
        new[] { "SalePrice", "RetailPrice", "PriceGradeA", "PriceGradeB", "PriceGradeC" }
            .SelectMany(field => new[] { new object[] { field, 0 }, new object[] { field, 98765 } });

    [Theory]
    [MemberData(nameof(HiddenCases))]
    public Task ActualCatalogCell_HidesUnknownPriceAndRebindsKnownZero(string field, int storedValue)
        => OnSta(async () =>
        {
            var (session, user) = Session();
            using var vm = new SalesViewModel(null!, null!, null!, session, VoucherType.Sales);
            var hidden = Item(true, storedValue);
            var cell = ReadCell(field);
            var window = new Window { DataContext = vm, Content = cell };
            try
            {
                cell.DataContext = hidden;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("비공개", cell.Text);
                Assert.Equal((decimal)storedValue, typeof(LocalItem).GetProperty(field)!.GetValue(hidden));
                Assert.False(hidden.IsDirty);

                // Catalog refresh replaces detached LocalItem rows; recycled cells must
                // close disclosure again without turning a known zero into unknown.
                cell.DataContext = Item(false, 0);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("0", cell.Text);
                cell.DataContext = Item(false, 12345);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(12345m.ToString("N0"), cell.Text);
                cell.DataContext = hidden;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("비공개", cell.Text);
            }
            finally { window.Content = null; window.Close(); }
        });

    [Fact]
    public Task ActualCatalogCells_TrackPermissionRevocationAndEditorSessionOwnership()
        => OnSta(async () =>
        {
            var (session, user) = Session();
            using var vm = new SalesViewModel(null!, null!, null!, session, VoucherType.Purchase);
            var panel = new StackPanel();
            var cells = HiddenCases.Select(c => (string)c[0]).Distinct().Select(ReadCell).ToArray();
            var known = Item(false, 12345);
            foreach (var cell in cells) { cell.DataContext = known; panel.Children.Add(cell); }
            var window = new Window { DataContext = vm, Content = panel };
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.All(cells, cell => Assert.Equal(12345m.ToString("N0"), cell.Text));
                user.Permissions = [AppPermissionNames.InvoiceEdit, AppPermissionNames.AmountViewPurchase];
                session.RefreshSession("restricted", user);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.All(cells, cell => Assert.Equal("비공개", cell.Text));
                user.Permissions.Add(AppPermissionNames.AmountViewSales);
                session.RefreshSession("restored", user);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.All(cells, cell => Assert.Equal(12345m.ToString("N0"), cell.Text));
                session.SetOfflineSession(user);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.All(cells, cell => Assert.Equal("비공개", cell.Text));
                Assert.Equal(12345m, known.SalePrice);
                Assert.False(known.IsDirty);
            }
            finally { window.Content = null; window.Close(); }
        });

    private static (SessionState Session, UserSessionDto User) Session()
    {
        var user = new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "catalog-binding", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.InvoiceEdit, AppPermissionNames.AmountViewSales, AppPermissionNames.AmountViewPurchase]
        };
        var session = new SessionState(); session.SetOfflineSession(user);
        return (session, user);
    }

    private static LocalItem Item(bool hidden, decimal amount) => new()
    {
        Id = Guid.NewGuid(), SalesAmountsHidden = hidden, PurchaseAmountsHidden = true,
        SalePrice = amount, RetailPrice = amount, PriceGradeA = amount, PriceGradeB = amount, PriceGradeC = amount,
        CurrentStock = 3, SimpleMemo = "preserve", IsDirty = false
    };

    private static TextBlock ReadCell(string field)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Desktop"))) root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "Desktop", "거래플랜.Desktop.App", "Views", "SalesWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var setter = document.Descendants(ns + "Setter").Single(e => (string?)e.Attribute("Value") == "{Binding " + field + ", StringFormat=N0}");
        var text = new XElement(setter.Ancestors(ns + "TextBlock").Single());
        // Isolate the real binding and trigger tree from unrelated application visuals.
        text.Descendants(ns + "Style").Single().Attribute("BasedOn")!.Remove();
        return (TextBlock)XamlReader.Parse(text.ToString());
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
