using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityIssueServicePerformanceTests
{
    [Fact]
    public async Task ScanAsync_UsesPreGroupedLinkedAssetsForManyRentalProfiles()
    {
        PrepareAppRoot("georaeplan-integrity-linked-assets-grouping");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int profileCount = 650;
            for (var index = 0; index < profileCount; index++)
            {
                var profileId = Guid.NewGuid();
                db.RentalBillingProfiles.Add(CreateProfile(profileId, index));
                db.RentalAssets.Add(CreateLinkedAsset(profileId, index));
            }

            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_UsesItemNameLookupForManyInventoryReferenceIssues()
    {
        PrepareAppRoot("georaeplan-integrity-inventory-item-lookup");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int itemCount = 650;
            for (var index = 0; index < itemCount; index++)
            {
                var item = CreateInventoryItem(index);
                db.Items.Add(item);
                db.ItemWarehouseStocks.Add(CreateStockWithMissingWarehouse(item.Id));
                db.InventoryMovements.Add(CreateMovementWithMissingWarehouse(item.Id));
            }

            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());
            var missingWarehouseIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing)
                .ToList();

            Assert.Equal(itemCount * 2, missingWarehouseIssues.Count);
            Assert.All(missingWarehouseIssues, issue => Assert.StartsWith("Inventory Item ", issue.ItemName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_PrefiltersInvoiceSourceLoadByOperationalScope()
    {
        PrepareAppRoot("georaeplan-integrity-invoice-scope-prefilter");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int outsideOfficeInvoiceCount = 650;
            for (var index = 0; index < outsideOfficeInvoiceCount; index++)
                db.Invoices.Add(CreateMismatchedInvoice(index, OfficeCodeCatalog.Usenet));
            db.Invoices.Add(CreateMismatchedInvoice(outsideOfficeInvoiceCount + 1, OfficeCodeCatalog.Yeonsu));
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateYeonsuAdminSession());
            var invoiceIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.InvoiceAmountMismatch)
                .ToList();

            var issue = Assert.Single(invoiceIssues);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, issue.OfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalBillingProfile CreateProfile(Guid profileId, int index)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"INTEGRITY-PROFILE-{index:D4}",
            CustomerName = $"Integrity Customer {index:D4}",
            ItemName = $"Integrity Copier {index:D4}",
            InstallSiteName = "Main Office",
            MonthlyAmount = 100_000m,
            BillingTemplateJson = "[]",
            BillingRunsJson = "[]",
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateLinkedAsset(Guid profileId, int index)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = profileId,
            ManagementId = $"INTEGRITY-ASSET-{index:D4}",
            ManagementNumber = $"INT-{index:D4}",
            AssetKey = $"INTEGRITY-ASSET-{Guid.NewGuid():N}",
            CustomerName = $"Integrity Customer {index:D4}",
            CurrentCustomerName = $"Integrity Customer {index:D4}",
            ItemCategoryName = "Copier",
            ItemName = $"Integrity Copier {index:D4}",
            MachineNumber = $"INT-SN-{index:D4}",
            InstallSiteName = "Main Office",
            InstallLocation = "Main Office",
            AssetStatus = "Rental",
            BillingEligibilityStatus = "Billable",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalItem CreateInventoryItem(int index)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = $"Inventory Item {index:D4}",
            NameMatchKey = $"INVENTORYITEM{index:D4}",
            SpecificationOriginal = $"Spec {index:D4}",
            SpecificationMatchKey = $"SPEC{index:D4}",
            CategoryName = "Inventory",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 1m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalItemWarehouseStock CreateStockWithMissingWarehouse(Guid itemId)
        => new()
        {
            ItemId = itemId,
            WarehouseCode = "MISSING-WAREHOUSE",
            Quantity = 1m,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalInventoryMovement CreateMovementWithMissingWarehouse(Guid itemId)
        => new()
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            WarehouseCode = "MISSING-WAREHOUSE",
            MovementType = "조정",
            QuantityDelta = 1m,
            OccurredDate = DateOnly.FromDateTime(DateTime.Today),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

    private static LocalInvoice CreateMismatchedInvoice(int index, string officeCode)
    {
        var invoiceId = Guid.NewGuid();
        return new LocalInvoice
        {
            Id = invoiceId,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            CustomerId = Guid.NewGuid(),
            InvoiceNumber = $"INV-SCOPE-{officeCode}-{index:D4}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            SupplyAmount = 200m,
            VatAmount = 0m,
            TotalAmount = 200m,
            VatMode = InvoiceVatModes.Included,
            SourceWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(officeCode),
            IsLatestVersion = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Lines =
            [
                new LocalInvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemNameOriginal = $"Scope Item {index:D4}",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m,
                    IsDeleted = false
                }
            ]
        };
    }

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

    private static SessionState CreateYeonsuAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "yeonsu-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
