using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceVatModeTests
{
    [Fact]
    public void InvoiceVatModes_CalculateTotals_UsesIncludedVatByDefault()
    {
        var totals = InvoiceVatModes.CalculateTotals([110_000m], null);

        Assert.Equal(110_000m, totals.TotalAmount);
        Assert.Equal(100_000m, totals.SupplyAmount);
        Assert.Equal(10_000m, totals.VatAmount);
    }

    [Fact]
    public void InvoiceVatModes_CalculateTotals_UsesTotalAsSupplyWhenVatNone()
    {
        var totals = InvoiceVatModes.CalculateTotals([110_000m], InvoiceVatModes.None);

        Assert.Equal(110_000m, totals.TotalAmount);
        Assert.Equal(110_000m, totals.SupplyAmount);
        Assert.Equal(0m, totals.VatAmount);
    }

    [Fact]
    public void SalesViewModel_IsVatNone_RecalculatesTotalsAndMarksPending()
    {
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);

        viewModel.Lines.Add(new InvoiceLineEditModel
        {
            ItemName = "테스트 품목",
            Quantity = 1m,
            UnitPrice = 110_000m,
            LineAmount = 110_000m
        });
        viewModel.MarkCurrentStateAsPristine();

        viewModel.IsVatNone = true;

        Assert.Equal(110_000m, viewModel.TotalAmount);
        Assert.Equal(110_000m, viewModel.SupplyAmount);
        Assert.Equal(0m, viewModel.VatAmount);
        Assert.True(viewModel.HasPendingChanges);
    }

    [Fact]
    public void SalesViewModel_ImportPreviousInvoices_DoesNotChangeVatMode()
    {
        var viewModel = new SalesViewModel(
            local: null!,
            print: null!,
            invoicePrintService: null!,
            session: new SessionState(),
            newInvoiceVoucherType: VoucherType.Sales);

        viewModel.IsVatNone = false;
        var previousInvoice = new LocalInvoice
        {
            Id = Guid.NewGuid(),
            InvoiceDate = new DateOnly(2026, 5, 4),
            VatMode = InvoiceVatModes.None,
            Memo = "이전 전표 메모",
            Lines =
            {
                new LocalInvoiceLine
                {
                    ItemNameOriginal = "이전 전표 품목",
                    Quantity = 1m,
                    UnitPrice = 110_000m,
                    LineAmount = 110_000m
                }
            }
        };

        viewModel.ImportPreviousInvoices([previousInvoice], replaceExistingLines: true);

        Assert.False(viewModel.IsVatNone);
        Assert.Equal(110_000m, viewModel.TotalAmount);
        Assert.Equal(100_000m, viewModel.SupplyAmount);
        Assert.Equal(10_000m, viewModel.VatAmount);
    }
}
