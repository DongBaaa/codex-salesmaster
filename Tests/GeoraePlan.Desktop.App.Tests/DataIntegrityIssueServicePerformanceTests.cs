using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityIssueServicePerformanceTests
{
    [Fact]
    public async Task ScanAsync_UsesPreGroupedLinkedAssetsForManyRentalProfiles()
    {
        PrepareAppRoot("georaeplan-integrity-linked-assets-grouping");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const int profileCount = 650;
            for (var index = 0; index < profileCount; index++)
            {
                var profileId = Guid.NewGuid();
                db.RentalBillingProfiles.Add(CreateProfile(profileId, index));
                db.RentalAssets.Add(CreateLinkedAsset(profileId, index));
            }

            await db.SaveChangesAsync();

            var result = await new DataIntegrityIssueService(db).ScanAsync(CreateAdminSession());

            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalBillingProfile CreateProfile(Guid profileId, int index)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"INTEGRITY-PROFILE-{index:D4}",
            CustomerName = $"Integrity Customer {index:D4}",
            ItemName = $"Integrity Copier {index:D4}",
            InstallSiteName = "Main Office",
            MonthlyAmount = 100_000m,
            BillingTemplateJson = "[]",
            BillingRunsJson = "[]",
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateLinkedAsset(Guid profileId, int index)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = profileId,
            ManagementId = $"INTEGRITY-ASSET-{index:D4}",
            ManagementNumber = $"INT-{index:D4}",
            AssetKey = $"INTEGRITY-ASSET-{Guid.NewGuid():N}",
            CustomerName = $"Integrity Customer {index:D4}",
            CurrentCustomerName = $"Integrity Customer {index:D4}",
            ItemCategoryName = "Copier",
            ItemName = $"Integrity Copier {index:D4}",
            MachineNumber = $"INT-SN-{index:D4}",
            InstallSiteName = "Main Office",
            InstallLocation = "Main Office",
            AssetStatus = "Rental",
            BillingEligibilityStatus = "Billable",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

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
