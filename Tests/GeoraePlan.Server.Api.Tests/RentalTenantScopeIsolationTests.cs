using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RentalTenantScopeIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public RentalTenantScopeIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task RentalScopes_TenantAdminExcludesOtherTenant_WhileGlobalAdminCanReadBothTenants()
    {
        var globalAdmin = CreateAdmin(TenantScopeCatalog.ScopeAdmin);
        await using (var seedDb = CreateDbContext(globalAdmin))
        {
            var usenetProfileId = Guid.NewGuid();
            var itworldProfileId = Guid.NewGuid();
            var usenetAssetId = Guid.NewGuid();
            var itworldAssetId = Guid.NewGuid();

            seedDb.RentalBillingProfiles.AddRange(
                CreateProfile(usenetProfileId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, "USENET-PROFILE"),
                CreateProfile(itworldProfileId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "ITWORLD-PROFILE"));
            seedDb.RentalAssets.AddRange(
                CreateAsset(usenetAssetId, usenetProfileId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, "USENET-ASSET"),
                CreateAsset(itworldAssetId, itworldProfileId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "ITWORLD-ASSET"));
            seedDb.RentalAssetAssignmentHistories.AddRange(
                CreateHistory(usenetAssetId, usenetProfileId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, "USENET-HISTORY"),
                CreateHistory(itworldAssetId, itworldProfileId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "ITWORLD-HISTORY"));
            seedDb.RentalBillingLogs.AddRange(
                CreateLog(usenetProfileId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, "USENET-LOG"),
                CreateLog(itworldProfileId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "ITWORLD-LOG"));
            await seedDb.SaveChangesAsync();
        }

        var tenantAdmin = CreateAdmin(TenantScopeCatalog.ScopeOfficeOnly);
        await using (var tenantDb = CreateDbContext(tenantAdmin))
        {
            var service = new OfficeScopeService(tenantAdmin, tenantDb);

            Assert.Equal(
                ["USENET-PROFILE"],
                await service.ApplyRentalBillingProfileScope(tenantDb.RentalBillingProfiles.AsNoTracking())
                    .Select(entity => entity.ProfileKey)
                    .ToListAsync());
            Assert.Equal(
                ["USENET-ASSET"],
                await service.ApplyRentalAssetScope(tenantDb.RentalAssets.AsNoTracking())
                    .Select(entity => entity.AssetKey)
                    .ToListAsync());
            Assert.Equal(
                ["USENET-HISTORY"],
                await service.ApplyRentalAssignmentHistoryScope(tenantDb.RentalAssetAssignmentHistories.AsNoTracking())
                    .Select(entity => entity.ChangeReason)
                    .ToListAsync());
            Assert.Equal(
                ["USENET-LOG"],
                await service.ApplyRentalBillingLogScope(tenantDb.RentalBillingLogs.AsNoTracking())
                    .Select(entity => entity.Note)
                    .ToListAsync());
        }

        await using (var globalDb = CreateDbContext(globalAdmin))
        {
            var service = new OfficeScopeService(globalAdmin, globalDb);

            Assert.Equal(
                2,
                await service.ApplyRentalBillingProfileScope(globalDb.RentalBillingProfiles.AsNoTracking()).CountAsync());
            Assert.Equal(
                2,
                await service.ApplyRentalAssetScope(globalDb.RentalAssets.AsNoTracking()).CountAsync());
            Assert.Equal(
                2,
                await service.ApplyRentalAssignmentHistoryScope(globalDb.RentalAssetAssignmentHistories.AsNoTracking()).CountAsync());
            Assert.Equal(
                2,
                await service.ApplyRentalBillingLogScope(globalDb.RentalBillingLogs.AsNoTracking()).CountAsync());
        }
    }

    public void Dispose()
        => _connection.Dispose();

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static TestCurrentUserContext CreateAdmin(string scopeType)
        => new()
        {
            Username = $"admin-{scopeType}",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = scopeType,
            IsAdmin = true
        };

    private static RentalBillingProfile CreateProfile(
        Guid id,
        string tenantCode,
        string officeCode,
        string profileKey)
        => new()
        {
            Id = id,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ProfileKey = profileKey,
            CustomerName = profileKey,
            ManagementCompanyCode = officeCode
        };

    private static RentalAsset CreateAsset(
        Guid id,
        Guid profileId,
        string tenantCode,
        string officeCode,
        string assetKey)
        => new()
        {
            Id = id,
            BillingProfileId = profileId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            AssetKey = assetKey,
            ManagementCompanyCode = officeCode
        };

    private static RentalAssetAssignmentHistory CreateHistory(
        Guid assetId,
        Guid profileId,
        string tenantCode,
        string officeCode,
        string changeReason)
        => new()
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            BillingProfileId = profileId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ChangeReason = changeReason
        };

    private static RentalBillingLog CreateLog(
        Guid profileId,
        string tenantCode,
        string officeCode,
        string note)
        => new()
        {
            Id = Guid.NewGuid(),
            BillingProfileId = profileId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            BillingYearMonth = tenantCode == TenantScopeCatalog.Itworld ? "202602" : "202601",
            Note = note
        };

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode;
    }
}
