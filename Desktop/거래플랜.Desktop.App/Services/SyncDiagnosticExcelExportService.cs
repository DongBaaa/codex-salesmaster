using System.Diagnostics;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace 거래플랜.Desktop.App.Services;

public sealed class SyncDiagnosticExcelExportService
{
    public async Task<string> ExportAsync(
        IReadOnlyList<SyncDiagnosticListItem> events,
        SyncDiagnosticSummary summary,
        SyncDiagnosticFilter filter,
        string filePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var workbook = new XLWorkbook();
        var summarySheet = workbook.Worksheets.Add("요약");
        var detailSheet = workbook.Worksheets.Add("진단이벤트");

        FillSummarySheet(summarySheet, events, summary, filter);
        FillDetailSheet(detailSheet, events);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await Task.Run(() => workbook.SaveAs(filePath), ct);
        OpenWithShell(filePath);
        return filePath;
    }

    private static void FillSummarySheet(
        IXLWorksheet sheet,
        IReadOnlyList<SyncDiagnosticListItem> events,
        SyncDiagnosticSummary summary,
        SyncDiagnosticFilter filter)
    {
        sheet.Cell(1, 1).Value = "동기화 진단 엑셀";
        sheet.Range(1, 1, 1, 2).Merge();
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 16;
        sheet.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var rows = new (string Label, string Value)[]
        {
            ("생성시각", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            ("내보낸 행 수", events.Count.ToString("N0")),
            ("미해결 확인 항목", summary.OpenIssueCount.ToString("N0")),
            ("자동 복구 가능", summary.RecoverableIssueCount.ToString("N0")),
            ("전체 누적", summary.TotalIssueCount.ToString("N0")),
            ("마지막 성공", summary.LastSuccessAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음"),
            ("마지막 실패", summary.LastFailureAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음"),
            ("마지막 확인 항목", string.IsNullOrWhiteSpace(summary.LastError) ? "없음" : summary.LastError),
            ("마지막 서버 revision", summary.LastKnownSyncRevision.ToString("N0")),
            ("검색어", string.IsNullOrWhiteSpace(filter.SearchText) ? "전체" : filter.SearchText),
            ("분류 필터", string.IsNullOrWhiteSpace(filter.Category) ? "전체" : filter.Category),
            ("상태 필터", string.IsNullOrWhiteSpace(filter.Status) ? "전체" : filter.Status),
            ("심각도 필터", string.IsNullOrWhiteSpace(filter.Severity) ? "전체" : filter.Severity),
            ("자동 복구 가능만", filter.OnlyRecoverable ? "예" : "아니오")
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
        sheet.Column(1).Width = 20;
        sheet.Column(2).Width = 96;
        sheet.Column(2).Style.Alignment.WrapText = true;
    }

    private static void FillDetailSheet(IXLWorksheet sheet, IReadOnlyList<SyncDiagnosticListItem> events)
    {
        var headers = new[]
        {
            "발생시각",
            "마지막발생",
            "횟수",
            "상태",
            "심각도",
            "분류",
            "세부분류",
            "엔터티",
            "엔터티 ID",
            "참조",
            "사용자범위",
            "장비/버전",
            "동기화 단계",
            "자동복구가능",
            "복구상태",
            "복구안내",
            "원인요약",
            "원문메시지",
            "정규화메시지",
            "로컬 dirty 스냅샷"
        };

        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(1, column + 1).Value = headers[column];

        var headerRange = sheet.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E5E5E5");
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        for (var rowIndex = 0; rowIndex < events.Count; rowIndex++)
        {
            var item = events[rowIndex];
            var excelRow = rowIndex + 2;
            sheet.Cell(excelRow, 1).Value = item.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            sheet.Cell(excelRow, 2).Value = item.LastOccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            sheet.Cell(excelRow, 3).Value = item.OccurrenceCount;
            sheet.Cell(excelRow, 4).Value = item.Status;
            sheet.Cell(excelRow, 5).Value = item.Severity;
            sheet.Cell(excelRow, 6).Value = item.Category;
            sheet.Cell(excelRow, 7).Value = item.Subcategory;
            sheet.Cell(excelRow, 8).Value = item.EntityName;
            sheet.Cell(excelRow, 9).Value = item.EntityId;
            sheet.Cell(excelRow, 10).Value = item.ReferenceText;
            sheet.Cell(excelRow, 11).Value = item.UserScopeText;
            sheet.Cell(excelRow, 12).Value = item.MachineVersionText;
            sheet.Cell(excelRow, 13).Value = item.SyncPhase;
            sheet.Cell(excelRow, 14).Value = item.IsRecoverable ? "예" : "아니오";
            sheet.Cell(excelRow, 15).Value = item.RecoverySucceeded
                ? "복구완료"
                : item.RecoveryAttempted
                    ? "복구시도"
                    : "미시도";
            sheet.Cell(excelRow, 16).Value = item.RecoveryAction;
            sheet.Cell(excelRow, 17).Value = item.SummaryText;
            sheet.Cell(excelRow, 18).Value = item.RawMessage;
            sheet.Cell(excelRow, 19).Value = item.NormalizedMessage;
            sheet.Cell(excelRow, 20).Value = BuildSnapshotText(item.Snapshot);

            var range = sheet.Range(excelRow, 1, excelRow, headers.Length);
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            if (rowIndex % 2 == 1)
                range.Style.Fill.BackgroundColor = XLColor.FromHtml("#FAFAFA");
        }

        sheet.Style.Font.FontName = "맑은 고딕";
        sheet.Style.Font.FontSize = 10;
        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Columns(1, headers.Length).AdjustToContents(10, 60);
        sheet.Column(10).Width = Math.Max(sheet.Column(10).Width, 24d);
        sheet.Column(11).Width = Math.Max(sheet.Column(11).Width, 22d);
        sheet.Column(12).Width = Math.Max(sheet.Column(12).Width, 20d);
        sheet.Column(16).Width = Math.Max(sheet.Column(16).Width, 34d);
        sheet.Column(17).Width = Math.Max(sheet.Column(17).Width, 22d);
        sheet.Column(18).Width = Math.Max(sheet.Column(18).Width, 48d);
        sheet.Column(19).Width = Math.Max(sheet.Column(19).Width, 40d);
        sheet.Column(20).Width = Math.Max(sheet.Column(20).Width, 30d);
        sheet.Columns(10, 20).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(events.Count + 1, 1), headers.Length).SetAutoFilter();
    }

    private static string BuildSnapshotText(SyncDiagnosticSnapshot snapshot)
        => string.Join(
            " / ",
            new[]
            {
                $"customerMaster {snapshot.DirtyCustomerMasterCount:N0}",
                $"customer {snapshot.DirtyCustomerCount:N0}",
                $"invoice {snapshot.DirtyInvoiceCount:N0}",
                $"transaction {snapshot.DirtyTransactionCount:N0}",
                $"attachment {snapshot.DirtyAttachmentCount:N0}",
                $"payment {snapshot.DirtyPaymentCount:N0}",
                $"rentalAsset {snapshot.DirtyRentalAssetCount:N0}",
                $"inventoryTransfer {snapshot.DirtyInventoryTransferCount:N0}",
                $"missingCustomerRef {snapshot.MissingCustomerReferenceCount:N0}",
                $"missingInvoiceRef {snapshot.MissingInvoiceReferenceCount:N0}",
                $"missingTransactionRef {snapshot.MissingTransactionReferenceCount:N0}",
                $"missingRentalItemRef {snapshot.MissingRentalItemReferenceCount:N0}"
            });

    private static void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }
}
