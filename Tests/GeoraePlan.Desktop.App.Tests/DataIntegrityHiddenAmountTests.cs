using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityHiddenAmountTests
{
    private const string Deferred = DataIntegrityIssueCodes.AmountVerificationDeferred;

    [Theory]
    [InlineData(false, 0, "청구대상", true, false)]
    [InlineData(false, 1100, "청구대상", false, false)]
    [InlineData(true, 0, "청구대상", false, true)]
    [InlineData(true, 9876543, "청구대상", false, true)]
    [InlineData(true, 0, "청구제외", false, false)]
    [InlineData(true, 0, "미확인", false, false)]
    public async Task RentalMonthlyFeeRequiresDisclosedEvidence(
        bool hidden, int fee, string eligibility, bool missingFee, bool deferred)
    {
        await using var f = await Fixture.CreateAsync();
        var asset = f.NewAsset(hidden, fee);
        asset.BillingEligibilityStatus = eligibility;
        // A hidden amount must not suppress an unrelated broken profile link.
        asset.BillingProfileId = Guid.NewGuid();
        f.Db.RentalAssets.Add(asset);
        await f.SaveAsync();
        f.Db.ChangeTracker.Clear();
        var result = await f.ScanAsync();
        Assert.Equal(missingFee, result.Issues.Any(x => x.EntityId == asset.Id &&
            x.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee));
        Assert.Equal(deferred, result.Issues.Any(x => x.EntityId == asset.Id && x.Code == Deferred));
        Assert.Contains(result.Issues, x => x.EntityId == asset.Id &&
            x.Code == DataIntegrityIssueCodes.RentalAssetMissingBillingProfile);
        Assert.Equal(eligibility == "미확인", result.Issues.Any(x => x.EntityId == asset.Id &&
            x.Code == DataIntegrityIssueCodes.RentalAssetBillingEligibilityUnconfirmed));
        Assert.DoesNotContain("9,876,543", JsonSerializer.Serialize(result));
        Assert.False(f.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());
        Assert.Equal(fee, (await f.Db.RentalAssets.AsNoTracking().SingleAsync()).MonthlyFee);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RentalProjectionPreservesHeaderAndAssetPrivacy(bool profileHidden, bool assetHidden)
    {
        await using var f = await Fixture.CreateAsync();
        var asset = f.NewAsset(assetHidden, 300);
        var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), CustomerId = f.Customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "HIDDEN-PROJECTION", CustomerName = "검증 거래처", IsActive = true,
            AmountsHidden = profileHidden, MonthlyAmount = 200,
            BillingTemplateJson = JsonSerializer.Serialize(new[] { new RentalBillingTemplateItemModel {
                ItemId = Guid.NewGuid(), DisplayItemName = "검증 품목", Quantity = 1, UnitPrice = 100,
                Amount = 100, IncludedAssetIds = [asset.Id] } }) };
        asset.BillingProfileId = profile.Id;
        f.Db.AddRange(asset, profile);
        await f.SaveAsync();
        f.Db.ChangeTracker.Clear();
        var result = await f.ScanAsync();
        Assert.Equal(!profileHidden, result.Issues.Any(x =>
            x.Code == DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch));
        // A hidden profile also redacts its template prices during parsing.
        Assert.Equal(!profileHidden && !assetHidden, result.Issues.Any(x =>
            x.Code == DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch));
        Assert.False(f.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());
    }
    public static IEnumerable<object[]> InvoiceCases()
    {
        foreach (var header in new[] { false, true })
        foreach (var line in new[] { false, true })
        foreach (var payment in new[] { false, true })
            yield return new object[] { header, line, payment };
    }

    [Theory]
    [MemberData(nameof(InvoiceCases))]
    public async Task InvoiceChecksRequireCompleteDisclosedEvidence(bool headerHidden, bool lineHidden, bool paymentHidden)
    {
        await using var f = await Fixture.CreateAsync();
        f.Invoice.AmountsHidden = headerHidden;
        f.Invoice.TotalAmount = headerHidden ? 0 : 100;
        f.Invoice.SupplyAmount = headerHidden ? 0 : 100;
        f.Db.InvoiceLines.Add(new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = f.Invoice.Id,
            ItemNameOriginal = "검증 품목", Quantity = 1, AmountsHidden = lineHidden,
            UnitPrice = lineHidden ? 0 : 50, LineAmount = lineHidden ? 0 : 50 });
        f.Db.Payments.Add(new LocalPayment { Id = Guid.NewGuid(), InvoiceId = f.Invoice.Id,
            AmountsHidden = paymentHidden, Amount = paymentHidden ? 9876543 : 150 });
        await f.SaveAsync();
        var result = await f.ScanAsync();
        Assert.Equal(!headerHidden && !lineHidden, result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.InvoiceAmountMismatch));
        Assert.Equal(!headerHidden && !paymentHidden, result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.InvoiceOverSettled));
        Assert.Equal(headerHidden || lineHidden || paymentHidden, result.Issues.Any(x => x.Code == Deferred));
        Assert.DoesNotContain("9,876,543", JsonSerializer.Serialize(result));
        Assert.False(f.Db.ChangeTracker.HasChanges()); Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task LinkedPaymentKeepsStructuralChecksWithoutComparingHiddenAmounts(
        bool transactionHidden, bool paymentHidden, bool wrongLink)
    {
        await using var f = await Fixture.CreateAsync();
        var transaction = f.Transaction(transactionHidden, transactionHidden ? 0 : 100);
        var other = f.NewInvoice(); f.Db.Invoices.Add(other);
        f.Db.Add(transaction);
        f.Db.Payments.Add(new LocalPayment { Id = transaction.Id, InvoiceId = wrongLink ? other.Id : f.Invoice.Id,
            AmountsHidden = paymentHidden, Amount = paymentHidden ? 9876543 : 200 });
        await f.SaveAsync();
        var result = await f.ScanAsync();
        var issues = result.Issues.Where(x => x.Code == DataIntegrityIssueCodes.InvoiceLinkedTransactionPaymentMismatch).ToList();
        Assert.Equal(wrongLink || (!transactionHidden && !paymentHidden), issues.Count == 1);
        if (wrongLink) Assert.Contains("링크 불일치", Assert.Single(issues).CurrentValue);
        Assert.DoesNotContain("9,876,543", JsonSerializer.Serialize(result));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RentalSummariesDeferOnlyWhenTheirSettlementEvidenceIsUnknown(
        bool hasTransaction, bool hidden, bool duplicateHiddenPayment)
    {
        await using var f = await Fixture.CreateAsync();
        var runId = Guid.NewGuid();
        var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), CustomerId = f.Customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "HIDDEN-INTEGRITY", CustomerName = "검증 거래처", IsActive = true,
            SettledAmount = 100, OutstandingAmount = 0,
            BillingRunsJson = JsonSerializer.Serialize(new[] { new RentalBillingRunModel { RunId = runId,
                RunKey = "2026-09", ScheduledDate = new DateOnly(2026, 9, 24),
                PeriodStartDate = new DateOnly(2026, 9, 1), PeriodEndDate = new DateOnly(2026, 9, 30),
                BilledAmount = 100, SettledAmount = 100 } }) };
        f.Invoice.LinkedRentalBillingProfileId = profile.Id; f.Invoice.LinkedRentalBillingRunId = runId;
        f.Db.Add(profile);
        var transaction = f.Transaction(hidden, hidden ? 0 : 60);
        transaction.LinkedRentalBillingProfileId = profile.Id; transaction.LinkedRentalBillingRunId = runId;
        transaction.TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt;
        if (hasTransaction) f.Db.Add(transaction);
        if (!hasTransaction || duplicateHiddenPayment)
            f.Db.Payments.Add(new LocalPayment { Id = transaction.Id, InvoiceId = f.Invoice.Id,
                AmountsHidden = hidden || duplicateHiddenPayment, Amount = hidden || duplicateHiddenPayment ? 0 : 60 });
        // Hidden backing values must not enter arithmetic, even before a result is deferred.
        if (hidden && hasTransaction)
        {
            transaction.SettlementAmount = decimal.MaxValue;
            var extra = f.Transaction(true, decimal.MaxValue);
            extra.LinkedRentalBillingProfileId = profile.Id; extra.LinkedRentalBillingRunId = runId;
            f.Db.Add(extra);
        }
        else if (hidden)
        {
            foreach (var payment in f.Db.ChangeTracker.Entries<LocalPayment>()) payment.Entity.Amount = decimal.MaxValue;
            f.Db.Payments.Add(new LocalPayment { Id = Guid.NewGuid(), InvoiceId = f.Invoice.Id,
                AmountsHidden = true, Amount = decimal.MaxValue });
        }
        await f.SaveAsync();
        var result = await f.ScanAsync();
        Assert.Equal(!hidden, result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch));
        Assert.Equal(!hidden, result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.RentalBillingProfileSummaryMismatch));
        Assert.Equal(hidden, result.Issues.Any(x => x.Code == Deferred && x.EntityId == profile.Id));
        Assert.False(f.Db.ChangeTracker.HasChanges()); Assert.Equal(100, profile.SettledAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPaymentIsAnErrorOnlyWhenPositiveSettlementIsDisclosed(bool hidden)
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.Add(f.Transaction(hidden, hidden ? 0 : 100)); await f.SaveAsync();
        var result = await f.ScanAsync();
        Assert.Equal(!hidden, result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.InvoiceLinkedTransactionPaymentMismatch));
        Assert.Equal(hidden, result.Issues.Any(x => x.Code == Deferred));
        Assert.All(result.Issues.Where(x => x.Code == Deferred), issue =>
        {
            Assert.Equal("Info", issue.Severity);
            Assert.Equal(DataIntegrityDirectActionKind.None, issue.DirectActionKind);
            Assert.Contains("보류", issue.CurrentValue);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedHiddenRowsDoNotPoisonKnownOrEmptyAggregates(bool hasLine)
    {
        await using var f = await Fixture.CreateAsync();
        if (hasLine) f.Db.InvoiceLines.Add(new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = f.Invoice.Id,
            Quantity = 1, UnitPrice = 100, LineAmount = 100 });
        f.Db.InvoiceLines.Add(new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = f.Invoice.Id,
            AmountsHidden = true, IsDeleted = true, LineAmount = 9876543 });
        f.Db.Payments.Add(new LocalPayment { Id = Guid.NewGuid(), InvoiceId = f.Invoice.Id,
            AmountsHidden = true, IsDeleted = true, Amount = 9876543 });
        await f.SaveAsync();
        var result = await f.ScanAsync();
        Assert.Equal(!hasLine, result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.InvoiceAmountMismatch));
        Assert.DoesNotContain(result.Issues, x => x.Code == Deferred || x.Code == DataIntegrityIssueCodes.InvoiceOverSettled);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task RentalDeletionAndLinkErrorsRemainVisibleWithoutHiddenAmounts(
        bool invoiceDeleted, bool wrongLink, bool hidden)
    {
        await using var f = await Fixture.CreateAsync();
        f.Invoice.IsDeleted = invoiceDeleted;
        f.Invoice.LinkedRentalBillingProfileId = Guid.NewGuid();
        f.Invoice.LinkedRentalBillingRunId = Guid.NewGuid();
        var transaction = f.Transaction(hidden, hidden ? 9876543 : 100);
        transaction.LinkedInvoiceId = wrongLink ? null : f.Invoice.Id;
        transaction.LinkedRentalBillingProfileId = f.Invoice.LinkedRentalBillingProfileId;
        transaction.LinkedRentalBillingRunId = f.Invoice.LinkedRentalBillingRunId;
        transaction.TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt;
        f.Db.Add(transaction);
        f.Db.Payments.Add(new LocalPayment { Id = transaction.Id, InvoiceId = f.Invoice.Id,
            AmountsHidden = hidden, Amount = hidden ? 9876543 : 200, IsDeleted = !invoiceDeleted });
        await f.SaveAsync();
        var result = await f.ScanAsync();
        Assert.Equal(invoiceDeleted, result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.RentalDeletedInvoiceActivePayment));
        Assert.Equal(!invoiceDeleted && (wrongLink || !hidden), result.Issues.Any(x => x.Code == DataIntegrityIssueCodes.RentalInvoiceDeletedPaymentDetachedTransaction));
        if (hidden) Assert.DoesNotContain("9,876,543", JsonSerializer.Serialize(result));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public LocalDbContext Db { get; }
        public LocalCustomer Customer { get; }
        public LocalInvoice Invoice { get; }
        private readonly SessionState session = new();
        private Fixture(SqliteConnection connection)
        {
            this.connection = connection;
            Db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            session.SetSession("isolated-test", new UserSessionDto { UserId = Guid.NewGuid(), Username = "integrity-test",
                Role = DomainConstants.RoleAdmin, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
            Customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "검증 거래처", NameMatchKey = "INTEGRITY",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
            Invoice = NewInvoice();
            Db.AddRange(Customer, Invoice);
        }
        public LocalInvoice NewInvoice() => new() { Id = Guid.NewGuid(), CustomerId = Customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, VoucherType = VoucherType.Sales,
            InvoiceNumber = Guid.NewGuid().ToString("N"), InvoiceDate = new DateOnly(2026, 9, 24),
            IsLatestVersion = true, VatMode = InvoiceVatModes.None, TotalAmount = 100, SupplyAmount = 100 };
        public LocalRentalAsset NewAsset(bool hidden, decimal fee) => new() { Id = Guid.NewGuid(),
            CustomerId = Customer.Id, CustomerName = Customer.NameOriginal, CurrentCustomerName = Customer.NameOriginal,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = Guid.NewGuid().ToString("N"), ItemName = "검증 품목", AssetStatus = "임대진행중",
            BillingEligibilityStatus = "청구대상", SalesAmountsHidden = hidden, MonthlyFee = fee };
        public LocalTransaction Transaction(bool hidden, decimal amount) => new() { Id = Guid.NewGuid(), CustomerId = Customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, LinkedInvoiceId = Invoice.Id,
            TransactionDate = Invoice.InvoiceDate, TransactionKind = PaymentFlowConstants.TransactionKindInvoiceReceipt,
            AmountsHidden = hidden, SettlementAmount = amount, BankReceipt = amount, ReceiptTotal = amount };
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var fixture = new Fixture(connection); await fixture.Db.Database.EnsureCreatedAsync(); return fixture;
        }
        public async Task SaveAsync()
        {
            foreach (var entry in Db.ChangeTracker.Entries<LocalSyncEntity>()) { entry.Entity.IsDirty = false; entry.Entity.Revision = 9; }
            await Db.SaveChangesAsync();
        }
        public Task<DataIntegrityScanResult> ScanAsync() => new DataIntegrityIssueService(Db).ScanAsync(session);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
