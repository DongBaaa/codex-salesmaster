using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
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
    public async Task GetAssetRowsAsync_UsesBoundedLinkedCustomerMatchKeyPrefixMatches()
    {
        PrepareAppRoot("georaeplan-rental-asset-linked-customer-matchkey-search");

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
                new RentalAssetFilter { SearchText = "LinkedAlpha" },
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
    public async Task GetAssetRowsAsync_UsesBoundedLinkedCustomerContainsMatches()
    {
        PrepareAppRoot("georaeplan-rental-asset-linked-customer-contains-search");

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
                new RentalAssetFilter { SearchText = "Alpha Customer" },
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
    public async Task GetAssetRowsAsync_OfficeUserKeepsSharedAssetViewAcrossOffices()
    {
        PrepareAppRoot("georaeplan-rental-asset-shared-office-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetAsset = CreateRentalAsset(1, "USENET Customer");
            usenetAsset.OfficeCode = OfficeCodeCatalog.Usenet;
            usenetAsset.ResponsibleOfficeCode = OfficeCodeCatalog.Usenet;
            usenetAsset.ManagementCompanyCode = OfficeCodeCatalog.Usenet;

            var itworldAsset = CreateRentalAsset(2, "ITWORLD Customer");
            itworldAsset.OfficeCode = OfficeCodeCatalog.Itworld;
            itworldAsset.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
            itworldAsset.ManagementCompanyCode = OfficeCodeCatalog.Itworld;

            db.RentalAssets.AddRange(usenetAsset, itworldAsset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetAssetRowsAsync(
                new RentalAssetFilter { MaxResults = 100 },
                CreateOfficeOnlySession(OfficeCodeCatalog.Usenet));

            Assert.Contains(rows, row => row.Source.Id == usenetAsset.Id);
            Assert.Contains(rows, row => row.Source.Id == itworldAsset.Id);
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

    [Fact]
    public async Task GetAssetRowsAsync_SearchContainsFallbackFindsMiddleMatch()
    {
        PrepareAppRoot("georaeplan-rental-asset-search-contains-fallback");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var asset = CreateRentalAsset(1, "Alpha Customer");
            asset.ItemName = "Rental Special Copier";
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "Special",
                    MaxResults = RentalStateService.AssetSearchResultLimit
                },
                CreateAdminSession());

            var row = Assert.Single(rows);
            Assert.Equal(asset.Id, row.Source.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetAssetRowsAsync_SearchFindsInstallSiteNamePrefix()
    {
        PrepareAppRoot("georaeplan-rental-asset-search-install-site");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var asset = CreateRentalAsset(2001, "Install Site Customer");
            asset.InstallLocation = string.Empty;
            asset.InstallSiteName = "Install Site Search Tower";
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "Install Site Search",
                    MaxResults = RentalStateService.AssetSearchResultLimit
                },
                CreateAdminSession());

            var row = Assert.Single(rows);
            Assert.Equal(asset.Id, row.Source.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetAssetRowsAsync_SearchFindsManagementNumberAndMachinePrefixes()
    {
        PrepareAppRoot("georaeplan-rental-asset-search-split-fields");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var managementAsset = CreateRentalAsset(3001, "ZZZ Management Search Customer");
            managementAsset.ManagementNumber = "ASSET-SPLIT-MN-001";
            var machineAsset = CreateRentalAsset(3002, "ZZZ Machine Search Customer");
            machineAsset.MachineNumber = "ASSET-SPLIT-SN-001";

            db.RentalAssets.AddRange(managementAsset, machineAsset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var managementRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "ASSET-SPLIT-MN",
                    MaxResults = RentalStateService.AssetSearchResultLimit
                },
                CreateAdminSession());

            var managementRow = Assert.Single(managementRows);
            Assert.Equal(managementAsset.Id, managementRow.Source.Id);

            var machineRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "ASSET-SPLIT-SN",
                    MaxResults = RentalStateService.AssetSearchResultLimit
                },
                CreateAdminSession());

            var machineRow = Assert.Single(machineRows);
            Assert.Equal(machineAsset.Id, machineRow.Source.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetAssetRowsAsync_SearchKeepsPinnedAssetOutsideSearchWindow()
    {
        PrepareAppRoot("georaeplan-rental-asset-search-pinned-window");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var limit = RentalStateService.AssetSearchResultLimit;
            for (var index = 0; index < limit; index++)
                db.RentalAssets.Add(CreateRentalAsset(index, $"Search Customer {index:D4}"));

            var pinnedAsset = CreateRentalAsset(limit + 1, "Search Customer ZZZ");
            db.RentalAssets.Add(pinnedAsset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "Search",
                    MaxResults = limit,
                    PinnedAssetId = pinnedAsset.Id
                },
                CreateAdminSession());

            Assert.Equal(limit, rows.Count);
            Assert.Contains(rows, row => row.Source.Id == pinnedAsset.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task GetAssetRowsAsync_SearchPrioritizesDirectAssetMatchesBeforeLinkedCustomerFallback()
    {
        PrepareAppRoot("georaeplan-rental-asset-search-direct-before-linked");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var limit = RentalStateService.AssetSearchResultLimit;
            for (var index = 0; index < limit; index++)
                db.RentalAssets.Add(CreateRentalAsset(index, $"Direct Search Customer {index:D4}"));

            var linkedCustomerId = Guid.NewGuid();
            var linkedAssetId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(linkedCustomerId, "Direct Search Linked Customer"));
            db.RentalAssets.Add(CreateLinkedCustomerAsset(linkedAssetId, linkedCustomerId));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "Direct Search",
                    MaxResults = limit
                },
                CreateAdminSession());

            Assert.Equal(limit, rows.Count);
            Assert.DoesNotContain(rows, row => row.Source.Id == linkedAssetId);
            Assert.All(rows, row => Assert.Contains("Direct Search", row.CurrentCustomerName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void SortAssetViewRowsForDisplay_OrdersByCustomerManagementNumberAndId()
    {
        var alphaSameLowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var alphaSecondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var alphaSameHighId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var betaId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var rows = new List<RentalAssetViewRow>
        {
            CreateSortableAssetRow(betaId, "Beta", "MN-001"),
            CreateSortableAssetRow(alphaSameHighId, "Alpha", "MN-001"),
            CreateSortableAssetRow(alphaSecondId, "Alpha", "MN-002"),
            CreateSortableAssetRow(alphaSameLowId, "alpha", "MN-001")
        };

        var sorted = InvokeSortAssetViewRowsForDisplay(rows);

        Assert.Same(rows, sorted);
        Assert.Equal(
            new[] { alphaSameLowId, alphaSameHighId, alphaSecondId, betaId },
            sorted.Select(row => row.Source.Id).ToArray());
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
        var resolvedCustomerName = customerName ?? $"검색고객 {index:D4}";
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
            CustomerName = resolvedCustomerName,
            CurrentCustomerName = resolvedCustomerName,
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

    private static RentalAssetViewRow CreateSortableAssetRow(
        Guid assetId,
        string customerName,
        string managementNumber)
        => new()
        {
            CurrentCustomerName = customerName,
            Source = new LocalRentalAsset
            {
                Id = assetId,
                ManagementNumber = managementNumber
            }
        };

    private static List<RentalAssetViewRow> InvokeSortAssetViewRowsForDisplay(List<RentalAssetViewRow> rows)
    {
        var method = typeof(RentalStateService).GetMethod(
            "SortAssetViewRowsForDisplay",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { rows });
        return Assert.IsType<List<RentalAssetViewRow>>(result);
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

    private static SessionState CreateOfficeOnlySession(string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"user-{officeCode}",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(TenantScopeCatalog.UsenetGroup, officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }
}
