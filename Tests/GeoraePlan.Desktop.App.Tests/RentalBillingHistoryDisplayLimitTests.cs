using System.Reflection;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingHistoryDisplayLimitTests
{
    [Fact]
    public void LimitBillingHistoryRowsForDisplay_ShowsRecentBoundedRows()
    {
        var rows = Enumerable.Range(0, 650)
            .Select(index => new RentalBillingHistoryRow
            {
                BillingRunId = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                ScheduledDate = DateOnly.FromDateTime(new DateTime(2026, 6, 1).AddDays(-index)),
                PeriodLabel = $"period-{index:D3}"
            })
            .ToList();
        var method = typeof(RentalBillingViewModel).GetMethod(
            "LimitBillingHistoryRowsForDisplay",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = Assert.IsAssignableFrom<IReadOnlyList<RentalBillingHistoryRow>>(
            method!.Invoke(null, [rows]));

        Assert.Equal(600, result.Count);
        Assert.Equal("period-000", result[0].PeriodLabel);
        Assert.Equal("period-599", result[^1].PeriodLabel);
    }
}
