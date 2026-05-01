using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingDeletionFlowTests
{
    [Fact]
    public async Task ExcludeUnlinkedBillingAsset_HidesFromBillingListButKeepsLinkCandidate()
    {
        PrepareAppRoot("georaeplan-rental-exclude-unlinked");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateRentalAsset(assetId, "A거래처", billingProfileId: null, "미확인"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var beforeRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { Status = "미연결", ExpandCustomerSummaryRows = true },
                session);
            Assert.Contains(beforeRows, row => row.SelectionId == assetId && !row.HasPersistedProfile);

            var result = await service.ExcludeUnlinkedBillingAssetFromBillingListAsync(assetId, session);
            Assert.True(result.Success, result.Message);

            var afterRows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { Status = "미연결", ExpandCustomerSummaryRows = true },
                session);
            Assert.DoesNotContain(afterRows, row => row.SelectionId == assetId);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal("청구제외", persistedAsset.BillingEligibilityStatus);
            Assert.Equal("청구관리 목록 정리", persistedAsset.BillingExclusionReason);
            Assert.Null(persistedAsset.BillingProfileId);

            var candidates = await service.GetAssetLinkCandidatesAsync(
                currentBillingProfileId: null,
                customerId: null,
                customerName: "A거래처",
                officeCode: OfficeCodeCatalog.Usenet,
                session,
                includeOtherOfficeAssets: true);
            Assert.Contains(candidates, candidate => candidate.Source.Id == assetId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteBillingProfile_UnlinksIncludedAssetsAndSuppressesFromUnlinkedBillingList()
    {
        PrepareAppRoot("georaeplan-rental-delete-profile");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, assetId, "A거래처"));
            db.RentalAssets.Add(CreateRentalAsset(assetId, "A거래처", profileId, "청구대상"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var result = await service.DeleteBillingProfileAsync(profileId, session);
            Assert.True(result.Success, result.Message);

            var deletedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
            Assert.True(deletedProfile.IsDeleted);

            var unlinkedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.Null(unlinkedAsset.BillingProfileId);
            Assert.Equal("청구제외", unlinkedAsset.BillingEligibilityStatus);
            Assert.Equal("청구 프로필 삭제로 청구목록 제외", unlinkedAsset.BillingExclusionReason);
            Assert.Equal(profileId, unlinkedAsset.LastBillingProfileId);

            var histories = await db.RentalAssetAssignmentHistories
                .Where(history => history.AssetId == assetId)
                .ToListAsync();
            var endedHistory = Assert.Single(histories);
            Assert.False(endedHistory.IsCurrent);
            Assert.Equal(profileId, endedHistory.BillingProfileId);
            Assert.Equal("청구 프로필 삭제", endedHistory.ChangeReason);

            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter { Status = "미연결", ExpandCustomerSummaryRows = true },
                session);
            Assert.DoesNotContain(rows, row => row.SelectionId == assetId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static LocalRentalAsset CreateRentalAsset(
        Guid assetId,
        string customerName,
        Guid? billingProfileId,
        string billingEligibilityStatus)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{assetId:N}",
            ManagementNumber = ShortCode("MN", assetId),
            AssetKey = $"AK-{assetId:N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            InstallSiteName = customerName,
            InstallLocation = "사무실",
            ItemName = "복합기",
            MachineNumber = ShortCode("SN", assetId),
            AssetStatus = "임대진행중",
            BillingProfileId = billingProfileId,
            BillingEligibilityStatus = billingEligibilityStatus,
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalBillingProfile CreateBillingProfile(Guid profileId, Guid assetId, string customerName)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerName = customerName,
            InstallSiteName = "사무실",
            ItemName = "복합기 렌탈료",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingDay = 25,
            BillingCycleMonths = 1,
            MonthlyAmount = 100_000m,
            BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "복합기 렌탈료",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m,
                    IncludedAssetIds = [assetId]
                }
            }),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static string ShortCode(string prefix, Guid id)
        => $"{prefix}-{id:N}".Substring(0, 12);

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
}
