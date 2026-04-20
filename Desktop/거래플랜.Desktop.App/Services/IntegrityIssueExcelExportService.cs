using System.Diagnostics;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class IntegrityIssueExcelExportService
{
    public async Task<string> ExportAsync(IntegrityIssueDetailResultDto detail, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var workbook = new XLWorkbook();
        var summarySheet = workbook.Worksheets.Add("요약");
        var detailSheet = workbook.Worksheets.Add("상세목록");

        FillSummarySheet(summarySheet, detail);
        FillDetailSheet(detailSheet, detail.Rows);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await Task.Run(() => workbook.SaveAs(filePath), ct);
        OpenWithShell(filePath);
        return filePath;
    }

    private static void FillSummarySheet(IXLWorksheet sheet, IntegrityIssueDetailResultDto detail)
    {
        sheet.Cell(1, 1).Value = "서버 무결성 상세 목록";
        sheet.Range(1, 1, 1, 2).Merge();
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 16;
        sheet.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var rows = new (string Label, string Value)[]
        {
            ("조회시각", detail.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            ("이슈 코드", detail.Code),
            ("심각도", detail.Severity),
            ("이슈 내용", detail.Message),
            ("대상 범위", string.Join(" / ", new[] { detail.OfficeCode, detail.TenantCode }.Where(value => !string.IsNullOrWhiteSpace(value)))),
            ("상세 건수", detail.DetailCount.ToString("N0"))
        };

        var rowIndex = 3;
        foreach (var row in rows)
        {
            sheet.Cell(rowIndex, 1).Value = row.Label;
            sheet.Cell(rowIndex, 2).Value = row.Value;
            sheet.Cell(rowIndex, 1).Style.Font.Bold = true;
            sheet.Cell(rowIndex, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E9EDF7");
            sheet.Range(rowIndex, 1, rowIndex, 2).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            sheet.Range(rowIndex, 1, rowIndex, 2).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            rowIndex++;
        }

        sheet.Style.Font.FontName = "맑은 고딕";
        sheet.Style.Font.FontSize = 10;
        sheet.Column(1).Width = 18;
        sheet.Column(2).Width = 90;
        sheet.Column(2).Style.Alignment.WrapText = true;
    }

    private static void FillDetailSheet(IXLWorksheet sheet, IReadOnlyList<IntegrityIssueDetailRowDto> rows)
    {
        var headers = new[] { "엔터티", "엔터티 ID", "주요 정보", "보조 정보", "참조", "범위", "상세" };
        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(1, column + 1).Value = headers[column];

        var headerRange = sheet.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E5E5E5");
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var excelRow = rowIndex + 2;
            sheet.Cell(excelRow, 1).Value = row.EntityType;
            sheet.Cell(excelRow, 2).Value = row.EntityIdText;
            sheet.Cell(excelRow, 3).Value = row.PrimaryText;
            sheet.Cell(excelRow, 4).Value = row.SecondaryText;
            sheet.Cell(excelRow, 5).Value = row.ReferenceText;
            sheet.Cell(excelRow, 6).Value = row.ScopeText;
            sheet.Cell(excelRow, 7).Value = row.DetailText;

            var range = sheet.Range(excelRow, 1, excelRow, headers.Length);
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            if (rowIndex % 2 == 1)
                range.Style.Fill.BackgroundColor = XLColor.FromHtml("#FAFAFA");
        }

        sheet.Style.Font.FontName = "맑은 고딕";
        sheet.Style.Font.FontSize = 10;
        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Columns(1, headers.Length).AdjustToContents(10, 70);
        sheet.Column(3).Width = Math.Max(sheet.Column(3).Width, 22d);
        sheet.Column(4).Width = Math.Max(sheet.Column(4).Width, 28d);
        sheet.Column(5).Width = Math.Max(sheet.Column(5).Width, 24d);
        sheet.Column(6).Width = Math.Max(sheet.Column(6).Width, 24d);
        sheet.Column(7).Width = Math.Max(sheet.Column(7).Width, 40d);
        sheet.Columns(3, 7).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(rows.Count + 1, 1), headers.Length).SetAutoFilter();
    }

    private static void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }
}
