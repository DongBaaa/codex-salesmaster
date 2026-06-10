using System.Reflection;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
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
}
