using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceListRowDisplayTests
{
    [Fact]
    public void VoucherTypeDisplay_AppendsBillLabel_ForRentalBillingInvoice()
    {
        var rentalRow = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 7, 6),
                VoucherType = VoucherType.Sales,
                LinkedRentalBillingProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                LinkedRentalBillingRunId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
            },
            "테스트 거래처",
            showCustomerName: true);

        var regularRow = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 7, 6),
                VoucherType = VoucherType.Sales
            },
            "테스트 거래처",
            showCustomerName: true);

        Assert.True(rentalRow.IsRentalBillingInvoice);
        Assert.Equal("매출(청구서)", rentalRow.VoucherTypeDisplay);
        Assert.False(regularRow.IsRentalBillingInvoice);
        Assert.Equal("매출", regularRow.VoucherTypeDisplay);
    }

    [Fact]
    public void VoucherTypeDisplay_UsesSummaryRentalLinks_WhenListIsLoadedFromSummary()
    {
        var row = InvoiceListRow.From(
            new LocalInvoiceListSummary
            {
                Id = Guid.NewGuid(),
                CustomerId = Guid.NewGuid(),
                InvoiceDate = new DateOnly(2026, 7, 6),
                VoucherType = VoucherType.Sales,
                LinkedRentalBillingProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                TotalAmount = 100_000m,
                SettledAmount = 0m
            },
            "테스트 거래처",
            showCustomerName: true);

        Assert.True(row.IsRentalBillingInvoice);
        Assert.Equal("매출(청구서)", row.VoucherTypeDisplay);
    }

    [Fact]
    public void IsBalanceCleared_IsTrueOnlyForSettledSalesOrPurchaseRows()
    {
        var settledSales = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 7, 6),
                VoucherType = VoucherType.Sales,
                TotalAmount = 100_000m,
                Payments =
                {
                    new LocalPayment { Amount = 100_000m }
                }
            },
            "테스트 거래처",
            showCustomerName: true);
        var partialSales = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 7, 6),
                VoucherType = VoucherType.Sales,
                TotalAmount = 100_000m,
                Payments =
                {
                    new LocalPayment { Amount = 40_000m }
                }
            },
            "테스트 거래처",
            showCustomerName: true);
        var settledPurchase = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 7, 6),
                VoucherType = VoucherType.Purchase,
                TotalAmount = 50_000m,
                Payments =
                {
                    new LocalPayment { Amount = 50_000m }
                }
            },
            "테스트 매입처",
            showCustomerName: true);
        var procurement = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 7, 6),
                VoucherType = VoucherType.Procurement,
                TotalAmount = 0m
            },
            "테스트 거래처",
            showCustomerName: true);

        Assert.True(settledSales.IsBalanceCleared);
        Assert.False(partialSales.IsBalanceCleared);
        Assert.True(settledPurchase.IsBalanceCleared);
        Assert.False(procurement.IsBalanceCleared);
    }
}
