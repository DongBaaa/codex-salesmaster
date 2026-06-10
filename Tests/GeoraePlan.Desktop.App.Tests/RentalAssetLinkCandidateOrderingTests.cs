using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetLinkCandidateOrderingTests
{
    [Fact]
    public async Task GetAssetLinkCandidatesAsync_CurrentProfileAssetOutsideDefaultWindowIsStillIncluded()
    {
        PrepareAppRoot("georaeplan-rental-link-candidate-order");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var currentProfileId = Guid.Parse("db4b0000-1111-4444-8888-000000000001");
            for (var index = 0; index < 50; index++)
                db.RentalAssets.Add(CreateRentalAsset($"A Customer {index:D4}", $"A-{index:D4}", billingProfileId: null));

            var linkedAsset = CreateRentalAsset("ZZZ Current Profile Customer", "Z-9999", currentProfileId);
            db.RentalAssets.Add(linkedAsset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var defaultCandidates = await service.GetAssetLinkCandidatesAsync(
                currentBillingProfileId: null,
                customerId: null,
                customerName: null,
                officeCode: OfficeCodeCatalog.Usenet,
                session,
                includeOtherOfficeAssets: false,
                maxResults: 50);
            Assert.DoesNotContain(defaultCandidates, candidate => candidate.Source.Id == linkedAsset.Id);

            var currentProfileCandidates = await service.GetAssetLinkCandidatesAsync(
                currentProfileId,
                customerId: null,
                customerName: null,
                officeCode: OfficeCodeCatalog.Usenet,
                session,
                includeOtherOfficeAssets: false,
                maxResults: 50);
            Assert.Contains(currentProfileCandidates, candidate => candidate.Source.Id == linkedAsset.Id);
            Assert.Equal(50, currentProfileCandidates.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetAssetLinkCandidatesAsync_BatchesLinkedProfileDisplayLookupsBeyondBatchWindow()
    {
        PrepareAppRoot("georaeplan-rental-link-candidate-profile-display-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int candidateCount = 600;
            for (var index = 0; index < candidateCount; index++)
            {
                var profileId = Guid.Parse($"a1000000-0000-0000-0000-{index + 1:000000000000}");
                db.RentalBillingProfiles.Add(CreateBillingProfile(profileId, $"Profile Customer {index:D4}", $"Plan {index:D4}"));
                db.RentalAssets.Add(CreateRentalAsset(
                    $"Candidate Customer {index:D4}",
                    $"C-{index:D4}",
                    profileId));
            }
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var candidates = await service.GetAssetLinkCandidatesAsync(
                currentBillingProfileId: null,
                customerId: null,
                customerName: null,
                officeCode: OfficeCodeCatalog.Usenet,
                CreateAdminSession(),
                includeOtherOfficeAssets: false,
                maxResults: candidateCount);

            Assert.Equal(candidateCount, candidates.Count);
            Assert.Contains(candidates, candidate => candidate.CurrentBillingProfileDisplay == "Profile Customer 0000");
            Assert.Contains(candidates, candidate => candidate.CurrentBillingProfileDisplay == "Profile Customer 0599");
            Assert.All(candidates, candidate => Assert.StartsWith("Profile Customer ", candidate.CurrentBillingProfileDisplay));
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

    private static LocalRentalAsset CreateRentalAsset(
        string customerName,
        string managementNumber,
        Guid? billingProfileId)
    {
        var assetId = Guid.NewGuid();
        return new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"MID-{assetId:N}",
            ManagementNumber = managementNumber,
            AssetKey = $"ASSET-{assetId:N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            BillingProfileId = billingProfileId,
            ItemCategoryName = "Copier",
            ItemName = "Rental Copier",
            MachineNumber = $"SN-{assetId:N}",
            InstallSiteName = "HQ",
            InstallLocation = "HQ",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static LocalRentalBillingProfile CreateBillingProfile(Guid profileId, string customerName, string itemName)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"PROFILE-{profileId:N}",
            CustomerName = customerName,
            ItemName = itemName,
            MonthlyAmount = 100_000m,
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
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
