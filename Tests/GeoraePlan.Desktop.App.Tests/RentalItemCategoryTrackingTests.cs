using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalItemCategoryTrackingTests
{
    [Fact]
    public async Task SaveAsset_UsesPersistedActiveCategoryWhenTrackedCopyIsStaleInactive()
    {
        PrepareAppRoot("georaeplan-rental-category-stale-inactive");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var category = CreateCategory(isActive: false);
            db.ItemCategoryOptions.Add(category);
            await db.SaveChangesAsync();
            Assert.False(category.IsActive);
            Assert.Equal(EntityState.Unchanged, db.Entry(category).State);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ItemCategoryOptions SET IsActive = 1 WHERE Id = {category.Id}");

            var result = await new RentalStateService(db).SaveAssetAsync(
                CreateAsset(category.Name),
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            Assert.True(await db.ItemCategoryOptions.AsNoTracking()
                .AnyAsync(option => option.Id == category.Id && option.IsActive && !option.IsDeleted));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveAsset_RejectsPersistedInactiveCategoryWhenTrackedCopyIsStaleActive()
    {
        PrepareAppRoot("georaeplan-rental-category-stale-active");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var category = CreateCategory(isActive: true);
            db.ItemCategoryOptions.Add(category);
            await db.SaveChangesAsync();
            Assert.True(category.IsActive);
            Assert.Equal(EntityState.Unchanged, db.Entry(category).State);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ItemCategoryOptions SET IsActive = 0 WHERE Id = {category.Id}");

            var result = await new RentalStateService(db).SaveAssetAsync(
                CreateAsset(category.Name),
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.Contains("비활성화된 품목분류", result.Message, StringComparison.Ordinal);
            Assert.False(await db.RentalAssets.AsNoTracking().AnyAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalItemCategoryOption CreateCategory(bool isActive)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "다중 PC 활성 분류",
            SortOrder = 10,
            IsActive = isActive,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalRentalAsset CreateAsset(string categoryName)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"TRACK-{Guid.NewGuid():N}",
            ManagementNumber = $"TRACK-{Guid.NewGuid():N}",
            CurrentLocation = "격리 테스트 창고",
            ItemCategoryName = categoryName,
            ItemName = string.Empty,
            AssetStatus = "창고",
            BillingEligibilityStatus = "청구제외",
            BillingExclusionReason = "격리 추적 상태 검증"
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

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
