using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;

namespace 거래플랜.Desktop.App.Services;

public static class SupplementDocumentBuilder
{
    private const double A4Width = 793.7;
    private const double A4Height = 1122.5;
    private const double SheetWidth = 748;
    private const double EstimateSheetHeight = 1002;
    private const double ClaimSheetHeight = 996;
    private const double LineThickness = 0.72;
    private const int EstimateRowsPerPage = 13;

    private static readonly Brush BorderBrushBlack = new SolidColorBrush(Color.FromRgb(217, 217, 217));
    private static readonly Brush TitleBrush = Brushes.Black;
    private static readonly Brush TextBrush = Brushes.Black;
    private static readonly Brush HeaderFillBrush = new SolidColorBrush(Color.FromRgb(242, 242, 242));
    private static readonly Brush BandFillBrush = new SolidColorBrush(Color.FromRgb(217, 217, 217));
    private static readonly Brush SectionRuleBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));

    // Excel source: "250602 (3)"
    private static readonly double[] EstimateColUnits =
    [
        3.56, 3.56, 3.56, 3.56, 3.56, 3.56, 5.06, 3.56, 3.56,
        3.56, 5.81, 4.06, 4.06, 3.06, 3.56, 3.56, 3.56, 3.56, 3.56
    ];

    private static readonly double[] EstimateRowUnits =
    [
        38, 17, 17, 28, 17, 17, 23, 14, 19.4, 19.4, 14, 19.4,
        17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
        19.4, 19.4, 14, 19.4, 17, 17, 17, 17, 14, 19.4
    ];

    // Excel source: "대금청구서"
    private static readonly double[] ClaimColUnits =
    [
        3.56, 4.06, 4.06, 4.06, 4.06, 3.56, 3.56, 3.56, 3.56,
        3.56, 4.69, 4.06, 4.06, 3.06, 3.56, 3.56, 3.56, 3.56, 3.56
    ];

    private static readonly double[] ClaimRowUnits =
    [
        38, 19.4, 19.4, 30, 19.4, 19.4, 23, 14, 23, 17, 17,
        46, 17, 17, 28, 28, 17, 17, 17, 17, 19.4, 19.4, 19.4,
        19.4, 19.4, 19.4, 19.4, 19.4, 19.4, 14, 19.4
    ];

    public static FixedDocument BuildEstimateDocument(LocalInvoice invoice, LocalCustomer customer, LocalCompanyProfile company)
    {
        var lines = invoice.Lines.Where(l => !l.IsDeleted).ToList();
        if (lines.Count == 0)
            lines.Add(new LocalInvoiceLine());

        var pageCount = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)EstimateRowsPerPage));
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(A4Width, A4Height);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageLines = lines
                .Skip(pageIndex * EstimateRowsPerPage)
                .Take(EstimateRowsPerPage)
                .ToList();

            var page = BuildEstimatePage(invoice, customer, company, pageLines, pageIndex + 1, pageCount, pageIndex == pageCount - 1);
            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(page);
            document.Pages.Add(pageContent);
        }

        return document;
    }

    private static FixedPage BuildEstimatePage(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        IReadOnlyList<LocalInvoiceLine> lines,
        int pageNumber,
        int totalPages,
        bool isFinalPage)
    {
        var grid = CreateSheetGrid(EstimateColUnits, EstimateRowUnits, SheetWidth, EstimateSheetHeight);
        var (supplyAmount, vatAmount, totalAmount) = ResolveTotals(invoice);
        var receiver = ResolveReceiver(customer);
        var title = totalPages > 1 ? $"견     적     서 ({pageNumber}/{totalPages})" : "견     적     서";

        AddMergedCell(grid, 1, 1, 1, 19, title,
            bold: true, fontSize: 42, align: TextAlignment.Center, foreground: TitleBrush, borderBrush: Brushes.Transparent);

        AddRecipientCell(grid, 2, 1, 6, 8, receiver);

        AddLabelCell(grid, 2, 9, 6, 9, "공\n\n급\n\n자", fontSize: 14, background: BandFillBrush);

        AddLabelCell(grid, 2, 10, 2, 11, "등록번호");
        AddMergedCell(grid, 2, 12, 2, 19, SpaceBusinessNumber(company.BusinessNumber), fontSize: 15);

        AddLabelCell(grid, 3, 10, 3, 11, "상호명");
        AddMergedCell(grid, 3, 12, 3, 14, Safe(company.TradeName, "기본 상호"));
        AddLabelCell(grid, 3, 15, 3, 16, "대   표");
        AddMergedCell(grid, 3, 17, 3, 19, Safe(company.Representative, "대표자"));

        AddLabelCell(grid, 4, 10, 4, 11, "주   소");
        AddMergedCell(grid, 4, 12, 4, 19, FormatAddressForDocument(company.Address), fontSize: 10.8, padding: new Thickness(6, 3, 6, 3), wrap: true);

        AddLabelCell(grid, 5, 10, 5, 11, "업   태");
        AddMergedCell(grid, 5, 12, 5, 14, Safe(company.BusinessType));
        AddLabelCell(grid, 5, 15, 5, 16, "종   목");
        AddMergedCell(grid, 5, 17, 5, 19, Safe(company.BusinessItem));

        AddLabelCell(grid, 6, 10, 6, 11, "연락처");
        AddMergedCell(grid, 6, 12, 6, 14, Safe(company.ContactNumber));
        AddLabelCell(grid, 6, 15, 6, 16, "팩   스");
        AddMergedCell(grid, 6, 17, 6, 19, Safe(customer.FaxNumber));

        AddLabelCell(grid, 7, 1, 7, 6, "견적금액 (공급가액+세액)", fontSize: 13);
        AddMergedCell(grid, 7, 7, 7, 19, BuildAmountLabel(totalAmount), fontSize: 13, foreground: TitleBrush);

        AddLabelCell(grid, 9, 1, 9, 3, "견적명");
        AddMergedCell(grid, 9, 4, 9, 19, Safe(invoice.Memo, "품목 견적"), wrap: true);
        AddLabelCell(grid, 10, 1, 10, 3, "견적유효기간");
        AddMergedCell(grid, 10, 4, 10, 19, $"{invoice.InvoiceDate:yyyy-MM-dd} ~ {invoice.InvoiceDate.AddDays(30):yyyy-MM-dd}");

        AddTableHeaderCell(grid, 12, 1, 12, 1, "NO");
        AddTableHeaderCell(grid, 12, 2, 12, 5, "품명");
        AddTableHeaderCell(grid, 12, 6, 12, 9, "규격");
        AddTableHeaderCell(grid, 12, 10, 12, 10, "단위");
        AddTableHeaderCell(grid, 12, 11, 12, 11, "수량");
        AddTableHeaderCell(grid, 12, 12, 12, 13, "단가");
        AddTableHeaderCell(grid, 12, 14, 12, 16, "공급가액");
        AddTableHeaderCell(grid, 12, 17, 12, 19, "비고");

        for (var i = 0; i < EstimateRowsPerPage; i++)
        {
            var row = 13 + i;
            var line = i < lines.Count ? lines[i] : null;

            AddMergedCell(grid, row, 1, row, 1, line is null ? string.Empty : (((pageNumber - 1) * EstimateRowsPerPage) + i + 1).ToString(CultureInfo.InvariantCulture), align: TextAlignment.Center);
            AddMergedCell(grid, row, 2, row, 5, line?.ItemNameOriginal ?? string.Empty, fontSize: 10.3, wrap: false, autoShrink: true);
            AddMergedCell(grid, row, 6, row, 9, line?.SpecificationOriginal ?? string.Empty, fontSize: 10.3, wrap: false, autoShrink: true);
            AddMergedCell(grid, row, 10, row, 10, line?.Unit ?? string.Empty, align: TextAlignment.Center, wrap: false);
            AddMergedCell(grid, row, 11, row, 11, line is null ? string.Empty : $"{line.Quantity:N0}", align: TextAlignment.Right, wrap: false);
            AddMergedCell(grid, row, 12, row, 13, line is null ? string.Empty : $"{line.UnitPrice:N0}", align: TextAlignment.Right, wrap: false);
            AddMergedCell(grid, row, 14, row, 16, line is null ? string.Empty : $"{line.LineAmount:N0}", align: TextAlignment.Right, wrap: false);
            AddMergedCell(grid, row, 17, row, 19, line?.Remark ?? string.Empty, fontSize: 10.1, wrap: false);
        }

        AddLabelCell(grid, 26, 1, 26, 11, "공  급  가  액  합  계", fontSize: 13, background: BandFillBrush);
        AddMergedCell(grid, 26, 12, 26, 13, string.Empty);
        AddMergedCell(grid, 26, 14, 26, 16, isFinalPage ? $"{supplyAmount:N0}" : string.Empty, bold: true, align: TextAlignment.Right, fontSize: 13);
        AddMergedCell(grid, 26, 17, 26, 19, string.Empty);

        AddLabelCell(grid, 27, 1, 27, 11, "부  가  세  액", fontSize: 13, background: BandFillBrush);
        AddMergedCell(grid, 27, 12, 27, 13, string.Empty);
        AddMergedCell(grid, 27, 14, 27, 16, isFinalPage ? $"{vatAmount:N0}" : string.Empty, bold: true, align: TextAlignment.Right, fontSize: 13);
        AddMergedCell(grid, 27, 17, 27, 19, isFinalPage ? "." : string.Empty);

        AddLabelCell(grid, 29, 1, 29, 3, isFinalPage ? "특이사항" : "안내");
        AddMergedCell(grid, 29, 4, 29, 19, isFinalPage ? string.Empty : "다음 페이지에 견적 내역이 계속됩니다.", wrap: false);

        AddStampOverlay(grid, company.StampImage, 3, 18, 4, 19);
        AddSectionRule(grid, 2, true);
        AddSectionRule(grid, 7, false);
        AddSectionRule(grid, 12, true);
        AddSectionRule(grid, 29, true);
        AddSectionRule(grid, 35, true);
        AddFooterRow(grid, 35, company);
        return BuildCenteredSinglePage(grid);
    }

    public static FixedDocument BuildPaymentClaimDocument(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        IReadOnlyList<string>? attachmentDisplayNames = null)
    {
        var grid = CreateSheetGrid(ClaimColUnits, ClaimRowUnits, SheetWidth, ClaimSheetHeight);
        var (supplyAmount, vatAmount, totalAmount) = ResolveTotals(invoice);
        var receiver = ResolveReceiver(customer);
        var attachmentText = BuildAttachmentListText(attachmentDisplayNames);
        var attachmentLineCount = attachmentText
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .Length;
        var attachmentBodyEndRow = Math.Min(28, 21 + Math.Max(3, attachmentLineCount + 1));
        var footerRow = Math.Min(31, attachmentBodyEndRow + 2);

        AddMergedCell(grid, 1, 1, 1, 19, "대 금 청 구 서",
            bold: true, fontSize: 42, align: TextAlignment.Center, foreground: TitleBrush, borderBrush: Brushes.Transparent);

        AddRecipientCell(grid, 2, 1, 6, 8, receiver);

        AddLabelCell(grid, 2, 9, 6, 9, "공\n\n급\n\n자", fontSize: 14, background: BandFillBrush);

        AddLabelCell(grid, 2, 10, 2, 11, "등록번호");
        AddMergedCell(grid, 2, 12, 2, 19, SpaceBusinessNumber(company.BusinessNumber), fontSize: 15);

        AddLabelCell(grid, 3, 10, 3, 11, "상호명");
        AddMergedCell(grid, 3, 12, 3, 14, Safe(company.TradeName, "기본 상호"));
        AddLabelCell(grid, 3, 15, 3, 16, "대   표");
        AddMergedCell(grid, 3, 17, 3, 19, Safe(company.Representative, "대표자"));

        AddLabelCell(grid, 4, 10, 4, 11, "주   소");
        AddMergedCell(grid, 4, 12, 4, 19, FormatAddressForDocument(company.Address), fontSize: 10.8, padding: new Thickness(6, 3, 6, 3), wrap: true);

        AddLabelCell(grid, 5, 10, 5, 11, "업   태");
        AddMergedCell(grid, 5, 12, 5, 14, Safe(company.BusinessType));
        AddLabelCell(grid, 5, 15, 5, 16, "종   목");
        AddMergedCell(grid, 5, 17, 5, 19, Safe(company.BusinessItem));

        AddLabelCell(grid, 6, 10, 6, 11, "연락처");
        AddMergedCell(grid, 6, 12, 6, 14, Safe(company.ContactNumber));
        AddLabelCell(grid, 6, 15, 6, 16, "팩   스");
        AddMergedCell(grid, 6, 17, 6, 19, Safe(customer.FaxNumber));

        AddLabelCell(grid, 7, 1, 7, 6, "청구금액 (공급가액+세액)", fontSize: 13);
        AddMergedCell(grid, 7, 7, 7, 19, BuildAmountLabel(totalAmount), fontSize: 13, foreground: TitleBrush);

        AddLabelCell(grid, 9, 1, 9, 3, "용역명");
        AddMergedCell(grid, 9, 4, 9, 19, Safe(invoice.Memo, "납품 대금"), wrap: true);

        AddMergedCell(grid, 12, 1, 12, 19, "위 금액을 청구하오니 지급하여 주시기 바랍니다.", fontSize: 14);

        AddTableHeaderCell(grid, 15, 1, 15, 3, "금융기관명");
        AddTableHeaderCell(grid, 15, 4, 15, 7, "입금은행/계좌번호");
        AddTableHeaderCell(grid, 15, 8, 15, 10, "예금주");
        AddTableHeaderCell(grid, 15, 11, 15, 13, "공급가액");
        AddTableHeaderCell(grid, 15, 14, 15, 16, "세액");
        AddTableHeaderCell(grid, 15, 17, 15, 19, "합계금액");

        var (bankName, accountNo, depositor) = ParseBankAccount(company);
        AddMergedCell(grid, 16, 1, 16, 3, bankName);
        AddMergedCell(grid, 16, 4, 16, 7, accountNo);
        AddMergedCell(grid, 16, 8, 16, 10, depositor);
        AddMergedCell(grid, 16, 11, 16, 13, $"{supplyAmount:N0}", align: TextAlignment.Right);
        AddMergedCell(grid, 16, 14, 16, 16, $"{vatAmount:N0}", align: TextAlignment.Right);
        AddMergedCell(grid, 16, 17, 16, 19, $"{totalAmount:N0}", align: TextAlignment.Right, bold: true);

        AddMergedCell(grid, 21, 1, 21, 19, "[ 첨부서류 ]", bold: true);
        AddMergedCell(
            grid,
            22,
            1,
            attachmentBodyEndRow,
            19,
            attachmentText,
            vertical: VerticalAlignment.Top,
            padding: new Thickness(10, 6, 10, 6),
            wrap: true);

        AddStampOverlay(grid, company.StampImage, 3, 18, 4, 19);
        AddSectionRule(grid, 2, true);
        AddSectionRule(grid, 7, false);
        AddSectionRule(grid, 15, true);
        AddSectionRule(grid, 21, true);
        AddSectionRule(grid, footerRow, true);
        AddFooterRow(grid, footerRow, company);
        return BuildCenteredSinglePageDocument(grid);
    }

    public static FixedDocument MergeDocuments(IEnumerable<FixedDocument> documents)
    {
        var list = documents?.Where(d => d is not null).ToList() ?? [];
        if (list.Count == 0)
            throw new InvalidOperationException("병합할 문서가 없습니다.");
        if (list.Count == 1)
            return list[0];

        var merged = new FixedDocument();
        merged.DocumentPaginator.PageSize = new Size(A4Width, A4Height);

        foreach (var source in list)
        {
            foreach (var sourcePageContent in source.Pages)
            {
                var sourcePage = sourcePageContent.GetPageRoot(true);
                if (sourcePage is null)
                    continue;

                var clonedPage = CloneFixedPage(sourcePage);
                var targetPageContent = new PageContent();
                ((IAddChild)targetPageContent).AddChild(clonedPage);
                merged.Pages.Add(targetPageContent);
            }
        }

        return merged;
    }

    public static FixedDocument BuildAttachmentPlaceholderDocument(string title)
    {
        var page = new FixedPage
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var frame = new Border
        {
            Width = SheetWidth,
            Height = 360,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Background = Brushes.White,
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontFamily = new FontFamily("맑은 고딕"),
                        FontSize = 34,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.Black,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 24)
                    },
                    new TextBlock
                    {
                        Text = "첨부서류 출력 페이지",
                        FontFamily = new FontFamily("맑은 고딕"),
                        FontSize = 18,
                        Foreground = Brushes.Black,
                        TextAlignment = TextAlignment.Center
                    }
                }
            }
        };

        FixedPage.SetLeft(frame, (A4Width - frame.Width) / 2d);
        FixedPage.SetTop(frame, (A4Height - frame.Height) / 2d);
        page.Children.Add(frame);
        page.Measure(new Size(A4Width, A4Height));
        page.Arrange(new Rect(0, 0, A4Width, A4Height));
        page.UpdateLayout();

        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(A4Width, A4Height);
        var pageContent = new PageContent();
        ((IAddChild)pageContent).AddChild(page);
        document.Pages.Add(pageContent);
        return document;
    }

    private static Grid CreateSheetGrid(double[] colUnits, double[] rowUnits, double width, double height)
    {
        var grid = new Grid
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var colSum = colUnits.Sum();
        foreach (var col in colUnits)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(width * (col / colSum))
            });
        }

        var rowSum = rowUnits.Sum();
        foreach (var row in rowUnits)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(height * (row / rowSum))
            });
        }

        return grid;
    }

    private static FixedDocument BuildCenteredSinglePageDocument(Grid sheetGrid)
    {
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(A4Width, A4Height);
        var pageContent = new PageContent();
        ((IAddChild)pageContent).AddChild(BuildCenteredSinglePage(sheetGrid));
        doc.Pages.Add(pageContent);
        return doc;
    }

    private static FixedPage BuildCenteredSinglePage(Grid sheetGrid)
    {
        var frame = new Border
        {
            Width = sheetGrid.Width,
            Height = sheetGrid.Height,
            Background = Brushes.White,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = sheetGrid,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var page = new FixedPage
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        FixedPage.SetLeft(frame, (A4Width - frame.Width) / 2d);
        FixedPage.SetTop(frame, (A4Height - frame.Height) / 2d);
        page.Children.Add(frame);

        page.Measure(new Size(A4Width, A4Height));
        page.Arrange(new Rect(0, 0, A4Width, A4Height));
        page.UpdateLayout();

        return page;
    }

    private static void AddFooterRow(Grid grid, int row, LocalCompanyProfile company)
    {
        AddLabelCell(grid, row, 1, row, 2, "담당자");
        AddMergedCell(grid, row, 3, row, 5, Safe(company.Representative, "대표자"));
        AddLabelCell(grid, row, 6, row, 7, "연락처");
        AddMergedCell(grid, row, 8, row, 10, Safe(company.ContactNumber));
        AddLabelCell(grid, row, 11, row, 13, "결재방법");
        AddMergedCell(grid, row, 14, row, 14, "□", align: TextAlignment.Center);
        AddMergedCell(grid, row, 15, row, 15, "현금", align: TextAlignment.Center);
        AddMergedCell(grid, row, 16, row, 16, "□", align: TextAlignment.Center);
        AddMergedCell(grid, row, 17, row, 17, "카드", align: TextAlignment.Center);
        AddMergedCell(grid, row, 18, row, 18, "□", align: TextAlignment.Center);
        AddMergedCell(grid, row, 19, row, 19, "기타", align: TextAlignment.Center);
    }

    private static void AddLabelCell(
        Grid grid,
        int rowStart,
        int colStart,
        int rowEnd,
        int colEnd,
        string text,
        double fontSize = 12,
        Brush? background = null)
        => AddMergedCell(
            grid,
            rowStart,
            colStart,
            rowEnd,
            colEnd,
            text,
            bold: true,
            align: TextAlignment.Center,
            fontSize: fontSize,
            background: background ?? HeaderFillBrush);

    private static void AddTableHeaderCell(
        Grid grid,
        int rowStart,
        int colStart,
        int rowEnd,
        int colEnd,
        string text,
        double fontSize = 13)
        => AddMergedCell(
            grid,
            rowStart,
            colStart,
            rowEnd,
            colEnd,
            text,
            bold: true,
            align: TextAlignment.Center,
            fontSize: fontSize,
            background: HeaderFillBrush);

    private static void AddMergedCell(
        Grid grid,
        int rowStart,
        int colStart,
        int rowEnd,
        int colEnd,
        string text,
        bool bold = false,
        TextAlignment align = TextAlignment.Left,
        Brush? foreground = null,
        Brush? borderBrush = null,
        Brush? background = null,
        double fontSize = 12,
        VerticalAlignment vertical = VerticalAlignment.Center,
        Thickness? padding = null,
        Thickness? borderThickness = null,
        bool wrap = false,
        bool autoShrink = false)
    {
        var cellText = new TextBlock
        {
            Text = text ?? string.Empty,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = fontSize,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = foreground ?? TextBrush,
            TextAlignment = align,
            VerticalAlignment = vertical,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap || autoShrink ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };

        if (autoShrink)
        {
            cellText.HorizontalAlignment = align switch
            {
                TextAlignment.Center => HorizontalAlignment.Center,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            };
        }

        var cell = new Border
        {
            BorderBrush = borderBrush ?? BorderBrushBlack,
            BorderThickness = borderThickness ?? new Thickness(LineThickness),
            Background = background ?? Brushes.White,
            Padding = padding ?? new Thickness(4, 1.5, 4, 1.5),
            Child = autoShrink
                ? new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    Child = cellText
                }
                : cellText,
            SnapsToDevicePixels = true
        };

        Grid.SetRow(cell, rowStart - 1);
        Grid.SetColumn(cell, colStart - 1);
        Grid.SetRowSpan(cell, rowEnd - rowStart + 1);
        Grid.SetColumnSpan(cell, colEnd - colStart + 1);
        grid.Children.Add(cell);
    }

    private static void AddSectionRule(Grid grid, int row, bool top)
    {
        if (row < 1 || row > grid.RowDefinitions.Count)
            return;

        var rule = new Border
        {
            Background = SectionRuleBrush,
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            SnapsToDevicePixels = true
        };

        Grid.SetRow(rule, row - 1);
        Grid.SetColumn(rule, 0);
        Grid.SetColumnSpan(rule, grid.ColumnDefinitions.Count);
        Panel.SetZIndex(rule, 30);
        grid.Children.Add(rule);
    }

    private static void AddRecipientCell(
        Grid grid,
        int rowStart,
        int colStart,
        int rowEnd,
        int colEnd,
        string receiver)
    {
        var recipientText = new TextBlock
        {
            Text = $"{Safe(receiver, "거래처")} 귀하",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 14,
            FontWeight = FontWeights.Normal,
            Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var border = new Border
        {
            BorderBrush = BorderBrushBlack,
            BorderThickness = new Thickness(LineThickness),
            Background = Brushes.White,
            Padding = new Thickness(10, 6, 10, 6),
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = recipientText
            },
            SnapsToDevicePixels = true
        };

        Grid.SetRow(border, rowStart - 1);
        Grid.SetColumn(border, colStart - 1);
        Grid.SetRowSpan(border, rowEnd - rowStart + 1);
        Grid.SetColumnSpan(border, colEnd - colStart + 1);
        grid.Children.Add(border);
    }

    private static string FormatAddressForDocument(string? address)
    {
        var normalized = Safe(address);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length <= 28)
            return normalized;

        var breakIndex = normalized.LastIndexOf(' ', Math.Min(30, normalized.Length - 1));
        if (breakIndex < 12)
            breakIndex = normalized.IndexOf(' ', 18);
        if (breakIndex < 0)
            return normalized;

        return $"{normalized[..breakIndex].Trim()}\n{normalized[(breakIndex + 1)..].Trim()}";
    }

    private static void AddStampOverlay(
        Grid grid,
        byte[]? stampBytes,
        int rowStart,
        int colStart,
        int rowEnd,
        int colEnd)
    {
        if (stampBytes is not { Length: > 0 })
            return;

        var stamp = TryCreateStampImage(stampBytes);
        if (stamp is null)
            return;

        var stampImage = new Image
        {
            Source = stamp,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(2),
            SnapsToDevicePixels = true
        };

        Grid.SetRow(stampImage, rowStart - 1);
        Grid.SetColumn(stampImage, colStart - 1);
        Grid.SetRowSpan(stampImage, rowEnd - rowStart + 1);
        Grid.SetColumnSpan(stampImage, colEnd - colStart + 1);
        Panel.SetZIndex(stampImage, 50);
        grid.Children.Add(stampImage);
    }

    private static string BuildAttachmentListText(IReadOnlyList<string>? attachmentDisplayNames)
    {
        var names = (attachmentDisplayNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            names = [AttachmentDocumentCatalog.GetDisplayName(AttachmentDocumentCatalog.PaymentClaim)];

        return string.Join(Environment.NewLine, names.Select((name, index) => $"{index + 1}. {name}"));
    }

    private static BitmapImage? TryCreateStampImage(byte[] stampBytes)
    {
        try
        {
            using var stream = new MemoryStream(stampBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static (decimal SupplyAmount, decimal VatAmount, decimal TotalAmount) ResolveTotals(LocalInvoice invoice)
    {
        var linesTotal = invoice.Lines.Where(l => !l.IsDeleted).Sum(l => l.LineAmount);
        var total = invoice.TotalAmount > 0 ? invoice.TotalAmount : linesTotal;
        if (total <= 0)
            return (0m, 0m, 0m);

        var supply = invoice.SupplyAmount > 0
            ? invoice.SupplyAmount
            : Math.Round(total / 1.1m, 0, MidpointRounding.AwayFromZero);
        var vat = invoice.VatAmount > 0
            ? invoice.VatAmount
            : total - supply;

        return (supply, vat, total);
    }

    private static string BuildAmountLabel(decimal amount)
    {
        return $"일금 {amount:N0} 원정   ( ₩{amount:N0} )";
    }

    private static string ResolveReceiver(LocalCustomer customer)
    {
        if (!string.IsNullOrWhiteSpace(customer.Recipient))
            return customer.Recipient.Trim();
        if (!string.IsNullOrWhiteSpace(customer.NameOriginal))
            return customer.NameOriginal.Trim();
        return "거래처";
    }

    private static string Safe(string? value, string fallback = "")
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string SpaceBusinessNumber(string? businessNumber)
    {
        if (string.IsNullOrWhiteSpace(businessNumber))
            return string.Empty;
        return string.Join(" ", businessNumber.Trim().ToCharArray());
    }

    private static (string BankName, string AccountNo, string Depositor) ParseBankAccount(LocalCompanyProfile company)
    {
        var raw = company.BankAccountText ?? string.Empty;
        var bankName = string.Empty;
        var accountNo = string.Empty;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var match = Regex.Match(raw, @"\d{2,4}(?:-\d+)+");
            if (match.Success)
                accountNo = match.Value;

            bankName = match.Success
                ? raw.Replace(match.Value, string.Empty).Trim('-', '/', ' ', '\t')
                : raw.Trim();
        }

        if (string.IsNullOrWhiteSpace(bankName))
            bankName = "입금은행/계좌번호";
        if (string.IsNullOrWhiteSpace(accountNo))
            accountNo = "입금은행/계좌번호를 입력하세요";

        var depositor = !string.IsNullOrWhiteSpace(company.Representative)
            ? company.Representative
            : company.TradeName;
        if (string.IsNullOrWhiteSpace(depositor))
            depositor = "대표자";

        return (bankName, accountNo, depositor);
    }

    private static FixedPage CloneFixedPage(FixedPage source)
    {
        try
        {
            var xaml = XamlWriter.Save(source);
            using var stringReader = new StringReader(xaml);
            using var xmlReader = XmlReader.Create(stringReader);
            return (FixedPage)XamlReader.Load(xmlReader);
        }
        catch
        {
            return CloneFixedPageAsBitmap(source);
        }
    }

    private static FixedPage CloneFixedPageAsBitmap(FixedPage source)
    {
        source.Measure(new Size(A4Width, A4Height));
        source.Arrange(new Rect(0, 0, A4Width, A4Height));
        source.UpdateLayout();

        var raster = new RenderTargetBitmap(
            (int)Math.Ceiling(A4Width),
            (int)Math.Ceiling(A4Height),
            96,
            96,
            PixelFormats.Pbgra32);
        raster.Render(source);
        raster.Freeze();

        var page = new FixedPage
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        page.Children.Add(new Image
        {
            Source = raster,
            Width = A4Width,
            Height = A4Height,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        });

        page.Measure(new Size(A4Width, A4Height));
        page.Arrange(new Rect(0, 0, A4Width, A4Height));
        page.UpdateLayout();
        return page;
    }
}
