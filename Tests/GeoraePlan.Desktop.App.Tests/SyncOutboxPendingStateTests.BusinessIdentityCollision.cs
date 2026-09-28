using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("customer", false, false)]
    [InlineData("customer", false, true)]
    [InlineData("customer", true, false)]
    [InlineData("customer", true, true)]
    [InlineData("asset", false, false)]
    [InlineData("asset", false, true)]
    [InlineData("asset", true, false)]
    [InlineData("asset", true, true)]
    [InlineData("profile", false, false)]
    [InlineData("profile", false, true)]
    [InlineData("profile", true, false)]
    [InlineData("profile", true, true)]
    public async Task CrossBusinessIdentityPull_PreservesWholeCacheAndCursor(
        string kind, bool localDeleted, bool incomingDeleted)
    {
        PrepareAppRoot("business-identity-collision");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var id = Guid.NewGuid();
            AddBusinessIdentityFixture(db, kind, id, localDeleted);
            db.Items.Add(new LocalItem { Id = Guid.NewGuid(), NameOriginal = "unsent item", IsDirty = true });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(), EntityName = "LocalInvoice", EntityId = Guid.NewGuid(),
                MutationId = "preserve-pending", Status = "Failed", TenantCode = "USENET_GROUP",
                OfficeCode = "USENET", ResponsibleOfficeCode = "USENET", BusinessDatabaseName = "USENET"
            });
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var before = SnapshotBusinessIdentityTables(db);
            var pull = BusinessIdentityPull(kind, id, "ITWORLD", "ITWORLD", incomingDeleted);
            pull.Units.Add(new UnitDto { Id = Guid.NewGuid(), Name = "must roll back", Revision = 9 });
            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() => InvokeApplyPullAndUpdateRevisionAsync(sync, pull, 5));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);
            db.ChangeTracker.Clear();
            Assert.Equal(before, SnapshotBusinessIdentityTables(db));
            // Raw snapshots include deleted rows and their original revisions.
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }

    [Theory]
    [InlineData("customer", "USENET")]
    [InlineData("customer", "YEONSU")]
    [InlineData("asset", "USENET")]
    [InlineData("asset", "YEONSU")]
    [InlineData("profile", "USENET")]
    [InlineData("profile", "YEONSU")]
    public async Task SameBusinessIdentityPull_AllowsUsenetAndYeonsu(string kind, string office)
    {
        PrepareAppRoot("business-identity-same-database");
        try
        {
            await using var db = new LocalDbContext(); await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var id = Guid.NewGuid(); var existing = AddBusinessIdentityFixture(db, kind, id, false);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeApplyPullAndUpdateRevisionAsync(sync, BusinessIdentityPull(kind, id, "USENET_GROUP", office, false), 5);
            db.ChangeTracker.Clear();
            var row = (LocalSyncEntity)(await db.FindAsync(existing.GetType(), id))!;
            Assert.Equal(9, row.Revision); Assert.False(row.IsDirty);
            Assert.Equal("USENET_GROUP", row.GetType().GetProperty("TenantCode")!.GetValue(row));
            // Existing mappings retain the owning office; YEONSU still belongs
            // to the same physical business database and must not be blocked.
            Assert.Equal("USENET", row.GetType().GetProperty("OfficeCode")!.GetValue(row));
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }

    private static LocalSyncEntity AddBusinessIdentityFixture(LocalDbContext db, string kind, Guid id, bool deleted)
    {
        LocalSyncEntity row = kind switch
        {
            "customer" => new LocalCustomer { Id = id, TenantCode = "USENET_GROUP", OfficeCode = "USENET", ResponsibleOfficeCode = "USENET", NameOriginal = "original customer", NameMatchKey = "ORIGINAL" },
            "asset" => new LocalRentalAsset { Id = id, TenantCode = "USENET_GROUP", OfficeCode = "USENET", ResponsibleOfficeCode = "USENET", AssetKey = id.ToString(), ManagementNumber = "1601-022", ManagementId = "560" },
            _ => new LocalRentalBillingProfile { Id = id, TenantCode = "USENET_GROUP", OfficeCode = "USENET", ResponsibleOfficeCode = "USENET", ProfileKey = id.ToString(), CustomerName = "original billing", MonthlyAmount = 55000 }
        };
        row.IsDeleted = deleted; row.IsDirty = false; row.Revision = 5;
        db.Add(row); return row;
    }

    private static SyncPullResponse BusinessIdentityPull(string kind, Guid id, string tenant, string office, bool deleted)
    {
        var pull = new SyncPullResponse { CurrentServerRevision = 9 };
        if (kind == "customer") pull.Customers.Add(new CustomerDto { Id = id, TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office, NameOriginal = "incoming customer", NameMatchKey = "INCOMING", Revision = 9, IsDeleted = deleted });
        else if (kind == "asset") pull.RentalAssets.Add(new RentalAssetDto { Id = id, TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office, ManagementCompanyCode = office, AssetKey = id.ToString(), ManagementNumber = "1601-022", ManagementId = "560", Revision = 9, IsDeleted = deleted });
        else pull.RentalBillingProfiles.Add(new RentalBillingProfileDto { Id = id, TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office, ManagementCompanyCode = office, ProfileKey = id.ToString(), CustomerName = "incoming billing", MonthlyAmount = 66000, Revision = 9, IsDeleted = deleted });
        return pull;
    }

    private static string SnapshotBusinessIdentityTables(LocalDbContext db)
    {
        var tables = new Dictionary<string, List<object?[]>>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        foreach (var table in new[] { "Customers", "RentalAssets", "RentalBillingProfiles", "Items", "Units", "Settings", "SyncOutboxEntries" })
        {
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            using var reader = command.ExecuteReader(); var rows = new List<object?[]>();
            while (reader.Read()) { var row = new object?[reader.FieldCount]; for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i); rows.Add(row); }
            tables[table] = rows;
        }
        return JsonSerializer.Serialize(tables);
    }
}
