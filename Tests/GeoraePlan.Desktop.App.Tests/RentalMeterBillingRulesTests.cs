using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalMeterBillingRulesTests
{
    [Fact]
    public void SelectOpeningBaseline_UsesOnlyLatestFinalizedReading()
    {
        var selected = RentalMeterBillingRules.SelectOpeningBaseline(
        [
            Reading("2026-04", 100, 20, finalized: true),
            Reading("2026-06", 300, 60, finalized: false),
            Reading("2026-05", 200, 40, finalized: true)
        ]);

        Assert.NotNull(selected);
        Assert.Equal("2026-05", selected!.BillingYearMonth);
        Assert.Equal(200, selected.BlackMeter);
        Assert.True(selected.IsOpeningBaseline);
    }

    [Fact]
    public void Calculate_PerAsset_ChargesOnlyUsageAboveIncludedPages()
    {
        var assetId = Guid.NewGuid();
        var result = RentalMeterBillingRules.Calculate(
            "2026-06",
            [Asset(assetId, blackIncluded: 100, blackRate: 10m, colorIncluded: 20, colorRate: 100m,
                Reading("2026-05", 1000, 200, finalized: true),
                Reading("2026-06", 1150, 230, finalized: true))],
            poolIncludedPages: false);

        Assert.True(result.Success, result.Message);
        Assert.Collection(
            result.Lines.OrderBy(line => line.ColorMode),
            line =>
            {
                Assert.Equal("컬러", line.ColorMode);
                Assert.Equal(10, line.OveragePages);
                Assert.Equal(1_000m, line.Amount);
            },
            line =>
            {
                Assert.Equal("흑백", line.ColorMode);
                Assert.Equal(50, line.OveragePages);
                Assert.Equal(500m, line.Amount);
            });
        Assert.Equal(1_500m, result.TotalAmount);
    }

    [Fact]
    public void Calculate_PooledContract_SumsUsageAndAllowanceBeforeCharging()
    {
        var result = RentalMeterBillingRules.Calculate(
            "2026-06",
            [
                Asset(Guid.NewGuid(), 100, 10m, 0, 100m,
                    Reading("2026-05", 100, 0, true), Reading("2026-06", 250, 0, true)),
                Asset(Guid.NewGuid(), 100, 10m, 0, 100m,
                    Reading("2026-05", 200, 0, true), Reading("2026-06", 250, 0, true))
            ],
            poolIncludedPages: true);

        Assert.True(result.Success, result.Message);
        Assert.Empty(result.Lines);
        Assert.Equal(0m, result.TotalAmount);
    }

    [Fact]
    public void Calculate_MissingCurrentFinalReading_RequiresReview()
    {
        var result = RentalMeterBillingRules.Calculate(
            "2026-06",
            [Asset(Guid.NewGuid(), 100, 10m, 10, 100m,
                Reading("2026-05", 100, 10, true))],
            poolIncludedPages: false);

        Assert.False(result.Success);
        Assert.Contains("확정 검침값", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Calculate_PooledContractWithDifferentRates_RequiresReview()
    {
        var result = RentalMeterBillingRules.Calculate(
            "2026-06",
            [
                Asset(Guid.NewGuid(), 0, 10m, 0, 100m,
                    Reading("2026-05", 0, 0, true), Reading("2026-06", 10, 0, true)),
                Asset(Guid.NewGuid(), 0, 20m, 0, 100m,
                    Reading("2026-05", 0, 0, true), Reading("2026-06", 10, 0, true))
            ],
            poolIncludedPages: true);

        Assert.False(result.Success);
        Assert.Contains("장당요금이 장비마다 다릅니다", result.Message, StringComparison.Ordinal);
    }

    private static RentalMeterAssetBillingInput Asset(
        Guid id,
        int blackIncluded,
        decimal blackRate,
        int colorIncluded,
        decimal colorRate,
        params RentalMeterReadingRecord[] readings)
        => new(
            id,
            $"M-{id:N}"[..10],
            "복합기",
            true,
            RentalMeterPolicyModes.Numeric,
            blackIncluded,
            blackRate,
            RentalMeterPolicyModes.Numeric,
            colorIncluded,
            colorRate,
            RentalMeterBillingRules.SerializeReadings(readings));

    private static RentalMeterReadingRecord Reading(
        string month,
        long black,
        long color,
        bool finalized)
        => new()
        {
            BillingYearMonth = month,
            ReadingDate = new DateOnly(
                int.Parse(month[..4], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(month[5..], System.Globalization.CultureInfo.InvariantCulture),
                25),
            BlackMeter = black,
            ColorMeter = color,
            IsFinalized = finalized,
            RecordedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 6, 25), DateTimeKind.Utc)
        };
}
