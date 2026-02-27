using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Printing;

namespace SalesMaster.Desktop.App.Services;

public sealed class WpfInvoicePrintService : IPrintService
{
    private const double A4Width = 793.7;   // 210mm @ 96dpi
    private const double A4Height = 1122.5; // 297mm @ 96dpi
    private const int RowsPerSlip = 12;
    private const double ContentMarginX = 28;
    private const double ContentMarginY = 24;
    private const double ContentWidth = A4Width - (ContentMarginX * 2d);
    private const double ContentHeight = A4Height - (ContentMarginY * 2d);

    private static readonly Brush RedAccent = new SolidColorBrush(Color.FromRgb(211, 47, 47));
    private static readonly Brush BlueAccent = new SolidColorBrush(Color.FromRgb(46, 83, 193));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(248, 248, 248));
    private static readonly Brush ValueForeground = Brushes.Black;

    public InvoicePrintModel CreateDefaultModel(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(company);

        var paidAmount = invoice.Payments.Where(p => !p.IsDeleted).Sum(p => p.Amount);
        var lines = invoice.Lines
            .Where(l => !l.IsDeleted)
            .Select((line, index) => new InvoicePrintLineModel
            {
                No = index + 1,
                ItemName = line.ItemNameOriginal ?? string.Empty,
                Specification = line.SpecificationOriginal ?? string.Empty,
                Unit = line.Unit ?? string.Empty,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Amount = line.LineAmount,
                Remark = line.Remark ?? string.Empty
            })
            .ToList();

        return new InvoicePrintModel
        {
            InvoiceId = invoice.Id,
            InvoiceNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? invoice.LocalTempNumber
                : invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            VoucherType = invoice.VoucherType.ToString(),
            SupplierBusinessNumber = company.BusinessNumber ?? string.Empty,
            SupplierName = company.TradeName ?? string.Empty,
            SupplierRepresentative = company.Representative ?? string.Empty,
            SupplierPhone = company.ContactNumber ?? string.Empty,
            SupplierAddress = company.Address ?? string.Empty,
            BuyerBusinessNumber = customer.BusinessNumber ?? string.Empty,
            BuyerName = customer.NameOriginal ?? string.Empty,
            BuyerRepresentative = customer.Representative ?? string.Empty,
            BuyerPhone = customer.Phone ?? string.Empty,
            BuyerAddress = customer.Address ?? string.Empty,
            ManagerName = customer.ContactPerson ?? string.Empty,
            Memo = invoice.Memo ?? string.Empty,
            FooterText = string.Empty,
            BankAccountText = company.BankAccountText ?? string.Empty,
            PrintWithDate = printWithDate,
            PrintWithPrice = printWithPrice,
            SupplyAmount = invoice.SupplyAmount,
            VatAmount = invoice.VatAmount,
            TotalAmount = invoice.TotalAmount,
            PaidAmount = paidAmount,
            BalanceAmount = invoice.TotalAmount - paidAmount,
            Lines = lines
        };
    }

    public FixedDocument BuildFixedDocument(InvoicePrintModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var lines = NormalizeLines(model.Lines);
        if (lines.Count == 0)
        {
            lines.Add(new InvoicePrintLineModel { No = 1 });
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)RowsPerSlip));
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(A4Width, A4Height);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageLines = lines
                .Skip(pageIndex * RowsPerSlip)
                .Take(RowsPerSlip)
                .ToList();

            var fixedPage = BuildPage(model, pageLines, pageIndex + 1, pageCount);
            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(fixedPage);
            document.Pages.Add(pageContent);
        }

        return document;
    }

    public bool TryPrint(FixedDocument document, string jobName, out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(document);
        errorMessage = null;

        try
        {
            using var printServer = new LocalPrintServer();
            if (!printServer.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local }).Any())
            {
                errorMessage = "설치된 프린터가 없습니다. 프린터를 먼저 설치해 주세요.";
                return false;
            }
        }
        catch
        {
            // 프린터 목록 조회 실패 시에도 PrintDialog 시도를 계속한다.
        }

        try
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true)
                return false;

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.PageSize = new Size(A4Width, A4Height);
            dialog.PrintDocument(paginator, string.IsNullOrWhiteSpace(jobName) ? "거래명세서" : jobName);
            return true;
        }
        catch (PrintQueueException ex)
        {
            errorMessage = $"프린터 큐 오류: {ex.Message}";
            return false;
        }
        catch (PrintSystemException ex)
        {
            errorMessage = $"인쇄 시스템 오류: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            errorMessage = $"인쇄 권한 오류: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"인쇄 중 오류가 발생했습니다: {ex.Message}";
            return false;
        }
    }

    private static List<InvoicePrintLineModel> NormalizeLines(IEnumerable<InvoicePrintLineModel>? source)
    {
        if (source is null)
            return new List<InvoicePrintLineModel>();

        var list = source
            .Select(line => new InvoicePrintLineModel
            {
                No = line.No,
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
                line.Amount != 0 ||
                !string.IsNullOrWhiteSpace(line.Remark))
            .OrderBy(line => line.No)
            .ToList();

        for (var i = 0; i < list.Count; i++)
            list[i].No = i + 1;

        return list;
    }

    private static FixedPage BuildPage(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> pageLines,
        int pageNumber,
        int totalPages)
    {
        var page = new FixedPage
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        var root = new Grid
        {
            Width = ContentWidth,
            Height = ContentHeight,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var supplierSlip = BuildSlip(model, pageLines, pageNumber, totalPages, "[공급자 보관용]", RedAccent);
        Grid.SetRow(supplierSlip, 0);
        root.Children.Add(supplierSlip);

        var separator = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 4, 0, 4)
        };
        Grid.SetRow(separator, 1);
        root.Children.Add(separator);

        var buyerSlip = BuildSlip(model, pageLines, pageNumber, totalPages, "[공급받는자 보관용]", BlueAccent);
        Grid.SetRow(buyerSlip, 2);
        root.Children.Add(buyerSlip);

        FixedPage.SetLeft(root, (A4Width - ContentWidth) / 2d);
        FixedPage.SetTop(root, (A4Height - ContentHeight) / 2d);
        page.Children.Add(root);

        // FixedPage 전체를 Measure/Arrange하여 모든 하위 레이아웃이 올바르게 계산되도록 함
        page.Measure(new Size(A4Width, A4Height));
        page.Arrange(new Rect(0, 0, A4Width, A4Height));
        page.UpdateLayout();
        return page;
    }

    private static UIElement BuildSlip(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> pageLines,
        int pageNumber,
        int totalPages,
        string copyLabel,
        Brush accent)
    {
        var slipBorder = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Padding = new Thickness(8, 6, 8, 6)
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader(model, pageNumber, totalPages, copyLabel, accent);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var partySection = BuildPartySection(model, accent);
        Grid.SetRow(partySection, 1);
        layout.Children.Add(partySection);

        var itemSection = BuildItemSection(model, pageLines, accent);
        Grid.SetRow(itemSection, 2);
        layout.Children.Add(itemSection);

        var totalsSection = BuildTotalsSection(model, accent);
        Grid.SetRow(totalsSection, 3);
        layout.Children.Add(totalsSection);

        var footerSection = BuildFooterSection(model, pageLines, accent);
        Grid.SetRow(footerSection, 4);
        layout.Children.Add(footerSection);

        slipBorder.Child = layout;
        return slipBorder;
    }

    private static UIElement BuildHeader(
        InvoicePrintModel model,
        int pageNumber,
        int totalPages,
        string copyLabel,
        Brush accent)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var left = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(new TextBlock
        {
            Text = "관리자",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 10,
            Foreground = accent
        });
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(model.ManagerName) ? "-" : model.ManagerName,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 11,
            Foreground = ValueForeground
        });
        left.Children.Add(new TextBlock
        {
            Text = "작성일자",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 10,
            Foreground = accent,
            Margin = new Thickness(0, 2, 0, 0)
        });
        left.Children.Add(new TextBlock
        {
            Text = model.PrintWithDate ? model.InvoiceDate.ToString("yyyy-MM-dd") : string.Empty,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 11,
            Foreground = ValueForeground
        });
        Grid.SetColumn(left, 0);
        header.Children.Add(left);

        var center = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        center.Children.Add(new TextBlock
        {
            Text = "거   래   명   세   서",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 32,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            TextAlignment = TextAlignment.Center
        });
        center.Children.Add(new TextBlock
        {
            Text = copyLabel,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            Margin = new Thickness(0, -4, 0, 0),
            TextAlignment = TextAlignment.Center
        });
        Grid.SetColumn(center, 1);
        header.Children.Add(center);

        var right = new TextBlock
        {
            Text = $"Page: {pageNumber}/{totalPages}",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 12,
            Foreground = accent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(right, 2);
        header.Children.Add(right);

        return header;
    }

    private static UIElement BuildPartySection(InvoicePrintModel model, Brush accent)
    {
        var section = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        section.ColumnDefinitions.Add(new ColumnDefinition());
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        section.ColumnDefinitions.Add(new ColumnDefinition());

        var supplier = BuildPartyCard(
            "공급자",
            model.SupplierBusinessNumber,
            model.SupplierName,
            model.SupplierPhone,
            model.SupplierRepresentative,
            model.SupplierAddress,
            accent);
        Grid.SetColumn(supplier, 0);
        section.Children.Add(supplier);

        var buyer = BuildPartyCard(
            "공급받는자",
            model.BuyerBusinessNumber,
            model.BuyerName,
            model.BuyerPhone,
            model.BuyerRepresentative,
            model.BuyerAddress,
            accent);
        Grid.SetColumn(buyer, 2);
        section.Children.Add(buyer);

        return section;
    }

    private static UIElement BuildPartyCard(
        string title,
        string businessNumber,
        string name,
        string phone,
        string representative,
        string address,
        Brush accent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        AddLabeledCell(grid, 0, title, businessNumber, accent);
        AddLabeledCell(grid, 1, "상호", name, accent);
        AddLabeledCell(grid, 2, "전화번호", phone, accent);
        AddLabeledCell(grid, 3, "대표자", representative, accent);
        AddLabeledCell(grid, 4, "주소", address, accent, true);

        return new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Child = grid
        };
    }

    private static void AddLabeledCell(
        Grid grid,
        int rowIndex,
        string label,
        string value,
        Brush accent,
        bool wrap = false)
    {
        var labelBorder = new Border
        {
            BorderBrush = accent,
            BorderThickness = GetInnerCellBorderThickness(grid, rowIndex, 0),
            Background = HeaderFill,
            Padding = new Thickness(4, 2, 4, 2),
            UseLayoutRounding = true,
            Child = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("맑은 고딕"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(labelBorder, rowIndex);
        Grid.SetColumn(labelBorder, 0);
        grid.Children.Add(labelBorder);

        var valueBorder = new Border
        {
            BorderBrush = accent,
            BorderThickness = GetInnerCellBorderThickness(grid, rowIndex, 1),
            Padding = new Thickness(5, 2, 5, 2),
            UseLayoutRounding = true,
            Child = new TextBlock
            {
                Text = value ?? string.Empty,
                FontFamily = new FontFamily("맑은 고딕"),
                FontSize = 11,
                Foreground = ValueForeground,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
            }
        };
        Grid.SetRow(valueBorder, rowIndex);
        Grid.SetColumn(valueBorder, 1);
        grid.Children.Add(valueBorder);
    }

    private static UIElement BuildItemSection(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> pageLines,
        Brush accent)
    {
        var table = new Grid
        {
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 품명: 남은 너비 자동 채움
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) }); // 헤더 고정
        for (var i = 0; i < RowsPerSlip; i++)
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 바디: 남은 높이 균등 분배

        AddHeaderCell(table, 0, 0, "순번", accent);
        AddHeaderCell(table, 0, 1, "품   명", accent);
        AddHeaderCell(table, 0, 2, "규   격", accent);
        AddHeaderCell(table, 0, 3, "단 위", accent);
        AddHeaderCell(table, 0, 4, "수 량", accent);
        AddHeaderCell(table, 0, 5, "단 가", accent);
        AddHeaderCell(table, 0, 6, "금 액", accent);

        for (var row = 0; row < RowsPerSlip; row++)
        {
            var line = row < pageLines.Count ? pageLines[row] : null;
            var targetRow = row + 1;
            var blankHint = line is null && row == pageLines.Count && pageLines.Count > 0;

            AddBodyCell(table, targetRow, 0, line?.No.ToString() ?? string.Empty, accent, TextAlignment.Center);
            AddBodyCell(table, targetRow, 1, line?.ItemName ?? (blankHint ? "*** 이하 여백 ***" : string.Empty), accent);
            AddBodyCell(table, targetRow, 2, line?.Specification ?? string.Empty, accent);
            AddBodyCell(table, targetRow, 3, line?.Unit ?? string.Empty, accent, TextAlignment.Center);
            AddBodyCell(table, targetRow, 4, line is null ? string.Empty : FormatQuantity(line.Quantity), accent, TextAlignment.Right);
            AddBodyCell(
                table,
                targetRow,
                5,
                line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.UnitPrice),
                accent,
                TextAlignment.Right);
            AddBodyCell(
                table,
                targetRow,
                6,
                line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.Amount),
                accent,
                TextAlignment.Right);
        }

        return new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 4), // Border 바깥에 여백 적용 (내부 공백 제거)
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Child = table
        };
    }

    private static void AddHeaderCell(Grid grid, int row, int column, string text, Brush accent)
    {
        var border = new Border
        {
            BorderBrush = accent,
            BorderThickness = GetInnerCellBorderThickness(grid, row, column),
            Background = HeaderFill,
            Padding = new Thickness(3, 1, 3, 1),
            UseLayoutRounding = true,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("맑은 고딕"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static void AddBodyCell(
        Grid grid,
        int row,
        int column,
        string text,
        Brush accent,
        TextAlignment alignment = TextAlignment.Left)
    {
        var border = new Border
        {
            BorderBrush = accent,
            BorderThickness = GetInnerCellBorderThickness(grid, row, column),
            Padding = new Thickness(3, 1, 3, 1),
            UseLayoutRounding = true,
            Child = new TextBlock
            {
                Text = text ?? string.Empty,
                FontFamily = new FontFamily("맑은 고딕"),
                FontSize = 10.5,
                Foreground = ValueForeground,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = alignment,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static UIElement BuildTotalsSection(InvoicePrintModel model, Brush accent)
    {
        var grid = new Grid
        {
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });

        AddTotalsPair(grid, 0, 0, "전표메모", model.Memo, accent, false, TextAlignment.Left);
        AddTotalsPair(grid, 0, 2, "공급가", model.PrintWithPrice ? FormatMoney(model.SupplyAmount) : string.Empty, accent, true, TextAlignment.Right);
        AddTotalsPair(grid, 0, 4, "부가세", model.PrintWithPrice ? FormatMoney(model.VatAmount) : string.Empty, accent, true, TextAlignment.Right);
        AddTotalsPair(grid, 0, 6, "합   계", model.PrintWithPrice ? FormatMoney(model.TotalAmount) : string.Empty, accent, true, TextAlignment.Right);

        AddTotalsPair(grid, 1, 0, "전미수", string.Empty, accent, false, TextAlignment.Left);
        AddTotalsPair(grid, 1, 2, "입금액", model.PrintWithPrice ? FormatMoney(model.PaidAmount) : string.Empty, accent, true, TextAlignment.Right);
        AddTotalsPair(grid, 1, 4, "미수잔액", model.PrintWithPrice ? FormatMoney(model.BalanceAmount) : string.Empty, accent, true, TextAlignment.Right);
        AddTotalsPair(grid, 1, 6, "인수자", "(인)", accent, false, TextAlignment.Right);

        return new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 3), // Border 바깥에 여백 적용
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Child = grid
        };
    }

    private static void AddTotalsPair(
        Grid grid,
        int row,
        int labelColumn,
        string label,
        string value,
        Brush accent,
        bool boldValue,
        TextAlignment valueAlignment)
    {
        var labelBorder = new Border
        {
            BorderBrush = accent,
            BorderThickness = GetInnerCellBorderThickness(grid, row, labelColumn),
            Background = HeaderFill,
            Padding = new Thickness(3, 1, 3, 1),
            UseLayoutRounding = true,
            Child = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("맑은 고딕"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(labelBorder, row);
        Grid.SetColumn(labelBorder, labelColumn);
        grid.Children.Add(labelBorder);

        var valueBorder = new Border
        {
            BorderBrush = accent,
            BorderThickness = GetInnerCellBorderThickness(grid, row, labelColumn + 1),
            Padding = new Thickness(4, 1, 4, 1),
            UseLayoutRounding = true,
            Child = new TextBlock
            {
                Text = value ?? string.Empty,
                FontFamily = new FontFamily("맑은 고딕"),
                FontSize = 10.5,
                FontWeight = boldValue ? FontWeights.Bold : FontWeights.Normal,
                Foreground = ValueForeground,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = valueAlignment,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Grid.SetRow(valueBorder, row);
        Grid.SetColumn(valueBorder, labelColumn + 1);
        grid.Children.Add(valueBorder);
    }

    private static UIElement BuildFooterSection(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> pageLines,
        Brush accent)
    {
        var quantitySum = pageLines.Sum(line => line.Quantity);
        var leftText = string.IsNullOrWhiteSpace(model.BankAccountText)
            ? (model.FooterText ?? string.Empty)
            : $"입금은행 {model.BankAccountText} {model.FooterText}".Trim();

        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        panel.Children.Add(new TextBlock
        {
            Text = leftText,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 10,
            Foreground = ValueForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var right = new TextBlock
        {
            Text = $"수량합 {FormatQuantity(quantitySum)}  * 개발회사 www.hkdb.co.kr  1566-1186",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 10,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(right, 1);
        panel.Children.Add(right);
        return panel;
    }

    private static string FormatMoney(decimal value)
        => $"{Math.Round(value, 0, MidpointRounding.AwayFromZero):N0}";

    private static string FormatQuantity(decimal value)
    {
        if (value == decimal.Truncate(value))
            return $"{value:N0}";

        return $"{value:0.##}";
    }

    private static Thickness GetInnerCellBorderThickness(Grid grid, int row, int column)
    {
        var right = column < grid.ColumnDefinitions.Count - 1 ? 1d : 0d;
        var bottom = row < grid.RowDefinitions.Count - 1 ? 1d : 0d;
        return new Thickness(0, 0, right, bottom);
    }

}
