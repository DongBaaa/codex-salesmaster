using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TaxInvoiceIssuedPersistenceTests
{
    [Fact]
    public void InvoiceListRow_TaxInvoiceDisplay_ShowsIssuedLabelWithoutExposingAssignedNumber()
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
        var assigned = InvoiceListRow.From(
            new LocalInvoice
            {
                InvoiceDate = new DateOnly(2026, 4, 30),
                VoucherType = VoucherType.Sales,
                TaxInvoiceIssued = true,
                TaxInvoiceNumber = "TAX-202604-0003"
            },
            "테스트 거래처",
            showCustomerName: true);

        Assert.Equal("발행", issued.TaxInvoiceDisplay);
        Assert.Equal("발행", assigned.TaxInvoiceDisplay);
        Assert.Equal(string.Empty, notIssued.TaxInvoiceDisplay);
    }

    [Fact]
    public void SalesViewModel_TaxInvoiceNumberDisplay_StillShowsAssignedNumberInInvoiceDetail()
    {
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);

        viewModel.TaxInvoiceIssued = true;
        viewModel.TaxInvoiceNumber = "TAX-202604-0003";

        Assert.Equal("TAX-202604-0003", viewModel.TaxInvoiceNumberDisplay);
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

    [Fact]
    public void SalesViewModel_RentalLinkedInvoice_ExposesEditBoundaryNotice()
    {
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);
        var profileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var runId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        SetRentalBillingLinks(viewModel, profileId, runId);

        Assert.True(viewModel.IsRentalBillingLinkedInvoice);
        Assert.Contains("렌탈 청구관리에서 만든 전표", viewModel.RentalBillingLinkedNoticeText);
        Assert.Contains("다음 청구 설정은 변경되지 않습니다", viewModel.RentalBillingLinkedNoticeText);
        Assert.Contains("aaaaaaaa", viewModel.RentalBillingLinkedReferenceText);
        Assert.Contains("bbbbbbbb", viewModel.RentalBillingLinkedReferenceText);
    }

    [Fact]
    public async Task SalesViewModel_RentalLinkedInvoice_CloseAutoSaveIsBlockedUntilExplicitSave()
    {
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);
        SetRentalBillingLinks(viewModel, Guid.NewGuid(), Guid.NewGuid());
        viewModel.MarkCurrentStateAsPristine();

        viewModel.InvoiceMemo = "렌탈 전표 금액 수정";

        var saved = await viewModel.TryAutoSaveOnCloseAsync();

        Assert.False(saved);
        Assert.Contains("자동저장하지 않습니다", viewModel.LastAutoSaveFailureMessage);
        Assert.Equal(viewModel.LastAutoSaveFailureMessage, viewModel.StatusMessage);
    }

    private static void SetRentalBillingLinks(SalesViewModel viewModel, Guid profileId, Guid runId)
    {
        var method = typeof(SalesViewModel).GetMethod(
            "SetRentalBillingLinks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(viewModel, [profileId, runId]);
    }
}
