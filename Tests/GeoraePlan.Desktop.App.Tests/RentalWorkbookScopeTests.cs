using System.IO.Compression;
using System.Security;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalWorkbookScopeTests
{
    [Fact]
    public async Task RebuildAssetsFromWorkbook_BlocksCrossTenantExistingAssetMutation()
    {
        PrepareAppRoot("georaeplan-rental-workbook-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var asset = new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                AssetKey = $"WB-{Guid.NewGuid():N}",
                ManagementId = "WB-ID-001",
                ManagementNumber = "WB-001",
                CustomerName = "ITWORLD 기존 고객",
                CurrentCustomerName = "ITWORLD 기존 고객",
                ItemName = "렌탈 장비",
                MachineNumber = "WB-MACHINE-001",
                AssetStatus = "임대진행중",
                IsDeleted = false,
                IsDirty = false
            };
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var workbookPath = Path.Combine(Path.GetTempPath(), $"itworld-rental-assets-{Guid.NewGuid():N}.xlsx");
            WriteRentalAssetWorkbook(
                workbookPath,
                officeCode: "ITWORLD",
                managementNumber: asset.ManagementNumber,
                managementId: asset.ManagementId,
                machineNumber: asset.MachineNumber,
                customerName: "ITWORLD 변경 고객",
                itemName: asset.ItemName);

            var session = CreateUsenetRentalImportSession();

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(workbookPath, session);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == asset.Id);

            Assert.True(result.IsBlocked, result.BlockReason);
            Assert.Equal("ITWORLD 기존 고객", storedAsset.CustomerName);
            Assert.False(storedAsset.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ImportBillingWorkbook_BlocksCrossTenantProfileCreation()
    {
        PrepareAppRoot("georaeplan-rental-billing-workbook-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = Path.Combine(Path.GetTempPath(), $"itworld-rental-billing-{Guid.NewGuid():N}.xlsx");
            WriteRentalBillingWorkbook(workbookPath);
            var session = CreateUsenetRentalImportSession();

            var result = await new RentalStateService(db).ImportBillingWorkbookAsync(workbookPath, session);
            var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();

            Assert.Equal(0, result.CreatedCount);
            Assert.Equal(0, result.UpdatedCount);
            Assert.Equal(1, result.ErrorCount);
            Assert.Empty(profiles);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ImportBillingWorkbook_AllowsCurrentTenantProfileCreationWithImportPermission()
    {
        PrepareAppRoot("georaeplan-rental-billing-workbook-current-tenant");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = Path.Combine(Path.GetTempPath(), $"usenet-rental-billing-{Guid.NewGuid():N}.xlsx");
            WriteRentalBillingWorkbook(workbookPath, "USENET");
            var session = CreateUsenetRentalImportSession(includeEditAll: false);

            var result = await new RentalStateService(db).ImportBillingWorkbookAsync(workbookPath, session);
            var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();

            Assert.Equal(1, result.CreatedCount);
            Assert.Equal(0, result.ErrorCount);
            var profile = Assert.Single(profiles);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, profile.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, profile.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    private static void PrepareAppRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
    }

    private static void WriteRentalAssetWorkbook(
        string path,
        string officeCode,
        string managementNumber,
        string managementId,
        string machineNumber,
        string customerName,
        string itemName)
    {
        if (File.Exists(path))
            File.Delete(path);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="렌탈재고관리" sheetId="1" r:id="rId1"/>
                <sheet name="거래처" sheetId="2" r:id="rId2"/>
              </sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
            </Relationships>
            """);

        var headers = new[]
        {
            "관리업체",
            "관리ID",
            "관리번호",
            "현재위치",
            "고객명",
            "품명",
            "기계번호",
            "설치위치",
            "렌탈요금"
        };
        var values = new[]
        {
            officeCode,
            managementId,
            managementNumber,
            "렌탈",
            customerName,
            itemName,
            machineNumber,
            "변경 설치처",
            "10000"
        };
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(headers, values));
        WriteEntry(archive, "xl/worksheets/sheet2.xml", BuildSheetXml(new[] { "상호명", "사업자번호" }, new[] { customerName, "123-45-67890" }));
    }

    private static void WriteRentalBillingWorkbook(string path, string officeCode = "ITWORLD")
    {
        if (File.Exists(path))
            File.Delete(path);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="렌탈판매 청구 리스트" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        WriteEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            BuildSheetXml(
                new[]
                {
                    "담당지점",
                    "청구일",
                    "거래처명",
                    "사업자번호",
                    "품명",
                    "청구기간[개월수]",
                    "청구방식",
                    "월청구대금",
                    "청구상태"
                },
                new[]
                {
                    officeCode,
                    "25",
                    "ITWORLD 청구 고객",
                    "123-45-67890",
                    "렌탈 장비",
                    "1",
                    "현금",
                    "10000",
                    "청구중"
                }));
    }

    private static SessionState CreateUsenetRentalImportSession(bool includeEditAll = true)
    {
        var permissions = new List<string> { AppPermissionNames.RentalImport };
        if (includeEditAll)
            permissions.Add(AppPermissionNames.RentalEditAll);

        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            UserId = Guid.NewGuid(),
            Username = "usenet-rental-importer",
            Role = DomainConstants.RoleUser,
            Permissions = permissions
        });
        return session;
    }

    private static string BuildSheetXml(IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        var headerCells = string.Concat(headers.Select((value, index) => BuildCell(1, index + 1, value)));
        var valueCells = string.Concat(values.Select((value, index) => BuildCell(2, index + 1, value)));
        return $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1">{{headerCells}}</row>
                <row r="2">{{valueCells}}</row>
              </sheetData>
            </worksheet>
            """;
    }

    private static string BuildCell(int row, int column, string value)
        => $"""<c r="{GetColumnName(column)}{row}" t="inlineStr"><is><t>{SecurityElement.Escape(value) ?? string.Empty}</t></is></c>""";

    private static string GetColumnName(int column)
    {
        var dividend = column;
        var name = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            name = Convert.ToChar('A' + modulo) + name;
            dividend = (dividend - modulo) / 26;
        }

        return name;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.Trim());
    }
}
