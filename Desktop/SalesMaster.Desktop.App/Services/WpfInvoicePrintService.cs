using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Printing;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Services;

public sealed class WpfInvoicePrintService : IPrintService
{
    private const double A4Width = 793.7;
    private const double A4Height = 1122.5;
    private const int SalesRowsPerPage = 12;
    private const int PurchaseRowsPerPage = 22;
    private const double PageMargin = 28;
    private static readonly Brush SalesSupplierAccent = new SolidColorBrush(Color.FromRgb(190, 45, 45));
    private static readonly Brush SalesBuyerAccent = new SolidColorBrush(Color.FromRgb(46, 83, 193));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(242, 242, 242));
    private static readonly Brush BorderGray = new SolidColorBrush(Color.FromRgb(186, 186, 186));
    private static readonly Brush PurchaseHeaderFill = new SolidColorBrush(Color.FromRgb(242, 242, 242));
    private static readonly Brush PurchaseBandFill = new SolidColorBrush(Color.FromRgb(217, 217, 217));
    private static readonly Brush PurchaseBorder = new SolidColorBrush(Color.FromRgb(186, 186, 186));

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
        var isPurchase = invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement;
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
            SupplierBusinessNumber = isPurchase ? customer.BusinessNumber ?? string.Empty : company.BusinessNumber ?? string.Empty,
            SupplierName = isPurchase ? customer.NameOriginal ?? string.Empty : company.TradeName ?? string.Empty,
            SupplierRepresentative = isPurchase ? customer.Representative ?? string.Empty : company.Representative ?? string.Empty,
            SupplierPhone = isPurchase ? customer.Phone ?? string.Empty : company.ContactNumber ?? string.Empty,
            SupplierAddress = isPurchase ? customer.Address ?? string.Empty : company.Address ?? string.Empty,
            BuyerBusinessNumber = isPurchase ? company.BusinessNumber ?? string.Empty : customer.BusinessNumber ?? string.Empty,
            BuyerName = isPurchase ? company.TradeName ?? string.Empty : customer.NameOriginal ?? string.Empty,
            BuyerRepresentative = isPurchase ? company.Representative ?? string.Empty : customer.Representative ?? string.Empty,
            BuyerPhone = isPurchase ? company.ContactNumber ?? string.Empty : customer.Phone ?? string.Empty,
            BuyerAddress = isPurchase ? company.Address ?? string.Empty : customer.Address ?? string.Empty,
            ManagerName = customer.ContactPerson ?? string.Empty,
            Memo = invoice.Memo ?? string.Empty,
            DocumentTitle = invoice.VoucherType == VoucherType.Procurement ? "발주서" : string.Empty,
            FooterText = string.Empty,
            BankAccountText = company.BankAccountText ?? string.Empty,
            SupplierStampImage = company.StampImage,
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
            lines.Add(new InvoicePrintLineModel { No = 1 });

        if (IsProcurement(model))
            return ProcurementDocumentBuilder.BuildDocument(model);

        return IsPurchase(model)
            ? BuildPurchaseDocument(model, lines)
            : BuildSalesDocument(model, lines);
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
                errorMessage = "설치된 프린터가 없습니다. 프린터를 먼저 설치하세요.";
                return false;
            }
        }
        catch
        {
        }

        try
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true)
                return false;

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.PageSize = new Size(A4Width, A4Height);
            dialog.PrintDocument(paginator, string.IsNullOrWhiteSpace(jobName) ? "전표 인쇄" : jobName);
            return true;
        }
        catch (PrintQueueException ex)
        {
            errorMessage = $"프린터 오류: {ex.Message}";
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

    private static bool IsPurchase(InvoicePrintModel model)
        => string.Equals(model.VoucherType, nameof(VoucherType.Purchase), StringComparison.OrdinalIgnoreCase);

    private static bool IsProcurement(InvoicePrintModel model)
        => string.Equals(model.VoucherType, nameof(VoucherType.Procurement), StringComparison.OrdinalIgnoreCase);

    private static List<InvoicePrintLineModel> NormalizeLines(IEnumerable<InvoicePrintLineModel>? source)
    {
        if (source is null)
            return new List<InvoicePrintLineModel>();

        var lines = source
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

        for (var i = 0; i < lines.Count; i++)
            lines[i].No = i + 1;

        return lines;
    }

    private static FixedDocument BuildPurchaseDocument(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)PurchaseRowsPerPage));
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(A4Width, A4Height);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageLines = lines
                .Skip(pageIndex * PurchaseRowsPerPage)
                .Take(PurchaseRowsPerPage)
                .ToList();

            var page = BuildPurchasePage(model, pageLines, pageIndex + 1, pageCount, pageIndex == pageCount - 1);
            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(page);
            document.Pages.Add(pageContent);
        }

        return document;
    }

    private static FixedPage BuildPurchasePage(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines,
        int pageNumber,
        int totalPages,
        bool isFinalPage)
    {
        var page = CreatePage();
        var root = CreateRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = CreateText(
            "매입 명세서",
            25,
            FontWeights.Bold,
            Brushes.Black,
            TextAlignment.Center);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 0, 0, 8);
        AddToGrid(root, title, 0);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(CreateText(
            model.PrintWithDate ? $"전표일자 : {model.InvoiceDate:yyyy-MM-dd}" : "전표일자 :",
            11));
        var pageLabel = CreateText($"Page: {pageNumber}/{totalPages}", 11);
        pageLabel.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(pageLabel, 1);
        header.Children.Add(pageLabel);
        AddToGrid(root, header, 1);

        var partyGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        partyGrid.ColumnDefinitions.Add(new ColumnDefinition());
        partyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        partyGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var buyerPanel = BuildPurchasePartyPanel(
            "공급받는 자",
            model.BuyerBusinessNumber,
            model.BuyerName,
            model.BuyerPhone,
            model.BuyerAddress,
            model.BuyerRepresentative);
        Grid.SetColumn(buyerPanel, 0);
        partyGrid.Children.Add(buyerPanel);

        var supplierPanel = BuildPurchasePartyPanel(
            "공급자",
            model.SupplierBusinessNumber,
            model.SupplierName,
            model.SupplierPhone,
            model.SupplierAddress,
            model.SupplierRepresentative);
        Grid.SetColumn(supplierPanel, 2);
        partyGrid.Children.Add(supplierPanel);
        AddToGrid(root, partyGrid, 2);

        AddToGrid(root, BuildPurchaseLineTable(model, lines, isFinalPage), 3);
        AddToGrid(root, BuildPurchaseTotals(model, isFinalPage), 4);

        FixedPage.SetLeft(root, 32);
        FixedPage.SetTop(root, 36);
        page.Children.Add(root);
        FinalizePage(page);
        return page;
    }

    private static Border BuildPurchasePartyPanel(
        string sideLabel,
        string businessNumber,
        string tradeName,
        string phone,
        string address,
        string representative)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());

        var side = new Border
        {
            BorderBrush = PurchaseBorder,
            BorderThickness = new Thickness(1),
            Background = PurchaseBandFill,
            Child = CreateText(sideLabel, 11, FontWeights.Bold, Brushes.Black, TextAlignment.Center)
        };
        layout.Children.Add(side);

        var infoGrid = new Grid();
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition());
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });

        AddLabelValue(infoGrid, 0, "사업번호", businessNumber, PurchaseBorder, labelBackground: PurchaseHeaderFill);
        AddLabelValue(infoGrid, 1, "상호", tradeName, PurchaseBorder, wrap: true, labelBackground: PurchaseHeaderFill);
        AddLabelValue(infoGrid, 2, "전화번호", phone, PurchaseBorder, labelBackground: PurchaseHeaderFill);
        AddLabelValue(infoGrid, 3, "주소", address, PurchaseBorder, wrap: true, labelBackground: PurchaseHeaderFill);
        AddLabelValue(infoGrid, 4, "대표자", representative, PurchaseBorder, labelBackground: PurchaseHeaderFill);

        Grid.SetColumn(infoGrid, 1);
        layout.Children.Add(infoGrid);

        return new Border
        {
            BorderBrush = PurchaseBorder,
            BorderThickness = new Thickness(1),
            Child = layout
        };
    }

    private static Border BuildPurchaseLineTable(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines,
        bool isFinalPage)
    {
        var visibleRows = isFinalPage
            ? Math.Max(lines.Count, 1)
            : PurchaseRowsPerPage;

        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        for (var row = 0; row < visibleRows; row++)
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        AddTableCell(table, 0, 0, "No", PurchaseBorder, isHeader: true, center: true, background: PurchaseHeaderFill);
        AddTableCell(table, 0, 1, "품명 / 규격", PurchaseBorder, isHeader: true, center: true, background: PurchaseHeaderFill);
        AddTableCell(table, 0, 2, "단위", PurchaseBorder, isHeader: true, center: true, background: PurchaseHeaderFill);
        AddTableCell(table, 0, 3, "수량", PurchaseBorder, isHeader: true, center: true, background: PurchaseHeaderFill);
        AddTableCell(table, 0, 4, "단가", PurchaseBorder, isHeader: true, center: true, background: PurchaseHeaderFill);
        AddTableCell(table, 0, 5, "금액", PurchaseBorder, isHeader: true, center: true, background: PurchaseHeaderFill);

        for (var row = 0; row < visibleRows; row++)
        {
            var line = row < lines.Count ? lines[row] : null;
            var itemText = line is null
                ? string.Empty
                : string.IsNullOrWhiteSpace(line.Specification)
                    ? line.ItemName
                    : $"{line.ItemName} / {line.Specification}";

            AddTableCell(table, row + 1, 0, line?.No.ToString() ?? string.Empty, PurchaseBorder, center: true);
            AddTableCell(table, row + 1, 1, itemText, PurchaseBorder, autoShrink: true);
            AddTableCell(table, row + 1, 2, line?.Unit ?? string.Empty, PurchaseBorder, center: true);
            AddTableCell(table, row + 1, 3, line is null ? string.Empty : FormatQuantity(line.Quantity), PurchaseBorder, alignRight: true);
            AddTableCell(table, row + 1, 4, line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.UnitPrice), PurchaseBorder, alignRight: true);
            AddTableCell(table, row + 1, 5, line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.Amount), PurchaseBorder, alignRight: true);
        }

        return new Border
        {
            BorderBrush = PurchaseBorder,
            BorderThickness = new Thickness(1),
            Child = table
        };
    }

    private static Border BuildPurchaseTotals(InvoicePrintModel model, bool isFinalPage)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        AddTotalsPair(grid, 0, 0, "전표메모", isFinalPage ? model.Memo : string.Empty, PurchaseBorder, false, false, labelBackground: PurchaseHeaderFill);
        AddTotalsPair(grid, 0, 2, "공급가", isFinalPage ? FormatMoney(model.SupplyAmount) : string.Empty, PurchaseBorder, true, true, labelBackground: PurchaseHeaderFill);
        AddTotalsPair(grid, 0, 4, "부가세", isFinalPage ? FormatMoney(model.VatAmount) : string.Empty, PurchaseBorder, true, true, labelBackground: PurchaseHeaderFill);
        AddTotalsPair(grid, 0, 6, "합계", isFinalPage ? FormatMoney(model.TotalAmount) : string.Empty, PurchaseBorder, true, true, labelBackground: PurchaseHeaderFill);

        AddTotalsPair(grid, 1, 0, "전미수", isFinalPage ? "0" : string.Empty, PurchaseBorder, false, true, labelBackground: PurchaseHeaderFill);
        AddTotalsPair(grid, 1, 2, "지불액", isFinalPage ? FormatMoney(model.PaidAmount) : string.Empty, PurchaseBorder, false, true, labelBackground: PurchaseHeaderFill);
        AddTotalsPair(grid, 1, 4, "미수잔액", isFinalPage ? FormatSignedMoney(model.BalanceAmount) : string.Empty, PurchaseBorder, false, true, labelBackground: PurchaseHeaderFill);
        AddTotalsPair(grid, 1, 6, string.Empty, isFinalPage ? string.Empty : "다음 페이지 계속", PurchaseBorder, false, true, labelBackground: PurchaseHeaderFill);

        return new Border
        {
            BorderBrush = PurchaseBorder,
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    private static FixedDocument BuildSalesDocument(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)SalesRowsPerPage));
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(A4Width, A4Height);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageLines = lines
                .Skip(pageIndex * SalesRowsPerPage)
                .Take(SalesRowsPerPage)
                .ToList();

            var page = BuildSalesPage(model, pageLines, pageIndex + 1, pageCount);
            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(page);
            document.Pages.Add(pageContent);
        }

        return document;
    }

    private static FixedPage BuildSalesPage(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines,
        int pageNumber,
        int totalPages)
    {
        var page = CreatePage();
        var root = CreateRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AddToGrid(root, BuildSalesSlip(model, lines, pageNumber, totalPages, "공급자 보관용", SalesSupplierAccent), 0);

        var separator = new Border
        {
            Background = BorderGray,
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        AddToGrid(root, separator, 1);

        AddToGrid(root, BuildSalesSlip(model, lines, pageNumber, totalPages, "공급받는자 보관용", SalesBuyerAccent), 2);

        FixedPage.SetLeft(root, PageMargin);
        FixedPage.SetTop(root, PageMargin);
        page.Children.Add(root);
        FinalizePage(page);
        return page;
    }

    private static Border BuildSalesSlip(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines,
        int pageNumber,
        int totalPages,
        string copyLabel,
        Brush accent)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddToGrid(layout, BuildSalesHeader(model, pageNumber, totalPages, copyLabel, accent), 0);
        AddToGrid(layout, BuildSalesPartySection(model, accent), 1);
        AddToGrid(layout, BuildSalesLineTable(model, lines, accent), 2);
        AddToGrid(layout, BuildSalesTotals(model, accent), 3);
        AddToGrid(layout, BuildSalesFooter(model, lines, accent), 4);

        return new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Padding = new Thickness(8, 6, 8, 6),
            Child = layout
        };
    }

    private static Grid BuildSalesHeader(
        InvoicePrintModel model,
        int pageNumber,
        int totalPages,
        string copyLabel,
        Brush accent)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

        var left = new StackPanel();
        left.Children.Add(CreateText("작성일자", 10, null, accent));
        left.Children.Add(CreateText(model.PrintWithDate ? model.InvoiceDate.ToString("yyyy-MM-dd") : string.Empty, 11));
        Grid.SetColumn(left, 0);
        header.Children.Add(left);

        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        center.Children.Add(CreateText("거 래 명 세 서", 31, FontWeights.Bold, accent, TextAlignment.Center));
        center.Children.Add(CreateText(copyLabel, 13, FontWeights.Bold, accent, TextAlignment.Center));
        Grid.SetColumn(center, 1);
        header.Children.Add(center);

        var right = CreateText($"Page: {pageNumber}/{totalPages}", 12, null, accent);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        right.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(right, 2);
        header.Children.Add(right);

        return header;
    }

    private static Grid BuildSalesPartySection(InvoicePrintModel model, Brush accent)
    {
        var section = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        section.ColumnDefinitions.Add(new ColumnDefinition());
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        section.ColumnDefinitions.Add(new ColumnDefinition());

        var supplier = BuildSalesPartyCard(
            "공급자",
            model.SupplierBusinessNumber,
            model.SupplierName,
            model.SupplierPhone,
            model.SupplierRepresentative,
            model.SupplierAddress,
            model.SupplierStampImage,
            accent);
        Grid.SetColumn(supplier, 0);
        section.Children.Add(supplier);

        var buyer = BuildSalesPartyCard(
            "공급받는자",
            model.BuyerBusinessNumber,
            model.BuyerName,
            model.BuyerPhone,
            model.BuyerRepresentative,
            model.BuyerAddress,
            null,
            accent);
        Grid.SetColumn(buyer, 2);
        section.Children.Add(buyer);

        return section;
    }

    private static UIElement BuildSalesPartyCard(
        string title,
        string businessNumber,
        string name,
        string phone,
        string representative,
        string address,
        byte[]? stampImage,
        Brush accent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        AddLabelValue(grid, 0, title, businessNumber, accent);
        AddLabelValue(grid, 1, "상호", name, accent);
        AddLabelValue(grid, 2, "전화번호", phone, accent);
        AddLabelValue(grid, 3, "대표자", representative, accent);
        AddLabelValue(grid, 4, "주소", address, accent, wrap: true);

        var frame = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Child = grid
        };

        if (stampImage is not { Length: > 0 })
            return frame;

        var image = TryCreateStampImage(stampImage);
        if (image is null)
            return frame;

        var root = new Grid();
        root.Children.Add(frame);
        root.Children.Add(new Image
        {
            Source = image,
            Width = 62,
            Height = 54,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 46, 8, 0),
            Stretch = Stretch.Uniform
        });
        return root;
    }

    private static Border BuildSalesLineTable(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines,
        Brush accent)
    {
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        for (var i = 0; i < SalesRowsPerPage; i++)
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AddTableCell(table, 0, 0, "순번", accent, isHeader: true, center: true);
        AddTableCell(table, 0, 1, "품명", accent, isHeader: true, center: true);
        AddTableCell(table, 0, 2, "규격", accent, isHeader: true, center: true);
        AddTableCell(table, 0, 3, "단위", accent, isHeader: true, center: true);
        AddTableCell(table, 0, 4, "수량", accent, isHeader: true, center: true);
        AddTableCell(table, 0, 5, "단가", accent, isHeader: true, center: true);
        AddTableCell(table, 0, 6, "금액", accent, isHeader: true, center: true);

        for (var row = 0; row < SalesRowsPerPage; row++)
        {
            var line = row < lines.Count ? lines[row] : null;
            var blankHint = line is null && row == lines.Count && lines.Count > 0;
            AddTableCell(table, row + 1, 0, line?.No.ToString() ?? string.Empty, accent, center: true);
            AddTableCell(table, row + 1, 1, line?.ItemName ?? (blankHint ? "*** 이하 여백 ***" : string.Empty), accent, autoShrink: true);
            AddTableCell(table, row + 1, 2, line?.Specification ?? string.Empty, accent, autoShrink: true);
            AddTableCell(table, row + 1, 3, line?.Unit ?? string.Empty, accent, center: true);
            AddTableCell(table, row + 1, 4, line is null ? string.Empty : FormatQuantity(line.Quantity), accent, alignRight: true);
            AddTableCell(table, row + 1, 5, line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.UnitPrice), accent, alignRight: true);
            AddTableCell(table, row + 1, 6, line is null || !model.PrintWithPrice ? string.Empty : FormatMoney(line.Amount), accent, alignRight: true);
        }

        return new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 4),
            Child = table
        };
    }

    private static Border BuildSalesTotals(InvoicePrintModel model, Brush accent)
    {
        var grid = new Grid();
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

        AddTotalsPair(grid, 0, 0, "전표메모", model.Memo, accent, false, false);
        AddTotalsPair(grid, 0, 2, "공급가", model.PrintWithPrice ? FormatMoney(model.SupplyAmount) : string.Empty, accent, true, true);
        AddTotalsPair(grid, 0, 4, "부가세", model.PrintWithPrice ? FormatMoney(model.VatAmount) : string.Empty, accent, true, true);
        AddTotalsPair(grid, 0, 6, "합계", model.PrintWithPrice ? FormatMoney(model.TotalAmount) : string.Empty, accent, true, true);

        AddTotalsPair(grid, 1, 0, "비고", string.Empty, accent, false, false);
        AddTotalsPair(grid, 1, 2, "받은금", model.PrintWithPrice ? FormatMoney(model.PaidAmount) : string.Empty, accent, true, true);
        AddTotalsPair(grid, 1, 4, "미수잔액", model.PrintWithPrice ? FormatMoney(model.BalanceAmount) : string.Empty, accent, true, true);
        AddTotalsPair(grid, 1, 6, "인수자", "(인)", accent, false, true);

        return new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 3),
            Child = grid
        };
    }

    private static Grid BuildSalesFooter(
        InvoicePrintModel model,
        IReadOnlyList<InvoicePrintLineModel> lines,
        Brush accent)
    {
        var quantitySum = lines.Sum(line => line.Quantity);
        var leftText = string.IsNullOrWhiteSpace(model.BankAccountText)
            ? (model.FooterText ?? string.Empty)
            : $"입금안내 {model.BankAccountText} {model.FooterText}".Trim();

        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        panel.Children.Add(CreateText(leftText, 10));

        var right = CreateText(
            $"수량합계 {FormatQuantity(quantitySum)}",
            10,
            null,
            accent);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(right, 1);
        panel.Children.Add(right);

        return panel;
    }

    private static void AddLabelValue(
        Grid grid,
        int row,
        string label,
        string value,
        Brush borderBrush,
        bool wrap = false,
        Brush? labelBackground = null,
        Brush? labelForeground = null,
        Brush? valueForeground = null)
    {
        var labelBorder = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = GetInnerCellBorderThickness(grid, row, 0),
            Background = labelBackground ?? HeaderFill,
            Padding = new Thickness(4, 2, 4, 2),
            Child = CreateText(label, 10.5, FontWeights.Bold, labelForeground ?? Brushes.Black, TextAlignment.Center)
        };
        Grid.SetRow(labelBorder, row);
        Grid.SetColumn(labelBorder, 0);
        grid.Children.Add(labelBorder);

        var valueBlock = CreateText(value ?? string.Empty, 10.5);
        valueBlock.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var valueBorder = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = GetInnerCellBorderThickness(grid, row, 1),
            Padding = new Thickness(5, 2, 5, 2),
            Child = valueBlock
        };
        valueBlock.Foreground = valueForeground ?? Brushes.Black;
        Grid.SetRow(valueBorder, row);
        Grid.SetColumn(valueBorder, 1);
        grid.Children.Add(valueBorder);
    }

    private static void AddTotalsPair(
        Grid grid,
        int row,
        int labelColumn,
        string label,
        string value,
        Brush borderBrush,
        bool boldValue,
        bool alignRight,
        Brush? labelBackground = null,
        Brush? labelForeground = null,
        Brush? valueForeground = null)
    {
        var labelBorder = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = GetInnerCellBorderThickness(grid, row, labelColumn),
            Background = labelBackground ?? HeaderFill,
            Padding = new Thickness(4, 2, 4, 2),
            Child = CreateText(label, 10.5, FontWeights.Bold, labelForeground ?? Brushes.Black, TextAlignment.Center)
        };
        Grid.SetRow(labelBorder, row);
        Grid.SetColumn(labelBorder, labelColumn);
        grid.Children.Add(labelBorder);

        var valueBorder = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = GetInnerCellBorderThickness(grid, row, labelColumn + 1),
            Padding = new Thickness(4, 2, 4, 2),
            Child = CreateText(
                value ?? string.Empty,
                10.5,
                boldValue ? FontWeights.Bold : FontWeights.Normal,
                valueForeground ?? Brushes.Black,
                alignRight ? TextAlignment.Right : TextAlignment.Left)
        };
        Grid.SetRow(valueBorder, row);
        Grid.SetColumn(valueBorder, labelColumn + 1);
        grid.Children.Add(valueBorder);
    }

    private static void AddTableCell(
        Grid grid,
        int row,
        int column,
        string text,
        Brush? borderBrush = null,
        bool isHeader = false,
        bool center = false,
        bool alignRight = false,
        Brush? background = null,
        Brush? foreground = null,
        bool autoShrink = false)
    {
        borderBrush ??= BorderGray;
        var alignment = center ? TextAlignment.Center : alignRight ? TextAlignment.Right : TextAlignment.Left;
        var textBlock = CreateText(
            text ?? string.Empty,
            isHeader ? 10.6 : 9.8,
            isHeader ? FontWeights.Bold : FontWeights.Normal,
            foreground ?? (isHeader ? borderBrush : Brushes.Black),
            alignment);
        textBlock.TextWrapping = TextWrapping.NoWrap;
        textBlock.TextTrimming = autoShrink ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        if (autoShrink)
        {
            textBlock.HorizontalAlignment = alignment switch
            {
                TextAlignment.Center => HorizontalAlignment.Center,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            };
        }

        var border = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = GetInnerCellBorderThickness(grid, row, column),
            Background = background ?? (isHeader ? HeaderFill : Brushes.White),
            Padding = new Thickness(3, isHeader ? 1 : 2, 3, isHeader ? 1 : 2),
            Child = autoShrink
                ? new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    Child = textBlock
                }
                : textBlock
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static TextBlock CreateText(
        string text,
        double size,
        FontWeight? fontWeight = null,
        Brush? foreground = null,
        TextAlignment alignment = TextAlignment.Left,
        double marginTop = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = size,
            FontWeight = fontWeight ?? FontWeights.Normal,
            Foreground = foreground ?? Brushes.Black,
            TextAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = marginTop == 0 ? new Thickness(0) : new Thickness(0, marginTop, 0, 0)
        };
    }

    private static FixedPage CreatePage()
    {
        return new FixedPage
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
    }

    private static Grid CreateRootGrid()
    {
        return new Grid
        {
            Width = A4Width - (PageMargin * 2d),
            Height = A4Height - (PageMargin * 2d),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
    }

    private static void AddToGrid(Grid grid, UIElement child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static void FinalizePage(FixedPage page)
    {
        page.Measure(new Size(A4Width, A4Height));
        page.Arrange(new Rect(0, 0, A4Width, A4Height));
        page.UpdateLayout();
    }

    private static string FormatMoney(decimal value)
        => $"{Math.Round(value, 0, MidpointRounding.AwayFromZero):N0}";

    private static string FormatSignedMoney(decimal value)
        => value == 0
            ? "0"
            : $"{Math.Round(value, 0, MidpointRounding.AwayFromZero):+#,0;-#,0}";

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

    private static BitmapImage? TryCreateStampImage(byte[] stampBytes)
    {
        try
        {
            using var memory = new MemoryStream(stampBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
