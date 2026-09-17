using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalItemRepairPermissionTests
{
    [Theory]
    [InlineData("USENET", "OfficeOnly", false, true)]
    [InlineData("USENET", "OfficeOnly", true, true)]
    [InlineData("YEONSU", "OfficeOnly", false, false)]
    [InlineData("YEONSU", "OfficeOnly", true, false)]
    [InlineData("YEONSU", "Admin", false, true)]
    [InlineData("YEONSU", "Admin", true, true)]
    [InlineData("ITWORLD", "OfficeOnly", false, true)]
    [InlineData("ITWORLD", "OfficeOnly", true, true)]
    [InlineData("USENET", "OfficeOnly", false, false, false)]
    [InlineData("USENET", "OfficeOnly", true, false, false)]
    [InlineData("YEONSU", "OfficeOnly", false, false, false)]
    [InlineData("YEONSU", "OfficeOnly", true, false, false)]
    public async Task Repair_OnlyCreatesOrChangesItemsThatSessionCanSend(string office, string scope, bool hasItem, bool canWrite, bool isAdmin = true)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var owner = office == "YEONSU" ? "USENET" : office;
        var tenant = office == "ITWORLD" ? "ITWORLD" : "USENET_GROUP";
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "repair-scope", Role = isAdmin ? DomainConstants.RoleAdmin : DomainConstants.RoleUser, OfficeCode = office, TenantCode = tenant, ScopeType = scope, Permissions = [AppPermissionNames.RentalAssetEdit] });
        var item = new LocalItem { Id = Guid.NewGuid(), OfficeCode = owner, TenantCode = tenant, NameOriginal = "REPAIR DEVICE", NameMatchKey = "REPAIRDEVICE", TrackingType = ItemTrackingTypes.Asset, ItemKind = ItemKinds.Asset, MaterialNumber = "REPAIR-001", SerialNumber = "SERIAL-001", IsRental = true, IsSale = false, IsDirty = false, Revision = 100 };
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), OfficeCode = owner, ResponsibleOfficeCode = office, TenantCode = tenant, ManagementCompanyCode = owner, ManagementNumber = item.MaterialNumber, MachineNumber = item.SerialNumber, ItemName = item.NameOriginal, ItemId = hasItem ? item.Id : null, MonthlyFee = 33000m, InstallLocation = "ASSET INSTALL", IsDirty = true };
        if (hasItem) db.Items.Add(item);
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        var original = hasItem ? JsonSerializer.Serialize(await db.Items.AsNoTracking().SingleAsync()) : null;
        db.ChangeTracker.Clear();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await rental.RepairRentalCatalogLinksAsync([asset.Id], session);
            db.ChangeTracker.Clear();
            var saved = await db.RentalAssets.AsNoTracking().SingleAsync();
            Assert.Equal(owner, saved.OfficeCode);
            Assert.Equal(office, saved.ResponsibleOfficeCode);
            Assert.Equal(33000m, saved.MonthlyFee);
            if (canWrite)
            {
                var linked = await db.Items.AsNoTracking().SingleAsync();
                Assert.Equal(linked.Id, saved.ItemId);
                Assert.True(linked.IsDirty);
                Assert.Contains(await local.GetDirtyItemsForSyncAsync(session), row => row.Id == linked.Id);
            }
            else if (hasItem)
            {
                Assert.Equal(item.Id, saved.ItemId);
                Assert.Equal(original, JsonSerializer.Serialize(await db.Items.AsNoTracking().SingleAsync()));
            }
            else
            {
                Assert.Null(saved.ItemId);
                Assert.Empty(await db.Items.ToListAsync());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repair_PreservesOtherOwnerOrphanAndPendingEdit(bool isDirty)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "repair-scope", Role = DomainConstants.RoleAdmin, OfficeCode = "YEONSU", TenantCode = "USENET_GROUP", ScopeType = "OfficeOnly" });
        var orphan = new LocalItem { Id = Guid.NewGuid(), OfficeCode = "USENET", TenantCode = "USENET_GROUP", NameOriginal = "ORPHAN", TrackingType = ItemTrackingTypes.Asset, ItemKind = ItemKinds.Asset, SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo, IsDirty = isDirty, Revision = 100 };
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), OfficeCode = "USENET", ResponsibleOfficeCode = "YEONSU", TenantCode = "USENET_GROUP", ManagementCompanyCode = "USENET", ManagementNumber = "DIFFERENT-001", ItemName = "OTHER DEVICE", MonthlyFee = 33000m, IsDirty = true };
        db.Items.Add(orphan);db.RentalAssets.Add(asset);await db.SaveChangesAsync();
        var original = JsonSerializer.Serialize(await db.Items.AsNoTracking().SingleAsync());
        await new RentalStateService(db).RepairRentalCatalogLinksAsync([asset.Id], session);
        db.ChangeTracker.Clear();
        Assert.Equal(original, JsonSerializer.Serialize(await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync()));
    }
}
