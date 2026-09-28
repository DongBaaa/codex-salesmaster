using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SettlementAmountPrivacyTests
{
    [Theory]
    [InlineData(VoucherType.Sales, AppPermissionNames.AmountViewSales, true)]
    [InlineData(VoucherType.Collection, AppPermissionNames.AmountViewSales, true)]
    [InlineData(VoucherType.Sales, AppPermissionNames.AmountViewPurchase, false)]
    [InlineData(VoucherType.Purchase, AppPermissionNames.AmountViewPurchase, true)]
    [InlineData(VoucherType.Procurement, AppPermissionNames.AmountViewPurchase, true)]
    [InlineData(VoucherType.Expense, AppPermissionNames.AmountViewPurchase, true)]
    [InlineData(VoucherType.Purchase, AppPermissionNames.AmountViewSales, false)]
    [InlineData((VoucherType)99, AppPermissionNames.AmountViewSales, false)]
    [InlineData(VoucherType.Sales, AppPermissionNames.PaymentEdit, false)]
    public async Task InvoiceSummaryRequiresMatchingAmountPermission(VoucherType type, string permission, bool visible)
    {
        await using var f = await Fixture.CreateAsync(permission);
        f.Invoice.VoucherType = type;
        await f.Db.SaveChangesAsync();
        var summary = await f.Local.GetInvoiceSettlementSummaryAsync(f.Invoice.Id, f.Session);
        Assert.Equal(visible ? 1000m : (decimal?)null, summary.InvoiceTotal);
        Assert.Equal(visible ? 200m : (decimal?)null, summary.SettledAmount);
        Assert.Equal(visible ? 800m : (decimal?)null, summary.RemainingAmount);
        Assert.Equal(1000m, f.Invoice.TotalAmount);
        Assert.False(f.Invoice.IsDirty);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("line")]
    [InlineData("payment")]
    [InlineData("transaction")]
    public async Task HiddenEvidencePreventsFalseSettlementAndLinkedWrites(string evidence)
    {
        await using var f = await Fixture.CreateAsync(AppPermissionNames.AmountViewSales);
        f.Hide(evidence);
        await f.Db.SaveChangesAsync();
        var summary = await f.Local.GetInvoiceSettlementSummaryAsync(f.Invoice.Id, f.Session);
        Assert.Null(summary.InvoiceTotal);
        Assert.Null(summary.SettledAmount);
        Assert.Null(summary.RemainingAmount);
        var candidate = f.NewReceipt();
        candidate.LinkedInvoiceId = f.Invoice.Id;
        var result = await f.Local.SaveTransactionAsync(candidate, f.Session);
        Assert.False(result.Success);
        Assert.Contains("비공개", result.Message);
        Assert.False(await f.Db.Transactions.AnyAsync(row => row.Id == candidate.Id));
        Assert.Equal(200m, (await f.Db.Payments.SingleAsync()).Amount);
        Assert.False(f.Invoice.IsDirty);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("header", false)]
    [InlineData("line", false)]
    [InlineData("payment", false)]
    [InlineData("transaction", false)]
    [InlineData("other-run", true)]
    [InlineData("other-office", true)]
    [InlineData("deleted", true)]
    public async Task RentalSummaryRespectsRunScopeAndUnknownEvidence(string evidence, bool visible)
    {
        await using var f = await Fixture.CreateAsync(AppPermissionNames.AmountViewSales);
        var runId = Guid.NewGuid();
        var profile = new LocalRentalBillingProfile
        {
            Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "privacy-fixture", CustomerName = "Fixture", MonthlyAmount = 1000m, IsActive = true, IsDirty = false
        };
        f.Invoice.LinkedRentalBillingProfileId = profile.Id;
        f.Invoice.LinkedRentalBillingRunId = runId;
        f.Db.RentalBillingProfiles.Add(profile);
        if (evidence is "header" or "line" or "payment") f.Hide(evidence);
        if (evidence is "transaction" or "other-run" or "other-office" or "deleted")
        {
            var tx = f.NewReceipt();
            tx.LinkedRentalBillingProfileId = profile.Id;
            tx.LinkedRentalBillingRunId = evidence == "other-run" ? Guid.NewGuid() : runId;
            tx.AmountsHidden = true;
            tx.IsDeleted = evidence == "deleted";
            if (evidence == "other-office")
            {
                tx.TenantCode = TenantScopeCatalog.Itworld;
                tx.OfficeCode = tx.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            }
            f.Db.Transactions.Add(tx);
        }
        await f.Db.SaveChangesAsync();
        var summary = await f.Local.GetRentalSettlementSummaryAsync(profile.Id, runId, 1000m, f.Session);
        Assert.Equal(visible ? 1000m : (decimal?)null, summary.BilledAmount);
        Assert.Equal(visible ? 200m : (decimal?)null, summary.SettledAmount);
        Assert.Equal(visible ? 800m : (decimal?)null, summary.OutstandingAmount);
        if (!visible)
        {
            Assert.Equal("비공개", summary.CompletionStatus);
            Assert.Equal("비공개", summary.SettlementStatus);
        }
        f.Session.RefreshSession("restricted", Fixture.User(f.Session.User!.UserId));
        var restricted = await f.Local.GetRentalSettlementSummaryAsync(profile.Id, runId, 1000m, f.Session);
        Assert.Null(restricted.BilledAmount);
        Assert.Null(restricted.OutstandingAmount);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("billed")]
    [InlineData("settled")]
    [InlineData("item")]
    [InlineData("invalid")]
    public async Task RentalOwnHiddenEvidenceCannotBeRevealedByOverrideOrLinkedInvoice(string evidence)
    {
        await using var f = await Fixture.CreateAsync(AppPermissionNames.AmountViewSales);
        var run = new RentalBillingRunModel { RunId = Guid.NewGuid(), RunKey = "2026-09", BilledAmount = evidence == "billed" ? null : 1000m,
            SettledAmount = evidence == "settled" ? null : 200m,
            Items = [new RentalBillingTemplateItemModel { Quantity = 2m, UnitPrice = evidence == "item" ? null : 500m, Amount = 1000m, Note = "keep" }] };
        var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), TenantCode = f.Invoice.TenantCode, OfficeCode = f.Invoice.OfficeCode,
            ResponsibleOfficeCode = f.Invoice.ResponsibleOfficeCode, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "own-evidence", CustomerName = "Fixture", MonthlyAmount = 9876m, SettledAmount = 123m, OutstandingAmount = 9753m,
            IsActive = true, IsDirty = false, AmountsHidden = evidence == "profile",
            BillingRunsJson = evidence == "invalid" ? "[{broken" : System.Text.Json.JsonSerializer.Serialize(new[] { run }) };
        f.Invoice.LinkedRentalBillingProfileId = profile.Id; f.Invoice.LinkedRentalBillingRunId = run.RunId;
        f.Db.Add(profile); await f.Db.SaveChangesAsync();
        var beforeJson = profile.BillingRunsJson;
        foreach (var overrideAmount in new decimal?[] { null, 777m })
        {
            var summary = await f.Local.GetRentalSettlementSummaryAsync(profile.Id, run.RunId, overrideAmount, f.Session);
            Assert.Null(summary.BilledAmount); Assert.Null(summary.SettledAmount); Assert.Null(summary.OutstandingAmount);
            Assert.Equal("비공개", summary.CompletionStatus);
        }
        var receipt = f.NewReceipt(); receipt.LinkedRentalBillingProfileId = profile.Id; receipt.LinkedRentalBillingRunId = run.RunId;
        var saved = await f.Local.SaveTransactionAsync(receipt, f.Session);
        Assert.False(saved.Success); Assert.False(await f.Db.Transactions.AnyAsync(x => x.Id == receipt.Id));
        await f.Local.RecalculateRentalSettlementsAsync([(profile.Id, (Guid?)run.RunId)], markDirty: true);
        await f.Db.Entry(profile).ReloadAsync();
        Assert.Equal(beforeJson, profile.BillingRunsJson); Assert.Equal(9876m, profile.MonthlyAmount);
        Assert.Equal(123m, profile.SettledAmount); Assert.Equal(9753m, profile.OutstandingAmount); Assert.False(profile.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RentalKnownRunRemainsReadableWhenOtherRunIsHidden(bool linkedZeroInvoice)
    {
        await using var f = await Fixture.CreateAsync(AppPermissionNames.AmountViewSales);
        var run = new RentalBillingRunModel { RunId = Guid.NewGuid(), RunKey = "2026-09", BilledAmount = 400m, SettledAmount = 0m };
        var hidden = new RentalBillingRunModel { RunId = Guid.NewGuid(), RunKey = "2026-08", BilledAmount = null, SettledAmount = null };
        var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), TenantCode = f.Invoice.TenantCode, OfficeCode = f.Invoice.OfficeCode,
            ResponsibleOfficeCode = f.Invoice.ResponsibleOfficeCode, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "other-run", CustomerName = "Fixture", MonthlyAmount = 999m, IsActive = true, IsDirty = false,
            BillingRunsJson = System.Text.Json.JsonSerializer.Serialize(new[] { run, hidden }) };
        if (linkedZeroInvoice)
        {
            f.Invoice.LinkedRentalBillingProfileId = profile.Id; f.Invoice.LinkedRentalBillingRunId = run.RunId;
            f.Invoice.TotalAmount = 0m; f.Invoice.Payments.Clear();
        }
        f.Db.Add(profile); await f.Db.SaveChangesAsync();
        var summary = await f.Local.GetRentalSettlementSummaryAsync(profile.Id, run.RunId, null, f.Session);
        Assert.Equal(linkedZeroInvoice ? 0m : 400m, summary.BilledAmount);
        Assert.Null((await f.Local.GetRentalSettlementSummaryAsync(profile.Id, hidden.RunId, null, f.Session)).BilledAmount);
        Assert.Null((await f.Local.GetRentalSettlementSummaryAsync(profile.Id, f.Session)).BilledAmount);
        Assert.False(profile.IsDirty);
    }

    [Fact]
    public async Task MissingOrOutOfScopeInvoiceIsUnknownAndRealZeroRemainsZero()
    {
        await using var f = await Fixture.CreateAsync(AppPermissionNames.AmountViewSales);
        Assert.Null((await f.Local.GetInvoiceSettlementSummaryAsync(Guid.NewGuid(), f.Session)).RemainingAmount);
        f.Invoice.TotalAmount = 200m;
        await f.Db.SaveChangesAsync();
        Assert.Equal(0m, (await f.Local.GetInvoiceSettlementSummaryAsync(f.Invoice.Id, f.Session)).RemainingAmount);
        f.Invoice.TenantCode = TenantScopeCatalog.Itworld;
        f.Invoice.OfficeCode = f.Invoice.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        await f.Db.SaveChangesAsync();
        Assert.Null((await f.Local.GetInvoiceSettlementSummaryAsync(f.Invoice.Id, f.Session)).InvoiceTotal);
    }

    [Fact]
    public async Task PaymentEditWithoutAmountReadStillAllowsExplicitGeneralReceipt()
    {
        await using var f = await Fixture.CreateAsync();
        var receipt = f.NewReceipt();
        var result = await f.Local.SaveTransactionAsync(receipt, f.Session);
        Assert.True(result.Success, result.Message);
        Assert.Equal(50m, (await f.Db.Transactions.SingleAsync(row => row.Id == receipt.Id)).ReceiptTotal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public LocalDbContext Db { get; }
        public SessionState Session { get; }
        public LocalStateService Local { get; }
        public LocalInvoice Invoice { get; }
        private Fixture(SqliteConnection connection, LocalDbContext db, SessionState session, LocalInvoice invoice)
        {
            _connection = connection; Db = db; Session = session; Invoice = invoice;
            Local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        }
        internal static UserSessionDto User(Guid id, params string[] permissions) => new()
        {
            UserId = id, Username = "settlement-fixture", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.Concat([AppPermissionNames.PaymentEdit, AppPermissionNames.RentalViewAll]).Distinct().ToList()
        };
        public static async Task<Fixture> CreateAsync(params string[] permissions)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var customer = new LocalCustomer { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Fixture", NameMatchKey = "FIXTURE", IsDirty = false };
            var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = customer.Id, TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode, ResponsibleOfficeCode = customer.OfficeCode, VoucherType = VoucherType.Sales,
                TotalAmount = 1000m, IsLatestVersion = true, IsConfirmed = true, IsDirty = false };
            invoice.VersionGroupId = invoice.Id;
            invoice.Lines.Add(new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = invoice.Id, Quantity = 1m, UnitPrice = 1000m, LineAmount = 1000m });
            invoice.Payments.Add(new LocalPayment { Id = Guid.NewGuid(), InvoiceId = invoice.Id, Amount = 200m, IsDirty = false });
            db.Customers.Add(customer); db.Invoices.Add(invoice); await db.SaveChangesAsync();
            var session = new SessionState();session.SetSession("fixture", User(Guid.NewGuid(), permissions));
            return new Fixture(connection, db, session, invoice);
        }
        public LocalTransaction NewReceipt() => new() { Id = Guid.NewGuid(), CustomerId = Invoice.CustomerId,
            TenantCode = Invoice.TenantCode, OfficeCode = Invoice.OfficeCode, ResponsibleOfficeCode = Invoice.ResponsibleOfficeCode,
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt, ReceiptTotal = 50m, CashReceipt = 50m, IsDirty = false };
        public void Hide(string evidence)
        {
            if (evidence == "header") Invoice.AmountsHidden = true;
            if (evidence == "line") Invoice.Lines.Single().AmountsHidden = true;
            if (evidence == "payment") Invoice.Payments.Single().AmountsHidden = true;
            if (evidence == "transaction") { var tx = NewReceipt(); tx.LinkedInvoiceId = Invoice.Id; tx.AmountsHidden = true; Db.Transactions.Add(tx); }
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
