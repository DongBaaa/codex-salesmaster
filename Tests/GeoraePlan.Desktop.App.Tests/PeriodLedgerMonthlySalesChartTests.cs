using System.Collections;
using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PeriodLedgerMonthlySalesChartTests
{
    [Fact]
    public void BuildMonthlySalesChartPoints_UsesEveryMonthInSelectedPeriodWithoutSixMonthLimit()
    {
        var query = new PeriodLedgerQuery
        {
            From = new DateOnly(2025, 1, 1),
            To = new DateOnly(2026, 12, 31),
            LedgerType = PeriodLedgerType.SalesOnly,
            Scope = PeriodLedgerScope.AllCustomers
        };
        var invoices = new List<LocalInvoice>
        {
            CreateInvoice(new DateOnly(2025, 1, 10), VoucherType.Sales, 100_000m),
            CreateInvoice(new DateOnly(2025, 6, 10), VoucherType.Purchase, 999_999m),
            CreateInvoice(new DateOnly(2025, 6, 15), VoucherType.Sales, 250_000m),
            CreateInvoice(new DateOnly(2026, 12, 25), VoucherType.Sales, 400_000m)
        };

        var result = InvokeBuildMonthlySalesChartPoints(query, invoices);

        Assert.Equal(24, result.Count);
        Assert.Equal("2025-01", result[0].MonthLabel);
        Assert.Equal(100_000m, result[0].SalesAmount);
        Assert.Equal("2025-06", result[5].MonthLabel);
        Assert.Equal(250_000m, result[5].SalesAmount);
        Assert.Equal("2026-12", result[^1].MonthLabel);
        Assert.Equal(400_000m, result[^1].SalesAmount);
        Assert.DoesNotContain(result, point => point.SalesAmount == 999_999m);
    }

    private static List<PeriodLedgerMonthlySalesChartPoint> InvokeBuildMonthlySalesChartPoints(
        PeriodLedgerQuery query,
        IReadOnlyList<LocalInvoice> invoices)
    {
        var method = typeof(PeriodLedgerAggregationService).GetMethod(
            "BuildMonthlySalesChartPoints",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var raw = Assert.IsAssignableFrom<IEnumerable>(method!.Invoke(null, [query, invoices]));
        return raw.Cast<PeriodLedgerMonthlySalesChartPoint>().ToList();
    }

    private static LocalInvoice CreateInvoice(DateOnly invoiceDate, VoucherType voucherType, decimal totalAmount)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            VoucherType = voucherType,
            InvoiceDate = invoiceDate,
            TotalAmount = totalAmount,
            SupplyAmount = totalAmount,
            VatAmount = 0m,
            IsLatestVersion = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
}
