using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssignmentHistoryDisplayLimitTests
{
    [Fact]
    public void RentalAssetViewModel_LimitAssignmentHistoriesForDisplay_ShowsRecentBoundedRows()
    {
        var rows = CreateRows(350);
        var result = InvokeLimit(typeof(RentalAssetViewModel), rows);

        Assert.Equal(300, result.Count);
        Assert.Equal("history-000", result[0].ChangeReason);
        Assert.Equal("history-299", result[^1].ChangeReason);
    }

    [Fact]
    public void RentalBillingViewModel_LimitAssignmentHistoriesForDisplay_ShowsRecentBoundedRows()
    {
        var rows = CreateRows(350);
        var result = InvokeLimit(typeof(RentalBillingViewModel), rows);

        Assert.Equal(300, result.Count);
        Assert.Equal("history-000", result[0].ChangeReason);
        Assert.Equal("history-299", result[^1].ChangeReason);
    }

    [Fact]
    public async Task RentalStateService_GetAssetAssignmentHistoriesAsync_WithDisplayLimit_BoundsRows()
    {
        PrepareAppRoot("georaeplan-rental-assignment-history-service-limit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:ASSIGNMENT-HISTORY-LIMIT",
                ManagementNumber = "AH-001",
                ItemName = "Printer",
                MachineNumber = "AH-SN-001",
                CustomerName = "Customer 000",
                CurrentCustomerName = "Customer 000",
                InstallLocation = "Office 000",
                AssetStatus = "Rental",
                BillingEligibilityStatus = "Billable"
            });

            var now = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc);
            foreach (var index in Enumerable.Range(0, 350))
            {
                db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
                {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                    AssetId = assetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    IsCurrent = index == 0,
                    LinkedAtUtc = now.AddDays(-index),
                    CustomerName = $"Customer {index:D3}",
                    InstallLocation = $"Office {index:D3}",
                    ItemName = "Printer",
                    MachineNumber = "AH-SN-001",
                    ManagementNumber = "AH-001",
                    ChangeReason = $"history-{index:D3}",
                    UpdatedAtUtc = now.AddDays(-index)
                });
            }
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetAssetAssignmentHistoriesAsync(assetId, 300, CreateOfficeSession(OfficeCodeCatalog.Usenet));

            Assert.Equal(300, rows.Count);
            Assert.Equal("history-000", rows[0].ChangeReason);
            Assert.Equal("history-299", rows[^1].ChangeReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_GetAssetAssignmentHistoriesAsync_BatchesProfileDisplayLookupsBeyondBatchWindow()
    {
        PrepareAppRoot("georaeplan-rental-assignment-history-profile-display-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:ASSIGNMENT-HISTORY-PROFILE-BATCH",
                ManagementNumber = "AH-BATCH-001",
                ItemName = "Printer",
                MachineNumber = "AH-BATCH-SN-001",
                CustomerName = "Customer 000",
                CurrentCustomerName = "Customer 000",
                InstallLocation = "Office 000",
                AssetStatus = "Rental",
                BillingEligibilityStatus = "Billable"
            });

            var now = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc);
            foreach (var index in Enumerable.Range(0, 650))
            {
                var profileId = Guid.Parse($"b1000000-0000-0000-0000-{index + 1:000000000000}");
                db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = $"HISTORY-PROFILE-{index:D4}",
                    CustomerName = $"Profile Customer {index:D4}",
                    ItemName = $"Plan {index:D4}",
                    MonthlyAmount = 100_000m,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });

                db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
                {
                    Id = Guid.Parse($"b2000000-0000-0000-0000-{index + 1:000000000000}"),
                    AssetId = assetId,
                    BillingProfileId = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    IsCurrent = index == 0,
                    LinkedAtUtc = now.AddDays(-index),
                    CustomerName = $"Customer {index:D3}",
                    InstallLocation = $"Office {index:D3}",
                    ItemName = "Printer",
                    MachineNumber = "AH-BATCH-SN-001",
                    ManagementNumber = "AH-BATCH-001",
                    ChangeReason = $"history-profile-{index:D3}",
                    UpdatedAtUtc = now.AddDays(-index)
                });
            }
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetAssetAssignmentHistoriesAsync(assetId, 300, CreateOfficeSession(OfficeCodeCatalog.Usenet));

            Assert.Equal(300, rows.Count);
            Assert.Equal("Profile Customer 0000 · Plan 0000", rows[0].BillingProfileDisplay);
            Assert.Equal("Profile Customer 0299 · Plan 0299", rows[^1].BillingProfileDisplay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_GetAssetAssignmentHistoriesAsync_DoesNotResolveOutOfScopeBillingProfileDisplay()
    {
        PrepareAppRoot("georaeplan-rental-assignment-history-profile-display-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var hiddenProfileId = Guid.Parse("33333333-aaaa-aaaa-aaaa-333333333333");
            var now = new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc);
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:ASSIGNMENT-HISTORY-PROFILE-SCOPE",
                ManagementNumber = "AH-SCOPE-001",
                ItemName = "Scoped Printer",
                MachineNumber = "AH-SCOPE-SN-001",
                CustomerName = "Visible Customer",
                CurrentCustomerName = "Visible Customer",
                InstallLocation = "Visible Office",
                AssetStatus = "Rental",
                BillingEligibilityStatus = "Billable",
                IsDeleted = false
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = hiddenProfileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                ProfileKey = "HIDDEN-HISTORY-PROFILE",
                CustomerName = "Hidden Profile Customer",
                ItemName = "Hidden Plan",
                MonthlyAmount = 100_000m,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = Guid.Parse("33333333-bbbb-bbbb-bbbb-333333333333"),
                AssetId = assetId,
                BillingProfileId = hiddenProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                IsCurrent = true,
                LinkedAtUtc = now,
                CustomerName = "Visible Customer",
                InstallLocation = "Visible Office",
                ItemName = "Scoped Printer",
                MachineNumber = "AH-SCOPE-SN-001",
                ManagementNumber = "AH-SCOPE-001",
                ChangeReason = "profile-scope-check",
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetAssetAssignmentHistoriesAsync(assetId, 300, CreateOfficeSession(OfficeCodeCatalog.Usenet));

            var row = Assert.Single(rows);
            Assert.Equal(string.Empty, row.BillingProfileDisplay);
            Assert.DoesNotContain("Hidden Profile Customer", row.BillingProfileDisplay, StringComparison.Ordinal);
            Assert.DoesNotContain(hiddenProfileId.ToString("D"), row.BillingProfileDisplay, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_CreateAssetAssignmentHistoryEditRequestAsync_ReturnsNullOutsideWritableAssetScope()
    {
        PrepareAppRoot("georaeplan-rental-assignment-history-edit-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                AssetKey = "TEST:ASSIGNMENT-HISTORY-EDIT-SCOPE",
                ManagementNumber = "AH-EDIT-SCOPE-001",
                ItemName = "Itworld Printer",
                MachineNumber = "AH-EDIT-SCOPE-SN-001",
                CustomerName = "Itworld Customer",
                CurrentCustomerName = "Itworld Customer",
                InstallLocation = "Itworld Office",
                AssetStatus = "Rental",
                BillingEligibilityStatus = "Billable",
                IsDeleted = false
            });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var blockedRequest = await service.CreateAssetAssignmentHistoryEditRequestAsync(assetId, CreateOfficeSession(OfficeCodeCatalog.Usenet));
            var allowedRequest = await service.CreateAssetAssignmentHistoryEditRequestAsync(assetId, CreateOfficeSession(OfficeCodeCatalog.Itworld));

            Assert.Null(blockedRequest);
            Assert.NotNull(allowedRequest);
            Assert.Equal(assetId, allowedRequest!.AssetId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static List<RentalAssetAssignmentHistoryViewItem> CreateRows(int count)
        => Enumerable.Range(0, count)
            .Select(index => new RentalAssetAssignmentHistoryViewItem
            {
                HistoryId = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                AssetId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                LinkedAtLocal = new DateTime(2026, 6, 1).AddDays(-index),
                ChangeReason = $"history-{index:D3}"
            })
            .ToList();

    private static IReadOnlyList<RentalAssetAssignmentHistoryViewItem> InvokeLimit(
        Type viewModelType,
        IReadOnlyList<RentalAssetAssignmentHistoryViewItem> rows)
    {
        var method = viewModelType.GetMethod(
            "LimitAssignmentHistoriesForDisplay",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<RentalAssetAssignmentHistoryViewItem>>(
            method!.Invoke(null, [rows]));
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static SessionState CreateOfficeSession(string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"assignment-history-{officeCode.ToLowerInvariant()}",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = []
        });
        return session;
    }
}
