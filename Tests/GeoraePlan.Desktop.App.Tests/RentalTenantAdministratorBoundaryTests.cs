using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalTenantAdministratorBoundaryTests
{
    [Theory]
    [InlineData("USENET", "TenantAll")]
    [InlineData("USENET", "OfficeOnly")]
    [InlineData("ITWORLD", "TenantAll")]
    [InlineData("ITWORLD", "OfficeOnly")]
    public async Task TenantAdmin_PreservesSharedAssetReadButCannotReadBillingOrSaveOtherTenant(string office, string scope)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var session = Session(office, scope);
        var service = new RentalStateService(db);
        var foreignOffice = office == "ITWORLD" ? "USENET" : "ITWORLD";
        Assert.True(service.CanEditAssetScope(office, session));
        Assert.False(service.CanEditAssetScope(foreignOffice, session));
        Assert.Contains(office, service.GetWritableAssetOfficeCodes(session));
        Assert.DoesNotContain(foreignOffice, service.GetWritableAssetOfficeCodes(session));
        var assets = await service.GetAssetRowsAsync(new RentalAssetFilter(), session);
        Assert.Equal(2, assets.Count); // Shared asset browsing is intentional.
        var rows = await service.GetBillingRowsAsync(new RentalBillingFilter { IncludeHistoryRows = false }, session);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(session.TenantCode, row.Source.TenantCode));

        var foreignProfile = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(x => x.TenantCode != session.TenantCode);
        var foreignAsset = await db.RentalAssets.AsNoTracking().SingleAsync(x => x.TenantCode != session.TenantCode);
        var profileBefore = JsonSerializer.Serialize(foreignProfile);
        var assetBefore = JsonSerializer.Serialize(foreignAsset);
        foreignProfile.Notes = "must not save outside tenant";
        foreignAsset.Notes = "must not save outside tenant";
        var profileResult = await service.SaveBillingProfileAsync(foreignProfile, session);
        Assert.False(profileResult.Success);
        Assert.Contains("권한", profileResult.Message);
        var assetResult = await service.SaveAssetAsync(foreignAsset, session);
        Assert.False(assetResult.Success);
        Assert.Contains("권한", assetResult.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(profileBefore, JsonSerializer.Serialize(await db.RentalBillingProfiles.AsNoTracking().SingleAsync(x => x.Id == foreignProfile.Id)));
        Assert.Equal(assetBefore, JsonSerializer.Serialize(await db.RentalAssets.AsNoTracking().SingleAsync(x => x.Id == foreignAsset.Id)));
        Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
    }

    [Fact]
    public async Task GlobalAdmin_RetainsBillingVisibilityAcrossTenants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var rows = await new RentalStateService(db).GetBillingRowsAsync(new RentalBillingFilter { IncludeHistoryRows = false }, Session("USENET", "Admin"));
        Assert.Equal(new[] { "ITWORLD", "USENET_GROUP" }, rows.Select(x => x.Source.TenantCode).Distinct().OrderBy(x => x).ToArray());
    }

    private static SessionState Session(string office, string scope)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "tenant-boundary", Role = "Admin",
            OfficeCode = office, TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(office), ScopeType = scope,
            Permissions = [AppPermissionNames.RentalViewAll, AppPermissionNames.RentalEditAll] });
        return session;
    }

    [Theory]
    [InlineData("USENET", "USENET")]
    [InlineData("ITWORLD", "ITWORLD")]
    [InlineData("YEONSU", "YEONSU")]
    [InlineData("YEONSU", "USENET")]
    public async Task UnlinkedBillingRow_PreservesSourceAssetTenantAndOwnerOffice(string office, string owner)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(office),
            OfficeCode = owner, ResponsibleOfficeCode = office, ManagementCompanyCode = owner, AssetKey = "unlinked-scope",
            ManagementNumber = "unlinked-001", ItemName = "Printer", ItemCategoryName = "복합기",
            CustomerName = "Unlinked scope customer", CurrentCustomerName = "Unlinked scope customer",
            AssetStatus = "임대진행중", BillingEligibilityStatus = "미확인", MonthlyFee = 55000m, IsDirty = false };
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var rows = await new RentalStateService(db).GetBillingRowsAsync(
            new RentalBillingFilter { ExpandCustomerSummaryRows = true, IncludeHistoryRows = false }, Session(office, "TenantAll"));
        var row = Assert.Single(rows, x => !x.HasPersistedProfile && x.SelectionId == asset.Id);
        Assert.Equal(asset.TenantCode, row.Source.TenantCode);
        Assert.Equal(asset.OfficeCode, row.Source.OfficeCode);
        Assert.Equal(asset.ResponsibleOfficeCode, row.Source.ResponsibleOfficeCode);
        Assert.Empty(await db.RentalBillingProfiles.ToListAsync());
        Assert.False((await db.RentalAssets.AsNoTracking().SingleAsync()).IsDirty);
    }

    private static async Task SeedAsync(LocalDbContext db)
    {
        foreach (var office in new[] { "USENET", "ITWORLD" })
        {
            var tenant = TenantScopeCatalog.GetTenantCodeForOffice(office);
            var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), TenantCode = tenant, OfficeCode = office,
                ResponsibleOfficeCode = office, ManagementCompanyCode = office, ProfileKey = office + "-PROFILE",
                CustomerName = office + " customer", ItemName = "Printer", MonthlyAmount = 55000m, BillingTemplateJson = "[]",
                IsActive = true, Revision = 10, IsDirty = false };
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.Add(new LocalRentalAsset { Id = Guid.NewGuid(), TenantCode = tenant, OfficeCode = office,
                ResponsibleOfficeCode = office, ManagementCompanyCode = office, AssetKey = office + "-ASSET",
                ManagementNumber = office + "-001", ItemName = "Printer", ItemCategoryName = "복합기",
                AssetStatus = "임대진행중", BillingEligibilityStatus = "미확인", BillingProfileId = profile.Id,
                MonthlyFee = 55000m, Revision = 10, IsDirty = false });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
