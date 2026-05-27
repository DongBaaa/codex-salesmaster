using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AttachmentDocumentCatalogTests
{
    [Fact]
    public void FourMajorInsuranceCompletion_IsPlacedNextToPaymentClaim()
    {
        var codes = AttachmentDocumentCatalog.OrderedDocuments
            .Select(document => document.Code)
            .ToList();

        var paymentClaimIndex = codes.IndexOf(AttachmentDocumentCatalog.PaymentClaim);
        var insuranceIndex = codes.IndexOf(AttachmentDocumentCatalog.FourMajorInsuranceCompletion);

        Assert.True(paymentClaimIndex >= 0);
        Assert.Equal(paymentClaimIndex + 1, insuranceIndex);
        Assert.Equal("4대 보험 완납 증명서", AttachmentDocumentCatalog.GetDisplayName(AttachmentDocumentCatalog.FourMajorInsuranceCompletion));
    }

    [Fact]
    public void ResetOrderCommand_ReordersCheckedDocumentsToDefaultCatalogOrder()
    {
        List<AttachmentSelectionState> initialSelections =
        [
            new() { DocCode = AttachmentDocumentCatalog.NationalTaxCompletion, IsChecked = true, OrderIndex = 1 },
            new() { DocCode = AttachmentDocumentCatalog.PaymentClaim, IsChecked = true, OrderIndex = 2 },
            new() { DocCode = AttachmentDocumentCatalog.Statement, IsChecked = true, OrderIndex = 3 },
            new() { DocCode = AttachmentDocumentCatalog.FourMajorInsuranceCompletion, IsChecked = true, OrderIndex = 4 },
            new() { DocCode = AttachmentDocumentCatalog.Estimate, IsChecked = true, OrderIndex = 5 }
        ];

        var viewModel = new AttachmentSelectionDialogViewModel(
            AttachmentDocumentCatalog.OrderedDocuments,
            initialSelections,
            AttachmentDocumentCatalog.PaymentClaim,
            [AttachmentDocumentCatalog.Statement, AttachmentDocumentCatalog.Estimate, AttachmentDocumentCatalog.PaymentClaim]);

        viewModel.ResetOrderCommand.Execute(null);

        var checkedCodes = viewModel.GetCheckedStatesInOrder()
            .Select(state => state.DocCode)
            .ToList();

        Assert.Equal(
            [
                AttachmentDocumentCatalog.Statement,
                AttachmentDocumentCatalog.Estimate,
                AttachmentDocumentCatalog.PaymentClaim,
                AttachmentDocumentCatalog.FourMajorInsuranceCompletion,
                AttachmentDocumentCatalog.NationalTaxCompletion
            ],
            checkedCodes);
        Assert.Contains(viewModel.Items, item =>
            item.Code == AttachmentDocumentCatalog.FourMajorInsuranceCompletion &&
            item.IsChecked &&
            item.OrderIndex == 4);
    }
}
