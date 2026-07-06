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

    [Fact]
    public void TransactionRow_DisplaysStandaloneReceiptAsLedgerEntry()
    {
        var row = InvoiceListRow.From(
            new LocalTransaction
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                TransactionDate = new DateOnly(2026, 7, 6),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                CashReceipt = 10_000m,
                BankReceipt = 39_500m,
                ReceiptTotal = 49_500m
            },
            "미추홀구 시설관리공단",
            showCustomerName: false);

        Assert.True(row.IsTransactionRow);
        Assert.Equal("수금 입력", row.PrimaryColumnText);
        Assert.Equal("혼합수금", row.VoucherTypeDisplay);
        Assert.Equal(string.Empty, row.TotalAmountDisplay);
        Assert.Equal("49,500", row.ReceiptAmountDisplay);
        Assert.Equal(string.Empty, row.PaymentAmountDisplay);
        Assert.Equal("49,500", row.BalanceAmountDisplay);
    }

    [Fact]
    public void TransactionRow_DisplaysStandalonePaymentAsLedgerEntry()
    {
        var row = InvoiceListRow.From(
            new LocalTransaction
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                CustomerId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                TransactionDate = new DateOnly(2026, 7, 6),
                TransactionKind = PaymentFlowConstants.TransactionKindPayment,
                BankPayment = 60_500m,
                PaymentTotal = 60_500m
            },
            "코스모스",
            showCustomerName: true);

        Assert.True(row.IsTransactionRow);
        Assert.Equal("코스모스 · 지불 입력", row.PrimaryColumnText);
        Assert.Equal("통장지급", row.VoucherTypeDisplay);
        Assert.Equal(string.Empty, row.SupplyAmountDisplay);
        Assert.Equal(string.Empty, row.ReceiptAmountDisplay);
        Assert.Equal("60,500", row.PaymentAmountDisplay);
        Assert.Equal("60,500", row.BalanceAmountDisplay);
    }

    [Fact]
    public void PaymentViewModel_DefaultTransactionKinds_HideAdvanceOptions()
    {
        var viewModel = new PaymentViewModel(null!, new SessionState());

        var values = viewModel.TransactionKinds.Select(option => option.Value).ToArray();

        Assert.Contains(PaymentFlowConstants.TransactionKindReceipt, values);
        Assert.Contains(PaymentFlowConstants.TransactionKindPayment, values);
        Assert.DoesNotContain(PaymentFlowConstants.TransactionKindAdvanceDeposit, values);
        Assert.DoesNotContain(PaymentFlowConstants.TransactionKindAdvanceRefund, values);
    }
}
