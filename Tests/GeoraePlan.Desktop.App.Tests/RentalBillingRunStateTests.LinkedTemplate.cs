using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class RentalBillingRunStateTests
{
    [Theory]
    [InlineData("single", true)]
    [InlineData("legacy", true)]
    [InlineData("planned", true)]
    [InlineData("multiple", false)]
    [InlineData("foreign-tenant", false)]
    [InlineData("excluded", false)]
    [InlineData("frozen", false)]
    [InlineData("single", true, "ITWORLD")]
    [InlineData("single", true, "YEONSU")]
    public async Task StartBilling_UsesOnlyAlreadyLinkedAssetsForUnambiguousTemplate(string scenario, bool succeeds, string office = "USENET")
    {
        PrepareAppRoot("georaeplan-linked-template-" + scenario);
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            const string customerName = "Linked template customer";
            var tenant = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup;
            var customer = CreateCustomer(customerId, customerName);
            customer.TenantCode = tenant;
            customer.OfficeCode = customer.ResponsibleOfficeCode = office;
            db.Customers.Add(customer);
            var asset = CreateRentalAsset(assetId, customerName, profileId);
            asset.CustomerId = customerId;
            asset.TenantCode = tenant;
            asset.OfficeCode = asset.ResponsibleOfficeCode = asset.ManagementCompanyCode = office;
            if (scenario == "foreign-tenant") asset.TenantCode = TenantScopeCatalog.Itworld;
            if (scenario == "excluded") asset.BillingEligibilityStatus = "청구제외";
            var unlinked = CreateRentalAsset(Guid.NewGuid(), customerName, profileId);
            unlinked.TenantCode = tenant;
            unlinked.OfficeCode = unlinked.ResponsibleOfficeCode = unlinked.ManagementCompanyCode = office;
            unlinked.CustomerId = customerId;
            unlinked.BillingProfileId = null;
            db.RentalAssets.AddRange(asset, unlinked);
            var profile = CreateBillingProfile(profileId, assetId, customerName, customerId);
            profile.TenantCode = tenant;
            profile.OfficeCode = profile.ResponsibleOfficeCode = profile.ManagementCompanyCode = office;
            var item = Assert.Single(DeserializeTemplateItems(profile.BillingTemplateJson));
            item.IncludedAssetIds = [];
            item.RepresentativeAssetId = null;
            var items = new List<RentalBillingTemplateItemModel> { item };
            if (scenario == "multiple") items.Add(new() { DisplayItemName = "Second line", BillingLineMode = "묶음", Quantity = 1m, UnitPrice = 20_000m, Amount = 20_000m });
            profile.BillingTemplateJson = scenario == "legacy" ? "[]" : JsonSerializer.Serialize(items);
            if (scenario is "planned" or "frozen")
                profile.BillingRunsJson = JsonSerializer.Serialize(new[] { new RentalBillingRunModel
                {
                    RunId = SyncIdentityGenerator.CreateRentalBillingRunId(profileId, "20260501-20260531"),
                    RunKey = "20260501-20260531", PeriodStartDate = new(2026, 5, 1), PeriodEndDate = new(2026, 5, 31),
                    ScheduledDate = new(2026, 5, 25), CycleMonths = 1, PeriodLabel = "2026-05",
                    Status = scenario == "planned" ? PaymentFlowConstants.BillingStatusPlanned : PaymentFlowConstants.BillingStatusInProgress,
                    BilledAmount = 100_000m, Items = items
                }});
            var originalTemplate = profile.BillingTemplateJson;
            var originalRuns = profile.BillingRunsJson;
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto { Username = "admin", Role = DomainConstants.RoleAdmin,
                TenantCode = tenant, OfficeCode = office, ScopeType = TenantScopeCatalog.ScopeAdmin });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var service = new RentalStateService(db, local);
            var result = await service.StartBillingAsync(profileId, new(2026, 5, 25), session);
            Assert.True(result.Success == succeeds, result.Message);
            var stored = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profileId);
            if (succeeds)
            {
                var invoice = Assert.Single(await db.Invoices.AsNoTracking().Where(i => !i.IsDeleted).ToListAsync());
                Assert.Equal(100_000m, invoice.TotalAmount);
                Assert.Equal(profileId, invoice.LinkedRentalBillingProfileId);
                Assert.Equal(new[] { assetId }, Assert.Single(DeserializeTemplateItems(stored.BillingTemplateJson)).IncludedAssetIds);
                Assert.Equal(new[] { assetId }, Assert.Single(Assert.Single(DeserializeRuns(stored.BillingRunsJson)).Items).IncludedAssetIds);
            }
            else
            {
                Assert.Empty(await db.Invoices.AsNoTracking().ToListAsync());
                Assert.Equal(originalTemplate, stored.BillingTemplateJson);
                Assert.Equal(originalRuns, stored.BillingRunsJson);
            }
            Assert.Null((await db.RentalAssets.AsNoTracking().SingleAsync(a => a.Id == unlinked.Id)).BillingProfileId);
        }
        finally { SqliteConnection.ClearAllPools(); }
    }
}
