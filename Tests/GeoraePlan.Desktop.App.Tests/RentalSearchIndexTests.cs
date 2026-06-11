using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalSearchIndexTests
{
    [Fact]
    public async Task LocalDbContext_EnsureCreated_CreatesRentalSearchIndexes()
    {
        PrepareAppRoot("georaeplan-rental-search-indexes");

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
                        'IX_RentalBillingProfiles_Search_BusinessNumber',
                        'IX_RentalBillingProfiles_Search_CustomerName',
                        'IX_RentalBillingProfiles_Search_ItemName',
                        'IX_RentalBillingProfiles_Search_Notes',
                        'IX_RentalAssets_Search_CurrentCustomerName',
                        'IX_RentalAssets_Search_CustomerName',
                        'IX_RentalAssets_Search_ItemCategoryName',
                        'IX_RentalAssets_Search_ItemName',
                        'IX_RentalAssets_Search_ManagementNumber',
                        'IX_RentalAssets_Search_MachineNumber',
                        'IX_RentalAssets_Search_InstallLocation',
                        'IX_RentalAssets_Search_InstallSiteName'
                      )
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    "IX_RentalAssets_Search_CurrentCustomerName",
                    "IX_RentalAssets_Search_CustomerName",
                    "IX_RentalAssets_Search_InstallLocation",
                    "IX_RentalAssets_Search_InstallSiteName",
                    "IX_RentalAssets_Search_ItemCategoryName",
                    "IX_RentalAssets_Search_ItemName",
                    "IX_RentalAssets_Search_MachineNumber",
                    "IX_RentalAssets_Search_ManagementNumber",
                    "IX_RentalBillingProfiles_Search_BusinessNumber",
                    "IX_RentalBillingProfiles_Search_CustomerName",
                    "IX_RentalBillingProfiles_Search_ItemName",
                    "IX_RentalBillingProfiles_Search_Notes"
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
    public async Task LocalDbContext_EnsureCreated_CreatesRentalAssetFilterListSortIndexes()
    {
        PrepareAppRoot("georaeplan-rental-asset-filter-indexes");

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
                        'IX_RentalAssets_Filter_ItemCategoryListSort',
                        'IX_RentalAssets_Filter_StatusListSort',
                        'IX_RentalAssets_TenantListSort',
                        'IX_RentalAssets_UnlinkedBillingCandidates'
                      )
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    "IX_RentalAssets_Filter_ItemCategoryListSort",
                    "IX_RentalAssets_Filter_StatusListSort",
                    "IX_RentalAssets_TenantListSort",
                    "IX_RentalAssets_UnlinkedBillingCandidates"
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
    public async Task LocalDbContext_EnsureCreated_CreatesRentalBillingListSortIndexes()
    {
        PrepareAppRoot("georaeplan-rental-billing-list-sort-indexes");

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
                        'IX_RentalBillingProfiles_ListSort',
                        'IX_RentalBillingProfiles_TenantListSort'
                      )
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    "IX_RentalBillingProfiles_ListSort",
                    "IX_RentalBillingProfiles_TenantListSort"
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
    public async Task LocalDbContext_EnsureCreated_CreatesRentalEquipmentReplacementCandidateIndex()
    {
        PrepareAppRoot("georaeplan-rental-equipment-replacement-indexes");

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
                      AND name = 'IX_RentalAssets_ReplacementCandidatePrefilter'
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[] { "IX_RentalAssets_ReplacementCandidatePrefilter" },
                indexNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_EnsureCreated_CreatesRentalReferenceIndexes()
    {
        PrepareAppRoot("georaeplan-rental-reference-indexes");

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
                        'IX_Invoices_RentalProfileReference',
                        'IX_Invoices_RentalRunReference',
                        'IX_Transactions_LinkedInvoiceReference',
                        'IX_Transactions_LinkedRentalBillingProfileId',
                        'IX_Transactions_RentalProfileReference',
                        'IX_Transactions_RentalRunReference'
                      )
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[]
                {
                    "IX_Invoices_RentalProfileReference",
                    "IX_Invoices_RentalRunReference",
                    "IX_Transactions_LinkedInvoiceReference",
                    "IX_Transactions_LinkedRentalBillingProfileId",
                    "IX_Transactions_RentalProfileReference",
                    "IX_Transactions_RentalRunReference"
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
    public async Task LocalDbContext_EnsureCreated_CreatesRentalAssignmentHistoryTimelineIndex()
    {
        PrepareAppRoot("georaeplan-rental-assignment-history-indexes");

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
                      AND name = 'IX_RentalAssetAssignmentHistories_AssetTimeline'
                    ORDER BY name
                    """)
                .ToListAsync();

            Assert.Equal(
                new[] { "IX_RentalAssetAssignmentHistories_AssetTimeline" },
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
