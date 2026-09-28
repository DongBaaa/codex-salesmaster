using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalReadAmountPrivacyTests
{
    [Fact]
    public async Task PermissionRevocationMasksExistingCacheWithoutChangingStoredAmounts()
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var profile = CreateProfile("known"); db.Add(profile); await db.SaveChangesAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username = "restricted", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.RentalViewAll, AppPermissionNames.RentalProfileEdit] });
        var service = new RentalStateService(db);
        var row = Assert.Single(await service.GetBillingRowsAsync(new RentalBillingFilter { ReferenceDate = new(2026, 9, 25) }, session));
        Assert.Null(row.CurrentBilledAmount); Assert.True(row.Source.AmountsHidden);
        var history = Assert.Single(await service.GetBillingHistoryRowsAsync([profile.Id], session, new(2026, 9, 25)));
        Assert.Null(history.BilledAmount); Assert.False(history.CanRegisterSettlement);
        await db.Entry(profile).ReloadAsync(); Assert.False(profile.AmountsHidden); Assert.False(profile.IsDirty); Assert.Equal(12345m, profile.MonthlyAmount);
        var authorized = Assert.Single(await service.GetBillingRowsAsync(new RentalBillingFilter { ReferenceDate = new(2026, 9, 25) }, Session()));
        Assert.Equal(12345m, authorized.CurrentBilledAmount); Assert.Equal(345m, authorized.SettledAmount);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("receipt")]
    [InlineData("payment")]
    public async Task HiddenLinkedFinancialReferencesDoNotRestoreCachedAmounts(string source)
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var profile = CreateProfile("known"); var run = JsonSerializer.Deserialize<List<RentalBillingRunModel>>(profile.BillingRunsJson)![0];
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), IsLatestVersion = true, InvoiceDate = run.ScheduledDate,
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode, ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            LinkedRentalBillingProfileId = profile.Id, LinkedRentalBillingRunId = run.RunId, TotalAmount = 77777m, AmountsHidden = source == "invoice" };
        db.Add(profile); db.Add(invoice);
        if (source == "receipt") db.Add(new LocalTransaction { Id = Guid.NewGuid(), CustomerId = invoice.CustomerId,
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode, ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            LinkedRentalBillingProfileId = profile.Id, LinkedRentalBillingRunId = run.RunId, TransactionDate = run.ScheduledDate,
            SettlementAmount = 999m, AmountsHidden = true });
        if (source == "payment") db.Add(new LocalPayment { Id = Guid.NewGuid(), InvoiceId = invoice.Id, Amount = 999m, AmountsHidden = true, PaymentDate = run.ScheduledDate });
        await db.SaveChangesAsync();
        var service = new RentalStateService(db);
        var history = Assert.Single(await service.GetBillingHistoryRowsAsync([profile.Id], Session(), new(2026, 9, 25)));
        Assert.True(history.HasInvoice); Assert.Equal(invoice.Id, history.InvoiceId);
        Assert.Null(history.BilledAmount); Assert.Null(history.SettledAmount); Assert.Null(history.OutstandingAmount);
        var row = Assert.Single(await service.GetBillingRowsAsync(new RentalBillingFilter { ReferenceDate = new(2026, 9, 25) }, Session()));
        Assert.Null(row.CurrentBilledAmount); Assert.Null(row.SettledAmount); Assert.Null(row.OutstandingAmount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedCustomerSummaryCannotPublishPartialTotals(bool includeHistory)
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        db.AddRange(CreateProfile("profile"), CreateProfile("known")); await db.SaveChangesAsync();
        var row = Assert.Single(await new RentalStateService(db).GetBillingRowsAsync(new RentalBillingFilter { ReferenceDate = new(2026, 10, 25), IncludeHistoryRows = includeHistory }, Session()));
        Assert.Equal(2, row.GroupedSourceCount); Assert.Null(row.CurrentBilledAmount); Assert.Null(row.SettledAmount); Assert.Null(row.OutstandingAmount);
        Assert.Null(row.PastUnresolvedAmount); Assert.Equal("비공개", row.OutstandingAmountDisplay);
    }

    [Fact]
    public async Task ExistingActiveZeroRunIsNotReplacedWithMonthlyFee()
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var profile = CreateProfile("known"); var runs = JsonSerializer.Deserialize<List<RentalBillingRunModel>>(profile.BillingRunsJson)!;
        runs[0].BilledAmount = 0m; runs[0].SettledAmount = 0m; profile.BillingRunsJson = JsonSerializer.Serialize(runs);
        db.Add(profile); await db.SaveChangesAsync();
        var row = Assert.Single(await new RentalStateService(db).GetBillingRowsAsync(new RentalBillingFilter { ReferenceDate = new(2026, 9, 25) }, Session()));
        Assert.Equal(0m, row.CurrentBilledAmount); Assert.Equal("0", row.CurrentBilledAmountDisplay); Assert.False(row.AmountsHidden);
    }

    [Fact]
    public async Task HiddenLinkedTemplateStillShowsMetadataWithoutFeeMismatchDiagnosis()
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var profile = CreateProfile("profile"); var assetId = Guid.NewGuid();
        var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson)!;
        items[0].IncludedAssetIds = [assetId]; profile.BillingTemplateJson = JsonSerializer.Serialize(items);
        db.Add(profile); db.Add(new LocalRentalAsset { Id = assetId, ManagementNumber = assetId.ToString(), BillingProfileId = profile.Id,
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode, ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            MonthlyFee = 0m, SalesAmountsHidden = true });
        await db.SaveChangesAsync();
        var row = Assert.Single(await new RentalStateService(db).GetBillingRowsAsync(new RentalBillingFilter { ReferenceDate = new(2026, 9, 25) }, Session()));
        Assert.Null(row.CurrentBilledAmount); Assert.Equal(1, row.IncludedAssetCount);
        Assert.DoesNotContain("월요금", row.DataIssueSummary);
    }

    [Theory]
    [InlineData("profile", false)]
    [InlineData("run", false)]
    [InlineData("item", false)]
    [InlineData("profile", true)]
    [InlineData("run", true)]
    [InlineData("item", true)]
    public async Task HiddenHistoryAndListRetainIdentityWithoutLeakingCachedMoney(string hiddenSource, bool list)
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var profile = CreateProfile(hiddenSource);
        db.Add(profile); await db.SaveChangesAsync();
        var raw = profile.BillingRunsJson;
        var service = new RentalStateService(db);
        if (list)
        {
            var row = Assert.Single(await service.GetBillingRowsAsync(new RentalBillingFilter { ReferenceDate = new(2026, 9, 25) }, Session()));
            Assert.Equal(profile.Id, row.Source.Id);
            Assert.Null((object?)row.CurrentBilledAmount); Assert.Null((object?)row.SettledAmount); Assert.Null((object?)row.OutstandingAmount);
            Assert.Equal("비공개", row.CurrentBilledAmountDisplay);
        }
        else
        {
            var row = Assert.Single(await service.GetBillingHistoryRowsAsync([profile.Id], Session(), new(2026, 9, 25)));
            Assert.Equal(profile.Id, row.BillingProfileId); Assert.NotEqual(Guid.Empty, row.BillingRunId);
            Assert.Equal("2026-09", row.PeriodLabel);
            Assert.Null((object?)row.BilledAmount); Assert.Null((object?)row.SettledAmount); Assert.Null((object?)row.OutstandingAmount);
            Assert.False(row.CanRegisterSettlement);
        }
        await db.Entry(profile).ReloadAsync();
        Assert.Equal(raw, profile.BillingRunsJson); Assert.False(profile.IsDirty);
    }

    internal static LocalRentalBillingProfile CreateProfile(string hiddenSource)
    {
        var profileId = Guid.NewGuid();
        var item = new RentalBillingTemplateItemModel { ItemId = Guid.NewGuid(), DisplayItemName = "privacy item", Quantity = 1,
            UnitPrice = hiddenSource == "item" ? null : 12345m, Amount = hiddenSource == "item" ? null : 12345m };
        var run = new RentalBillingRunModel { RunId = Guid.NewGuid(), RunKey = "20260901-20260930", PeriodLabel = "2026-09",
            ScheduledDate = new(2026, 9, 25), PeriodStartDate = new(2026, 9, 1), PeriodEndDate = new(2026, 9, 30),
            Status = PaymentFlowConstants.BillingStatusInProgress, BilledAmount = hiddenSource == "run" ? null : 12345m,
            SettledAmount = hiddenSource == "run" ? null : 345m, Items = [item] };
        return new LocalRentalBillingProfile { Id = profileId, ProfileKey = profileId.ToString(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            CustomerName = "privacy customer", ItemName = "privacy item", MonthlyAmount = 12345m, AmountsHidden = hiddenSource == "profile",
            BillingDay = 25, BillingCycleMonths = 1, BillingAnchorMonth = 9, BillingStartDate = new(2026, 9, 1),
            BillingTemplateJson = JsonSerializer.Serialize(new[] { item }), BillingRunsJson = JsonSerializer.Serialize(new[] { run }), IsActive = true, IsDirty = false };
    }

    internal static SessionState Session()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username = "read-privacy", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
        return session;
    }
}
