using ClosedXML.Excel;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ExcelExportRoundTripTests
{
    [Fact]
    public async Task PeriodLedgerExport_OpenerFailure_PreservesSavedWorkbookContent()
    {
        var directory = CreateTempDirectory(nameof(PeriodLedgerExport_OpenerFailure_PreservesSavedWorkbookContent));

        try
        {
            var openProbe = new ThrowingWorkbookOpenProbe();
            var exporter = new PeriodLedgerExcelExportService(openProbe.Open);
            var data = new PeriodLedgerBuildResult
            {
                Query = new PeriodLedgerQuery
                {
                    From = new DateOnly(2026, 7, 1),
                    To = new DateOnly(2026, 7, 31),
                    LedgerType = PeriodLedgerType.ReceiptPayment,
                    Scope = PeriodLedgerScope.AllCustomers,
                    SortByCustomerName = true
                },
                Title = "기간별 수금/지급 테스트 원장",
                ScopeLabel = "전체 거래처",
                Blocks = [],
                PaymentRows =
                [
                    new PeriodLedgerPaymentRow
                    {
                        No = 1,
                        Date = new DateOnly(2026, 7, 15),
                        Division = "수금",
                        Summary = "테스트 품목 외 1건",
                        TradeAmount = 123_400m,
                        ReceiptAmount = 100_000m,
                        PaymentAmount = 0m,
                        RunningBalance = 23_400m,
                        ReceivableBalance = 23_400m,
                        CustomerName = "테스트 거래처",
                        Note = "원장 메모"
                    }
                ],
                YeonsuDeliveryRows = [],
                MonthlySalesChartPoints = [],
                Totals = new PeriodLedgerTotals
                {
                    TradeAmount = 123_400m,
                    ReceiptAmount = 100_000m,
                    RunningBalance = 23_400m,
                    ReceivableBalance = 23_400m
                }
            };

            var exportedPath = await exporter.ExportAsync(data, directory);

            openProbe.AssertOpenedSavedWorkbookExactlyOnce(exportedPath);
            Assert.True(File.Exists(exportedPath));
            Assert.Equal(".xlsx", Path.GetExtension(exportedPath), ignoreCase: true);

            using var workbook = new XLWorkbook(exportedPath);
            var sheet = workbook.Worksheet("원장");

            Assert.Single(workbook.Worksheets);
            Assert.Equal(data.Title, sheet.Cell(1, 1).GetString());
            Assert.Equal("No", sheet.Cell(5, 1).GetString());
            Assert.Equal("거래날짜", sheet.Cell(5, 2).GetString());
            Assert.Equal("전표메모", sheet.Cell(5, 11).GetString());
            Assert.Equal(1, sheet.Cell(6, 1).GetValue<int>());
            Assert.Equal("2026-07-15", sheet.Cell(6, 2).GetString());
            Assert.Equal("테스트 품목 외 1건", sheet.Cell(6, 4).GetString());
            Assert.Equal(123_400m, sheet.Cell(6, 5).GetValue<decimal>());
            Assert.Equal("테스트 거래처", sheet.Cell(6, 10).GetString());
            Assert.Equal("원장 메모", sheet.Cell(6, 11).GetString());
            Assert.Equal("기간내 총 합계", sheet.Cell(7, 1).GetString());
            Assert.Equal(100_000m, sheet.Cell(7, 6).GetValue<decimal>());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task SyncDiagnosticExport_OpenerFailure_PreservesSavedWorkbookContent()
    {
        var directory = CreateTempDirectory(nameof(SyncDiagnosticExport_OpenerFailure_PreservesSavedWorkbookContent));
        var filePath = Path.Combine(directory, "sync-diagnostics.xlsx");

        try
        {
            var openProbe = new ThrowingWorkbookOpenProbe();
            var exporter = new SyncDiagnosticExcelExportService(openProbe.Open);
            var occurredAtUtc = new DateTime(2026, 7, 28, 1, 2, 3, DateTimeKind.Utc);
            var events = new[]
            {
                new SyncDiagnosticListItem
                {
                    Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    OccurredAtUtc = occurredAtUtc,
                    LastOccurredAtUtc = occurredAtUtc.AddMinutes(5),
                    OccurrenceCount = 3,
                    Status = "Open",
                    Severity = "Error",
                    Category = "Push",
                    Subcategory = "Conflict",
                    EntityName = "Invoice",
                    EntityId = "INV-100",
                    ReferenceEntityName = "Customer",
                    ReferenceEntityId = "CUST-200",
                    UserName = "tester",
                    OfficeCode = "USENET",
                    TenantCode = "USENET-GROUP",
                    MachineName = "DESKTOP-TEST",
                    AppVersion = "1.2.3",
                    SyncPhase = "Upload",
                    IsRecoverable = true,
                    RecoveryAttempted = true,
                    RecoverySucceeded = false,
                    RecoveryAction = "다시 동기화",
                    RawMessage = "raw conflict",
                    NormalizedMessage = "normalized conflict",
                    Snapshot = new SyncDiagnosticSnapshot(
                        42,
                        "snapshot error",
                        1,
                        2,
                        3,
                        4,
                        5,
                        6,
                        7,
                        8,
                        9,
                        10,
                        11,
                        12)
                }
            };
            var summary = new SyncDiagnosticSummary(
                2,
                1,
                7,
                occurredAtUtc.AddHours(-1),
                occurredAtUtc,
                "마지막 오류",
                42);
            var filter = new SyncDiagnosticFilter("INV-100", "Push", "Open", "Error", true);

            var exportedPath = await exporter.ExportAsync(events, summary, filter, filePath);

            Assert.Equal(filePath, exportedPath);
            openProbe.AssertOpenedSavedWorkbookExactlyOnce(exportedPath);
            Assert.True(File.Exists(exportedPath));

            using var workbook = new XLWorkbook(exportedPath);
            Assert.Equal(["요약", "진단이벤트"], workbook.Worksheets.Select(sheet => sheet.Name).ToArray());

            var summarySheet = workbook.Worksheet("요약");
            Assert.Equal("동기화 진단 엑셀", summarySheet.Cell(1, 1).GetString());
            Assert.Equal("내보낸 행 수", summarySheet.Cell(4, 1).GetString());
            Assert.Equal("1", summarySheet.Cell(4, 2).GetString());
            Assert.Equal("미해결 확인 항목", summarySheet.Cell(5, 1).GetString());
            Assert.Equal("2", summarySheet.Cell(5, 2).GetString());
            Assert.Equal("INV-100", summarySheet.Cell(12, 2).GetString());

            var detailSheet = workbook.Worksheet("진단이벤트");
            Assert.Equal("발생시각", detailSheet.Cell(1, 1).GetString());
            Assert.Equal("로컬 dirty 스냅샷", detailSheet.Cell(1, 20).GetString());
            Assert.Equal(3, detailSheet.Cell(2, 3).GetValue<int>());
            Assert.Equal("Open", detailSheet.Cell(2, 4).GetString());
            Assert.Equal("INV-100", detailSheet.Cell(2, 9).GetString());
            Assert.Equal("Customer CUST-200", detailSheet.Cell(2, 10).GetString());
            Assert.Equal("tester / USENET / USENET-GROUP", detailSheet.Cell(2, 11).GetString());
            Assert.Equal("예", detailSheet.Cell(2, 14).GetString());
            Assert.Equal("복구시도", detailSheet.Cell(2, 15).GetString());
            Assert.Equal("Invoice INV-100", detailSheet.Cell(2, 17).GetString());
            Assert.Contains("missingRentalItemRef 12", detailSheet.Cell(2, 20).GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task IntegrityIssueExport_OpenerFailure_PreservesSavedWorkbookContent()
    {
        var directory = CreateTempDirectory(nameof(IntegrityIssueExport_OpenerFailure_PreservesSavedWorkbookContent));
        var filePath = Path.Combine(directory, "integrity-issue.xlsx");

        try
        {
            var openProbe = new ThrowingWorkbookOpenProbe();
            var exporter = new IntegrityIssueExcelExportService(openProbe.Open);
            var detail = new IntegrityIssueDetailResultDto
            {
                GeneratedAtUtc = new DateTime(2026, 7, 28, 1, 2, 3, DateTimeKind.Utc),
                Code = "ORPHAN_PAYMENT",
                Severity = "Warning",
                Message = "연결되지 않은 수금 데이터",
                OfficeCode = "USENET",
                TenantCode = "USENET-GROUP",
                DetailCount = 1,
                Rows =
                [
                    new IntegrityIssueDetailRowDto
                    {
                        EntityType = "Payment",
                        EntityIdText = "PAY-100",
                        PrimaryText = "테스트 거래처",
                        SecondaryText = "100,000원",
                        ReferenceText = "Invoice INV-200",
                        ScopeText = "USENET / USENET-GROUP",
                        DetailText = "전표 참조 누락"
                    }
                ]
            };

            var exportedPath = await exporter.ExportAsync(detail, filePath);

            Assert.Equal(filePath, exportedPath);
            openProbe.AssertOpenedSavedWorkbookExactlyOnce(exportedPath);
            Assert.True(File.Exists(exportedPath));

            using var workbook = new XLWorkbook(exportedPath);
            Assert.Equal(["요약", "상세목록"], workbook.Worksheets.Select(sheet => sheet.Name).ToArray());

            var summarySheet = workbook.Worksheet("요약");
            Assert.Equal("서버 무결성 상세 목록", summarySheet.Cell(1, 1).GetString());
            Assert.Equal("이슈 코드", summarySheet.Cell(4, 1).GetString());
            Assert.Equal("ORPHAN_PAYMENT", summarySheet.Cell(4, 2).GetString());
            Assert.Equal("대상 범위", summarySheet.Cell(7, 1).GetString());
            Assert.Equal("USENET / USENET-GROUP", summarySheet.Cell(7, 2).GetString());
            Assert.Equal("1", summarySheet.Cell(8, 2).GetString());

            var detailSheet = workbook.Worksheet("상세목록");
            Assert.Equal("엔터티", detailSheet.Cell(1, 1).GetString());
            Assert.Equal("상세", detailSheet.Cell(1, 7).GetString());
            Assert.Equal("Payment", detailSheet.Cell(2, 1).GetString());
            Assert.Equal("PAY-100", detailSheet.Cell(2, 2).GetString());
            Assert.Equal("테스트 거래처", detailSheet.Cell(2, 3).GetString());
            Assert.Equal("Invoice INV-200", detailSheet.Cell(2, 5).GetString());
            Assert.Equal("전표 참조 누락", detailSheet.Cell(2, 7).GetString());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory(string testName)
    {
        var directory = Path.Combine(TestProcessIsolation.TempRoot, testName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class ThrowingWorkbookOpenProbe
    {
        private int callCount;
        private string? openedPath;
        private bool fileExisted;
        private bool workbookWasReadable;

        public void Open(string path)
        {
            callCount++;
            openedPath = path;
            fileExisted = File.Exists(path);

            if (fileExisted)
            {
                try
                {
                    using var workbook = new XLWorkbook(path);
                    workbookWasReadable = workbook.Worksheets.Count > 0;
                }
                catch
                {
                    workbookWasReadable = false;
                }
            }

            throw new InvalidOperationException("테스트용 연결 프로그램 실행 실패");
        }

        public void AssertOpenedSavedWorkbookExactlyOnce(string expectedPath)
        {
            Assert.Equal(1, callCount);
            Assert.Equal(expectedPath, openedPath);
            Assert.True(fileExisted);
            Assert.True(workbookWasReadable);
        }
    }
}
