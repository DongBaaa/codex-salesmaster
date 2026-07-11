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

public sealed class RentalWorkbookSafetyTests
{
    private static string? _preparedAppRoot;

    [Theory]
    [InlineData("", "관리업체 값이 비어 있어 담당지점을 변환할 수 없습니다.")]
    [InlineData("잘못된업체", "관리업체 '잘못된업체'는 담당지점 변환 규칙에 없습니다.")]
    public async Task RebuildAssetsFromWorkbook_BlankOrInvalidManagementOffice_IsBlocked(string officeValue, string expectedMessage)
    {
        PrepareAppRoot("georaeplan-rental-workbook-invalid-office");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: officeValue,
                    managementId: "1001",
                    managementNumber: "2607-101",
                    currentLocation: "창고",
                    customerName: string.Empty,
                    itemName: "IMC2010",
                    machineNumber: "SAFE-001")
            ]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.True(result.IsBlocked);
            Assert.Contains(expectedMessage, result.BlockReason, StringComparison.Ordinal);
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_BusinessNumberNormalization_LinksCustomerBeforeName()
    {
        PrepareAppRoot("georaeplan-rental-workbook-import-business-number");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("실제 거래처", OfficeCodeCatalog.Usenet, "1234567890");
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "1002",
                    managementNumber: "2607-102",
                    currentLocation: "렌탈",
                    customerName: "워크북 별칭",
                    itemName: "IMC2010",
                    machineNumber: "SAFE-002",
                    installLocation: "본관")
            ],
            [("워크북 별칭", "123-45 67890")]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(1, result.CreatedCount);

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(customer.Id, asset.CustomerId);
            Assert.Equal(customer.NameOriginal, asset.CustomerName);
            Assert.Equal(customer.NameOriginal, asset.CurrentCustomerName);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_BusinessNumberNormalization_LinksCustomerBeforeName()
    {
        PrepareAppRoot("georaeplan-rental-workbook-rebuild-business-number");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("실제 거래처", OfficeCodeCatalog.Usenet, "123 45 67890");
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "1003",
                    managementNumber: "2607-103",
                    currentLocation: "렌탈",
                    customerName: "장부 별칭",
                    itemName: "IMC2010",
                    machineNumber: "SAFE-003",
                    installLocation: "별관")
            ],
            [("장부 별칭", "123-45-67890")]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.False(result.IsBlocked, result.BlockReason);

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(customer.Id, asset.CustomerId);
            Assert.Equal(customer.NameOriginal, asset.CustomerName);
            Assert.Equal(customer.NameOriginal, asset.CurrentCustomerName);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_AssignmentHistoryBusinessNumber_LinksPastCustomerWithoutChangingCurrentCustomer()
    {
        PrepareAppRoot("georaeplan-rental-workbook-import-history-business-number");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var currentCustomer = CreateCustomer("현재 거래처", OfficeCodeCatalog.Usenet, "3333333333");
            var expectedPastCustomer = CreateCustomer("㈜테스트", OfficeCodeCatalog.Usenet, "1111111111");
            var wrongPastCustomer = CreateCustomer("주식회사 테스트", OfficeCodeCatalog.Usenet, "2222222222");
            db.Customers.AddRange(currentCustomer, expectedPastCustomer, wrongPastCustomer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "1007",
                    managementNumber: "2607-107",
                    currentLocation: "렌탈",
                    customerName: currentCustomer.NameOriginal,
                    currentCustomerId: currentCustomer.Id.ToString("D"),
                    itemName: "IMC2010",
                    machineNumber: "SAFE-007",
                    installLocation: "민원실",
                    rental1: wrongPastCustomer.NameOriginal,
                    recall1: "2025-12-31")
            ],
            [(wrongPastCustomer.NameOriginal, "111-11-11111")]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(1, result.CreatedCount);

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(currentCustomer.Id, asset.CustomerId);
            Assert.Equal(currentCustomer.NameOriginal, asset.CustomerName);
            Assert.Equal(currentCustomer.NameOriginal, asset.CurrentCustomerName);

            var history = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .SingleAsync(current => !current.IsCurrent && !current.IsDeleted);
            Assert.Equal(expectedPastCustomer.Id, history.CustomerId);
            Assert.Equal(expectedPastCustomer.NameOriginal, history.CustomerName);
            Assert.NotEqual(wrongPastCustomer.Id, history.CustomerId);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_AssignmentHistoryBusinessNumber_LinksPastCustomerWithoutChangingCurrentCustomer()
    {
        PrepareAppRoot("georaeplan-rental-workbook-rebuild-history-business-number");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var currentCustomer = CreateCustomer("현재 거래처", OfficeCodeCatalog.Usenet, "3333333333");
            var expectedPastCustomer = CreateCustomer("㈜테스트", OfficeCodeCatalog.Usenet, "1111111111");
            var wrongPastCustomer = CreateCustomer("주식회사 테스트", OfficeCodeCatalog.Usenet, "2222222222");
            db.Customers.AddRange(currentCustomer, expectedPastCustomer, wrongPastCustomer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "1008",
                    managementNumber: "2607-108",
                    currentLocation: "렌탈",
                    customerName: currentCustomer.NameOriginal,
                    currentCustomerId: currentCustomer.Id.ToString("D"),
                    itemName: "IMC2010",
                    machineNumber: "SAFE-008",
                    installLocation: "민원실",
                    rental1: wrongPastCustomer.NameOriginal,
                    recall1: "2025-12-31")
            ],
            [(wrongPastCustomer.NameOriginal, "111-11-11111")]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.False(result.IsBlocked, result.BlockReason);

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(currentCustomer.Id, asset.CustomerId);
            Assert.Equal(currentCustomer.NameOriginal, asset.CustomerName);
            Assert.Equal(currentCustomer.NameOriginal, asset.CurrentCustomerName);

            var history = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .SingleAsync(current => !current.IsCurrent && !current.IsDeleted);
            Assert.Equal(expectedPastCustomer.Id, history.CustomerId);
            Assert.Equal(expectedPastCustomer.NameOriginal, history.CustomerName);
            Assert.NotEqual(wrongPastCustomer.Id, history.CustomerId);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_SameNamedCustomers_PrefersOfficeScopedMatch()
    {
        PrepareAppRoot("georaeplan-rental-workbook-same-name-office-scope");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomer = CreateCustomer("동일명 거래처", OfficeCodeCatalog.Usenet);
            var yeonsuCustomer = CreateCustomer("동일명 거래처", OfficeCodeCatalog.Yeonsu);
            db.Customers.AddRange(usenetCustomer, yeonsuCustomer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: OfficeCodeCatalog.Yeonsu,
                    managementId: "1004",
                    managementNumber: "2607-104",
                    currentLocation: "렌탈",
                    customerName: yeonsuCustomer.NameOriginal,
                    itemName: "IMC2010",
                    machineNumber: "SAFE-004",
                    installLocation: "연수점")
            ]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Yeonsu));

            Assert.False(result.IsBlocked, result.BlockReason);

            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(yeonsuCustomer.Id, asset.CustomerId);
            Assert.Equal(yeonsuCustomer.NameOriginal, asset.CustomerName);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, asset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, asset.ManagementCompanyCode);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_UniqueBusinessNumberOutsideOfficeScope_DoesNotCrossLink()
    {
        PrepareAppRoot("georaeplan-rental-workbook-business-number-scope");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomer = CreateCustomer("유즈넷 전용 거래처", OfficeCodeCatalog.Usenet, "123-45-67890");
            db.Customers.Add(usenetCustomer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: OfficeCodeCatalog.Yeonsu,
                    managementId: "1005",
                    managementNumber: "2607-105",
                    currentLocation: "렌탈",
                    customerName: "연수 별칭 거래처",
                    itemName: "IMC2010",
                    machineNumber: "SAFE-005",
                    installLocation: "연수점")
            ],
            [("연수 별칭 거래처", "1234567890")]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Yeonsu));

            Assert.True(result.IsBlocked);
            Assert.Contains("거래처", result.BlockReason, StringComparison.Ordinal);
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_DuplicateWorkbookNameBusinessNumbers_FallsBackToOfficeScopedName()
    {
        PrepareAppRoot("georaeplan-rental-workbook-ambiguous-business-number");

        try
        {
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomer = CreateCustomer("중복 원장명", OfficeCodeCatalog.Usenet, "222-22-22222");
            var yeonsuCustomer = CreateCustomer("중복 원장명", OfficeCodeCatalog.Yeonsu, "333-33-33333");
            db.Customers.AddRange(usenetCustomer, yeonsuCustomer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook(
            [
                AssetRow(
                    officeCode: OfficeCodeCatalog.Yeonsu,
                    managementId: "1006",
                    managementNumber: "2607-106",
                    currentLocation: "렌탈",
                    customerName: "중복 원장명",
                    itemName: "IMC2010",
                    machineNumber: "SAFE-006",
                    installLocation: "연수점")
            ],
            [
                ("중복 원장명", "111-11-11111"),
                ("중복 원장명", "222-22-22222")
            ]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Yeonsu));

            Assert.False(result.IsBlocked, result.BlockReason);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(yeonsuCustomer.Id, asset.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, asset.ResponsibleOfficeCode);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    private static LocalCustomer CreateCustomer(string name, string officeCode, string businessNumber = "")
        => new()
        {
            Id = Guid.NewGuid(),
            NameOriginal = name,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(name),
            BusinessNumber = businessNumber,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            TradeType = CustomerTradeTypes.Sales,
            IsDeleted = false,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static SessionState CreateRentalImportSession(string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-rental-safety",
            Role = DomainConstants.RoleAdmin,
            Permissions = [AppPermissionNames.RentalImport, AppPermissionNames.RentalEditAll]
        });
        return session;
    }

    private static Dictionary<string, string> AssetRow(
        string officeCode,
        string managementId,
        string managementNumber,
        string currentLocation,
        string customerName,
        string itemName,
        string machineNumber,
        string installLocation = "",
        string currentCustomerId = "",
        string rental1 = "",
        string historyCustomerId1 = "",
        string recall1 = "",
        string validationStatus = "ready")
        => new(StringComparer.Ordinal)
        {
            ["관리업체"] = officeCode,
            ["관리ID"] = managementId,
            ["관리번호"] = managementNumber,
            ["현재위치"] = currentLocation,
            ["품목분류"] = "A4컬러복합기",
            ["제조사"] = "테스트제조사",
            ["고객명"] = customerName,
            ["현재거래처ID"] = currentCustomerId,
            ["품명"] = itemName,
            ["기계번호"] = machineNumber,
            ["설치위치"] = installLocation,
            ["렌탈1"] = rental1,
            ["회수1"] = recall1,
            ["이력1거래처ID"] = historyCustomerId1,
            ["렌탈요금"] = "10000",
            ["반영검증상태"] = validationStatus
        };

    private static string CreateWorkbook(
        IReadOnlyList<Dictionary<string, string>> assetRows,
        IReadOnlyList<(string Name, string BusinessNumber)>? customerRows = null)
    {
        var path = Path.Combine(
            _preparedAppRoot ?? Path.Combine(FindRepositoryRoot(), "temp"),
            $"rental-workbook-safety-{Guid.NewGuid():N}.xlsx");
        var headers = assetRows.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).ToList();
        customerRows ??= [];

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

        var assetSheetRows = new List<IReadOnlyList<string>> { headers };
        assetSheetRows.AddRange(assetRows.Select(row =>
            (IReadOnlyList<string>)headers.Select(header => row.GetValueOrDefault(header, string.Empty)).ToList()));
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(assetSheetRows));

        var customerSheetRows = new List<IReadOnlyList<string>>
        {
            new[] { "상호명", "사업자번호" }
        };
        customerSheetRows.AddRange(customerRows.Select(row => (IReadOnlyList<string>)[row.Name, row.BusinessNumber]));
        WriteEntry(archive, "xl/worksheets/sheet2.xml", BuildSheetXml(customerSheetRows));

        return path;
    }

    private static string BuildSheetXml(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sheetRows = string.Concat(rows.Select((row, rowIndex) =>
            $"<row r=\"{rowIndex + 1}\">" +
            string.Concat(row.Select((value, columnIndex) => BuildCell(rowIndex + 1, columnIndex + 1, value))) +
            "</row>"));
        return $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>{{sheetRows}}</sheetData>
            </worksheet>
            """;
    }

    private static string BuildCell(int row, int column, string value)
        => $"<c r=\"{GetColumnName(column)}{row}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value) ?? string.Empty}</t></is></c>";

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
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }

    private static void PrepareAppRoot(string name)
    {
        _preparedAppRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            name,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_preparedAppRoot);
    }

    private static LocalDbContext CreateDbContext()
    {
        var root = _preparedAppRoot ?? throw new InvalidOperationException("테스트 작업 폴더가 준비되지 않았습니다.");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "거래플랜-tests.db")}")
            .Options;
        return new LocalDbContext(options);
    }

    private static void CleanupAppRoot()
    {
        SqliteConnection.ClearAllPools();
        if (!string.IsNullOrWhiteSpace(_preparedAppRoot) && Directory.Exists(_preparedAppRoot))
            Directory.Delete(_preparedAppRoot, recursive: true);
        _preparedAppRoot = null;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }
}
