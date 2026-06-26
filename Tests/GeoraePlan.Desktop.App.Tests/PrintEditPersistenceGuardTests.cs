using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PrintEditPersistenceGuardTests
{
    [Fact]
    public void PrintEditWindow_KeepsStatementEstimatePaymentClaimEditorAndJsonPersistenceWiring()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = File.ReadAllText(Path.Combine(appRoot, "Views", "PrintEditWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "PrintEditViewModel.cs"));
        var salesViewModel = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "SalesViewModel.cs"));
        var mainViewModel = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "MainViewModel.cs"));
        var localState = File.ReadAllText(Path.Combine(appRoot, "Services", "LocalStateService.cs"));

        Assert.Contains("PreviewDocumentStatement", viewModel, StringComparison.Ordinal);
        Assert.Contains("PreviewDocumentEstimate", viewModel, StringComparison.Ordinal);
        Assert.Contains("PreviewDocumentPaymentClaim", viewModel, StringComparison.Ordinal);
        Assert.Contains("ShowLineEditor", viewModel, StringComparison.Ordinal);
        Assert.Contains("BuildModel()", viewModel, StringComparison.Ordinal);

        Assert.Contains("DocumentViewer", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewDocument", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Lines}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SaveCommand}\"", xaml, StringComparison.Ordinal);

        Assert.Contains("JsonSerializer.Serialize(model, PrintModelJsonOptions)", salesViewModel, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Deserialize<InvoicePrintModel>", salesViewModel, StringComparison.Ordinal);
        Assert.Contains("SaveInvoicePrintModelForInvoiceAsync", salesViewModel, StringComparison.Ordinal);
        Assert.Contains("InvoicePrintModelCurrentInfoSynchronizer.RefreshLinkedBusinessPartyFields(saved, defaultModel)", salesViewModel, StringComparison.Ordinal);
        Assert.Contains("InvoicePrintLineSynchronizer.AlignToInvoiceLineOrder(saved, defaultModel)", salesViewModel, StringComparison.Ordinal);

        Assert.Contains("JsonSerializer.Deserialize<InvoicePrintModel>", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("InvoicePrintModelCurrentInfoSynchronizer.RefreshLinkedBusinessPartyFields(saved, defaultModel)", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("InvoicePrintLineSynchronizer.AlignToInvoiceLineOrder(saved, defaultModel)", mainViewModel, StringComparison.Ordinal);

        Assert.Contains("GetInvoicePrintPayloadAsync", localState, StringComparison.Ordinal);
        Assert.Contains("SaveInvoicePrintPayloadAsync", localState, StringComparison.Ordinal);
        Assert.Contains("BuildInvoicePrintSettingKey", localState, StringComparison.Ordinal);
        Assert.Contains("InvoicePrint:", localState, StringComparison.Ordinal);
    }

    [Fact]
    public void InvoicePrintModel_JsonRoundTripsEditableHeaderAmountsAndLines()
    {
        var sourceLineId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var model = new InvoicePrintModel
        {
            InvoiceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            InvoiceNumber = "S-2026-0001",
            InvoiceDate = new DateOnly(2026, 6, 27),
            VoucherType = VoucherType.Sales.ToString(),
            SupplierBusinessNumber = "123-45-67890",
            SupplierName = "ITWORLD",
            SupplierRepresentative = "대표",
            SupplierPhone = "032-000-0000",
            SupplierAddress = "공급자 주소",
            BuyerBusinessNumber = "987-65-43210",
            BuyerName = "구매자",
            BuyerRepresentative = "담당",
            BuyerPhone = "010-0000-0000",
            BuyerAddress = "구매자 주소",
            ManagerName = "영업담당",
            Memo = "거래명세 메모",
            EstimateOrganization = "견적기관",
            EstimateValidityText = "견적 유효기간",
            EstimateRemarks = "견적 특이사항",
            FooterText = "하단 문구",
            BankAccountText = "입금 계좌",
            PrintWithDate = false,
            PrintWithPrice = true,
            VatMode = InvoiceVatModes.Included,
            SupplyAmount = 100_000m,
            VatAmount = 10_000m,
            TotalAmount = 110_000m,
            PaidAmount = 20_000m,
            BalanceAmount = 90_000m,
            Lines =
            [
                new InvoicePrintLineModel
                {
                    SourceLineId = sourceLineId,
                    No = 1,
                    ItemName = "복합기 임대료",
                    Specification = "IMC2010",
                    Unit = "대",
                    Quantity = 2m,
                    UnitPrice = 50_000m,
                    Amount = 100_000m,
                    Remark = "6월분"
                }
            ]
        };

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(model, options);
        var restored = JsonSerializer.Deserialize<InvoicePrintModel>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(model.InvoiceId, restored!.InvoiceId);
        Assert.Equal("S-2026-0001", restored.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 6, 27), restored.InvoiceDate);
        Assert.Equal("ITWORLD", restored.SupplierName);
        Assert.Equal("구매자", restored.BuyerName);
        Assert.Equal("입금 계좌", restored.BankAccountText);
        Assert.Equal(110_000m, restored.TotalAmount);
        Assert.False(restored.PrintWithDate);
        var restoredLine = Assert.Single(restored.Lines);
        Assert.Equal(sourceLineId, restoredLine.SourceLineId);
        Assert.Equal("복합기 임대료", restoredLine.ItemName);
        Assert.Equal("IMC2010", restoredLine.Specification);
        Assert.Equal(2m, restoredLine.Quantity);
        Assert.Equal(100_000m, restoredLine.Amount);
    }

    [Fact]
    public void PrintEditViewModel_BuildModelPreservesEditableFieldsAndRenumbersVisibleLines()
    {
        RunOnSta(() =>
        {
            var sourceLineId = Guid.Parse("22222222-3333-4444-5555-666666666666");
            var model = new InvoicePrintModel
            {
                InvoiceId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
                InvoiceNumber = "  S-2026-0002  ",
                InvoiceDate = new DateOnly(2026, 6, 27),
                VoucherType = VoucherType.Sales.ToString(),
                SupplierName = "  공급자  ",
                BuyerName = "  구매자  ",
                Memo = "  메모  ",
                BankAccountText = "  계좌  ",
                PrintWithDate = true,
                PrintWithPrice = false,
                VatMode = InvoiceVatModes.None,
                TotalAmount = 33_000m,
                Lines =
                [
                    new InvoicePrintLineModel
                    {
                        SourceLineId = sourceLineId,
                        No = 10,
                        ItemName = "  유지보수  ",
                        Specification = "  A형  ",
                        Unit = "  식  ",
                        Quantity = 3m,
                        UnitPrice = 11_000m,
                        Amount = 33_000m,
                        Remark = "  비고  "
                    },
                    new InvoicePrintLineModel
                    {
                        No = 20
                    }
                ]
            };

            using var viewModel = new PrintEditViewModel(
                model,
                _ => Task.CompletedTask,
                (_, _) => BuildSimpleFixedDocument());

            viewModel.SelectedPreviewDocument = PrintEditViewModel.PreviewDocumentPaymentClaim;
            viewModel.FooterText = "  하단  ";

            var built = viewModel.BuildModel();

            Assert.Equal(model.InvoiceId, built.InvoiceId);
            Assert.Equal("S-2026-0002", built.InvoiceNumber);
            Assert.Equal("공급자", built.SupplierName);
            Assert.Equal("구매자", built.BuyerName);
            Assert.Equal("메모", built.Memo);
            Assert.Equal("계좌", built.BankAccountText);
            Assert.Equal("하단", built.FooterText);
            Assert.True(built.PrintWithDate);
            Assert.False(built.PrintWithPrice);
            Assert.Equal(InvoiceVatModes.None, built.VatMode);
            Assert.Equal(33_000m, built.TotalAmount);

            var line = Assert.Single(built.Lines);
            Assert.Equal(sourceLineId, line.SourceLineId);
            Assert.Equal(1, line.No);
            Assert.Equal("유지보수", line.ItemName);
            Assert.Equal("A형", line.Specification);
            Assert.Equal("식", line.Unit);
            Assert.Equal(3m, line.Quantity);
            Assert.Equal(11_000m, line.UnitPrice);
            Assert.Equal(33_000m, line.Amount);
            Assert.Equal("비고", line.Remark);

            Assert.True(viewModel.IsPaymentClaimPreviewSelected);
            Assert.False(viewModel.ShowLineEditor);
        });
    }

    private static FixedDocument BuildSimpleFixedDocument()
    {
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(300, 420);

        var page = new FixedPage
        {
            Width = 300,
            Height = 420
        };
        page.Children.Add(new TextBlock
        {
            Text = "print edit preview",
            Margin = new Thickness(24)
        });

        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        document.Pages.Add(content);
        return document;
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
            throw captured;
    }

    private static string FindDesktopAppRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var desktopRoot = Path.Combine(current.FullName, "Desktop");
            if (Directory.Exists(desktopRoot))
            {
                var appRoot = Directory
                    .EnumerateDirectories(desktopRoot, "*.Desktop.App", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => File.Exists(Path.Combine(path, "Views", "PrintEditWindow.xaml")));
                if (appRoot is not null)
                    return appRoot;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Desktop app root could not be located.");
    }
}
