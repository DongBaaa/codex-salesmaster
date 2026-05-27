using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public static class ProcurementDocumentBuilder
{
    private const double A4Width = 793.7;
    private const double A4Height = 1122.5;
    private static readonly Thickness PageFrameMargin = new(48, 60, 48, 64);
    private const double LineThickness = 1.0;
    private const double FrameLineThickness = 1.15;
    private const double HeaderHeight = 250;
    private const double SummaryRowHeight = 36;
    private const double FooterHeight = 52;
    private const double ItemsHeaderHeight = 28;
    private const double ItemRowHeight = 28.2;
    private const double TotalsRowHeight = 32.2;
    private const int RowsPerPage = 19;

    private static readonly Brush BorderBrush = Brushes.Black;
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(216, 232, 235));
    private static readonly Brush SideFill = new SolidColorBrush(Color.FromRgb(216, 232, 235));
    private static readonly Brush TextBrush = Brushes.Black;

    public static FixedDocument BuildDocument(InvoicePrintModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var lines = NormalizeLines(model.Lines);
        if (lines.Count == 0)
            lines.Add(new InvoicePrintLineModel { No = 1 });

        var pageCount = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)RowsPerPage));
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(A4Width, A4Height);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageLines = lines
                .Skip(pageIndex * RowsPerPage)
                .Take(RowsPerPage)
                .ToList();

            var page = BuildPage(model, pageLines, pageIndex + 1, pageCount, pageIndex == pageCount - 1);
            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(page);
            document.Pages.Add(pageContent);
        }

        return document;
    }

    private static FixedPage BuildPage(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines,
        int pageNumber,
        int totalPages,
        bool isFinalPage)
    {
        var title = ResolveTitle(model.DocumentTitle);
        var totalAmount = model.TotalAmount > 0
            ? model.TotalAmount
            : lines.Sum(line => line.Amount);

        var page = new FixedPage
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var frameWidth = A4Width - PageFrameMargin.Left - PageFrameMargin.Right;
        var frameHeight = A4Height - PageFrameMargin.Top - PageFrameMargin.Bottom;
        var root = new Grid
        {
            Width = frameWidth,
            Height = frameHeight,
            Background = Brushes.White
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SummaryRowHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FooterHeight) });

        AddToGrid(root, BuildHeader(title, model, frameWidth), 0);
        AddToGrid(root, BuildTotalSummary(totalAmount, model.PrintWithPrice), 1);
        AddToGrid(root, BuildItemsTable(lines, model, isFinalPage), 2);
        AddToGrid(root, BuildPageFooter(model, pageNumber, totalPages), 3);

        var outerFrame = new Border
        {
            Width = frameWidth,
            Height = frameHeight,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(FrameLineThickness),
            Background = Brushes.White,
            Child = root
        };

        FixedPage.SetLeft(outerFrame, PageFrameMargin.Left);
        FixedPage.SetTop(outerFrame, PageFrameMargin.Top);
        page.Children.Add(outerFrame);
        page.Measure(new Size(A4Width, A4Height));
        page.Arrange(new Rect(0, 0, A4Width, A4Height));
        page.UpdateLayout();
        return page;
    }

    private static UIElement BuildHeader(string title, InvoicePrintModel model, double frameWidth)
    {
        var canvas = new Canvas
        {
            Width = frameWidth,
            Height = HeaderHeight,
            ClipToBounds = true
        };

        var titlePanel = new StackPanel
        {
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        titlePanel.Children.Add(CreateText(FormatDisplayedTitle(title), 31, FontWeights.Bold, TextAlignment.Center));
        titlePanel.Children.Add(new Border
        {
            Height = LineThickness,
            Width = 220,
            Background = BorderBrush,
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        titlePanel.Children.Add(new Border
        {
            Height = LineThickness,
            Width = 220,
            Background = BorderBrush,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        Canvas.SetLeft(titlePanel, (frameWidth - titlePanel.Width) / 2d);
        Canvas.SetTop(titlePanel, 42);
        canvas.Children.Add(titlePanel);

        var dateText = CreateText($"서기 : {model.InvoiceDate:yyyy} 년 {model.InvoiceDate:MM} 월 {model.InvoiceDate:dd} 일", 12.4);
        Canvas.SetLeft(dateText, 10);
        Canvas.SetTop(dateText, 120);
        canvas.Children.Add(dateText);

        var recipient = BuildRecipientPanel(model);
        Canvas.SetLeft(recipient, 8);
        Canvas.SetTop(recipient, 153);
        canvas.Children.Add(recipient);

        var supplier = BuildSupplierPanel(model);
        Canvas.SetLeft(supplier, frameWidth - 336);
        Canvas.SetTop(supplier, 128);
        canvas.Children.Add(supplier);

        var requestText = CreateText(
            ResolveRequestPhrase(title),
            13.2,
            margin: new Thickness(0));
        Canvas.SetLeft(requestText, 10);
        Canvas.SetTop(requestText, 218);
        canvas.Children.Add(requestText);

        return canvas;
    }

    private static UIElement BuildRecipientPanel(InvoicePrintModel model)
    {
        var stack = new StackPanel
        {
            Width = 286,
            Height = 62,
            Orientation = Orientation.Vertical
        };

        var recipientGrid = new Grid { Height = 30 };
        recipientGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recipientGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var recipientLine = new TextBlock
        {
            Text = Safe(model.SupplierName, "거래처"),
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None
        };
        recipientGrid.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = recipientLine
        });

        var honorific = CreateText("귀하", 18, FontWeights.Normal, margin: new Thickness(10, 0, 0, 0));
        Grid.SetColumn(honorific, 1);
        recipientGrid.Children.Add(honorific);
        stack.Children.Add(recipientGrid);

        stack.Children.Add(new Border
        {
            Height = LineThickness,
            Background = BorderBrush,
            Margin = new Thickness(0, 1, 0, 8)
        });

        stack.Children.Add(CreateText($"대표전화 : {Safe(model.SupplierPhone)}", 12.4, margin: new Thickness(4, 0, 0, 0)));
        return stack;
    }

    private static UIElement BuildSupplierPanel(InvoicePrintModel model)
    {
        var panel = new Grid
        {
            Width = 336,
            Height = 122
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(59) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });

        AddCell(panel, 0, 0, "공\n\n급\n\n자", background: SideFill, center: true, bold: true, rowSpan: 4, padding: new Thickness(0), fontSize: 12.2);
        AddCell(panel, 0, 1, "사업번호", background: HeaderFill, center: true, bold: true, fontSize: 11.2);
        AddCell(panel, 0, 2, Safe(model.BuyerBusinessNumber), columnSpan: 3, fontSize: 11.6);
        AddCell(panel, 1, 1, "상      호", background: HeaderFill, center: true, bold: true, fontSize: 11.2);
        AddCell(panel, 1, 2, Safe(model.BuyerName), columnSpan: 3, fontSize: 11.6);
        AddCell(panel, 2, 1, "전화번호", background: HeaderFill, center: true, bold: true, fontSize: 11.2);
        AddCell(panel, 2, 2, Safe(model.BuyerPhone), fontSize: 11.2);
        AddCell(panel, 2, 3, "대표자", background: HeaderFill, center: true, bold: true, fontSize: 11.2);
        AddCell(panel, 2, 4, Safe(model.BuyerRepresentative), fontSize: 11.2);
        AddCell(panel, 3, 1, "주      소", background: HeaderFill, center: true, bold: true, fontSize: 11.2);
        AddCell(panel, 3, 2, Safe(model.BuyerAddress), wrap: true, padding: new Thickness(6, 2, 6, 2), fontSize: 10.6, columnSpan: 3);

        var frame = new Border
        {
            Width = panel.Width,
            Height = panel.Height,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(LineThickness),
            Child = panel
        };

        var stamp = TryCreateStampImage(model.SupplierStampImage);
        if (stamp is null)
            return frame;

        var overlay = new Canvas
        {
            Width = panel.Width,
            Height = panel.Height
        };
        overlay.Children.Add(frame);
        var image = new Image
        {
            Source = stamp,
            Width = 68,
            Height = 68,
            Stretch = Stretch.Uniform
        };
        Canvas.SetLeft(image, panel.Width - 62);
        Canvas.SetTop(image, 45);
        overlay.Children.Add(image);

        return overlay;
    }

    private static UIElement BuildTotalSummary(decimal totalAmount, bool printWithPrice)
    {
        var grid = new Grid { Height = SummaryRowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });

        var rounded = Math.Round(totalAmount, 0, MidpointRounding.AwayFromZero);
        var totalText = printWithPrice ? $"{rounded:N0}" : string.Empty;
        AddThinBorderedText(grid, 0, "합계금액 :", HeaderFill, FontWeights.Bold, TextAlignment.Center);
        AddThinBorderedText(grid, 1, printWithPrice ? $"금 {totalText} 원정" : "금            원정", Brushes.White, FontWeights.Normal, TextAlignment.Left);
        AddThinBorderedText(grid, 2, printWithPrice ? $"( {totalText} )" : "(     )", Brushes.White, FontWeights.Normal, TextAlignment.Center);
        AddThinBorderedText(grid, 3, "부가세별도", HeaderFill, FontWeights.Bold, TextAlignment.Center);

        return grid;
    }

    private static UIElement BuildItemsTable(
        IReadOnlyList<InvoicePrintLineModel> lines,
        InvoicePrintModel model,
        bool isFinalPage)
    {
        var visibleRows = RowsPerPage;

        var table = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });

        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ItemsHeaderHeight) });
        for (var row = 0; row < visibleRows; row++)
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ItemRowHeight) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TotalsRowHeight) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TotalsRowHeight) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TotalsRowHeight) });

        AddThinCell(table, 0, 0, "No", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 1, "품        명       /      규        격", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 2, "단위", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 3, "수    량", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 4, "단      가", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 5, "금      액", background: HeaderFill, center: true, bold: true);

        for (var index = 0; index < visibleRows; index++)
        {
            var row = index + 1;
            var line = index < lines.Count ? lines[index] : null;
            var itemText = line is null
                ? string.Empty
                : FormatItemAndSpecification(line);

            if (line is null && index == lines.Count && lines.Count > 0)
                itemText = "*** 이하여백 ***";

            AddThinCell(table, row, 0, line?.No.ToString() ?? string.Empty, center: true, fontSize: 10.2);
            AddThinCell(table, row, 1, itemText, padding: new Thickness(8, 2, 6, 2), fontSize: 11.0);
            AddThinCell(table, row, 2, line?.Unit ?? string.Empty, center: true, fontSize: 10.8);
            AddThinCell(table, row, 3, line is null ? string.Empty : FormatQuantity(line.Quantity), align: TextAlignment.Right);
            AddThinCell(table, row, 4, line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.UnitPrice), align: TextAlignment.Right);
            AddThinCell(table, row, 5, line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.Amount), align: TextAlignment.Right);
        }

        var supplyAmount = model.SupplyAmount;
        var vatAmount = model.VatAmount;
        var totalAmount = model.TotalAmount;
        var supplyText = isFinalPage && model.PrintWithPrice ? FormatMoney(supplyAmount) : string.Empty;
        var vatText = isFinalPage && model.PrintWithPrice ? FormatMoney(vatAmount) : string.Empty;
        var totalText = isFinalPage && model.PrintWithPrice ? FormatMoney(totalAmount) : string.Empty;
        if (isFinalPage && model.TotalAmount == 0)
        {
            var lineTotal = lines.Sum(line => line.Amount);
            var totals = InvoiceVatModes.CalculateTotals([lineTotal], model.VatMode);
            supplyAmount = totals.SupplyAmount;
            vatAmount = totals.VatAmount;
            totalAmount = lineTotal;
            supplyText = model.PrintWithPrice ? FormatMoney(supplyAmount) : string.Empty;
            vatText = model.PrintWithPrice ? FormatMoney(vatAmount) : string.Empty;
            totalText = model.PrintWithPrice ? FormatMoney(totalAmount) : string.Empty;
        }

        var totalsRowStart = visibleRows + 1;
        AddThinCell(table, totalsRowStart, 0, string.Empty, columnSpan: 4);
        AddThinCell(table, totalsRowStart + 1, 0, string.Empty, columnSpan: 4);
        AddThinCell(table, totalsRowStart + 2, 0, string.Empty, columnSpan: 4);

        AddThinCell(table, totalsRowStart, 4, "공  급  가", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, totalsRowStart, 5, supplyText, align: TextAlignment.Right);
        AddThinCell(table, totalsRowStart + 1, 4, "부  가  세", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, totalsRowStart + 1, 5, vatText, align: TextAlignment.Right);
        AddThinCell(table, totalsRowStart + 2, 4, "합 계 금 액", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, totalsRowStart + 2, 5, totalText, align: TextAlignment.Right, bold: isFinalPage);

        return table;
    }

    private static UIElement BuildPageFooter(InvoicePrintModel model, int pageNumber, int totalPages)
    {
        var grid = new Grid { Height = FooterHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerText = ResolveFooterText(model);
        var footer = CreateText(footerText, 12.4, margin: new Thickness(10, 7, 20, 0));
        footer.TextWrapping = TextWrapping.Wrap;
        footer.LineHeight = 20;
        footer.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(footer, 0);
        grid.Children.Add(footer);

        var pageText = CreateText($"Page: {pageNumber} / {totalPages}", 12.4, margin: new Thickness(12, 0, 14, 8));
        pageText.VerticalAlignment = VerticalAlignment.Bottom;
        pageText.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(pageText, 1);
        grid.Children.Add(pageText);
        return grid;
    }

    private static void AddCell(
        Grid grid,
        int row,
        int column,
        string text,
        Brush? background = null,
        bool center = false,
        bool bold = false,
        TextAlignment align = TextAlignment.Left,
        bool wrap = false,
        Thickness? padding = null,
        double fontSize = 9.8,
        int rowSpan = 1,
        int columnSpan = 1)
    {
        var finalAlignment = center ? TextAlignment.Center : align;
        var block = CreateText(text, fontSize, bold ? FontWeights.Bold : FontWeights.Normal, finalAlignment);
        block.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        block.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;

        var border = new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = GetInnerBorderThickness(grid, row, column, rowSpan, columnSpan),
            Background = background ?? Brushes.White,
            Padding = padding ?? new Thickness(4, 1, 4, 1),
            Child = block
        };

        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        Grid.SetRowSpan(border, rowSpan);
        Grid.SetColumnSpan(border, columnSpan);
        grid.Children.Add(border);
    }

    private static void AddThinCell(
        Grid grid,
        int row,
        int column,
        string text,
        Brush? background = null,
        bool center = false,
        bool bold = false,
        TextAlignment align = TextAlignment.Left,
        bool wrap = false,
        Thickness? padding = null,
        double fontSize = 9.6,
        int rowSpan = 1,
        int columnSpan = 1)
    {
        var finalAlignment = center ? TextAlignment.Center : align;
        var block = CreateText(text, fontSize, bold ? FontWeights.Bold : FontWeights.Normal, finalAlignment);
        block.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        block.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;

        var border = new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = GetSingleLineBorderThickness(grid, row, column, rowSpan, columnSpan),
            Background = background ?? Brushes.White,
            Padding = padding ?? new Thickness(4, 1, 4, 1),
            Child = block
        };

        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        Grid.SetRowSpan(border, rowSpan);
        Grid.SetColumnSpan(border, columnSpan);
        grid.Children.Add(border);
    }

    private static void AddBorderedText(
        Grid grid,
        int column,
        string text,
        Brush background,
        FontWeight fontWeight,
        TextAlignment alignment,
        int row = 0,
        int? explicitColumn = null)
    {
        var border = new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = GetInnerBorderThickness(grid, row, explicitColumn ?? column, 1, 1),
            Background = background,
            Padding = new Thickness(6, 2, 6, 2),
            Child = CreateText(text, 10.6, fontWeight, alignment)
        };

        Grid.SetRow(border, row);
        Grid.SetColumn(border, explicitColumn ?? column);
        grid.Children.Add(border);
    }

    private static void AddThinBorderedText(
        Grid grid,
        int column,
        string text,
        Brush background,
        FontWeight fontWeight,
        TextAlignment alignment,
        int row = 0)
    {
        var border = new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = GetSingleLineBorderThickness(grid, row, column, 1, 1),
            Background = background,
            Padding = new Thickness(8, 2, 8, 2),
            Child = CreateText(text, 12.4, fontWeight, alignment)
        };

        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static BitmapImage? TryCreateStampImage(byte[]? stampBytes)
    {
        if (stampBytes is not { Length: > 0 })
            return null;

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

    private static TextBlock CreateText(
        string text,
        double fontSize,
        FontWeight? fontWeight = null,
        TextAlignment textAlignment = TextAlignment.Left,
        Thickness? margin = null)
    {
        return new TextBlock
        {
            Text = text ?? string.Empty,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = fontSize,
            FontWeight = fontWeight ?? FontWeights.Normal,
            Foreground = TextBrush,
            TextAlignment = textAlignment,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? new Thickness(0)
        };
    }

    private static void AddToGrid(Grid grid, UIElement child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static Thickness GetInnerBorderThickness(Grid grid, int row, int column, int rowSpan, int columnSpan)
    {
        var right = column + columnSpan - 1 < grid.ColumnDefinitions.Count - 1 ? LineThickness : 0d;
        var bottom = row + rowSpan - 1 < grid.RowDefinitions.Count - 1 ? LineThickness : 0d;
        return new Thickness(0, 0, right, bottom);
    }

    private static Thickness GetSingleLineBorderThickness(Grid grid, int row, int column, int rowSpan, int columnSpan)
    {
        var left = column == 0 ? LineThickness : 0d;
        var top = row == 0 ? LineThickness : 0d;
        var right = LineThickness;
        var bottom = LineThickness;
        return new Thickness(left, top, right, bottom);
    }

    private static List<InvoicePrintLineModel> NormalizeLines(IEnumerable<InvoicePrintLineModel>? source)
    {
        if (source is null)
            return [];

        var lines = source
            .Select((line, index) => new InvoicePrintLineModel
            {
                SourceLineId = line.SourceLineId,
                No = line.No > 0 ? line.No : index + 1,
                ItemName = line.ItemName ?? string.Empty,
                Specification = line.Specification ?? string.Empty,
                Unit = line.Unit ?? string.Empty,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Amount = line.Amount,
                Remark = line.Remark ?? string.Empty
            })
            .Where(line =>
                !string.IsNullOrWhiteSpace(line.ItemName) ||
                !string.IsNullOrWhiteSpace(line.Specification) ||
                line.Quantity != 0 ||
                line.UnitPrice != 0 ||
                line.Amount != 0)
            .ToList();

        for (var i = 0; i < lines.Count; i++)
            lines[i].No = i + 1;

        return lines;
    }

    private static string ResolveTitle(string? documentTitle)
    {
        var normalized = Safe(documentTitle);
        return normalized is "납품서" or "의뢰서" ? normalized : "발주서";
    }

    private static string ResolveRequestPhrase(string title)
        => Safe(title) == "발주서"
            ? "아래와 같이 발주 합니다."
            : "아래와 같이 요청 드립니다.";

    private static string ResolveFooterText(InvoicePrintModel model)
    {
        var footer = Safe(model.FooterText);
        if (!string.IsNullOrWhiteSpace(footer))
            return footer;

        return Safe(model.Memo);
    }

    private static string FormatItemAndSpecification(InvoicePrintLineModel line)
    {
        var itemName = Safe(line.ItemName);
        var specification = Safe(line.Specification);
        if (string.IsNullOrWhiteSpace(specification))
            return itemName;

        var normalizedSpecification = specification.StartsWith("[", StringComparison.Ordinal)
            ? specification
            : $"[{specification}]";
        return $"{itemName}    {normalizedSpecification}".Trim();
    }

    private static string FormatDisplayedTitle(string title)
        => string.Join("   ", Safe(title, "발주서").ToCharArray());

    private static string Safe(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatMoney(decimal value)
        => $"{Math.Round(value, 0, MidpointRounding.AwayFromZero):N0}";

    private static string FormatQuantity(decimal value)
        => value == decimal.Truncate(value) ? $"{value:N0}" : $"{value:0.##}";
}
