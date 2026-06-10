using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalEquipmentReplacementTests
{
    [Fact]
    public async Task ReplaceRentalEquipment_MovesAssignmentAndBillingTemplateToReplacementAsset()
    {
        PrepareAppRoot("georaeplan-rental-equipment-replacement");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var originalAssetId = Guid.NewGuid();
            var replacementAssetId = Guid.NewGuid();
            var replacementDate = new DateOnly(2026, 6, 9);

            db.Customers.Add(CreateCustomer(customerId, "교체테스트거래처"));
            db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, customerId, originalAssetId));
            db.RentalAssets.Add(CreateAssignedAsset(originalAssetId, customerId, profileId));
            db.RentalAssets.Add(CreateReplacementCandidate(replacementAssetId));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();
            var candidates = await service.GetRentalEquipmentReplacementCandidatesAsync(originalAssetId, session);
            Assert.Contains(candidates, asset => asset.Id == replacementAssetId);

            var result = await service.ReplaceRentalEquipmentAsync(
                new RentalEquipmentReplacementRequest
                {
                    OriginalAssetId = originalAssetId,
                    ReplacementAssetId = replacementAssetId,
                    ReplacementDate = replacementDate,
                    OriginalAssetNextStatus = "창고",
                    ChangeReason = "렌탈 장비 교체"
                },
                session);

            Assert.True(result.Success, result.Message);
            Assert.Equal(replacementAssetId, result.RelatedEntityId);

            var original = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == originalAssetId);
            Assert.Null(original.BillingProfileId);
            Assert.Null(original.CustomerId);
            Assert.Equal(string.Empty, original.CustomerName);
            Assert.Equal(string.Empty, original.InstallLocation);
            Assert.Equal("창고", original.AssetStatus);
            Assert.Equal("청구제외", original.BillingEligibilityStatus);
            Assert.Equal("자산상태: 창고", original.BillingExclusionReason);
            Assert.Equal(profileId, original.LastBillingProfileId);
            Assert.Equal("교체테스트거래처", original.LastCustomerName);
            Assert.Equal(replacementDate, original.RentalEndDate);
            Assert.True(original.IsDirty);

            var replacement = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == replacementAssetId);
            Assert.Equal(profileId, replacement.BillingProfileId);
            Assert.Equal(customerId, replacement.CustomerId);
            Assert.Equal("교체테스트거래처", replacement.CustomerName);
            Assert.Equal("본관 2층", replacement.InstallLocation);
            Assert.Equal("임대진행중", replacement.AssetStatus);
            Assert.Equal("청구대상", replacement.BillingEligibilityStatus);
            Assert.Equal(string.Empty, replacement.BillingExclusionReason);
            Assert.Equal(123_000m, replacement.MonthlyFee);
            Assert.Equal(replacementDate, replacement.InstallDate);
            Assert.Equal(replacementDate, replacement.ContractStartDate);
            Assert.Equal(new DateOnly(2027, 12, 31), replacement.RentalEndDate);
            Assert.True(replacement.IsDirty);

            var profile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == profileId);
            var templateItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson)
                                ?? new List<RentalBillingTemplateItemModel>();
            var templateItem = Assert.Single(templateItems);
            Assert.Equal(replacementAssetId, templateItem.RepresentativeAssetId);
            Assert.Contains(replacementAssetId, templateItem.IncludedAssetIds);
            Assert.DoesNotContain(originalAssetId, templateItem.IncludedAssetIds);
            Assert.True(profile.IsDirty);

            var histories = await db.RentalAssetAssignmentHistories
                .AsNoTracking()
                .ToListAsync();
            var originalHistory = Assert.Single(histories, history => history.AssetId == originalAssetId);
            Assert.False(originalHistory.IsCurrent);
            Assert.Equal(profileId, originalHistory.BillingProfileId);
            Assert.Equal("렌탈 장비 교체", originalHistory.ChangeReason);
            Assert.NotNull(originalHistory.UnlinkedAtUtc);
            Assert.Equal(replacementDate, DateOnly.FromDateTime(originalHistory.UnlinkedAtUtc.Value));

            var replacementHistory = Assert.Single(histories, history => history.AssetId == replacementAssetId);
            Assert.True(replacementHistory.IsCurrent);
            Assert.Equal(profileId, replacementHistory.BillingProfileId);
            Assert.Equal(customerId, replacementHistory.CustomerId);
            Assert.Equal("렌탈 장비 교체", replacementHistory.ChangeReason);
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

    private static LocalCustomer CreateCustomer(Guid customerId, string customerName)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = customerName,
            NameMatchKey = customerName,
            TradeType = CustomerTradeTypes.Sales,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateAssignedAsset(Guid assetId, Guid customerId, Guid billingProfileId)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{assetId:N}",
            ManagementNumber = "OLD-001",
            AssetKey = $"AK-{assetId:N}",
            CustomerId = customerId,
            CustomerName = "교체테스트거래처",
            CurrentCustomerName = "교체테스트거래처",
            InstallSiteName = "교체테스트거래처",
            InstallLocation = "본관 2층",
            ItemName = "복합기",
            MachineNumber = "SN-OLD",
            AssetStatus = "임대진행중",
            CurrentLocation = "렌탈",
            BillingProfileId = billingProfileId,
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 123_000m,
            ContractDate = new DateOnly(2026, 1, 1),
            InstallDate = new DateOnly(2026, 1, 10),
            ContractStartDate = new DateOnly(2026, 1, 10),
            RentalEndDate = new DateOnly(2027, 12, 31),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1)
        };

    private static LocalRentalAsset CreateReplacementCandidate(Guid assetId)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{assetId:N}",
            ManagementNumber = "NEW-001",
            AssetKey = $"AK-{assetId:N}",
            ItemName = "복합기",
            MachineNumber = "SN-NEW",
            AssetStatus = "창고",
            CurrentLocation = "창고",
            BillingEligibilityStatus = "청구제외",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-5)
        };

    private static LocalRentalBillingProfile CreateBillingProfile(Guid profileId, Guid customerId, Guid assetId)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "교체테스트거래처",
            InstallSiteName = "본관 2층",
            ItemName = "복합기 렌탈료",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingDay = 25,
            BillingCycleMonths = 1,
            MonthlyAmount = 123_000m,
            BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "복합기 렌탈료",
                    BillingLineMode = "묶음",
                    RepresentativeAssetId = assetId,
                    Quantity = 1m,
                    UnitPrice = 123_000m,
                    Amount = 123_000m,
                    IncludedAssetIds = [assetId]
                }
            }),
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1)
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
}
