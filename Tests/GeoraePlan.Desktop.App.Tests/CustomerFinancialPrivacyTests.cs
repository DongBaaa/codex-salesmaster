using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerFinancialPrivacyTests
{
    private static UserSessionDto User(Guid id, params string[] permissions) => new()
    {
        UserId = id, Username = "financial-fixture", Role = DomainConstants.RoleUser,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions.ToList()
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SummaryUsesDirectionPermissionsWithoutChangingStoredMoney(bool sales, bool purchase)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var customer = Customer();
        db.Customers.Add(customer);
        db.Invoices.AddRange(Invoice(customer.Id, VoucherType.Sales), Invoice(customer.Id, VoucherType.Purchase));
        var transaction = Transaction(customer.Id);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        var permissions = new List<string> { AppPermissionNames.PaymentEdit, AppPermissionNames.InvoiceEdit };
        if (sales) permissions.Add(AppPermissionNames.AmountViewSales);
        if (purchase) permissions.Add(AppPermissionNames.AmountViewPurchase);
        var session = new SessionState();
        session.SetSession("fixture", User(Guid.NewGuid(), permissions.ToArray()));
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var summary = await service.GetCustomerFinancialSummaryAsync(customer.Id, session);
        Assert.Equal(sales ? 100m : (decimal?)null, summary.AdvanceBalance);
        Assert.Equal(sales ? 100m : (decimal?)null, await service.GetAdvanceBalanceAsync(customer.Id, session));
        Assert.Equal(sales ? 900m : (decimal?)null, summary.ReceivableAmount);
        Assert.Equal(purchase ? 900m : (decimal?)null, summary.PayableAmount);
        Assert.Equal(purchase ? 20m : (decimal?)null, summary.PrepaidAmount);
        Assert.Equal(100m, transaction.AdvanceDelta);
        Assert.False(transaction.IsDirty);
        Assert.True(session.HasPermission(AppPermissionNames.InvoiceEdit));
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("line")]
    [InlineData("payment")]
    [InlineData("transaction")]
    [InlineData("deleted-payment")]
    [InlineData("out-of-scope-transaction")]
    public async Task HiddenEvidencePropagatesUnknownButDeletedAndOutOfScopeEvidenceDoesNot(string hidden)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var customer = Customer();
        var invoice = Invoice(customer.Id, VoucherType.Sales);
        invoice.AmountsHidden = hidden == "invoice";
        invoice.Lines.Single().AmountsHidden = hidden == "line";
        invoice.Payments.Single().AmountsHidden = hidden is "payment" or "deleted-payment";
        invoice.Payments.Single().IsDeleted = hidden == "deleted-payment";
        var transaction = Transaction(customer.Id);
        transaction.AmountsHidden = hidden == "transaction";
        db.Customers.Add(customer);
        db.Invoices.Add(invoice);
        db.Transactions.Add(transaction);
        if (hidden == "out-of-scope-transaction")
        {
            var other = Transaction(customer.Id);
            other.AmountsHidden = true;
            other.TenantCode = TenantScopeCatalog.Itworld;
            other.OfficeCode = other.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            db.Transactions.Add(other);
        }
        await db.SaveChangesAsync();
        var session = new SessionState();
        session.SetSession("fixture", User(Guid.NewGuid(), AppPermissionNames.AmountViewSales, AppPermissionNames.AmountViewPurchase));
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var summary = await service.GetCustomerFinancialSummaryAsync(customer.Id, session);
        Assert.Equal(hidden is "invoice" or "line" or "payment" ? null : hidden == "deleted-payment" ? 1000m : (decimal?)900m, summary.ReceivableAmount);
        Assert.Equal(hidden == "transaction" ? null : (decimal?)100m, summary.AdvanceBalance);
        Assert.Equal(hidden == "transaction" ? null : (decimal?)20m, summary.PrepaidAmount);
        Assert.Equal(0m, summary.PayableAmount);
    }

    [Fact]
    public async Task ClearedCacheCannotBeRepopulatedByAnOlderInFlightResponse()
    {
        var cache = new InvoiceLedgerScreenCache();
        var customerId = Guid.NewGuid();
        var pending = new TaskCompletionSource<CustomerFinancialSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequest = cache.GetCustomerFinancialSummaryAsync(customerId, false, () => pending.Task);
        cache.Clear();
        var fresh = await cache.GetCustomerFinancialSummaryAsync(customerId, false, () => Task.FromResult(new CustomerFinancialSummary { AdvanceBalance = null }));
        pending.SetResult(new CustomerFinancialSummary { AdvanceBalance = 999m });
        await oldRequest;
        var cached = await cache.GetCustomerFinancialSummaryAsync(customerId, false, () => throw new Exception("Expected fresh cache"));
        Assert.True(cached.CacheHit);
        Assert.Null(cached.Value.AdvanceBalance);
        Assert.Same(fresh.Value, cached.Value);
    }

    [Fact]
    public async Task LookupInvalidatesAmountsOnRevocationAndRegrantButNotTokenRefresh()
    {
        var session = new SessionState();
        var id = Guid.NewGuid();
        var allowed = User(id, AppPermissionNames.AmountViewSales, AppPermissionNames.AmountViewPurchase);
        session.SetSession("fixture", allowed);
        await using var vm = new CustomerInvoiceLookupViewModel(null!, session);
        vm.PreviewCustomerAdvanceBalance = 1200m;
        vm.PreviewCustomerPayableBalance = 200m;
        Assert.Equal("1,200원", vm.PreviewCustomerAdvanceBalanceDisplay);
        session.RefreshSession("new-token", allowed);
        Assert.Equal("1,200원", vm.PreviewCustomerAdvanceBalanceDisplay);
        session.RefreshSession("restricted", User(id));
        Assert.Null(vm.PreviewCustomerAdvanceBalance);
        Assert.Equal("비공개", vm.PreviewCustomerPayableBalanceDisplay);
        session.RefreshSession("restored", allowed);
        Assert.Equal("비공개", vm.PreviewCustomerAdvanceBalanceDisplay);
        vm.PreviewCustomerAdvanceBalance = 0m;
        Assert.Equal("0원", vm.PreviewCustomerAdvanceBalanceDisplay);
    }

    [Fact]
    public async Task HiddenRentalRowDoesNotTurnIntoZeroOutstanding()
    {
        var session = new SessionState();
        session.SetSession("fixture", User(Guid.NewGuid(), AppPermissionNames.AmountViewSales));
        await using var vm = new CustomerInvoiceLookupViewModel(null!, session);
        var rows = new List<LocalInvoiceListSummary>
        {
            new() { LinkedRentalBillingProfileId = Guid.NewGuid(), VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026,9,24), AmountsHidden = true, TotalAmount = 0m },
            new() { LinkedRentalBillingProfileId = Guid.NewGuid(), VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026,9,23), TotalAmount = 100m }
        };
        typeof(CustomerInvoiceLookupViewModel).GetMethod("ApplyRentalInvoicePreviewSummary", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [rows]);
        Assert.Null(vm.PreviewLatestRentalInvoiceAmount);
        Assert.Null(vm.PreviewRentalOutstandingAmount);
        Assert.Equal("비공개", vm.PreviewRentalOutstandingAmountDisplay);
    }

    [Fact]
    public void PaymentSummaryRemovesBalanceOnPermissionChangeAndUnsubscribesOnClose()
    {
        var session = new SessionState();
        var id = Guid.NewGuid();
        var allowed = User(id, AppPermissionNames.AmountViewSales, AppPermissionNames.PaymentEdit);
        session.SetSession("fixture", allowed);
        using var vm = new PaymentViewModel(null!, session);
        vm.AdvanceBalance = 500m;
        Assert.Equal("500", vm.AdvanceBalanceDisplay);
        session.RefreshSession("token-refresh", allowed);
        Assert.Equal(500m, vm.AdvanceBalance);
        session.RefreshSession("restricted", User(id, AppPermissionNames.PaymentEdit));
        Assert.Null(vm.AdvanceBalance);
        Assert.Equal("비공개", vm.AdvanceBalanceDisplay);
        Assert.Equal("잔액 비공개", vm.TransactionSummary);
        Assert.True(vm.CanEditPayments);
        session.RefreshSession("restored", allowed);
        Assert.Null(vm.AdvanceBalance);
        vm.Dispose();
        vm.AdvanceBalance = 75m;
        session.Clear();
        Assert.Equal(75m, vm.AdvanceBalance);
        Assert.Equal("비공개", vm.AdvanceBalanceDisplay);
    }

    private static LocalCustomer Customer() => new()
    {
        Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, NameOriginal = "Fixture", NameMatchKey = "FIXTURE", IsDirty = false
    };
    private static LocalTransaction Transaction(Guid customerId) => new()
    {
        Id = Guid.NewGuid(), CustomerId = customerId, TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
        AdvanceDelta = 100m, PrepaidDelta = 20m, IsDirty = false
    };
    private static LocalInvoice Invoice(Guid customerId, VoucherType type)
    {
        var id = Guid.NewGuid();
        return new LocalInvoice
        {
            Id = id, CustomerId = customerId, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = type, TotalAmount = 1000m, IsConfirmed = true, IsLatestVersion = true, IsDirty = false,
            VersionGroupId = id, VersionNumber = 1,
            Lines = [new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = id, Quantity = 1m, UnitPrice = 1000m, LineAmount = 1000m }],
            Payments = [new LocalPayment { Id = Guid.NewGuid(), InvoiceId = id, Amount = 100m, IsDirty = false }]
        };
    }
}
