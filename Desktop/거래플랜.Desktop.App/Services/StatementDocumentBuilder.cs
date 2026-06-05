using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public static class StatementDocumentBuilder
{
    private const string FontName = "맑은 고딕";
    private const double ContentWidth = 744;

    private static readonly SolidColorBrush RedAccentBrush = new(Color.FromRgb(229, 57, 53));
    private static readonly SolidColorBrush BlueAccentBrush = new(Color.FromRgb(63, 81, 181));
    private static readonly SolidColorBrush HeaderFillBrush = new(Color.FromRgb(246, 246, 246));
    private static readonly SolidColorBrush DividerBrush = new(Color.FromRgb(120, 120, 120));

    private static readonly Thickness GridBorder = new(0.55);
    private static readonly Thickness CellPadding = new(3, 1.5, 3, 1.5);

    public static FlowDocument BuildStatementPrintDocument(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company)
        => BuildStatementPrintDocument(
            invoice,
            customer,
            company,
            NativeStatementLayoutType.TradeHalf,
            printWithDate: true,
            printWithPrice: true);

    public static FlowDocument BuildStatementPrintDocument(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        NativeStatementLayoutType layout,
        bool printWithDate,
        bool printWithPrice)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily(FontName),
            FontSize = 9,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            LineHeight = 12
        };

        switch (layout)
        {
            case NativeStatementLayoutType.TradeA4:
                document.Blocks.Add(BuildTradeSection(
                    invoice,
                    customer,
                    company,
                    "공급자 보관용",
                    RedAccentBrush,
                    minItemRows: 28,
                    printWithDate,
                    printWithPrice));
                break;

            case NativeStatementLayoutType.Receipt:
                document.Blocks.Add(BuildReceiptSection(
                    invoice,
                    customer,
                    company,
                    printWithDate,
                    printWithPrice));
                break;

            default:
                document.Blocks.Add(BuildTradeSection(
                    invoice,
                    customer,
                    company,
                    "공급자 보관용",
                    RedAccentBrush,
                    minItemRows: 12,
                    printWithDate,
                    printWithPrice));
                document.Blocks.Add(CreateSectionDivider());
                document.Blocks.Add(BuildTradeSection(
                    invoice,
                    customer,
                    company,
                    "공급받는자 보관용",
                    BlueAccentBrush,
                    minItemRows: 12,
                    printWithDate,
                    printWithPrice));
                break;
        }

        return document;
    }

    private static Block CreateSectionDivider()
    {
        var line = new Border
        {
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 14, 0, 14)
        };

        return new BlockUIContainer(line);
    }

    private static Section BuildTradeSection(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        string copyLabel,
        SolidColorBrush accent,
        int minItemRows,
        bool printWithDate,
        bool printWithPrice)
    {
        var section = new Section
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0)
        };

        section.Blocks.Add(new Paragraph(new Bold(new Run("거 래 명 세 서")))
        {
            Margin = new Thickness(0, 0, 0, 0),
            TextAlignment = TextAlignment.Center,
            Foreground = accent,
            FontSize = 22
        });

        section.Blocks.Add(new Paragraph(new Bold(new Run($"[{copyLabel}]")))
        {
            Margin = new Thickness(0, 0, 0, 2),
            TextAlignment = TextAlignment.Center,
            Foreground = accent,
            FontSize = 10
        });

        section.Blocks.Add(BuildMetaTable(invoice, accent, printWithDate));
        section.Blocks.Add(BuildPartyTable(customer, company, accent));
        section.Blocks.Add(BuildItemsTable(invoice, accent, minItemRows, printWithPrice));
        section.Blocks.Add(BuildTotalsTable(invoice, accent, printWithPrice));
        section.Blocks.Add(BuildFooterTable(invoice, company, accent));

        return section;
    }

    private static Table BuildMetaTable(LocalInvoice invoice, SolidColorBrush accent, bool printWithDate)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 2)
        };
        table.Columns.Add(new TableColumn { Width = new GridLength(ContentWidth - 120) });
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });

        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);

        var dateText = printWithDate ? invoice.InvoiceDate.ToString("yyyy-MM-dd") : string.Empty;

        var row1 = new TableRow();
        row1.Cells.Add(CreateNoBorderCell(string.Empty, TextAlignment.Left, accent));
        row1.Cells.Add(CreateNoBorderCell("Page: 1/1", TextAlignment.Right, accent));
        rows.Rows.Add(row1);

        var row2 = new TableRow();
        row2.Cells.Add(CreateNoBorderCell($"작성일자  {dateText}", TextAlignment.Left, accent));
        row2.Cells.Add(CreateNoBorderCell(string.Empty, TextAlignment.Right, accent));
        rows.Rows.Add(row2);

        return table;
    }

    private static Table BuildPartyTable(LocalCustomer customer, LocalCompanyProfile company, SolidColorBrush accent)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 2)
        };

        // 총 744
        table.Columns.Add(new TableColumn { Width = new GridLength(16) });
        table.Columns.Add(new TableColumn { Width = new GridLength(60) });
        table.Columns.Add(new TableColumn { Width = new GridLength(170) });
        table.Columns.Add(new TableColumn { Width = new GridLength(44) });
        table.Columns.Add(new TableColumn { Width = new GridLength(82) });
        table.Columns.Add(new TableColumn { Width = new GridLength(16) });
        table.Columns.Add(new TableColumn { Width = new GridLength(60) });
        table.Columns.Add(new TableColumn { Width = new GridLength(170) });
        table.Columns.Add(new TableColumn { Width = new GridLength(44) });
        table.Columns.Add(new TableColumn { Width = new GridLength(82) });

        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);

        var row1 = new TableRow();
        row1.Cells.Add(CreateLabelCell("공\n급\n자", accent, rowSpan: 4, center: true));
        row1.Cells.Add(CreateLabelCell("사업번호", accent));
        row1.Cells.Add(CreateValueCell(company.BusinessNumber, accent, colSpan: 3, bold: true));
        row1.Cells.Add(CreateLabelCell("공\n급\n받\n는\n자", accent, rowSpan: 4, center: true));
        row1.Cells.Add(CreateLabelCell("사업번호", accent));
        row1.Cells.Add(CreateValueCell(customer.BusinessNumber, accent, colSpan: 3, bold: true));
        rows.Rows.Add(row1);

        var row2 = new TableRow();
        row2.Cells.Add(CreateLabelCell("상호", accent));
        row2.Cells.Add(CreateValueCell(company.TradeName, accent, colSpan: 3, bold: true));
        row2.Cells.Add(CreateLabelCell("상호", accent));
        row2.Cells.Add(CreateValueCell(customer.NameOriginal, accent, colSpan: 3, bold: true));
        rows.Rows.Add(row2);

        var row3 = new TableRow();
        row3.Cells.Add(CreateLabelCell("전화번호", accent));
        row3.Cells.Add(CreateValueCell(company.ContactNumber, accent));
        row3.Cells.Add(CreateLabelCell("대표자", accent));
        row3.Cells.Add(CreateValueCell(company.Representative, accent));
        row3.Cells.Add(CreateLabelCell("전화번호", accent));
        row3.Cells.Add(CreateValueCell(customer.Phone, accent));
        row3.Cells.Add(CreateLabelCell("대표자", accent));
        row3.Cells.Add(CreateValueCell(customer.Representative, accent));
        rows.Rows.Add(row3);

        var row4 = new TableRow();
        row4.Cells.Add(CreateLabelCell("주소", accent));
        row4.Cells.Add(CreateValueCell(company.Address, accent, colSpan: 3, wrap: true));
        row4.Cells.Add(CreateLabelCell("주소", accent));
        row4.Cells.Add(CreateValueCell(customer.Address, accent, colSpan: 3, wrap: true));
        rows.Rows.Add(row4);

        return table;
    }

    private static Table BuildItemsTable(
        LocalInvoice invoice,
        SolidColorBrush accent,
        int minRows,
        bool printWithPrice)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 2)
        };

        // 총 744
        table.Columns.Add(new TableColumn { Width = new GridLength(40) });
        table.Columns.Add(new TableColumn { Width = new GridLength(350) });
        table.Columns.Add(new TableColumn { Width = new GridLength(62) });
        table.Columns.Add(new TableColumn { Width = new GridLength(80) });
        table.Columns.Add(new TableColumn { Width = new GridLength(106) });
        table.Columns.Add(new TableColumn { Width = new GridLength(106) });

        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);

        var header = new TableRow();
        header.Cells.Add(CreateLabelCell("순번", accent, center: true));
        header.Cells.Add(CreateLabelCell("품 명 / 규 격", accent, center: true));
        header.Cells.Add(CreateLabelCell("단위", accent, center: true));
        header.Cells.Add(CreateLabelCell("수량", accent, center: true));
        header.Cells.Add(CreateLabelCell("공급단가", accent, center: true));
        header.Cells.Add(CreateLabelCell("공급가액", accent, center: true));
        rows.Rows.Add(header);

        var lines = invoice.Lines
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue)
            .ThenBy(line => line.Id)
            .ToList();
        var rowCount = Math.Max(minRows, lines.Count + 1);
        var addedBlankGuide = false;

        for (var i = 0; i < rowCount; i++)
        {
            var row = new TableRow();
            if (i < lines.Count)
            {
                var line = lines[i];
                row.Cells.Add(CreateValueCell((i + 1).ToString(), accent, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell(FormatItemText(line), accent, align: TextAlignment.Left, wrap: true));
                row.Cells.Add(CreateValueCell(line.Unit, accent, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell($"{line.Quantity:N0}", accent, align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(
                    printWithPrice ? $"{ResolvePrintedSupplyUnitPrice(line, invoice.VatMode):N0}" : string.Empty,
                    accent,
                    align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(
                    printWithPrice ? $"{ResolvePrintedSupplyAmount(line, invoice.VatMode):N0}" : string.Empty,
                    accent,
                    align: TextAlignment.Right));
            }
            else if (!addedBlankGuide)
            {
                addedBlankGuide = true;
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell("*** 이하 여백 ***", accent, align: TextAlignment.Left));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Right));
            }
            else
            {
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Left));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(string.Empty, accent, align: TextAlignment.Right));
            }

            rows.Rows.Add(row);
        }

        return table;
    }

    private static string FormatItemText(LocalInvoiceLine line)
    {
        var itemName = line.ItemNameOriginal?.Trim() ?? string.Empty;
        var spec = line.SpecificationOriginal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(spec))
            return itemName;

        return $"{itemName} [{spec}]";
    }

    private static decimal ResolvePrintedSupplyUnitPrice(LocalInvoiceLine line, string? vatMode)
        => InvoicePrintLineSynchronizer.ResolvePrintedSupplyUnitPrice(
            line.UnitPrice,
            line.Quantity,
            line.LineAmount,
            vatMode);

    private static decimal ResolvePrintedSupplyAmount(LocalInvoiceLine line, string? vatMode)
        => InvoicePrintLineSynchronizer.ResolvePrintedSupplyAmount(
            line.LineAmount,
            vatMode);

    private static Table BuildTotalsTable(LocalInvoice invoice, SolidColorBrush accent, bool printWithPrice)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 1)
        };

        // 총 744
        table.Columns.Add(new TableColumn { Width = new GridLength(70) });
        table.Columns.Add(new TableColumn { Width = new GridLength(246) });
        table.Columns.Add(new TableColumn { Width = new GridLength(56) });
        table.Columns.Add(new TableColumn { Width = new GridLength(82) });
        table.Columns.Add(new TableColumn { Width = new GridLength(56) });
        table.Columns.Add(new TableColumn { Width = new GridLength(82) });
        table.Columns.Add(new TableColumn { Width = new GridLength(56) });
        table.Columns.Add(new TableColumn { Width = new GridLength(96) });

        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);

        var paidAmount = invoice.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
        var balanceAmount = invoice.TotalAmount - paidAmount;
        var isPurchaseDocument = invoice.VoucherType == VoucherType.Purchase;
        var previousOutstandingLabel = isPurchaseDocument ? "전미지급" : "전미수";
        var settlementLabel = isPurchaseDocument ? "지급액" : "입금액";
        var outstandingLabel = isPurchaseDocument ? "미지급잔액" : "미수잔액";

        var row1 = new TableRow();
        row1.Cells.Add(CreateTotalsLabelCell("전표메모", accent));
        row1.Cells.Add(CreateTotalsValueCell(invoice.Memo, accent));
        row1.Cells.Add(CreateTotalsLabelCell("공급가", accent));
        row1.Cells.Add(CreateTotalsValueCell(printWithPrice ? $"{invoice.SupplyAmount:N0}" : string.Empty, accent, TextAlignment.Right));
        row1.Cells.Add(CreateTotalsLabelCell("부가세", accent));
        row1.Cells.Add(CreateTotalsValueCell(printWithPrice ? $"{invoice.VatAmount:N0}" : string.Empty, accent, TextAlignment.Right));
        row1.Cells.Add(CreateTotalsLabelCell("합계", accent));
        row1.Cells.Add(CreateTotalsValueCell(printWithPrice ? $"{invoice.TotalAmount:N0}" : string.Empty, accent, TextAlignment.Right));
        rows.Rows.Add(row1);

        var row2 = new TableRow();
        row2.Cells.Add(CreateTotalsLabelCell(previousOutstandingLabel, accent));
        row2.Cells.Add(CreateTotalsValueCell(string.Empty, accent, TextAlignment.Right));
        row2.Cells.Add(CreateTotalsLabelCell(settlementLabel, accent));
        row2.Cells.Add(CreateTotalsValueCell(printWithPrice ? $"{paidAmount:N0}" : string.Empty, accent, TextAlignment.Right));
        row2.Cells.Add(CreateTotalsLabelCell(outstandingLabel, accent));
        row2.Cells.Add(CreateTotalsValueCell(printWithPrice ? $"{balanceAmount:N0}" : string.Empty, accent, TextAlignment.Right));
        row2.Cells.Add(CreateTotalsLabelCell("인수자", accent));
        row2.Cells.Add(CreateTotalsValueCell("(인)", accent, TextAlignment.Right));
        rows.Rows.Add(row2);

        return table;
    }

    private static Table BuildFooterTable(LocalInvoice invoice, LocalCompanyProfile company, SolidColorBrush accent)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 1, 0, 0)
        };

        table.Columns.Add(new TableColumn { Width = new GridLength(520) });
        table.Columns.Add(new TableColumn { Width = new GridLength(224) });

        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);

        var accountText = string.IsNullOrWhiteSpace(company.BankAccountText)
            ? string.Empty
            : $"입금은행 {company.BankAccountText}";
        var quantitySum = invoice.Lines.Where(line => !line.IsDeleted).Sum(line => line.Quantity);

        var footerRow = new TableRow();
        footerRow.Cells.Add(CreateNoBorderCell(accountText, TextAlignment.Left, accent));
        footerRow.Cells.Add(CreateNoBorderCell(
            $"수량합 {quantitySum:N0}",
            TextAlignment.Right,
            accent));
        rows.Rows.Add(footerRow);

        return table;
    }

    private static Section BuildReceiptSection(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice)
    {
        var section = new Section
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0)
        };

        section.Blocks.Add(new Paragraph(new Bold(new Run("영 수 증")))
        {
            FontSize = 22,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var dateText = printWithDate ? invoice.InvoiceDate.ToString("yyyy-MM-dd") : string.Empty;
        section.Blocks.Add(new Paragraph(new Run($"거래처 {customer.NameOriginal}    작성일자 {dateText}"))
        {
            Margin = new Thickness(0, 0, 0, 4)
        });

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 4) };
        table.Columns.Add(new TableColumn { Width = new GridLength(42) });
        table.Columns.Add(new TableColumn { Width = new GridLength(542) });
        table.Columns.Add(new TableColumn { Width = new GridLength(70) });
        table.Columns.Add(new TableColumn { Width = new GridLength(90) });

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        var header = new TableRow();
        header.Cells.Add(CreateLabelCell("No", BlueAccentBrush, center: true));
        header.Cells.Add(CreateLabelCell("품목", BlueAccentBrush, center: true));
        header.Cells.Add(CreateLabelCell("수량", BlueAccentBrush, center: true));
        header.Cells.Add(CreateLabelCell("금액", BlueAccentBrush, center: true));
        group.Rows.Add(header);

        var lines = invoice.Lines
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue)
            .ThenBy(line => line.Id)
            .ToList();
        var rowCount = Math.Max(8, lines.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var row = new TableRow();
            if (i < lines.Count)
            {
                var line = lines[i];
                row.Cells.Add(CreateValueCell((i + 1).ToString(), BlueAccentBrush, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell(FormatItemText(line), BlueAccentBrush, align: TextAlignment.Left, wrap: true));
                row.Cells.Add(CreateValueCell($"{line.Quantity:N0}", BlueAccentBrush, align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(
                    printWithPrice ? $"{ResolvePrintedSupplyAmount(line, invoice.VatMode):N0}" : string.Empty,
                    BlueAccentBrush,
                    align: TextAlignment.Right));
            }
            else
            {
                row.Cells.Add(CreateValueCell(string.Empty, BlueAccentBrush, align: TextAlignment.Center));
                row.Cells.Add(CreateValueCell(string.Empty, BlueAccentBrush));
                row.Cells.Add(CreateValueCell(string.Empty, BlueAccentBrush, align: TextAlignment.Right));
                row.Cells.Add(CreateValueCell(string.Empty, BlueAccentBrush, align: TextAlignment.Right));
            }

            group.Rows.Add(row);
        }

        section.Blocks.Add(table);

        section.Blocks.Add(new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 0),
            TextAlignment = TextAlignment.Right,
            Inlines =
            {
                new Run("합계: "),
                new Bold(new Run(printWithPrice ? $"{invoice.TotalAmount:N0}원" : string.Empty))
            }
        });

        section.Blocks.Add(new Paragraph(new Run($"발행처 {company.TradeName}   {company.ContactNumber}"))
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = DividerBrush
        });

        return section;
    }

    private static TableCell CreateLabelCell(
        string text,
        SolidColorBrush accent,
        bool center = false,
        int colSpan = 1,
        int rowSpan = 1)
        => CreateTableCell(
            text,
            accent,
            background: HeaderFillBrush,
            align: center ? TextAlignment.Center : TextAlignment.Left,
            bold: true,
            foreground: accent,
            colSpan: colSpan,
            rowSpan: rowSpan);

    private static TableCell CreateValueCell(
        string text,
        SolidColorBrush accent,
        TextAlignment align = TextAlignment.Left,
        int colSpan = 1,
        bool bold = false,
        bool wrap = false)
        => CreateTableCell(
            text,
            accent,
            background: Brushes.White,
            align: align,
            bold: bold,
            foreground: Brushes.Black,
            colSpan: colSpan,
            rowSpan: 1,
            wrap: wrap);

    private static TableCell CreateTotalsLabelCell(string text, SolidColorBrush accent)
        => CreateTableCell(
            text,
            accent,
            background: HeaderFillBrush,
            align: TextAlignment.Center,
            bold: true,
            foreground: accent);

    private static TableCell CreateTotalsValueCell(string text, SolidColorBrush accent, TextAlignment align = TextAlignment.Left)
        => CreateTableCell(
            text,
            accent,
            background: Brushes.White,
            align: align,
            bold: false,
            foreground: Brushes.Black);

    private static TableCell CreateNoBorderCell(string text, TextAlignment align, Brush foreground)
    {
        var paragraph = new Paragraph(new Run(text ?? string.Empty))
        {
            Margin = new Thickness(0),
            TextAlignment = align,
            FontSize = 8.4
        };

        return new TableCell(paragraph)
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0.5, 0, 0.5),
            Background = Brushes.Transparent,
            Foreground = foreground
        };
    }

    private static TableCell CreateTableCell(
        string text,
        SolidColorBrush borderBrush,
        Brush background,
        TextAlignment align,
        bool bold,
        Brush foreground,
        int colSpan = 1,
        int rowSpan = 1,
        bool wrap = false)
    {
        var paragraph = new Paragraph(new Run(text ?? string.Empty))
        {
            Margin = new Thickness(0),
            TextAlignment = align,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 11.4
        };

        if (!wrap)
            paragraph.TextIndent = 0;

        var cell = new TableCell(paragraph)
        {
            Padding = CellPadding,
            BorderBrush = borderBrush,
            BorderThickness = GridBorder,
            Background = background,
            ColumnSpan = colSpan,
            RowSpan = rowSpan,
            Foreground = foreground,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontFamily = new FontFamily(FontName),
            FontSize = 8.8
        };

        return cell;
    }
}
