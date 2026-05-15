using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TaxInvoiceIssuedPersistenceTests
{
    [Fact]
    public void InvoiceListRow_TaxInvoiceDisplay_UsesCenteredVMarker()
    {
        var issued = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 4, 30),
                VoucherType = VoucherType.Sales,
                TaxInvoiceIssued = true
            },
            "테스트 거래처",
            showCustomerName: true);

        var notIssued = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 4, 30),
                VoucherType = VoucherType.Purchase,
                TaxInvoiceIssued = false
            },
            "테스트 거래처",
            showCustomerName: true);

        Assert.Equal("V", issued.TaxInvoiceDisplay);
        Assert.Equal(string.Empty, notIssued.TaxInvoiceDisplay);
    }

    [Fact]
    public void SalesViewModel_TaxInvoiceIssuedChange_IsIncludedInPendingState()
    {
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);

        viewModel.MarkCurrentStateAsPristine();

        viewModel.TaxInvoiceIssued = true;

        Assert.True(viewModel.HasPendingChanges);
    }

    [Fact]
    public void SalesViewModel_CounterpartyLabels_DistinguishSalesAndPurchase()
    {
        var salesViewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);
        var purchaseViewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Purchase);

        Assert.Equal("거래처 정보", salesViewModel.CustomerSectionTitleText);
        Assert.Equal("고객/거래처", salesViewModel.CustomerNameLabelText);
        Assert.Equal("고객분류", salesViewModel.CustomerCategoryLabelText);
        Assert.Equal("총미수금", salesViewModel.CustomerBalanceLabelText);
        Assert.Equal("선수금", salesViewModel.CustomerReserveLabelText);

        Assert.Equal("거래처 정보", purchaseViewModel.CustomerSectionTitleText);
        Assert.Equal("거래처", purchaseViewModel.CustomerNameLabelText);
        Assert.Equal("거래처분류", purchaseViewModel.CustomerCategoryLabelText);
        Assert.Equal("총미지급금", purchaseViewModel.CustomerBalanceLabelText);
        Assert.Equal("선지급금", purchaseViewModel.CustomerReserveLabelText);
    }
}
