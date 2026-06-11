using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityIndexTests
{
    [Fact]
    public async Task LocalDbContext_EnsureCreated_CreatesIntegrityAggregateIndexes()
    {
        PrepareAppRoot("georaeplan-integrity-aggregate-indexes");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var indexNames = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT name
                    FROM sqlite_master
                    WHERE type = 'index'
                      AND name IN (
                        'IX_InvoiceLines_InvoiceActiveAggregate',
                        'IX_Payments_InvoiceActiveAggregate',
                        'IX_InventoryMovements_ItemActiveWarehouse'
                      )
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    "IX_InventoryMovements_ItemActiveWarehouse",
                    "IX_InvoiceLines_InvoiceActiveAggregate",
                    "IX_Payments_InvoiceActiveAggregate"
                },
                indexNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_EnsureCreated_CreatesIntegritySourcePrefilterIndexes()
    {
        PrepareAppRoot("georaeplan-integrity-source-indexes");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var indexNames = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT name
                    FROM sqlite_master
                    WHERE type = 'index'
                      AND name IN (
                        'IX_Customers_IntegrityOfficeActive',
                        'IX_Customers_IntegrityResponsibleActive',
                        'IX_Invoices_IntegrityResponsibleLatest',
                        'IX_Items_IntegrityOfficeActive',
                        'IX_RentalAssetAssignmentHistories_IntegrityResponsibleActive',
                        'IX_RentalAssets_IntegrityManagementActive',
                        'IX_RentalAssets_IntegrityOfficeActive',
                        'IX_RentalAssets_IntegrityResponsibleActive',
                        'IX_RentalBillingProfiles_IntegrityManagementActive',
                        'IX_RentalBillingProfiles_IntegrityOfficeActive',
                        'IX_RentalBillingProfiles_IntegrityResponsibleActive',
                        'IX_Warehouses_IntegrityOfficeActive'
                      )
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    "IX_Customers_IntegrityOfficeActive",
                    "IX_Customers_IntegrityResponsibleActive",
                    "IX_Invoices_IntegrityResponsibleLatest",
                    "IX_Items_IntegrityOfficeActive",
                    "IX_RentalAssetAssignmentHistories_IntegrityResponsibleActive",
                    "IX_RentalAssets_IntegrityManagementActive",
                    "IX_RentalAssets_IntegrityOfficeActive",
                    "IX_RentalAssets_IntegrityResponsibleActive",
                    "IX_RentalBillingProfiles_IntegrityManagementActive",
                    "IX_RentalBillingProfiles_IntegrityOfficeActive",
                    "IX_RentalBillingProfiles_IntegrityResponsibleActive",
                    "IX_Warehouses_IntegrityOfficeActive"
                },
                indexNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_EnsureCreated_CreatesCustomerSearchIndexes()
    {
        PrepareAppRoot("georaeplan-customer-search-indexes");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var indexNames = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT name
                    FROM sqlite_master
                    WHERE type = 'index'
                      AND name IN (
                        'IX_Customers_Search_BusinessNumber',
                        'IX_Customers_Search_NameMatchKey',
                        'IX_Customers_Search_NameOriginal'
                      )
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    "IX_Customers_Search_BusinessNumber",
                    "IX_Customers_Search_NameMatchKey",
                    "IX_Customers_Search_NameOriginal"
                },
                indexNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
