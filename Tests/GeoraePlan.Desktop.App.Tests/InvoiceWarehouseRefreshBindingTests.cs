using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceWarehouseRefreshBindingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var type in new[] { VoucherType.Purchase, VoucherType.Procurement, VoucherType.Sales })
        foreach (var office in new[] { "USENET", "YEONSU", "ITWORLD" })
        foreach (var bindBeforeLoad in new[] { true, false })
            yield return new object[] { type, office, bindBeforeLoad };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task NewInvoice_WithBoundWarehouse_KeepsVisibleSelectionAndOfficeScope(
        VoucherType type, string office, bool bindBeforeLoad)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var session = new SessionState();
                session.SetOfflineSession(new UserSessionDto
                {
                    Username = "warehouse-binding-test", Role = DomainConstants.RoleAdmin,
                    OfficeCode = office,
                    TenantCode = office == "ITWORLD" ? "ITWORLD" : TenantScopeCatalog.UsenetGroup,
                    ScopeType = TenantScopeCatalog.ScopeOfficeOnly
                });
                using var vm = new SalesViewModel(null!, null!, null!, session, type);
                vm.Offices.Add(new LocalOffice { Code = office, Name = office });
                var warehouses = (List<LocalWarehouse>)typeof(SalesViewModel)
                    .GetField("_allWritableWarehouses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
                foreach (var candidate in new[] { "USENET", "YEONSU", "ITWORLD" })
                    warehouses.Add(new LocalWarehouse
                    {
                        Code = OfficeCodeCatalog.GetMainWarehouseCode(candidate), OfficeCode = candidate,
                        Name = candidate + " 창고", IsActive = true
                    });
                if (!bindBeforeLoad) vm.NewInvoice();
                var selector = new ComboBox { DataContext = vm, DisplayMemberPath = "Name", SelectedValuePath = "Code" };
                selector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Warehouses)));
                selector.SetBinding(Selector.SelectedValueProperty,
                    new Binding(nameof(vm.SelectedWarehouseCode)) { Mode = BindingMode.TwoWay });
                try
                {
                    Pump();
                    for (var reset = 0; reset < 2; reset++)
                    {
                        vm.NewInvoice();
                        Pump();
                        var selected = Assert.IsType<LocalWarehouse>(selector.SelectedItem);
                        Assert.Equal(OfficeCodeCatalog.GetMainWarehouseCode(office), selected.Code);
                        Assert.Equal(office, selected.OfficeCode);
                        Assert.Equal(selected.Code, vm.SelectedWarehouseCode);
                        Assert.Equal(selected.Code, selector.SelectedValue);
                        Assert.Single(selector.Items.Cast<object>());
                        Assert.False(vm.HasPendingChanges);
                    }
                }
                finally { BindingOperations.ClearAllBindings(selector); }
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
