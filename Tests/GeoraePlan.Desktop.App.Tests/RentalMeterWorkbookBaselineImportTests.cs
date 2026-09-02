using System.Data;
using ClosedXML.Excel;
using ExcelDataReader;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalMeterWorkbookBaselineImportTests
{
    [Fact]
    public void Read_OperatorWorkbook_WhenExplicitlyProvided()
    {
        var workbookPath = Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_METER_WORKBOOK");
        if (string.IsNullOrWhiteSpace(workbookPath))
            return;

        Assert.True(File.Exists(workbookPath), $"검침 원본 파일을 찾을 수 없습니다: {workbookPath}");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = File.Open(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(stream);
        var dataSet = reader.AsDataSet();

        var result = RentalMeterWorkbookBaselineReader.Read(dataSet);

        var candidate = Assert.Single(
            result.Candidates,
            current => string.Equals(current.ManagementNumber, "1512-004", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new DateOnly(2026, 6, 30), candidate.ReadingDate);
        Assert.Equal(179_883, candidate.BlackMeter);
        Assert.Equal(115_935, candidate.ColorMeter);
    }

    [Fact]
    public void Read_UsesLatestClosingRowAndConvertsA3ToA4Equivalent()
    {
        var dataSet = new DataSet();
        var summary = CreateTable("작성일자", 8, 12);
        summary.Rows[3][2] = "작성일자";
        summary.Rows[3][3] = new DateTime(2026, 6, 30);
        dataSet.Tables.Add(summary);

        var device = CreateTable("1512-004", 20, 12);
        device.Rows[2][0] = "관리번호 :";
        device.Rows[2][1] = "1512-004";
        device.Rows[17][0] = "카운트";
        device.Rows[17][1] = 100d;
        device.Rows[17][2] = 2d;
        device.Rows[17][3] = 50d;
        device.Rows[17][4] = 1d;
        device.Rows[17][5] = 3d;
        device.Rows[17][6] = 4d;
        device.Rows[17][7] = 5d;
        device.Rows[17][8] = 6d;
        dataSet.Tables.Add(device);

        var result = RentalMeterWorkbookBaselineReader.Read(dataSet);

        var candidate = Assert.Single(result.Candidates);
        Assert.Empty(result.Messages);
        Assert.Equal("1512-004", candidate.ManagementNumber);
        Assert.Equal(new DateOnly(2026, 6, 30), candidate.ReadingDate);
        Assert.Equal(126, candidate.BlackMeter);
        Assert.Equal(86, candidate.ColorMeter);
    }

    [Fact]
    public async Task ImportMeterBaselineWorkbookAsync_ReplacesOnlyOlderOpeningBaselineAndPreservesBillingDisabled()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-meter-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
        var workbookPath = Path.Combine(tempRoot, "baseline.xlsx");
        var evidencePath = Path.Combine(tempRoot, "baseline.pdf");
        CreateWorkbook(workbookPath, "1512-004", new DateTime(2026, 6, 30), 179_883, 115_935);
        await File.WriteAllBytesAsync(evidencePath, "%PDF-1.4\n% test evidence"u8.ToArray());

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "1512-004",
                ItemName = "복합기",
                MeterBillingEnabled = false,
                MeterReadingsJson = RentalMeterBillingRules.SerializeReadings([
                    new RentalMeterReadingRecord
                    {
                        BillingYearMonth = "2026-05",
                        ReadingDate = new DateOnly(2026, 5, 31),
                        BlackMeter = 170_000,
                        ColorMeter = 110_000,
                        IsFinalized = true,
                        IsOpeningBaseline = true
                    },
                    new RentalMeterReadingRecord
                    {
                        BillingYearMonth = "2026-07",
                        ReadingDate = new DateOnly(2026, 7, 31),
                        BlackMeter = 181_000,
                        ColorMeter = 116_000,
                        IsFinalized = true,
                        IsOpeningBaseline = false
                    }
                ])
            });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var result = await service.ImportMeterBaselineWorkbookAsync(
                workbookPath,
                evidencePath,
                CreateAdminSession());

            Assert.Equal(1, result.UpdatedCount);
            Assert.Equal(0, result.ErrorCount);
            var saved = await db.RentalAssets.SingleAsync(asset => asset.Id == assetId);
            Assert.False(saved.MeterBillingEnabled);
            Assert.True(saved.IsDirty);
            var readings = RentalMeterBillingRules.ParseReadings(saved.MeterReadingsJson);
            var opening = Assert.Single(readings, reading => reading.IsOpeningBaseline);
            Assert.Equal("2026-06", opening.BillingYearMonth);
            Assert.Equal(179_883, opening.BlackMeter);
            Assert.Equal(115_935, opening.ColorMeter);
            Assert.Contains(workbookPath, opening.EvidenceReference, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(evidencePath, opening.EvidenceReference, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(readings, reading => reading.BillingYearMonth == "2026-07" && !reading.IsOpeningBaseline);

            var second = await service.ImportMeterBaselineWorkbookAsync(
                workbookPath,
                evidencePath,
                CreateAdminSession());
            Assert.Equal(0, second.UpdatedCount);
            Assert.Equal(1, second.SkippedCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ImportMeterBaselineWorkbookAsync_DoesNotOverwriteNewerOpeningBaseline()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-meter-baseline-newer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
        var workbookPath = Path.Combine(tempRoot, "baseline.xlsx");
        CreateWorkbook(workbookPath, "1512-004", new DateTime(2026, 6, 30), 179_883, 115_935);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "1512-004",
                MeterReadingsJson = RentalMeterBillingRules.SerializeReadings([
                    new RentalMeterReadingRecord
                    {
                        BillingYearMonth = "2026-07",
                        ReadingDate = new DateOnly(2026, 7, 31),
                        BlackMeter = 181_000,
                        ColorMeter = 116_000,
                        IsFinalized = true,
                        IsOpeningBaseline = true
                    }
                ])
            });
            await db.SaveChangesAsync();

            var result = await new RentalStateService(db).ImportMeterBaselineWorkbookAsync(
                workbookPath,
                string.Empty,
                CreateAdminSession());

            Assert.Equal(0, result.UpdatedCount);
            Assert.Equal(1, result.SkippedCount);
            var saved = await db.RentalAssets.SingleAsync(asset => asset.Id == assetId);
            var opening = Assert.Single(RentalMeterBillingRules.ParseReadings(saved.MeterReadingsJson));
            Assert.Equal("2026-07", opening.BillingYearMonth);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static DataTable CreateTable(string name, int rows, int columns)
    {
        var table = new DataTable(name);
        for (var column = 0; column < columns; column++)
            table.Columns.Add($"C{column}", typeof(object));
        for (var row = 0; row < rows; row++)
            table.Rows.Add(table.NewRow());
        return table;
    }

    private static void CreateWorkbook(
        string path,
        string managementNumber,
        DateTime closingDate,
        long blackMeter,
        long colorMeter)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.Worksheets.Add("작성일자");
        summary.Cell(4, 3).Value = "작성일자";
        summary.Cell(4, 4).Value = closingDate;
        var device = workbook.Worksheets.Add(managementNumber);
        device.Cell(2, 7).Value = "작성일";
        device.Cell(2, 8).Value = closingDate;
        device.Cell(3, 1).Value = "관리번호 :";
        device.Cell(3, 2).Value = managementNumber;
        device.Cell(18, 1).Value = "카운트";
        device.Cell(18, 2).Value = blackMeter;
        device.Cell(18, 4).Value = colorMeter;
        workbook.SaveAs(path);
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
