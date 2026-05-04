using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PurchaseReceivingConfirmationTests
{
    [Fact]
    public void NewPurchaseInvoice_DefaultsToReceivingPending()
    {
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Purchase);

        viewModel.NewInvoice();

        Assert.True(viewModel.PurchaseReceivingRequired);
        Assert.Equal(InvoiceReceivingStatuses.Pending, viewModel.PurchaseReceivingStatusDisplay);
        Assert.Contains("입고 사무실/창고", viewModel.PurchaseReceivingMetaDisplay);
    }

    [Fact]
    public void InvoiceListRow_PurchaseReceivingDisplay_ShowsPendingForLegacyPurchase()
    {
        var row = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 5, 4),
                VoucherType = VoucherType.Purchase
            },
            "테스트 매입처",
            showCustomerName: true);

        Assert.Equal(InvoiceReceivingStatuses.Pending, row.PurchaseReceivingDisplay);
    }
}