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
    private const double PageMargin = 28;
    private static readonly Thickness FramePadding = new(16, 12, 16, 10);
    private const double LineThickness = 1.0;
    private const int RowsPerPage = 24;

    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(186, 186, 186));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(242, 242, 242));
    private static readonly Brush SideFill = new SolidColorBrush(Color.FromRgb(230, 230, 230));
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

        var frameWidth = A4Width - (PageMargin * 2d);
        var frameHeight = A4Height - (PageMargin * 2d);
        var root = new Grid
        {
            Width = frameWidth - FramePadding.Left - FramePadding.Right,
            Height = frameHeight - FramePadding.Top - FramePadding.Bottom,
            Background = Brushes.White
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddToGrid(root, BuildHeader(title, model, totalAmount), 0);
        AddToGrid(root, CreateText("아래와 같이 요청 드립니다.", 11, margin: new Thickness(0, 8, 0, 6)), 1);
        AddToGrid(root, BuildTotalSummary(totalAmount), 2);
        AddToGrid(root, BuildItemsTable(lines, model, isFinalPage), 3);
        AddToGrid(root, BuildPageFooter(pageNumber, totalPages), 4);

        var outerFrame = new Border
        {
            Width = frameWidth,
            Height = frameHeight,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(LineThickness),
            Background = Brushes.White,
            Padding = FramePadding,
            Child = root
        };

        FixedPage.SetLeft(outerFrame, PageMargin);
        FixedPage.SetTop(outerFrame, PageMargin);
        page.Children.Add(outerFrame);
        page.Measure(new Size(A4Width, A4Height));
        page.Arrange(new Rect(0, 0, A4Width, A4Height));
        page.UpdateLayout();
        return page;
    }

    private static UIElement BuildHeader(string title, InvoicePrintModel model, decimal totalAmount)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(318) });

        var titlePanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 10)
        };
        titlePanel.Children.Add(CreateText(FormatDisplayedTitle(title), 23, FontWeights.Bold, TextAlignment.Center));
        titlePanel.Children.Add(new Border
        {
            Height = LineThickness,
            Width = 170,
            Background = BorderBrush,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        Grid.SetColumnSpan(titlePanel, 2);
        AddToGrid(grid, titlePanel, 0);

        var dateText = CreateText($"서기 : {model.InvoiceDate:yyyy} 년 {model.InvoiceDate:MM} 월 {model.InvoiceDate:dd} 일", 9.8);
        dateText.Margin = new Thickness(2, 2, 0, 10);
        Grid.SetColumnSpan(dateText, 2);
        AddToGrid(grid, dateText, 1);

        var left = BuildRecipientPanel(model);
        Grid.SetRow(left, 2);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = BuildSupplierPanel(model);
        Grid.SetRow(right, 2);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return grid;
    }

    private static UIElement BuildRecipientPanel(InvoicePrintModel model)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(4, 6, 16, 0)
        };

        var recipientGrid = new Grid();
        recipientGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recipientGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var recipientLine = new TextBlock
        {
            Text = Safe(model.SupplierName, "거래처"),
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 18,
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
        var honorific = CreateText("귀하", 17, FontWeights.Normal, margin: new Thickness(12, 0, 0, 0));
        Grid.SetColumn(honorific, 1);
        recipientGrid.Children.Add(honorific);
        stack.Children.Add(recipientGrid);

        stack.Children.Add(new Border
        {
            Height = LineThickness,
            Background = BorderBrush,
            Margin = new Thickness(0, 5, 0, 10)
        });

        stack.Children.Add(CreateText($"대표전화 : {Safe(model.SupplierPhone)}", 10.2, margin: new Thickness(2, 0, 0, 0)));
        return stack;
    }

    private static UIElement BuildSupplierPanel(InvoicePrintModel model)
    {
        var panel = new Grid
        {
            Width = 318,
            Margin = new Thickness(0, 0, 0, 4)
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        AddCell(panel, 0, 0, "공\n급\n자", background: SideFill, center: true, bold: true, rowSpan: 7, padding: new Thickness(0));
        AddCell(panel, 0, 1, "사업번호", background: HeaderFill, center: true, bold: true);
        AddCell(panel, 0, 2, Safe(model.BuyerBusinessNumber));
        AddCell(panel, 1, 1, "상호", background: HeaderFill, center: true, bold: true);
        AddCell(panel, 1, 2, Safe(model.BuyerName));
        AddCell(panel, 2, 1, "전화번호", background: HeaderFill, center: true, bold: true);
        AddCell(panel, 2, 2, Safe(model.BuyerPhone));
        AddCell(panel, 3, 1, "주소", background: HeaderFill, center: true, bold: true);
        AddCell(panel, 3, 2, Safe(model.BuyerAddress), wrap: true, padding: new Thickness(6, 3, 6, 3));
        AddCell(panel, 4, 1, "대표자", background: HeaderFill, center: true, bold: true);
        AddCell(panel, 4, 2, Safe(model.BuyerRepresentative));
        AddCell(panel, 5, 1, string.Empty, background: HeaderFill);
        AddCell(panel, 5, 2, string.Empty);
        AddCell(panel, 6, 1, string.Empty, background: HeaderFill);
        AddCell(panel, 6, 2, string.Empty);
        var frame = new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(LineThickness),
            Child = panel
        };

        var stamp = TryCreateStampImage(model.SupplierStampImage);
        if (stamp is null)
            return frame;

        var overlay = new Grid();
        overlay.Children.Add(frame);
        overlay.Children.Add(new Image
        {
            Source = stamp,
            Width = 64,
            Height = 64,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -8, 12)
        });

        return overlay;
    }

    private static UIElement BuildTotalSummary(decimal totalAmount)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });

        var rounded = Math.Round(totalAmount, 0, MidpointRounding.AwayFromZero);
        AddThinBorderedText(grid, 0, "합계금액 :", HeaderFill, FontWeights.Bold, TextAlignment.Center);
        AddThinBorderedText(grid, 1, $"금 {rounded:N0} 원정", Brushes.White, FontWeights.Normal, TextAlignment.Left);
        AddThinBorderedText(grid, 2, $"( {rounded:N0} )", Brushes.White, FontWeights.Normal, TextAlignment.Center);
        AddThinBorderedText(grid, 3, "부가세별도", HeaderFill, FontWeights.Bold, TextAlignment.Center);

        return grid;
    }

    private static UIElement BuildItemsTable(
        IReadOnlyList<InvoicePrintLineModel> lines,
        InvoicePrintModel model,
        bool isFinalPage)
    {
        var visibleRows = RowsPerPage;

        var table = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });

        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        for (var row = 0; row < visibleRows; row++)
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(23) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });

        AddThinCell(table, 0, 0, "No", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 1, "품        명     /      규        격", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 2, "단위", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 3, "수량", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 4, "단가", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, 0, 5, "금액", background: HeaderFill, center: true, bold: true);

        for (var index = 0; index < visibleRows; index++)
        {
            var row = index + 1;
            var line = index < lines.Count ? lines[index] : null;
            var itemText = line is null
                ? string.Empty
                : string.IsNullOrWhiteSpace(line.Specification)
                    ? Safe(line.ItemName)
                    : $"{Safe(line.ItemName)}   [{Safe(line.Specification)}]";

            if (line is null && index == lines.Count && lines.Count > 0)
                itemText = "*** 이하여백 ***";

            AddThinCell(table, row, 0, line?.No.ToString() ?? string.Empty, center: true);
            AddThinCell(table, row, 1, itemText, padding: new Thickness(6, 2, 6, 2), fontSize: 9.4);
            AddThinCell(table, row, 2, line?.Unit ?? string.Empty, center: true);
            AddThinCell(table, row, 3, line is null ? string.Empty : FormatQuantity(line.Quantity), align: TextAlignment.Right);
            AddThinCell(table, row, 4, line is null ? string.Empty : FormatMoney(line.UnitPrice), align: TextAlignment.Right);
            AddThinCell(table, row, 5, line is null ? string.Empty : FormatMoney(line.Amount), align: TextAlignment.Right);
        }

        var supplyAmount = model.SupplyAmount;
        var vatAmount = model.VatAmount;
        var totalAmount = model.TotalAmount;
        var supplyText = isFinalPage ? FormatMoney(supplyAmount) : string.Empty;
        var vatText = isFinalPage ? FormatMoney(vatAmount) : string.Empty;
        var totalText = isFinalPage ? FormatMoney(totalAmount) : string.Empty;
        if (isFinalPage && model.TotalAmount == 0)
        {
            var lineTotal = lines.Sum(line => line.Amount);
            var totals = InvoiceVatModes.CalculateTotals([lineTotal], model.VatMode);
            supplyAmount = totals.SupplyAmount;
            vatAmount = totals.VatAmount;
            totalAmount = lineTotal;
            supplyText = FormatMoney(supplyAmount);
            vatText = FormatMoney(vatAmount);
            totalText = FormatMoney(totalAmount);
        }

        var totalsRowStart = visibleRows + 1;
        AddThinCell(table, totalsRowStart, 0, string.Empty, columnSpan: 4);
        AddThinCell(table, totalsRowStart + 1, 0, string.Empty, columnSpan: 4);
        AddThinCell(table, totalsRowStart + 2, 0, string.Empty, columnSpan: 4);

        AddThinCell(table, totalsRowStart, 4, "공 급 가", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, totalsRowStart, 5, supplyText, align: TextAlignment.Right);
        AddThinCell(table, totalsRowStart + 1, 4, "부 가 세", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, totalsRowStart + 1, 5, vatText, align: TextAlignment.Right);
        AddThinCell(table, totalsRowStart + 2, 4, "합 계 금 액", background: HeaderFill, center: true, bold: true);
        AddThinCell(table, totalsRowStart + 2, 5, totalText, align: TextAlignment.Right, bold: isFinalPage);

        return table;
    }

    private static UIElement BuildPageFooter(int pageNumber, int totalPages)
    {
        var text = CreateText($"Page: {pageNumber} / {totalPages}", 9.5, margin: new Thickness(0, 6, 0, 0));
        text.HorizontalAlignment = HorizontalAlignment.Right;
        return text;
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
            Padding = new Thickness(6, 2, 6, 2),
            Child = CreateText(text, 10.6, fontWeight, alignment)
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

    private static string FormatDisplayedTitle(string title)
        => string.Join("   ", Safe(title, "발주서").ToCharArray());

    private static string Safe(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatMoney(decimal value)
        => $"{Math.Round(value, 0, MidpointRounding.AwayFromZero):N0}";

    private static string FormatQuantity(decimal value)
        => value == decimal.Truncate(value) ? $"{value:N0}" : $"{value:0.##}";
}
