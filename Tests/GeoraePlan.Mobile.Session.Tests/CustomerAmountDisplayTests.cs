using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Mobile.Session.Tests;

public sealed class CustomerAmountDisplayTests
{
    public CustomerAmountDisplayTests() { Preferences.Default.Reset(); SecureStorage.Default.Reset(); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedCustomerCards_HideCachedAndServerMoneyWithoutMutatingSource(bool offline)
    {
        var session = new SessionStore(); await session.SaveAsync(Login(false));
        var (customer, state, api) = Fixture(); api.Offline = offline; state.Normalize();
        var before = JsonSerializer.Serialize(state); var apiBefore = JsonSerializer.Serialize(api.Detail);
        var vm = new CustomersViewModel(api, new(session), new(state), session);
        await vm.SelectCustomerAsync(customer);
        Assert.Null(Assert.Single(vm.SelectedCustomerInvoices).TotalAmount);
        Assert.Null(Assert.Single(vm.SelectedCustomerPayments).Amount);
        Assert.Equal(3, vm.SelectedCustomerRentals.Count);
        Assert.All(vm.SelectedCustomerRentals, row => Assert.Contains("비공개", row.Meta));
        Assert.Equal(before, JsonSerializer.Serialize(state));
        Assert.Equal(apiBefore, JsonSerializer.Serialize(api.Detail));
    }

    [Fact]
    public void RentalRows_MarkNullAmountsAsUnknown()
    {
        Assert.Contains("비공개", CustomerRentalLinkRow.FromProfile(new() { MonthlyAmount = null }).Meta);
        Assert.Contains("비공개", CustomerRentalLinkRow.FromAsset(new() { MonthlyFee = null }, null).Meta);
        Assert.Contains("비공개", CustomerRentalLinkRow.FromAssignmentHistory(new() { MonthlyFee = null }, null, null).Meta);
    }

    public static IEnumerable<object[]> PermissionCases()
    {
        foreach (var type in Enum.GetValues<VoucherType>().Append((VoucherType)999))
        foreach (var grants in Enumerable.Range(0, 4))
        foreach (var directPayment in new[] { false, true })
            yield return [type, grants, directPayment];
    }

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public async Task FinancialRows_UseTheirOwnVoucherDirection(VoucherType type, int grants, bool directPayment)
    {
        var session = new SessionStore(); var login = Login(false);
        login.User.Permissions = [(grants & 1) != 0 ? "Amount.ViewSales" : "", (grants & 2) != 0 ? "Amount.ViewPurchase" : ""];
        await session.SaveAsync(login);
        var (customer, state, api) = Fixture(); var invoice = state.SyncedInvoices[0]; invoice.VoucherType = type;
        if (directPayment)
            api.Detail.RecentPayments = [new() { PaymentId = Guid.NewGuid(), InvoiceId = invoice.Id, VoucherType = type, Amount = 98765, Note = "독립 수금" }];
        var expected = type switch
        {
            VoucherType.Sales or VoucherType.Collection => (grants & 1) != 0,
            VoucherType.Purchase or VoucherType.Procurement or VoucherType.Expense => (grants & 2) != 0,
            _ => false
        };
        var vm = new CustomersViewModel(api, new(session), new(state), session);
        await vm.SelectCustomerAsync(customer);
        var shown = Assert.Single(vm.SelectedCustomerInvoices);
        Assert.Equal(expected ? 123456m : (decimal?)null, shown.TotalAmount);
        Assert.Equal(expected ? 123456m : (decimal?)null, Assert.Single(shown.Lines).UnitPrice);
        Assert.Equal(expected ? 98765m : (decimal?)null, Assert.Single(shown.Payments).Amount);
        Assert.Equal(expected ? 98765m : (decimal?)null, Assert.Single(vm.SelectedCustomerPayments).Amount);
        Assert.Equal(1, shown.Lines[0].Quantity); Assert.Equal("보존", shown.Lines[0].Remark);
        Assert.Equal(123456m, invoice.TotalAmount); Assert.Equal(123456m, invoice.Lines[0].UnitPrice);
        Assert.All(vm.SelectedCustomerRentals, row => Assert.Equal((grants & 1) == 0, row.Meta.Contains("비공개")));
    }

    [Fact]
    public async Task SameGenerationRevocationAndLogout_ClearAllVisibleFinancialRows()
    {
        var session = new SessionStore(); await session.SaveAsync(Login(true));
        var (customer, state, api) = Fixture(); var vm = new CustomersViewModel(api, new(session), new(state), session);
        await vm.SelectCustomerAsync(customer); Assert.Equal(123456m, vm.SelectedCustomerInvoices[0].TotalAmount);
        var owner = session.CaptureOwner(); session.SessionChanged += (_, _) => vm.RefreshAccess();
        Assert.True(await session.ReplaceIfCurrentAsync(owner, Login(false), preserveGeneration: true));
        Assert.True(session.IsOwnerCurrent(owner)); Assert.Null(vm.SelectedCustomer);
        Assert.Empty(vm.SelectedCustomerInvoices); Assert.Empty(vm.SelectedCustomerPayments); Assert.Empty(vm.SelectedCustomerRentals);
        Assert.True(vm.NeedsRefresh(TimeSpan.FromDays(1)));
        await vm.SelectCustomerAsync(customer); Assert.Null(vm.SelectedCustomerInvoices[0].TotalAmount);
        await session.ClearAsync(); Assert.Null(vm.SelectedCustomer); Assert.Empty(vm.SelectedCustomerRentals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionChangeDuringDetailRead_InvalidatesPendingPublication(bool eventSubscribed)
    {
        var session = new SessionStore(); await session.SaveAsync(Login(true));
        var (customer, state, api) = Fixture(); var coordinator = new SyncCoordinator(state);
        var vm = new CustomersViewModel(api, new(session), coordinator, session);
        if (eventSubscribed) session.SessionChanged += (_, _) => vm.RefreshAccess();
        coordinator.BeforeLoad = async () =>
            await session.ReplaceIfCurrentAsync(session.CaptureOwner(), Login(false), preserveGeneration: true);
        await Assert.ThrowsAsync<StaleCacheOwnerSessionException>(() => vm.SelectCustomerAsync(customer));
        Assert.Empty(vm.SelectedCustomerInvoices); Assert.Empty(vm.SelectedCustomerRentals); Assert.False(vm.IsDetailBusy);
    }

    [Fact]
    public async Task ScopeReduction_DropsOtherOfficeWithoutModifyingData()
    {
        var session = new SessionStore(); var login = Login(true); login.User.ScopeType = TenantScopeCatalog.ScopeTenantAll;
        await session.SaveAsync(login);
        var (customer, state, api) = Fixture(); customer.ResponsibleOfficeCode = customer.OfficeCode = "YEONSU";
        var vm = new CustomersViewModel(api, new(session), new(state), session);
        await vm.SelectCustomerAsync(customer); Assert.NotNull(vm.SelectedCustomer);
        session.SessionChanged += (_, _) => vm.RefreshAccess();
        Assert.True(await session.ReplaceIfCurrentAsync(session.CaptureOwner(), Login(true), preserveGeneration: true));
        Assert.Null(vm.SelectedCustomer);
        await vm.SelectCustomerAsync(customer); Assert.Null(vm.SelectedCustomer);
        Assert.Equal("YEONSU", customer.ResponsibleOfficeCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenAndKnownZeroRemainDistinctEvenWithGrantedSalesAccess(bool hidden)
    {
        var session = new SessionStore(); await session.SaveAsync(Login(true));
        var (customer, state, api) = Fixture();
        var invoice = state.SyncedInvoices[0]; invoice.TotalAmount = hidden ? null : 0;
        invoice.Payments[0].Amount = hidden ? null : 0;
        state.SyncedRentalBillingProfiles[0].MonthlyAmount = hidden ? null : 0;
        state.SyncedRentalAssets[0].MonthlyFee = hidden ? null : 0;
        state.SyncedRentalAssetAssignmentHistories[0].MonthlyFee = hidden ? null : 0;
        var vm = new CustomersViewModel(api, new(session), new(state), session);
        await vm.SelectCustomerAsync(customer);
        Assert.Equal(hidden ? (decimal?)null : 0m, Assert.Single(vm.SelectedCustomerInvoices).TotalAmount);
        Assert.Equal(hidden ? "비공개" : "0원", Assert.Single(vm.SelectedCustomerPayments).AmountDisplay);
        Assert.All(vm.SelectedCustomerRentals, row => Assert.Contains(hidden ? "비공개" : "월 0원", row.Meta));
    }

    private static LoginResponse Login(bool sales) => new()
    {
        Token = "synthetic-session-only", ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        User = new() { Username = "customer-reader", Role = "User", TenantCode = "USENET_GROUP",
            OfficeCode = "USENET", ScopeType = "OfficeOnly", Permissions = sales ? ["Amount.ViewSales"] : [] }
    };

    private static (CustomerDto Customer, MobileSyncState State, GeoraePlanApiClient Api) Fixture()
    {
        var customer = new CustomerDto { Id = Guid.NewGuid(), NameOriginal = "격리 거래처", OfficeCode = "USENET", ResponsibleOfficeCode = "USENET" };
        var invoice = new InvoiceDto { Id = Guid.NewGuid(), CustomerId = customer.Id, VoucherType = VoucherType.Sales,
            TotalAmount = 123456, SupplyAmount = 123456, VatAmount = 0,
            Lines = [new() { Quantity = 1, UnitPrice = 123456, LineAmount = 123456, Remark = "보존" }] };
        var payment = new PaymentDto { Id = Guid.NewGuid(), InvoiceId = invoice.Id, Amount = 98765, Note = "수금 비고" };
        invoice.Payments = [payment];
        var profile = new RentalBillingProfileDto { Id = Guid.NewGuid(), CustomerId = customer.Id, MonthlyAmount = 123456, OutstandingAmount = 456789 };
        var asset = new RentalAssetDto { Id = Guid.NewGuid(), CustomerId = customer.Id, BillingProfileId = profile.Id, MonthlyFee = 123456 };
        var state = new MobileSyncState
        {
            SyncedCustomers = [customer], SyncedInvoices = [invoice], SyncedPayments = [payment],
            SyncedRentalBillingProfiles = [profile], SyncedRentalAssets = [asset],
            SyncedRentalAssetAssignmentHistories = [new() { Id = Guid.NewGuid(), CustomerId = customer.Id,
                AssetId = asset.Id, BillingProfileId = profile.Id, MonthlyFee = 123456 }]
        };
        return (customer, state, new() { Detail = new() { Customer = customer, RecentInvoices = [invoice] } });
    }
}
