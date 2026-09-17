using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalItemOwnershipStabilityTests
{
    [Theory]
    [InlineData("USENET", "YEONSU", "USENET_GROUP", true)]
    [InlineData("USENET", "YEONSU", "USENET_GROUP", false)]
    [InlineData("USENET", "USENET", "USENET_GROUP", true)]
    [InlineData("ITWORLD", "ITWORLD", "ITWORLD", true)]
    [InlineData("USENET", "YEONSU", "USENET_GROUP", true, "")]
    [InlineData("USENET", "YEONSU", "USENET_GROUP", true, "YEONSU")]
    public async Task RepairAndStartupBackfill_PreserveOwnerAndItemIdentity(string owner, string responsible, string tenant, bool hasItem, string? itemTenant = null)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var item = CreateItem(owner, itemTenant ?? tenant);
        var asset = CreateAsset(owner, responsible, tenant, hasItem ? item.Id : null);
        if (hasItem) db.Items.Add(item);
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        var service = new RentalStateService(db);
        var session = CreateSession(owner, tenant);
        await service.RepairRentalCatalogLinksAsync([asset.Id], session);
        var firstId = (await db.RentalAssets.AsNoTracking().SingleAsync()).ItemId;
        Assert.NotNull(firstId);
        if (hasItem) Assert.Equal(item.Id, firstId);
        Assert.Equal(owner, (await db.Items.AsNoTracking().SingleAsync()).OfficeCode);
        db.ChangeTracker.Clear();
        var backfill = typeof(LocalDbInitializer).GetMethod("BackfillItemScopeFieldsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)backfill.Invoke(null, [db])!;
        db.ChangeTracker.Clear();
        await service.RepairRentalCatalogLinksAsync([asset.Id], session);
        var finalAsset = await db.RentalAssets.AsNoTracking().SingleAsync();
        Assert.Equal(firstId, finalAsset.ItemId);
        Assert.Equal(owner, finalAsset.OfficeCode);
        Assert.Equal(responsible, finalAsset.ResponsibleOfficeCode);
        Assert.Equal(33000m, finalAsset.MonthlyFee);
        Assert.Single(await db.Items.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(owner, (await db.Items.AsNoTracking().SingleAsync()).OfficeCode);
    }

    [Theory]
    [InlineData("ITWORLD", "ITWORLD", false, true)]
    [InlineData("ALL", "USENET_GROUP", false, true)]
    [InlineData("USENET", "ITWORLD", false, true)]
    [InlineData("ITWORLD", "ITWORLD", true, true)]
    [InlineData("ALL", "USENET_GROUP", true, true)]
    [InlineData("USENET", "ITWORLD", true, true)]
    [InlineData("ITWORLD", "ITWORLD", false, false)]
    [InlineData("ALL", "USENET_GROUP", false, false)]
    [InlineData("USENET", "ITWORLD", false, false)]
    [InlineData("ITWORLD", "ITWORLD", true, false)]
    [InlineData("ALL", "USENET_GROUP", true, false)]
    [InlineData("USENET", "ITWORLD", true, false)]
    public async Task Repair_DoesNotAdoptOrModifyItemOutsideOwnerScope(string itemOffice, string itemTenant, bool autoCreated, bool explicitSelection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var outside = CreateItem(itemOffice, itemTenant);
        if (autoCreated) outside.SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo;
        var asset = CreateAsset("USENET", "YEONSU", "USENET_GROUP", outside.Id);
        db.Items.Add(outside);
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        var original = JsonSerializer.Serialize(await db.Items.AsNoTracking().SingleAsync());
        await new RentalStateService(db).RepairRentalCatalogLinksAsync(explicitSelection ? [asset.Id] : null, CreateSession("USENET", "USENET_GROUP"));
        var saved = await db.RentalAssets.AsNoTracking().SingleAsync();
        Assert.NotEqual(outside.Id, saved.ItemId);
        var linked = await db.Items.AsNoTracking().SingleAsync(i => i.Id == saved.ItemId);
        Assert.Equal("USENET", linked.OfficeCode);
        Assert.Equal("USENET_GROUP", linked.TenantCode);
        Assert.Equal(original, JsonSerializer.Serialize(await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == outside.Id)));
    }

    private static LocalItem CreateItem(string owner, string tenant) => new()
    {
        Id = Guid.NewGuid(), OfficeCode = owner, TenantCode = tenant,
        NameOriginal = "OWNERSHIP-DEVICE", NameMatchKey = "OWNERSHIPDEVICE", ItemKind = ItemKinds.Asset,
        TrackingType = ItemTrackingTypes.Asset, MaterialNumber = "OWNER-001", SerialNumber = "SERIAL-001",
        IsRental = true, IsSale = false, IsDirty = false
    };

    private static LocalRentalAsset CreateAsset(string owner, string responsible, string tenant, Guid? itemId) => new()
    {
        Id = Guid.NewGuid(), ItemId = itemId, OfficeCode = owner, TenantCode = tenant,
        ResponsibleOfficeCode = responsible, ManagementCompanyCode = owner,
        ManagementId = "OWNERSHIP-TEST", ManagementNumber = "OWNER-001", MachineNumber = "SERIAL-001",
        ItemName = "OWNERSHIP-DEVICE", MonthlyFee = 33000m, AssetStatus = "임대", IsDirty = false
    };

    private static SessionState CreateSession(string office, string tenant)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "ownership-test",
            Role = DomainConstants.RoleAdmin, OfficeCode = office, TenantCode = tenant, ScopeType = TenantScopeCatalog.ScopeAdmin });
        return session;
    }
}
