using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetPermissionGuardTests
{
    [Fact]
    public async Task SaveAssetAsync_DeniesUserWithoutRentalAssetEditAndDoesNotCreateDirtyAsset()
    {
        PrepareAppRoot("georaeplan-rental-asset-save-permission");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOfficeSession();
            var asset = CreateAsset(Guid.NewGuid());

            var result = await new RentalStateService(db).SaveAssetAsync(asset, session, allowCategoryRecovery: true);

            Assert.False(result.Success);
            Assert.Contains("권한", result.Message, StringComparison.Ordinal);
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteAssetAsync_DeniesUserWithoutRentalAssetEditAndLeavesAssetClean()
    {
        PrepareAppRoot("georaeplan-rental-asset-delete-permission");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateAsset(assetId));
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).DeleteAssetAsync(assetId, CreateOfficeSession());

            Assert.False(result.Success);
            Assert.Contains("권한", result.Message, StringComparison.Ordinal);
            var stored = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.False(stored.IsDeleted);
            Assert.False(stored.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RentalAssetViewModel_CreateAndSaveCommandsFollowRentalAssetEditPermission()
    {
        PrepareAppRoot("georaeplan-rental-asset-command-permission");

        try
        {
            using var blockedDb = new LocalDbContext();
            using var allowedDb = new LocalDbContext();
            var blockedSession = CreateOfficeSession();
            var allowedSession = CreateOfficeSession(AppPermissionNames.RentalAssetEdit);

            var blockedViewModel = CreateViewModel(blockedDb, blockedSession);
            var allowedViewModel = CreateViewModel(allowedDb, allowedSession);

            Assert.False(blockedViewModel.CanCreateAsset);
            Assert.False(blockedViewModel.CanSave);
            Assert.False(blockedViewModel.NewAssetCommand.CanExecute(null));
            Assert.False(blockedViewModel.SaveCommand.CanExecute(null));

            Assert.True(allowedViewModel.CanCreateAsset);
            Assert.True(allowedViewModel.CanSave);
            Assert.True(allowedViewModel.NewAssetCommand.CanExecute(null));
            Assert.True(allowedViewModel.SaveCommand.CanExecute(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static RentalAssetViewModel CreateViewModel(LocalDbContext db, SessionState session)
    {
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        return new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);
    }

    private static LocalRentalAsset CreateAsset(Guid assetId)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"PERMISSION-ASSET-{assetId:N}",
            ManagementNumber = $"PERM-{assetId:N}",
            ManagementId = $"PERM-ID-{assetId:N}",
            MachineNumber = $"PERM-SN-{assetId:N}",
            CustomerName = "권한 테스트 거래처",
            CurrentCustomerName = "권한 테스트 거래처",
            InstallLocation = "본관",
            InstallSiteName = "본관",
            ItemCategoryName = "복합기",
            ItemName = "권한 테스트 장비",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "미확인",
            IsDirty = false,
            IsDeleted = false
        };

    private static SessionState CreateOfficeSession(params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"rental-asset-permission-{Guid.NewGuid():N}",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
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
