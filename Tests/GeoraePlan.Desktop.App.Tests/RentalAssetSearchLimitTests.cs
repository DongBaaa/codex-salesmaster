using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetSearchLimitTests
{
    [Fact]
    public async Task GetAssetRowsAsync_CapsSearchResultsButKeepsUnfilteredListLimit()
    {
        PrepareAppRoot("georaeplan-rental-asset-search-limit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var totalAssets = RentalStateService.AssetSearchResultLimit + 25;
            for (var index = 0; index < totalAssets; index++)
                db.RentalAssets.Add(CreateRentalAsset(index));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var unfilteredRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter { MaxResults = RentalStateService.AssetListResultLimit },
                session);
            Assert.Equal(totalAssets, unfilteredRows.Count);

            var searchRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "검색고객",
                    MaxResults = RentalStateService.AssetListResultLimit
                },
                session);
            Assert.Equal(RentalStateService.AssetSearchResultLimit, searchRows.Count);
            Assert.All(searchRows, row => Assert.Contains("검색고객", row.CurrentCustomerName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetAssetRowsAsync_UsesBoundedLinkedCustomerPrefixMatches()
    {
        PrepareAppRoot("georaeplan-rental-asset-linked-customer-search");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, "Linked Alpha Customer"));
            db.RentalAssets.Add(CreateLinkedCustomerAsset(assetId, customerId));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetAssetRowsAsync(
                new RentalAssetFilter { SearchText = "Linked Alpha" },
                CreateAdminSession());

            var row = Assert.Single(rows, current => current.Source.Id == assetId);
            Assert.Equal("Linked Alpha Customer", row.CurrentCustomerName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetAssetRowsAsync_PinnedAssetOutsideDefaultSortWindowIsStillIncluded()
    {
        PrepareAppRoot("georaeplan-rental-asset-pinned-window");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var limit = RentalStateService.AssetListResultLimit;
            for (var index = 0; index < limit; index++)
                db.RentalAssets.Add(CreateRentalAsset(index, $"A Customer {index:D4}"));

            var pinnedAsset = CreateRentalAsset(limit + 1, "ZZZ Pinned Customer");
            db.RentalAssets.Add(pinnedAsset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var defaultRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter { MaxResults = limit },
                session);
            Assert.DoesNotContain(defaultRows, row => row.Source.Id == pinnedAsset.Id);

            var pinnedRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    MaxResults = limit,
                    PinnedAssetId = pinnedAsset.Id
                },
                session);
            Assert.Contains(pinnedRows, row => row.Source.Id == pinnedAsset.Id);
            Assert.Equal(limit, pinnedRows.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static LocalRentalAsset CreateRentalAsset(int index, string? customerName = null)
    {
        var assetId = Guid.NewGuid();
        return new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{index:D4}",
            ManagementNumber = $"MN-{index:D4}",
            AssetKey = $"AK-{assetId:N}",
            CustomerName = $"검색고객 {index:D4}",
            CurrentCustomerName = $"검색고객 {index:D4}",
            ItemCategoryName = "복합기",
            ItemName = $"렌탈 복합기 {index:D4}",
            MachineNumber = $"SN-{index:D4}",
            InstallSiteName = "본점",
            InstallLocation = "본점",
            AssetStatus = "렌탈중",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static LocalCustomer CreateCustomer(Guid customerId, string customerName)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = customerName,
            NameMatchKey = customerName.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
            TradeType = CustomerTradeTypes.Sales,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateLinkedCustomerAsset(Guid assetId, Guid customerId)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{assetId:N}",
            ManagementNumber = "MN-LINKED-001",
            AssetKey = $"AK-{assetId:N}",
            CustomerId = customerId,
            CustomerName = string.Empty,
            CurrentCustomerName = string.Empty,
            ItemCategoryName = "Copier",
            ItemName = "Rental Copier",
            MachineNumber = "SN-LINKED-001",
            InstallSiteName = "HQ",
            InstallLocation = "HQ",
            AssetStatus = "렌탈중",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

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
