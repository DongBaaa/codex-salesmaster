using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerRentalLinkSyncTests
{
    [Fact]
    public async Task UpsertCustomerAsync_PropagatesResponsibleOfficeToLinkedRentalProfilesAssetsAndCurrentHistory()
    {
        PrepareAppRoot("georaeplan-customer-rental-office-sync");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("e1000000-1111-4444-8888-000000000001");
            var profileId = Guid.Parse("e1000000-1111-4444-8888-0000000000b1");
            var assetId = Guid.Parse("e1000000-1111-4444-8888-0000000000a1");
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Office Sync Customer",
                NameMatchKey = "office sync customer",
                BusinessNumber = "123-45-67890",
                Email = "old@example.com",
                IsDirty = false
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "Office Sync Customer",
                BusinessNumber = "123-45-67890",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                BillingProfileId = profileId,
                CustomerName = "Office Sync Customer",
                CurrentCustomerName = "Office Sync Customer",
                AssetKey = "OFFICE-SYNC-ASSET",
                ItemName = "IMC2010",
                MonthlyFee = 100_000m,
                IsDirty = false
            });
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = Guid.Parse("e1000000-1111-4444-8888-0000000000c1"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetId = assetId,
                BillingProfileId = profileId,
                CustomerId = customerId,
                CustomerName = "Office Sync Customer",
                IsCurrent = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var storedCustomer = await db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            var changedCustomer = new LocalCustomer
            {
                Id = storedCustomer.Id,
                CreatedAtUtc = storedCustomer.CreatedAtUtc,
                UpdatedAtUtc = storedCustomer.UpdatedAtUtc,
                Revision = storedCustomer.Revision,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = storedCustomer.NameOriginal,
                NameMatchKey = storedCustomer.NameMatchKey,
                BusinessNumber = storedCustomer.BusinessNumber,
                Email = "new@example.com",
                TradeType = storedCustomer.TradeType
            };

            var result = await new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession())
                .UpsertCustomerAsync(changedCustomer, CreateAdminSession());

            Assert.True(result.Success, result.Message);

            var profile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, profile.ResponsibleOfficeCode);
            Assert.Equal("new@example.com", profile.Email);
            Assert.True(profile.IsDirty);

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, asset.ResponsibleOfficeCode);
            Assert.True(asset.IsDirty);

            var history = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(current => current.AssetId == assetId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, history.ResponsibleOfficeCode);
            Assert.True(history.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
