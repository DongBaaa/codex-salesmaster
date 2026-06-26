using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PrintDocumentRenderingSmokeTests
{
    private static readonly Regex StandaloneDotPattern = new(@"(^|\s)\.(\s|$)", RegexOptions.Compiled);

    [Fact]
    public void SalesStatementDocument_RendersExpectedPartiesAmountsAndNoStrayDot()
    {
        RunOnSta(() =>
        {
            var (invoice, customer, company) = CreateSampleInvoice(VoucherType.Sales);

            var document = StatementDocumentBuilder.BuildStatementPrintDocument(
                invoice,
                customer,
                company,
                NativeStatementLayoutType.TradeHalf,
                printWithDate: true,
                printWithPrice: true);

            RenderFlowDocumentFirstPage(document);
            var text = NormalizeText(ReadFlowDocumentText(document));

            Assert.Contains("거 래 명 세 서", text);
            Assert.Contains("공급자 보관용", text);
            Assert.Contains("공급받는자 보관용", text);
            Assert.Contains("유즈넷", text);
            Assert.Contains("연수구 테스트거래처", text);
            Assert.Contains("IMC2010", text);
            Assert.Contains("80,000", text);
            Assert.Contains("8,000", text);
            Assert.Contains("88,000", text);
            Assert.DoesNotMatch(StandaloneDotPattern, text);
        });
    }

    [Fact]
    public void SupplementDocuments_RenderExpectedLabelsAmountsAttachmentsAndNoStrayDot()
    {
        RunOnSta(() =>
        {
            var (invoice, customer, company) = CreateSampleInvoice(VoucherType.Sales);
            var printModel = CreatePrintModel(VoucherType.Sales);
            printModel.Memo = "사무기기 렌탈대금 5월";
            printModel.EstimateValidityText = "발행일로부터 30일";
            printModel.EstimateRemarks = "검수 후 세금계산서 발행";

            var estimateDocument = SupplementDocumentBuilder.BuildEstimateDocument(invoice, customer, company, printModel);
            RenderFixedDocumentFirstPage(estimateDocument);
            var estimateText = NormalizeText(ReadFixedDocumentText(estimateDocument));

            Assert.Contains("견 적 서", estimateText);
            Assert.Contains("견적금액", estimateText);
            Assert.Contains("IMC2010", estimateText);
            Assert.Contains("80,000", estimateText);
            Assert.Contains("8,000", estimateText);
            Assert.DoesNotMatch(StandaloneDotPattern, estimateText);

            var claimDocument = SupplementDocumentBuilder.BuildPaymentClaimDocument(
                invoice,
                customer,
                company,
                new[] { "거래명세서", "견적서", "대금청구서", "4대 보험 완납 증명서" },
                printModel);
            RenderFixedDocumentFirstPage(claimDocument);
            var claimText = NormalizeText(ReadFixedDocumentText(claimDocument));

            Assert.Contains("대 금 청 구 서", claimText);
            Assert.Contains("용역명", claimText);
            Assert.Contains("사무기기 렌탈대금 5월", claimText);
            Assert.Contains("4대 보험 완납 증명서", claimText);
            Assert.Contains("80,000", claimText);
            Assert.Contains("8,000", claimText);
            Assert.Contains("88,000", claimText);
            Assert.DoesNotMatch(StandaloneDotPattern, claimText);
        });
    }

    [Fact]
    public void SalesDocuments_RenderCurrentMasterInfoWhenSavedPrintSnapshotIsStale()
    {
        RunOnSta(() =>
        {
            var (invoice, customer, company) = CreateSampleInvoice(VoucherType.Sales);
            var printService = new WpfInvoicePrintService();
            var savedSnapshot = printService.CreateDefaultModel(
                invoice,
                customer,
                company,
                printWithDate: true,
                printWithPrice: true);
            savedSnapshot.SupplierName = "스냅샷 공급자";
            savedSnapshot.SupplierRepresentative = "스냅샷 대표";
            savedSnapshot.SupplierAddress = "스냅샷 공급자 주소";
            savedSnapshot.BuyerName = "스냅샷 거래처";
            savedSnapshot.BuyerRepresentative = "스냅샷 거래처 대표";
            savedSnapshot.BuyerAddress = "스냅샷 거래처 주소";
            savedSnapshot.ManagerName = "스냅샷 담당자";
            savedSnapshot.BankAccountText = "스냅샷은행 000-000";

            customer.NameOriginal = "현재 연동 거래처";
            customer.BusinessNumber = "222-33-44444";
            customer.Representative = "현재 거래처 대표";
            customer.Phone = "032-222-4444";
            customer.Address = "인천광역시 현재거래처로 10";
            customer.ContactPerson = "현재 담당자";
            customer.Recipient = "현재 연동 수신처";
            customer.FaxNumber = "032-222-4445";

            company.TradeName = "현재 연동 회사";
            company.BusinessNumber = "555-66-77777";
            company.Representative = "현재 회사 대표";
            company.BusinessType = "현재 업태";
            company.BusinessItem = "현재 종목";
            company.Address = "인천광역시 현재회사로 20";
            company.ContactNumber = "032-555-7777";
            company.BankAccountText = "신한은행 123-456-789 현재회사";

            var currentDefault = printService.CreateDefaultModel(
                invoice,
                customer,
                company,
                printWithDate: true,
                printWithPrice: true);
            InvoicePrintModelCurrentInfoSynchronizer.RefreshLinkedBusinessPartyFields(savedSnapshot, currentDefault);

            var fixedStatementDocument = printService.BuildFixedDocument(savedSnapshot);
            RenderFixedDocumentFirstPage(fixedStatementDocument);
            var fixedStatementText = NormalizeText(ReadFixedDocumentText(fixedStatementDocument));
            Assert.Contains("현재 연동 회사", fixedStatementText);
            Assert.Contains("현재 회사 대표", fixedStatementText);
            Assert.Contains("현재 연동 거래처", fixedStatementText);
            Assert.Contains("현재 거래처 대표", fixedStatementText);
            Assert.DoesNotContain("스냅샷 공급자", fixedStatementText);
            Assert.DoesNotContain("스냅샷 거래처", fixedStatementText);

            var nativeStatementDocument = StatementDocumentBuilder.BuildStatementPrintDocument(
                invoice,
                customer,
                company,
                NativeStatementLayoutType.TradeHalf,
                printWithDate: true,
                printWithPrice: true);
            RenderFlowDocumentFirstPage(nativeStatementDocument);
            var nativeStatementText = NormalizeText(ReadFlowDocumentText(nativeStatementDocument));
            Assert.Contains("현재 연동 회사", nativeStatementText);
            Assert.Contains("현재 연동 거래처", nativeStatementText);
            Assert.Contains("인천광역시 현재거래처로 10", nativeStatementText);
            Assert.DoesNotContain("스냅샷 공급자", nativeStatementText);
            Assert.DoesNotContain("스냅샷 거래처", nativeStatementText);

            var estimateDocument = SupplementDocumentBuilder.BuildEstimateDocument(invoice, customer, company, savedSnapshot);
            RenderFixedDocumentFirstPage(estimateDocument);
            var estimateText = NormalizeText(ReadFixedDocumentText(estimateDocument));
            Assert.Contains("현재 연동 수신처", estimateText);
            Assert.Contains("현재 연동 회사", estimateText);
            Assert.Contains("현재 회사 대표", estimateText);
            Assert.Contains("인천광역시 현재회사로 20", estimateText);
            Assert.DoesNotContain("스냅샷 공급자", estimateText);
            Assert.DoesNotContain("스냅샷 거래처", estimateText);

            var claimDocument = SupplementDocumentBuilder.BuildPaymentClaimDocument(
                invoice,
                customer,
                company,
                new[] { "거래명세서", "견적서", "대금청구서" },
                savedSnapshot);
            RenderFixedDocumentFirstPage(claimDocument);
            var claimText = NormalizeText(ReadFixedDocumentText(claimDocument));
            Assert.Contains("현재 연동 수신처", claimText);
            Assert.Contains("현재 연동 회사", claimText);
            Assert.Contains("현재 회사 대표", claimText);
            Assert.Contains("신한은행", claimText);
            Assert.Contains("123-456-789", claimText);
            Assert.DoesNotContain("스냅샷 공급자", claimText);
            Assert.DoesNotContain("스냅샷 거래처", claimText);
        });
    }

    [Fact]
    public void SalesPurchaseAndProcurementFixedDocuments_RenderWithoutLosingBusinessLabels()
    {
        RunOnSta(() =>
        {
            var printService = new WpfInvoicePrintService();

            var salesModel = CreatePrintModel(VoucherType.Sales);
            var salesDocument = printService.BuildFixedDocument(salesModel);
            RenderFixedDocumentFirstPage(salesDocument);
            var salesText = NormalizeText(ReadFixedDocumentText(salesDocument));
            Assert.Contains("거 래 명 세 서", salesText);
            Assert.Contains("공급자", salesText);
            Assert.Contains("공급받는자", salesText);
            Assert.Contains("88,000", salesText);
            Assert.DoesNotMatch(StandaloneDotPattern, salesText);

            var purchaseModel = CreatePrintModel(VoucherType.Purchase);
            var purchaseDocument = printService.BuildFixedDocument(purchaseModel);
            RenderFixedDocumentFirstPage(purchaseDocument);
            var purchaseText = NormalizeText(ReadFixedDocumentText(purchaseDocument));
            Assert.Contains("매입 명세서", purchaseText);
            Assert.Contains("공급받는 자", purchaseText);
            Assert.Contains("공급자", purchaseText);
            Assert.Contains("88,000", purchaseText);
            Assert.DoesNotMatch(StandaloneDotPattern, purchaseText);

            var procurementModel = CreatePrintModel(VoucherType.Procurement);
            procurementModel.DocumentTitle = "발주서";
            var procurementDocument = printService.BuildFixedDocument(procurementModel);
            RenderFixedDocumentFirstPage(procurementDocument);
            var procurementText = NormalizeText(ReadFixedDocumentText(procurementDocument));
            Assert.Contains("발 주 서", procurementText);
            Assert.Contains("연수구 테스트거래처", procurementText);
            Assert.Contains("유즈넷", procurementText);
            Assert.Contains("88,000", procurementText);
            Assert.DoesNotMatch(StandaloneDotPattern, procurementText);
        });
    }

    private static (LocalInvoice Invoice, LocalCustomer Customer, LocalCompanyProfile Company) CreateSampleInvoice(VoucherType voucherType)
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var invoice = new LocalInvoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            VoucherType = voucherType,
            InvoiceDate = new DateOnly(2026, 5, 27),
            VatMode = InvoiceVatModes.Included,
            SupplyAmount = 80_000m,
            VatAmount = 8_000m,
            TotalAmount = 88_000m,
            Memo = "사무기기 렌탈대금 5월",
            Lines =
            {
                new LocalInvoiceLine
                {
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "IMC2010[5월]",
                    SpecificationOriginal = "컬러복합기",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 88_000m,
                    LineAmount = 88_000m,
                    Remark = "월 렌탈료"
                }
            }
        };

        var customer = new LocalCustomer
        {
            Id = customerId,
            NameOriginal = "연수구 테스트거래처",
            BusinessNumber = "131-83-00122",
            Representative = "홍길동",
            Phone = "032-123-4567",
            FaxNumber = "032-123-4568",
            Address = "인천광역시 연수구 테스트로 1",
            ContactPerson = "담당자",
            Notes = "세금계산서 발행"
        };

        var company = new LocalCompanyProfile
        {
            TradeName = "유즈넷",
            BusinessNumber = "123-45-67890",
            Representative = "대표자",
            BusinessType = "서비스",
            BusinessItem = "사무기기",
            Address = "인천광역시 남동구 테스트로 2",
            ContactNumber = "032-999-0000",
            BankAccountText = "국민은행 123-456-789 유즈넷"
        };

        return (invoice, customer, company);
    }

    private static InvoicePrintModel CreatePrintModel(VoucherType voucherType)
    {
        var (invoice, customer, company) = CreateSampleInvoice(voucherType);
        return new WpfInvoicePrintService().CreateDefaultModel(
            invoice,
            customer,
            company,
            printWithDate: true,
            printWithPrice: true);
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

    private static void RenderFixedDocumentFirstPage(FixedDocument document)
    {
        var paginator = document.DocumentPaginator;
        paginator.PageSize = new Size(793.7, 1122.5);
        var page = paginator.GetPage(0);
        RenderVisual(page.Visual, paginator.PageSize);
        page.Dispose();
    }

    private static void RenderFlowDocumentFirstPage(FlowDocument document)
    {
        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.PageSize = new Size(793.7, 1122.5);
        var page = paginator.GetPage(0);
        RenderVisual(page.Visual, paginator.PageSize);
        page.Dispose();
    }

    private static void RenderVisual(Visual visual, Size size)
    {
        if (visual is UIElement element)
        {
            element.Measure(size);
            element.Arrange(new Rect(size));
            element.UpdateLayout();
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width)),
            Math.Max(1, (int)Math.Ceiling(size.Height)),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Assert.True(bitmap.Width > 0);
        Assert.True(bitmap.Height > 0);
    }

    private static string ReadFlowDocumentText(FlowDocument document)
        => new TextRange(document.ContentStart, document.ContentEnd).Text;

    private static string ReadFixedDocumentText(FixedDocument document)
    {
        var builder = new StringBuilder();
        foreach (PageContent pageContent in document.Pages)
        {
            if (pageContent.Child is null)
                continue;

            AppendText(pageContent.Child, builder);
        }

        return builder.ToString();
    }

    private static void AppendText(DependencyObject node, StringBuilder builder)
    {
        if (node is TextBlock textBlock)
        {
            var text = ReadTextBlockText(textBlock);
            if (!string.IsNullOrWhiteSpace(text))
                builder.AppendLine(text);
        }
        else if (node is TextElement textElement)
        {
            var text = new TextRange(textElement.ContentStart, textElement.ContentEnd).Text;
            if (!string.IsNullOrWhiteSpace(text))
                builder.AppendLine(text);
        }
        else if (node is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
        {
            builder.AppendLine(content);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < childCount; i++)
            AppendText(VisualTreeHelper.GetChild(node, i), builder);
    }

    private static string ReadTextBlockText(TextBlock textBlock)
    {
        if (!string.IsNullOrWhiteSpace(textBlock.Text))
            return textBlock.Text;

        var builder = new StringBuilder();
        foreach (var inline in textBlock.Inlines)
        {
            if (inline is Run run)
                builder.Append(run.Text);
        }

        return builder.ToString();
    }

    private static string NormalizeText(string text)
        => Regex.Replace(text.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
}
