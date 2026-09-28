using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SalesAmountAccessTests
{
    private static UserSessionDto User(params string[] permissions) => new()
    {
        UserId = Guid.NewGuid(), Username = "amount-editor", Role = DomainConstants.RoleUser,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions.ToList()
    };

    [Theory]
    [InlineData(VoucherType.Sales, AppPermissionNames.AmountViewSales, true)]
    [InlineData(VoucherType.Sales, AppPermissionNames.AmountViewPurchase, false)]
    [InlineData(VoucherType.Purchase, AppPermissionNames.AmountViewPurchase, true)]
    [InlineData(VoucherType.Purchase, AppPermissionNames.AmountViewSales, false)]
    [InlineData(VoucherType.Procurement, AppPermissionNames.AmountViewPurchase, true)]
    [InlineData(VoucherType.Expense, AppPermissionNames.AmountViewSales, false)]
    public void CachedAmountsFollowCurrentDocumentPermissionWithoutMutatingSource(VoucherType type, string permission, bool allowed)
    {
        var session = new SessionState();
        session.SetOfflineSession(User(permission));
        using var vm = new SalesViewModel(null!, null!, null!, session, type);
        var cached = new LocalInvoiceLine { ItemNameOriginal = "복사기", Quantity = 2, UnitPrice = 1100, LineAmount = 2200, Remark = "기존 비고" };
        vm.ImportPreviousInvoices([new LocalInvoice { Lines = [cached] }], true);
        vm.CustomerBalance = 77777;
        vm.CustomerAdvanceBalance = 88888;
        var row = Assert.Single(vm.Lines);
        Assert.Equal(allowed, vm.CanViewInvoiceAmounts);
        Assert.Equal(allowed, row.CanEditAmounts);
        Assert.Equal(allowed ? "2,200" : "비공개", row.LineAmountDisplay);
        Assert.Equal(allowed ? "77,777" : "비공개", vm.CustomerBalanceDisplay);
        Assert.Equal(allowed ? "88,888" : "비공개", vm.CustomerAdvanceBalanceDisplay);
        row.Quantity = 4;
        row.Remark = "수정 비고";
        var dto = LocalMappings.ToDto(row.ToLocal(Guid.NewGuid()));
        Assert.Equal(4m, dto.Quantity);
        Assert.Equal("수정 비고", dto.Remark);
        Assert.Equal(allowed ? 4400m : null, dto.LineAmount);
        Assert.Equal(1100m, cached.UnitPrice);
        Assert.Equal(2200m, cached.LineAmount);
        Assert.Equal(2m, cached.Quantity);
    }

    [Fact]
    public void RestrictedNewLineAcceptsQuantityAndMemoButRejectsMoneyEditorAndSendsNull()
    {
        var session = new SessionState();
        session.SetOfflineSession(User(AppPermissionNames.InvoiceEdit));
        using var vm = new SalesViewModel(null!, null!, null!, session);
        var item = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "품목", SalePrice = 1100, TrackingType = ItemTrackingTypes.NonStock };
        vm.SelectedInputItem = item;
        vm.InputQty = 3;
        vm.InputRemark = "비고 저장";
        Assert.Null(vm.EditableInputUnitPrice);
        Assert.Null(vm.EditableInputLineAmount);
        vm.EditableInputUnitPrice = 99999;
        Assert.NotEqual(99999m, vm.InputUnitPrice);
        vm.AddLineCommand.Execute(null);
        var row = Assert.Single(vm.Lines);
        Assert.Equal(item.Id, row.ItemId);
        Assert.Equal(3m, row.Quantity);
        Assert.Equal("비공개", row.UnitPriceDisplay);
        Assert.Null(LocalMappings.ToDto(row.ToLocal(Guid.NewGuid())).UnitPrice);
        Assert.Equal(1100m, item.SalePrice);
    }

    [Fact]
    public void PermissionRevocationRefreshesDisplaysAndPreservesUnsavedDraftWithoutCreatingDirtyChanges()
    {
        var session = new SessionState();
        var user = User(AppPermissionNames.AmountViewSales);
        session.SetSession("fixture", user);
        using var vm = new SalesViewModel(null!, null!, null!, session);
        vm.InputUnitPrice = 1100;
        vm.Lines.Add(new InvoiceLineEditModel { ItemName = "품목", Quantity = 2, UnitPrice = 1100 });
        vm.MarkCurrentStateAsPristine();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var restricted = User();
        restricted.UserId = user.UserId;
        session.RefreshSession("fixture-refreshed", restricted);
        Assert.Contains(nameof(vm.CanViewCatalogSalesAmounts), changed);
        Assert.Equal("비공개", vm.TotalAmountDisplay);
        Assert.Equal("비공개", vm.Lines[0].UnitPriceDisplay);
        Assert.Null(vm.EditableInputUnitPrice);
        Assert.False(vm.HasPendingChanges);
        Assert.Equal(1100m, vm.InputUnitPrice);
        Assert.Equal(1100m, vm.Lines[0].UnitPrice);
        session.RefreshSession("fixture-restored", user);
        Assert.True(vm.Lines[0].CanEditAmounts);
        Assert.Equal(2200m, vm.TotalAmount);
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public async Task DifferentAccountCannotDiscloseOrAutoSavePreviousEditorsDraft()
    {
        var session = new SessionState();
        session.SetOfflineSession(User(AppPermissionNames.AmountViewSales));
        using var vm = new SalesViewModel(null!, null!, null!, session);
        vm.Lines.Add(new InvoiceLineEditModel { ItemName = "전 계정 품목", UnitPrice = 1000 });
        session.SetOfflineSession(User(AppPermissionNames.AmountViewSales));
        Assert.False(vm.IsEditorOwnerCurrent);
        Assert.False(vm.CanViewCatalogSalesAmounts);
        Assert.Equal("비공개", vm.Lines[0].UnitPriceDisplay);
        Assert.False(await vm.TryAutoSaveOnCloseAsync());
        Assert.Contains("계정", vm.StatusMessage);
    }

    [Fact]
    public void QuantityEditedWhileHiddenCannotRevealOrSubmitStalePriceAfterPermissionReturns()
    {
        var session = new SessionState();
        var allowed = User(AppPermissionNames.AmountViewSales);
        session.SetSession("fixture", allowed);
        using var vm = new SalesViewModel(null!, null!, null!, session);
        vm.Lines.Add(new InvoiceLineEditModel { ItemName = "품목", Quantity = 2, UnitPrice = 1100 });
        var restricted = User();
        restricted.UserId = allowed.UserId;
        session.RefreshSession("restricted", restricted);
        vm.Lines[0].Quantity = 4;
        session.RefreshSession("restored", allowed);
        Assert.True(vm.HasPendingChanges);
        Assert.Equal("비공개", vm.Lines[0].LineAmountDisplay);
        Assert.Null(LocalMappings.ToDto(vm.Lines[0].ToLocal(Guid.NewGuid())).LineAmount);
    }

    [Fact]
    public void WpfBindingsRemoveVisibleAmountsWhenPermissionIsRevoked()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var session = new SessionState();
                var user = User(AppPermissionNames.AmountViewSales);
                session.SetSession("fixture", user);
                using var vm = new SalesViewModel(null!, null!, null!, session);
                vm.InputUnitPrice = 1100;
                vm.CustomerBalance = 55000;
                vm.Lines.Add(new InvoiceLineEditModel { ItemName = "품목", Quantity = 2, UnitPrice = 1100 });
                var total = new TextBlock { DataContext = vm };
                var balance = new TextBlock { DataContext = vm };
                var input = new TextBox { DataContext = vm };
                var rowPrice = new TextBlock { DataContext = vm.Lines[0] };
                BindingOperations.SetBinding(total, TextBlock.TextProperty, new Binding(nameof(vm.TotalAmountDisplay)));
                BindingOperations.SetBinding(balance, TextBlock.TextProperty, new Binding(nameof(vm.CustomerBalanceDisplay)));
                BindingOperations.SetBinding(input, TextBox.TextProperty, new Binding(nameof(vm.EditableInputUnitPrice)) { TargetNullValue = "", Mode = BindingMode.OneWay });
                BindingOperations.SetBinding(input, UIElement.IsEnabledProperty, new Binding(nameof(vm.CanEditInputAmounts)));
                BindingOperations.SetBinding(rowPrice, TextBlock.TextProperty, new Binding(nameof(InvoiceLineEditModel.UnitPriceDisplay)));
                Assert.Equal("2,200", total.Text);
                Assert.Equal("1100", input.Text);
                Assert.True(input.IsEnabled);
                var restricted = User();
                restricted.UserId = user.UserId;
                session.RefreshSession("restricted", restricted);
                Assert.Equal("비공개", total.Text);
                Assert.Equal("비공개", balance.Text);
                Assert.Equal("비공개", rowPrice.Text);
                Assert.Equal("", input.Text);
                Assert.False(input.IsEnabled);
                vm.InputQty = 4;
                vm.InputRemark = "입력 유지";
                Assert.Equal(4m, vm.InputQty);
                Assert.Equal("입력 유지", vm.InputRemark);
                foreach (var target in new DependencyObject[] { total, balance, input, rowPrice }) BindingOperations.ClearAllBindings(target);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF binding verification timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void SessionAccessNotificationRunsOutsideMutationLease()
    {
        var session = new SessionState();
        var notifications = 0;
        session.AccessChanged += (_, _) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var lease = session.AcquireSyncScopeCommitLeaseAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            notifications++;
        };
        session.SetOfflineSession(User());
        session.SetOfficeCode(OfficeCodeCatalog.Yeonsu);
        session.Clear();
        Assert.Equal(3, notifications);
    }
}
