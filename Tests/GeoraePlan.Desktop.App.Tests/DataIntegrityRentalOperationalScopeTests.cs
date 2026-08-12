using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityRentalOperationalScopeTests
{
    [Theory]
    [InlineData(OfficeCodeCatalog.Shared)]
    [InlineData(OfficeCodeCatalog.Usenet)]
    public async Task ScanAsync_AcceptsSharedUsenetProfileWithSharedOrResponsibleManagement(
        string managementCompanyCode)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-integrity-shared-rental-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profile = CreateProfile(
                Guid.NewGuid(),
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Shared,
                managementCompanyCode,
                OfficeCodeCatalog.Usenet);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateUsenetAdminSession());

            Assert.DoesNotContain(
                result.Issues,
                issue => issue.Code == DataIntegrityIssueCodes.RentalOperationalScopeMismatch &&
                         issue.ProfileId == profile.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_AcceptsSharedAssetOwnedByUsenet_ButStillReportsMissingOwnerScope()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-integrity-shared-rental-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var sharedAssetId = Guid.NewGuid();
            var sharedManagementAssetId = Guid.NewGuid();
            var inconsistentAssetId = Guid.NewGuid();
            db.RentalAssets.AddRange(
                CreateAsset(
                    sharedAssetId,
                    OfficeCodeCatalog.Shared,
                    OfficeCodeCatalog.Usenet),
                CreateAsset(
                    sharedManagementAssetId,
                    OfficeCodeCatalog.Shared,
                    OfficeCodeCatalog.Shared),
                CreateAsset(
                    inconsistentAssetId,
                    OfficeCodeCatalog.Usenet,
                    managementCompanyCode: string.Empty));
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateUsenetAdminSession());
            var scopeIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.RentalOperationalScopeMismatch)
                .ToList();

            Assert.Single(scopeIssues, issue => issue.AssetId == inconsistentAssetId);
            Assert.DoesNotContain(scopeIssues, issue => issue.AssetId == sharedAssetId);
            Assert.DoesNotContain(scopeIssues, issue => issue.AssetId == sharedManagementAssetId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_AcceptsCanonicalItworldScope_ButReportsEachMismatchedScopeField()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-integrity-itworld-rental-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonicalProfile = CreateProfile(
                Guid.NewGuid(),
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            var wrongProfileManagement = CreateProfile(
                Guid.NewGuid(),
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Itworld);
            var wrongProfileResponsible = CreateProfile(
                Guid.NewGuid(),
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Usenet);
            var wrongProfileTenant = CreateProfile(
                Guid.NewGuid(),
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);

            var canonicalAsset = CreateAsset(
                Guid.NewGuid(),
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            var wrongAssetManagement = CreateAsset(
                Guid.NewGuid(),
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Itworld);
            var wrongAssetResponsible = CreateAsset(
                Guid.NewGuid(),
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Usenet);
            var wrongAssetTenant = CreateAsset(
                Guid.NewGuid(),
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);

            db.RentalBillingProfiles.AddRange(
                canonicalProfile,
                wrongProfileManagement,
                wrongProfileResponsible,
                wrongProfileTenant);
            db.RentalAssets.AddRange(
                canonicalAsset,
                wrongAssetManagement,
                wrongAssetResponsible,
                wrongAssetTenant);
            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateItworldAdminSession());
            var scopeIssues = result.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.RentalOperationalScopeMismatch)
                .ToList();

            Assert.DoesNotContain(scopeIssues, issue => issue.ProfileId == canonicalProfile.Id);
            Assert.DoesNotContain(scopeIssues, issue => issue.AssetId == canonicalAsset.Id);
            Assert.Contains(scopeIssues, issue => issue.ProfileId == wrongProfileManagement.Id);
            Assert.Contains(scopeIssues, issue => issue.ProfileId == wrongProfileResponsible.Id);
            Assert.Contains(scopeIssues, issue => issue.ProfileId == wrongProfileTenant.Id);
            Assert.Contains(scopeIssues, issue => issue.AssetId == wrongAssetManagement.Id);
            Assert.Contains(scopeIssues, issue => issue.AssetId == wrongAssetResponsible.Id);
            Assert.Contains(scopeIssues, issue => issue.AssetId == wrongAssetTenant.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalBillingProfile CreateProfile(
        Guid profileId,
        string tenantCode,
        string officeCode,
        string managementCompanyCode,
        string responsibleOfficeCode)
        => new()
        {
            Id = profileId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ManagementCompanyCode = managementCompanyCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            ProfileKey = $"SCOPE-{profileId:N}",
            CustomerName = "Scope regression customer",
            ItemName = "Scope regression item",
            BillingTemplateJson = "[]",
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateAsset(
        Guid assetId,
        string officeCode,
        string managementCompanyCode)
        => CreateAsset(
            assetId,
            TenantScopeCatalog.UsenetGroup,
            officeCode,
            managementCompanyCode,
            OfficeCodeCatalog.Usenet);

    private static LocalRentalAsset CreateAsset(
        Guid assetId,
        string tenantCode,
        string officeCode,
        string managementCompanyCode,
        string responsibleOfficeCode)
        => new()
        {
            Id = assetId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            ManagementCompanyCode = managementCompanyCode,
            AssetKey = $"SCOPE-{assetId:N}",
            ManagementNumber = $"SCOPE-{assetId:N}",
            ItemName = "Scope regression asset",
            AssetStatus = "ACTIVE",
            BillingEligibilityStatus = "EXCLUDED",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static SessionState CreateUsenetAdminSession()
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

    private static SessionState CreateItworldAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
