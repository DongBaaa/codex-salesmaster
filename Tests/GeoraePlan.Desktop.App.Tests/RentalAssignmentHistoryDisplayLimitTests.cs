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

            var rows = await new RentalStateService(db).GetAssetAssignmentHistoriesAsync(assetId, 300);

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
}
