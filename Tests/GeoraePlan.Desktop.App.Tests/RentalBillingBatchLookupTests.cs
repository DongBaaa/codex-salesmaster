using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingBatchLookupTests
{
    [Fact]
    public async Task GetBillingRowsAsync_BatchesLinkedAssetLookupsBeyondSqliteParameterWindow()
    {
        PrepareAppRoot("georaeplan-rental-billing-linked-asset-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int profileCount = 650;
            var now = DateTime.UtcNow;
            for (var index = 0; index < profileCount; index++)
            {
                var profileId = Guid.Parse($"20000000-0000-0000-0000-{index + 1:000000000000}");
                var assetId = Guid.Parse($"30000000-0000-0000-0000-{index + 1:000000000000}");
                db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = $"BATCH-PROFILE-{index:D4}",
                    CustomerName = $"Customer {index:D4}",
                    ItemName = "Printer",
                    BillingType = "Bundle",
                    BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    MonthlyAmount = 100_000m + index,
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = assetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = $"BATCH-ASSET-{index:D4}",
                    ManagementId = $"BATCH-ASSET-{index:D4}",
                    ManagementNumber = $"BATCH-ASSET-{index:D4}",
                    CustomerName = $"Customer {index:D4}",
                    CurrentCustomerName = $"Customer {index:D4}",
                    InstallSiteName = $"Site {index:D4}",
                    InstallLocation = $"Site {index:D4}",
                    ItemName = "Printer",
                    MachineNumber = $"SN-{index:D4}",
                    MonthlyFee = 100_000m + index,
                    AssetStatus = "Rental",
                    BillingEligibilityStatus = "Billable",
                    BillingProfileId = profileId,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = true,
                    IncludeHistoryRows = false,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            Assert.Equal(profileCount, rows.Count);
            Assert.Equal(profileCount, rows.Sum(row => row.AssetCount));
            Assert.All(rows, row => Assert.True(row.HasPersistedProfile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_BatchesLinkedCustomerNameLookupsBeyondBatchWindow()
    {
        PrepareAppRoot("georaeplan-rental-billing-linked-customer-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int profileCount = 650;
            var now = DateTime.UtcNow;
            for (var index = 0; index < profileCount; index++)
            {
                var customerId = Guid.Parse($"41000000-0000-0000-0000-{index + 1:000000000000}");
                var profileId = Guid.Parse($"42000000-0000-0000-0000-{index + 1:000000000000}");
                db.Customers.Add(CreateCustomer(customerId, $"Linked Customer {index:D4}", now));
                db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
                {
                    Id = profileId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = $"CUSTOMER-BATCH-PROFILE-{index:D4}",
                    CustomerName = string.Empty,
                    ItemName = "Printer",
                    BillingType = "Bundle",
                    BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    MonthlyAmount = 100_000m + index,
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    ExpandCustomerSummaryRows = true,
                    IncludeHistoryRows = false,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            Assert.Equal(profileCount, rows.Count);
            Assert.Contains(rows, row => row.CustomerDisplayName == "Linked Customer 0000");
            Assert.Contains(rows, row => row.CustomerDisplayName == "Linked Customer 0649");
            Assert.All(rows, row => Assert.StartsWith("Linked Customer ", row.CustomerDisplayName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_BatchesUnlinkedAssetCustomerLookupsBeyondBatchWindow()
    {
        PrepareAppRoot("georaeplan-rental-billing-unlinked-customer-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int assetCount = 650;
            var now = DateTime.UtcNow;
            for (var index = 0; index < assetCount; index++)
            {
                var customerId = Guid.Parse($"51000000-0000-0000-0000-{index + 1:000000000000}");
                db.Customers.Add(CreateCustomer(customerId, $"Unlinked Linked Customer {index:D4}", now));
                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = Guid.Parse($"52000000-0000-0000-0000-{index + 1:000000000000}"),
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = $"UNLINKED-CUSTOMER-BATCH-ASSET-{index:D4}",
                    ManagementId = $"UNLINKED-CUSTOMER-BATCH-ASSET-{index:D4}",
                    ManagementNumber = $"UNLINKED-CUSTOMER-BATCH-ASSET-{index:D4}",
                    CustomerName = $"Fallback Customer {index:D4}",
                    CurrentCustomerName = $"Fallback Customer {index:D4}",
                    InstallSiteName = $"Site {index:D4}",
                    InstallLocation = $"Site {index:D4}",
                    ItemName = "Printer",
                    MachineNumber = $"UNLINKED-SN-{index:D4}",
                    MonthlyFee = 100_000m + index,
                    AssetStatus = "Rental",
                    BillingEligibilityStatus = "Billable",
                    BillingProfileId = null,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    Status = "청구설정 필요",
                    ExpandCustomerSummaryRows = true,
                    IncludeHistoryRows = false,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            Assert.Equal(assetCount, rows.Count);
            Assert.Contains(rows, row => row.CustomerDisplayName == "Unlinked Linked Customer 0000");
            Assert.Contains(rows, row => row.CustomerDisplayName == "Unlinked Linked Customer 0649");
            Assert.All(rows, row => Assert.StartsWith("Unlinked Linked Customer ", row.CustomerDisplayName));
            Assert.All(rows, row => Assert.True(row.RequiresBillingProfileCreation));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalCustomer CreateCustomer(Guid customerId, string name, DateTime now)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal),
            TradeType = CustomerTradeTypes.Sales,
            BusinessNumber = string.Empty,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
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
}
