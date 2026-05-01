using System.Diagnostics;
using System.IO;
using ClosedXML.Excel;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

public sealed class PeriodLedgerExcelExportService
{
    private const int BlockLedgerColumnCount = 10;

    public async Task<string> ExportAsync(
        PeriodLedgerBuildResult data,
        string? exportDirectory = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("엑셀 작성 중...");

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("원장");

        switch (data.Query.LedgerType)
        {
            case PeriodLedgerType.ReceiptPayment:
                FillPaymentLedgerSheet(sheet, data);
                break;
            case PeriodLedgerType.YeonsuDelivery:
                FillYeonsuDeliverySheet(sheet, data);
                break;
            default:
                FillBlockLedgerSheet(sheet, data);
                break;
        }

        ApplyPrintSetup(sheet);

        progress?.Report("저장 중...");

        var directory = ResolveExportDirectory(exportDirectory);
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, BuildFileName(data));

        await Task.Run(() => workbook.SaveAs(filePath), ct);

        OpenWithShell(filePath);
        progress?.Report($"완료: {Path.GetFileName(filePath)}");

        return filePath;
    }

    private static void FillBlockLedgerSheet(IXLWorksheet ws, PeriodLedgerBuildResult data)
    {
        var includeProfit = data.Query.IncludeProfit;

        ws.Cell(1, 1).Value = data.Title;
        ws.Range(1, 1, 1, BlockLedgerColumnCount).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(2, 1).Value = $"기간: {data.Query.From:yyyy-MM-dd} ~ {data.Query.To:yyyy-MM-dd}";
        ws.Range(2, 1, 2, BlockLedgerColumnCount).Merge();

        ws.Cell(3, 1).Value = $"구분: {data.ScopeLabel}  /  옵션: 가나다라순({(data.Query.SortByCustomerName ? "ON" : "OFF")}), 순이익표시({(includeProfit ? "ON" : "OFF")})";
        ws.Range(3, 1, 3, BlockLedgerColumnCount).Merge();

        var row = 5;
        WriteBlockLedgerHeader(ws, row, includeProfit);
        var headerRow = row;
        row++;

        foreach (var block in data.Blocks)
        {
            ws.Cell(row, 1).Value = block.CustomerName;
            ws.Range(row, 1, row, BlockLedgerColumnCount).Merge();
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E9EDF7");
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            row++;

            foreach (var entry in block.Rows)
            {
                if (entry.IsSubTotal)
                {
                    ws.Cell(row, 3).Value = "(소계)";
                    ws.Cell(row, 5).Value = entry.SubTotalQuantity;
                    ws.Cell(row, 7).Value = (entry.SubTotalAmount ?? 0m) - (entry.SubTotalVat ?? 0m);
                    ws.Cell(row, 8).Value = entry.SubTotalVat;
                    ws.Cell(row, 9).Value = entry.SubTotalAmount;
                    ws.Range(row, 1, row, BlockLedgerColumnCount).Style.Font.Bold = true;
                    ws.Range(row, 1, row, BlockLedgerColumnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8F8F8");
                    ApplyRowBorder(ws, row, includeProfit, endColumnOverride: BlockLedgerColumnCount);
                    ApplyNumberFormats(ws, row, includeProfit, subtotalRow: true);
                    row++;
                    continue;
                }

                ws.Cell(row, 1).Value = entry.Date.ToString("yyyy-MM-dd");
                ws.Cell(row, 2).Value = entry.Division;
                ws.Cell(row, 3).Value = entry.Summary;
                ws.Cell(row, 4).Value = entry.TradeAmount;
                ws.Cell(row, 5).Value = entry.ReceiptAmount;
                ws.Cell(row, 6).Value = entry.PaymentAmount;
                ws.Cell(row, 7).Value = entry.RunningBalance;
                ws.Cell(row, 8).Value = entry.ReceivableBalance;
                if (includeProfit)
                    ws.Cell(row, 9).Value = entry.ProfitAmount;
                ws.Cell(row, includeProfit ? 10 : 9).Value = entry.Note;

                ApplyRowBorder(ws, row, includeProfit, endColumnOverride: BlockLedgerColumnCount);
                ApplyNumberFormats(ws, row, includeProfit);

                row++;

                if (entry.Items.Count > 0)
                {
                    ws.Cell(row, 3).Value = "품명";
                    ws.Cell(row, 4).Value = "규격";
                    ws.Cell(row, 5).Value = "수량";
                    ws.Cell(row, 6).Value = "단가";
                    ws.Cell(row, 7).Value = "공급가";
                    ws.Cell(row, 8).Value = "부가세";
                    ws.Cell(row, 9).Value = "합계";
                    ws.Cell(row, 10).Value = "품목비고";
                    ws.Range(row, 3, row, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F3F8");
                    ws.Range(row, 3, row, 10).Style.Font.Bold = true;
                    ApplyRowBorder(ws, row, includeProfit, endColumnOverride: BlockLedgerColumnCount);
                    row++;

                    foreach (var item in entry.Items)
                    {
                        ws.Cell(row, 2).Value = "상세";
                        ws.Cell(row, 3).Value = item.ItemName;
                        ws.Cell(row, 4).Value = item.Specification;
                        ws.Cell(row, 5).Value = item.Quantity;
                        ws.Cell(row, 6).Value = item.UnitPrice;
                        ws.Cell(row, 7).Value = item.SupplyAmount;
                        ws.Cell(row, 8).Value = item.VatAmount;
                        ws.Cell(row, 9).Value = item.LineAmount;
                        ws.Cell(row, 10).Value = item.ItemNote;
                        ApplyRowBorder(ws, row, includeProfit, endColumnOverride: BlockLedgerColumnCount);
                        ws.Range(row, 5, row, 9).Style.NumberFormat.Format = "#,##0";
                        ws.Range(row, 5, row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        row++;
                    }
                }
            }
        }

        ws.Cell(row, 1).Value = "기간내 총 합계";
        ws.Cell(row, 4).Value = data.Totals.TradeAmount;
        ws.Cell(row, 5).Value = data.Totals.ReceiptAmount;
        ws.Cell(row, 6).Value = data.Totals.PaymentAmount;
        ws.Cell(row, 7).Value = data.Totals.RunningBalance;
        ws.Cell(row, 8).Value = data.Totals.ReceivableBalance;
        if (includeProfit)
            ws.Cell(row, 9).Value = data.Totals.ProfitAmount;

        ws.Range(row, 1, row, BlockLedgerColumnCount).Style.Font.Bold = true;
        ws.Range(row, 1, row, BlockLedgerColumnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("#EFEFEF");
        ApplyRowBorder(ws, row, includeProfit, endColumnOverride: BlockLedgerColumnCount);
        ApplyNumberFormats(ws, row, includeProfit);

        SetCommonSheetStyles(ws, BlockLedgerColumnCount, headerRow, row, applyBlockDefaultWidths: true);
    }

    private static void FillPaymentLedgerSheet(IXLWorksheet ws, PeriodLedgerBuildResult data)
    {
        ws.Cell(1, 1).Value = data.Title;
        ws.Range(1, 1, 1, 11).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(2, 1).Value = $"기간: {data.Query.From:yyyy-MM-dd} ~ {data.Query.To:yyyy-MM-dd}";
        ws.Range(2, 1, 2, 11).Merge();

        ws.Cell(3, 1).Value = $"구분: {data.ScopeLabel}  /  옵션: 가나다라순({(data.Query.SortByCustomerName ? "ON" : "OFF")})";
        ws.Range(3, 1, 3, 11).Merge();

        var row = 5;
        var headers = new[] { "No", "거래날짜", "전표구분", "품목거래내역", "거래금액", "수금액", "지불액", "누적잔액", "전미수/미지불", "거래처명", "전표메모" };
        for (var i = 0; i < headers.Length; i++)
            ws.Cell(row, i + 1).Value = headers[i];

        var headerRow = row;
        ws.Range(row, 1, row, 11).Style.Fill.BackgroundColor = XLColor.FromHtml("#E5E5E5");
        ws.Range(row, 1, row, 11).Style.Font.Bold = true;
        ws.Range(row, 1, row, 11).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ApplyRowBorder(ws, row, false, endColumnOverride: 11);

        row++;

        foreach (var entry in data.PaymentRows)
        {
            ws.Cell(row, 1).Value = entry.No;
            ws.Cell(row, 2).Value = entry.Date.ToString("yyyy-MM-dd");
            ws.Cell(row, 3).Value = entry.Division;
            ws.Cell(row, 4).Value = entry.Summary;
            ws.Cell(row, 5).Value = entry.TradeAmount;
            ws.Cell(row, 6).Value = entry.ReceiptAmount;
            ws.Cell(row, 7).Value = entry.PaymentAmount;
            ws.Cell(row, 8).Value = entry.RunningBalance;
            ws.Cell(row, 9).Value = entry.ReceivableBalance;
            ws.Cell(row, 10).Value = entry.CustomerName;
            ws.Cell(row, 11).Value = entry.Note;

            ApplyRowBorder(ws, row, false, endColumnOverride: 11);
            ws.Range(row, 5, row, 9).Style.NumberFormat.Format = "#,##0";
            ws.Range(row, 5, row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            row++;
        }

        ws.Cell(row, 1).Value = "기간내 총 합계";
        ws.Cell(row, 5).Value = data.Totals.TradeAmount;
        ws.Cell(row, 6).Value = data.Totals.ReceiptAmount;
        ws.Cell(row, 7).Value = data.Totals.PaymentAmount;
        ws.Cell(row, 8).Value = data.Totals.RunningBalance;
        ws.Cell(row, 9).Value = data.Totals.ReceivableBalance;

        ws.Range(row, 1, row, 11).Style.Font.Bold = true;
        ws.Range(row, 1, row, 11).Style.Fill.BackgroundColor = XLColor.FromHtml("#EFEFEF");
        ApplyRowBorder(ws, row, false, endColumnOverride: 11);
        ws.Range(row, 5, row, 9).Style.NumberFormat.Format = "#,##0";

        SetCommonSheetStyles(ws, 11, headerRow, row, applyBlockDefaultWidths: false);
        SetPaymentColumnWidths(ws);
    }

    private static void FillYeonsuDeliverySheet(IXLWorksheet ws, PeriodLedgerBuildResult data)
    {
        ws.Cell(1, 1).Value = data.Title;
        ws.Range(1, 1, 1, 9).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(2, 1).Value = $"기간: {data.Query.From:yyyy-MM-dd} ~ {data.Query.To:yyyy-MM-dd}";
        ws.Range(2, 1, 2, 9).Merge();

        ws.Cell(3, 1).Value = $"구분: {data.ScopeLabel}  /  옵션: 가나다라순({(data.Query.SortByCustomerName ? "ON" : "OFF")})";
        ws.Range(3, 1, 3, 9).Merge();

        var row = 5;
        var headers = new[]
        {
            "No", "납품일자", "거래처명", "품목요약", "합계금액", "출고창고", "전표메모", "마지막 저장자", "마지막 저장시간"
        };
        for (var i = 0; i < headers.Length; i++)
            ws.Cell(row, i + 1).Value = headers[i];

        var headerRow = row;
        ws.Range(row, 1, row, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#E5E5E5");
        ws.Range(row, 1, row, 9).Style.Font.Bold = true;
        ws.Range(row, 1, row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ApplyRowBorder(ws, row, includeProfit: false, endColumnOverride: 9);
        row++;

        foreach (var entry in data.YeonsuDeliveryRows)
        {
            ws.Cell(row, 1).Value = entry.No;
            ws.Cell(row, 2).Value = entry.DeliveryDate.ToString("yyyy-MM-dd");
            ws.Cell(row, 3).Value = entry.CustomerName;
            ws.Cell(row, 4).Value = entry.ItemSummary;
            ws.Cell(row, 5).Value = entry.TotalAmount;
            ws.Cell(row, 6).Value = entry.WarehouseName;
            ws.Cell(row, 7).Value = entry.Note;
            ws.Cell(row, 8).Value = entry.LastSavedBy;
            ws.Cell(row, 9).Value = entry.LastSavedAtUtc == default
                ? string.Empty
                : entry.LastSavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            ApplyRowBorder(ws, row, includeProfit: false, endColumnOverride: 9);
            ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            row++;
        }

        ws.Cell(row, 1).Value = "기간내 총 합계";
        ws.Cell(row, 5).Value = data.Totals.TradeAmount;
        ws.Range(row, 1, row, 9).Style.Font.Bold = true;
        ws.Range(row, 1, row, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#EFEFEF");
        ApplyRowBorder(ws, row, includeProfit: false, endColumnOverride: 9);
        ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";

        SetCommonSheetStyles(ws, 9, headerRow, row, applyBlockDefaultWidths: false);
        SetYeonsuColumnWidths(ws);
    }

    private static void WriteBlockLedgerHeader(IXLWorksheet ws, int row, bool includeProfit)
    {
        var headers = includeProfit
            ? new[] { "거래날짜", "구분", "품목거래내역", "거래금액", "수금액", "지불액", "누적잔액", "전미수/미지불", "순이익", "전표메모" }
            : new[] { "거래날짜", "구분", "품목거래내역", "거래금액", "수금액", "지불액", "누적잔액", "전미수/미지불", "전표메모", "품목비고" };

        for (var i = 0; i < headers.Length; i++)
            ws.Cell(row, i + 1).Value = headers[i];

        ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#E5E5E5");
        ws.Range(row, 1, row, headers.Length).Style.Font.Bold = true;
        ws.Range(row, 1, row, headers.Length).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ApplyRowBorder(ws, row, includeProfit, endColumnOverride: BlockLedgerColumnCount);
    }

    private static void ApplyRowBorder(IXLWorksheet ws, int row, bool includeProfit, int? endColumnOverride = null)
    {
        var end = endColumnOverride ?? (includeProfit ? 9 : 8);
        ws.Range(row, 1, row, end).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row, end).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }

    private static void ApplyNumberFormats(IXLWorksheet ws, int row, bool includeProfit, bool subtotalRow = false)
    {
        if (!subtotalRow)
        {
            var lastAmountColumn = includeProfit ? 9 : 8;
            ws.Range(row, 4, row, lastAmountColumn).Style.NumberFormat.Format = "#,##0";
            ws.Range(row, 4, row, lastAmountColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        else
        {
            ws.Range(row, 5, row, 9).Style.NumberFormat.Format = "#,##0";
            ws.Range(row, 5, row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
    }

    private static void SetCommonSheetStyles(
        IXLWorksheet ws,
        int lastCol,
        int headerRow,
        int lastRow,
        bool applyBlockDefaultWidths)
    {
        ws.Style.Font.FontName = "맑은 고딕";
        ws.Style.Font.FontSize = 10;
        ws.Rows(1, lastRow).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Columns(1, lastCol).AdjustToContents(8, 60);
        ws.Range(1, 1, lastRow, lastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

        ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);

        var lastColLetter = XLHelper.GetColumnLetterFromNumber(lastCol);
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add($"A1:{lastColLetter}{lastRow}");

        // hard minimum-like readability: do not drop below 9pt by avoiding workbook scaling over-compression.
        ws.PageSetup.Scale = 100;

        if (applyBlockDefaultWidths)
            SetBlockColumnWidths(ws, lastCol >= BlockLedgerColumnCount && ws.Cell(headerRow, 9).GetString() == "순이익");
    }

    private static void SetBlockColumnWidths(IXLWorksheet ws, bool includeProfit)
    {
        EnsureColumnMinWidth(ws.Column(1), 12);
        EnsureColumnMinWidth(ws.Column(2), 8);
        EnsureColumnMinWidth(ws.Column(3), 32);
        EnsureColumnMinWidth(ws.Column(4), 13);
        EnsureColumnMinWidth(ws.Column(5), 13);
        EnsureColumnMinWidth(ws.Column(6), 13);
        EnsureColumnMinWidth(ws.Column(7), 13);
        EnsureColumnMinWidth(ws.Column(8), 13);
        if (includeProfit)
        {
            EnsureColumnMinWidth(ws.Column(9), 13);
            EnsureColumnMinWidth(ws.Column(10), 26);
        }
        else
        {
            EnsureColumnMinWidth(ws.Column(9), 26);
            EnsureColumnMinWidth(ws.Column(10), 24);
        }

        ws.Column(3).Style.Alignment.WrapText = true;
        ws.Column(9).Style.Alignment.WrapText = true;
        ws.Column(10).Style.Alignment.WrapText = true;
    }

    private static void SetPaymentColumnWidths(IXLWorksheet ws)
    {
        EnsureColumnMinWidth(ws.Column(1), 6);
        EnsureColumnMinWidth(ws.Column(2), 12);
        EnsureColumnMinWidth(ws.Column(3), 10);
        EnsureColumnMinWidth(ws.Column(4), 35);
        EnsureColumnMinWidth(ws.Column(5), 15);
        EnsureColumnMinWidth(ws.Column(6), 15);
        EnsureColumnMinWidth(ws.Column(7), 15);
        EnsureColumnMinWidth(ws.Column(8), 15);
        EnsureColumnMinWidth(ws.Column(9), 15);
        EnsureColumnMinWidth(ws.Column(10), 24);
        EnsureColumnMinWidth(ws.Column(11), 24);
        ws.Column(4).Style.Alignment.WrapText = true;
        ws.Column(10).Style.Alignment.WrapText = true;
        ws.Column(11).Style.Alignment.WrapText = true;
    }

    private static void SetYeonsuColumnWidths(IXLWorksheet ws)
    {
        EnsureColumnMinWidth(ws.Column(1), 6);
        EnsureColumnMinWidth(ws.Column(2), 12);
        EnsureColumnMinWidth(ws.Column(3), 24);
        EnsureColumnMinWidth(ws.Column(4), 36);
        EnsureColumnMinWidth(ws.Column(5), 15);
        EnsureColumnMinWidth(ws.Column(6), 14);
        EnsureColumnMinWidth(ws.Column(7), 24);
        EnsureColumnMinWidth(ws.Column(8), 14);
        EnsureColumnMinWidth(ws.Column(9), 20);
        ws.Column(4).Style.Alignment.WrapText = true;
        ws.Column(7).Style.Alignment.WrapText = true;
    }

    private static void EnsureColumnMinWidth(IXLColumn column, double minimumWidth)
    {
        if (column.Width < minimumWidth)
            column.Width = minimumWidth;
    }

    private static void ApplyPrintSetup(IXLWorksheet ws)
    {
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PagesWide = 1;
        ws.PageSetup.PagesTall = 0;
        ws.PageSetup.Margins.Left = 0.3;
        ws.PageSetup.Margins.Right = 0.3;
        ws.PageSetup.Margins.Top = 0.5;
        ws.PageSetup.Margins.Bottom = 0.5;
    }

    private static string ResolveExportDirectory(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred.Trim();

        return AppPaths.UserDownloadsDir;
    }

    private static string BuildFileName(PeriodLedgerBuildResult data)
    {
        var kind = data.Query.LedgerType switch
        {
            PeriodLedgerType.SalesPurchase => "판매+구매",
            PeriodLedgerType.SalesOnly => "판매매출",
            PeriodLedgerType.PurchaseOnly => "구매매입",
            PeriodLedgerType.ReceiptPayment => "수금지불",
            PeriodLedgerType.YeonsuDelivery => "연수구납품내역",
            _ => "거래원장"
        };

        var raw = $"{data.Query.From:yyyy-MM-dd}~{data.Query.To:yyyy-MM-dd} 의 {kind} 거래원장_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return ReplaceInvalidFileNameChars(raw);
    }

    private static string ReplaceInvalidFileNameChars(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 파일 저장은 완료되었으므로, 기본 연결 프로그램 실행 실패는 집계 실패로 처리하지 않습니다.
        }
    }
}


