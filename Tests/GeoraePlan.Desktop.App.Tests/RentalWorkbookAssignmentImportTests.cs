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

public sealed class RentalWorkbookAssignmentImportTests
{
    [Theory]
    [InlineData("창고")]
    [InlineData("폐기")]
    [InlineData("판매")]
    public void RentalAssetStatus_NonOperatingStates_AreNotCurrentRental(string status)
    {
        Assert.True(RentalAssetStatusNormalizer.IsNonOperating(status));
    }

    [Fact]
    public async Task ImportAssetWorkbook_DisposedAsset_ClearsCurrentLinksAndPreservesNameOnlyHistory()
    {
        PrepareAppRoot("georaeplan-rental-workbook-disposed-history");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Itworld,
                    managementId: "190",
                    managementNumber: "1703-003",
                    currentLocation: "폐기",
                    customerName: "당근판매",
                    itemName: "SL-M3820ND",
                    machineNumber: "070LB8GJ3A001CY",
                    installLocation: "과거 설치처",
                    rental1: "[인천시의회]행정안전위원회",
                    recall1: "2025-09-30")
            ]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Itworld));

            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(1, result.CreatedCount);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("폐기", asset.AssetStatus);
            Assert.Null(asset.CustomerId);
            Assert.Null(asset.BillingProfileId);
            Assert.Equal(string.Empty, asset.CustomerName);
            Assert.Equal(string.Empty, asset.CurrentCustomerName);
            Assert.Equal(string.Empty, asset.InstallLocation);
            Assert.Contains("원본 고객명: 당근판매", asset.Notes);

            var history = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync();
            Assert.False(history.IsCurrent);
            Assert.False(history.IsDeleted);
            Assert.Null(history.CustomerId);
            Assert.Null(history.BillingProfileId);
            Assert.Equal("[인천시의회]행정안전위원회", history.CustomerName);
            Assert.Equal(new DateTime(2025, 9, 30, 0, 0, 0, DateTimeKind.Utc), history.UnlinkedAtUtc);
            Assert.Contains("회수이력 시작일 추정", history.ChangeReason);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_ExplicitCustomerId_IsScopeSafeAndIdempotent()
    {
        PrepareAppRoot("georaeplan-rental-workbook-explicit-customer");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("정확 거래처", OfficeCodeCatalog.Usenet);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "900",
                    managementNumber: "2607-001",
                    currentLocation: "렌탈",
                    customerName: "원장 별칭",
                    currentCustomerId: customer.Id.ToString("D"),
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-001",
                    installLocation: "민원실")
            ]);
            var session = CreateRentalImportSession(OfficeCodeCatalog.Usenet);
            var service = new RentalStateService(db);

            var first = await service.ImportAssetWorkbookAsync(workbookPath, session);
            var second = await service.ImportAssetWorkbookAsync(workbookPath, session);

            Assert.Equal(0, first.ErrorCount);
            Assert.Equal(1, first.CreatedCount);
            Assert.Equal(0, second.ErrorCount);
            Assert.Equal(1, second.UpdatedCount);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(customer.Id, asset.CustomerId);
            Assert.Equal("정확 거래처", asset.CustomerName);
            Assert.Equal(OfficeCodeCatalog.Usenet, asset.ResponsibleOfficeCode);
            var currentHistory = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .Where(current => current.IsCurrent && !current.IsDeleted)
                .ToListAsync();
            Assert.Single(currentHistory);
            Assert.Equal(customer.Id, currentHistory[0].CustomerId);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_CrossTenantExplicitCustomer_RollsBackAllRows()
    {
        PrepareAppRoot("georaeplan-rental-workbook-cross-tenant-rollback");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomer = CreateCustomer("유즈넷 정상 거래처", OfficeCodeCatalog.Usenet);
            var itworldCustomer = CreateCustomer("아이티월드 거래처", OfficeCodeCatalog.Itworld);
            db.Customers.AddRange(usenetCustomer, itworldCustomer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "901",
                    managementNumber: "2607-002",
                    currentLocation: "렌탈",
                    customerName: usenetCustomer.NameOriginal,
                    currentCustomerId: usenetCustomer.Id.ToString("D"),
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-002"),
                RentalRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "902",
                    managementNumber: "2607-003",
                    currentLocation: "렌탈",
                    customerName: itworldCustomer.NameOriginal,
                    currentCustomerId: itworldCustomer.Id.ToString("D"),
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-003")
            ]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.Equal(1, result.ErrorCount);
            Assert.Equal(0, result.CreatedCount);
            Assert.Equal(0, result.UpdatedCount);
            Assert.Contains(result.Messages, message => message.Contains("전체를 롤백", StringComparison.Ordinal));
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_PastHistoryMayUseDifferentOfficeWithinSameTenant()
    {
        PrepareAppRoot("georaeplan-rental-workbook-history-cross-office-same-tenant");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var currentCustomer = CreateCustomer("연수 현재 거래처", OfficeCodeCatalog.Yeonsu);
            var pastCustomer = CreateCustomer("유즈넷 과거 거래처", OfficeCodeCatalog.Usenet);
            db.Customers.AddRange(currentCustomer, pastCustomer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Yeonsu,
                    managementId: "908",
                    managementNumber: "2607-009",
                    currentLocation: "렌탈",
                    customerName: currentCustomer.NameOriginal,
                    currentCustomerId: currentCustomer.Id.ToString("D"),
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-009",
                    rental1: pastCustomer.NameOriginal,
                    historyCustomerId1: pastCustomer.Id.ToString("D"),
                    recall1: "2025-12-31")
            ]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Yeonsu));

            Assert.Equal(0, result.ErrorCount);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(OfficeCodeCatalog.Yeonsu, asset.ResponsibleOfficeCode);
            Assert.Equal(currentCustomer.Id, asset.CustomerId);
            var pastHistory = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .SingleAsync(current => !current.IsCurrent && !current.IsDeleted);
            Assert.Equal(pastCustomer.Id, pastHistory.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Usenet, pastHistory.ResponsibleOfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, pastHistory.TenantCode);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_BlockedPlanRow_StopsBeforeAnyWrite()
    {
        PrepareAppRoot("georaeplan-rental-workbook-blocked-plan");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "903",
                    managementNumber: "2607-004",
                    currentLocation: "렌탈",
                    customerName: string.Empty,
                    itemName: "P5021CDN",
                    machineNumber: "TEST-SERIAL-004",
                    validationStatus: "blocked")
            ]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.Equal(1, result.ErrorCount);
            Assert.Contains(result.Messages, message => message.Contains("전체 가져오기를 중단", StringComparison.Ordinal));
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_MixedBusinessDatabases_StopsBeforeAnyWrite()
    {
        PrepareAppRoot("georaeplan-rental-workbook-mixed-business-databases");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "904",
                    managementNumber: "2607-005",
                    currentLocation: "창고",
                    customerName: string.Empty,
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-005"),
                RentalRow(
                    officeCode: OfficeCodeCatalog.Itworld,
                    managementId: "905",
                    managementNumber: "2607-006",
                    currentLocation: "창고",
                    customerName: string.Empty,
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-006")
            ]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.Equal(1, result.ErrorCount);
            Assert.Contains(result.Messages, message => message.Contains("한 workbook에 섞여", StringComparison.Ordinal));
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_MixedBusinessDatabases_IsBlocked()
    {
        PrepareAppRoot("georaeplan-rental-rebuild-mixed-business-databases");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "906",
                    managementNumber: "2607-007",
                    currentLocation: "창고",
                    customerName: string.Empty,
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-007"),
                RentalRow(
                    officeCode: OfficeCodeCatalog.Itworld,
                    managementId: "907",
                    managementNumber: "2607-008",
                    currentLocation: "창고",
                    customerName: string.Empty,
                    itemName: "IMC2010",
                    machineNumber: "TEST-SERIAL-008")
            ]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Usenet));

            Assert.True(result.IsBlocked);
            Assert.Contains("한 workbook에 섞여", result.BlockReason, StringComparison.Ordinal);
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_ExistingDisposedAsset_ClosesCurrentAndReusesImportedHistory()
    {
        PrepareAppRoot("georaeplan-rental-rebuild-disposed-history");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("이전 정상 거래처", OfficeCodeCatalog.Itworld);
            var asset = new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                AssetKey = $"REBUILD-{Guid.NewGuid():N}",
                ManagementId = "950",
                ManagementNumber = "2607-050",
                CurrentLocation = "렌탈",
                CustomerId = customer.Id,
                CustomerName = customer.NameOriginal,
                CurrentCustomerName = customer.NameOriginal,
                InstallSiteName = customer.NameOriginal,
                InstallLocation = "기존 설치처",
                ItemCategoryName = "A4컬러복합기",
                Manufacturer = "테스트 제조사",
                ItemName = "IMC2010",
                MachineNumber = "REBUILD-SERIAL-050",
                AssetStatus = "임대진행중",
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow.AddYears(-1),
                UpdatedAtUtc = DateTime.UtcNow.AddDays(-1)
            };
            var currentHistory = new LocalRentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                CustomerId = customer.Id,
                TenantCode = asset.TenantCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                CustomerName = customer.NameOriginal,
                InstallLocation = asset.InstallLocation,
                ItemName = asset.ItemName,
                MachineNumber = asset.MachineNumber,
                ManagementNumber = asset.ManagementNumber,
                IsCurrent = true,
                LinkedAtUtc = DateTime.UtcNow.AddMonths(-6),
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow.AddMonths(-6),
                UpdatedAtUtc = DateTime.UtcNow.AddMonths(-6)
            };
            db.AddRange(customer, asset, currentHistory);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Itworld,
                    managementId: asset.ManagementId,
                    managementNumber: asset.ManagementNumber,
                    currentLocation: "폐기",
                    customerName: "당근판매",
                    itemName: asset.ItemName,
                    machineNumber: asset.MachineNumber,
                    installLocation: "과거 설치처",
                    rental1: customer.NameOriginal,
                    recall1: "2026-06-30")
            ]);

            var result = await new RentalStateService(db).RebuildAssetsFromWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Itworld));

            Assert.False(result.IsBlocked, result.BlockReason);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == asset.Id);
            Assert.Equal("폐기", storedAsset.AssetStatus);
            Assert.Null(storedAsset.CustomerId);
            Assert.Null(storedAsset.BillingProfileId);
            Assert.Equal(string.Empty, storedAsset.CustomerName);
            Assert.Equal("이전 정상 거래처", storedAsset.LastCustomerName);
            Assert.DoesNotContain("당근판매", storedAsset.LastCustomerName, StringComparison.Ordinal);

            var histories = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .Where(current => current.AssetId == asset.Id && !current.IsDeleted)
                .ToListAsync();
            var history = Assert.Single(histories);
            Assert.False(history.IsCurrent);
            Assert.Equal(customer.Id, history.CustomerId);
            Assert.Equal(customer.NameOriginal, history.CustomerName);
            Assert.Equal(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), history.UnlinkedAtUtc);
            Assert.StartsWith("원장 과거 임대이력 1", history.ChangeReason, StringComparison.Ordinal);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task RebuildAssetsFromWorkbook_ExplicitCustomerId_IsIdempotent()
    {
        PrepareAppRoot("georaeplan-rental-rebuild-explicit-customer");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("재구성 정확 거래처", OfficeCodeCatalog.Usenet);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Usenet,
                    managementId: "951",
                    managementNumber: "2607-051",
                    currentLocation: "렌탈",
                    customerName: "재구성 원장 별칭",
                    currentCustomerId: customer.Id.ToString("D"),
                    itemName: "IMC2010",
                    machineNumber: "REBUILD-SERIAL-051")
            ]);
            var session = CreateRentalImportSession(OfficeCodeCatalog.Usenet);
            var service = new RentalStateService(db);

            var first = await service.RebuildAssetsFromWorkbookAsync(workbookPath, session);
            var second = await service.RebuildAssetsFromWorkbookAsync(workbookPath, session);

            Assert.False(first.IsBlocked, first.BlockReason);
            Assert.False(second.IsBlocked, second.BlockReason);
            var asset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(customer.Id, asset.CustomerId);
            Assert.Equal(customer.NameOriginal, asset.CustomerName);
            var currentHistories = await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .Where(current => current.IsCurrent && !current.IsDeleted)
                .ToListAsync();
            Assert.Single(currentHistories);
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    [Fact]
    public async Task ImportAssetWorkbook_PlaceholderSerial_DoesNotCollapseDistinctManagementNumbers()
    {
        PrepareAppRoot("georaeplan-rental-workbook-placeholder-serial");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var workbookPath = CreateWorkbook([
                RentalRow(
                    officeCode: OfficeCodeCatalog.Itworld,
                    managementId: "980",
                    managementNumber: "2607-080",
                    currentLocation: "창고",
                    customerName: string.Empty,
                    itemName: "IMC2010",
                    machineNumber: "미상"),
                RentalRow(
                    officeCode: OfficeCodeCatalog.Itworld,
                    managementId: "981",
                    managementNumber: "2607-081",
                    currentLocation: "창고",
                    customerName: string.Empty,
                    itemName: "IMC2010",
                    machineNumber: "미상")
            ]);

            var result = await new RentalStateService(db).ImportAssetWorkbookAsync(
                workbookPath,
                CreateRentalImportSession(OfficeCodeCatalog.Itworld));

            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(2, result.CreatedCount);
            var assets = await db.RentalAssets
                .IgnoreQueryFilters()
                .OrderBy(current => current.ManagementNumber)
                .ToListAsync();
            Assert.Equal(2, assets.Count);
            Assert.Equal(["2607-080", "2607-081"], assets.Select(current => current.ManagementNumber).ToArray());
            Assert.All(assets, current => Assert.Equal("미상", current.MachineNumber));
        }
        finally
        {
            CleanupAppRoot();
        }
    }

    private static LocalCustomer CreateCustomer(string name, string officeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            NameOriginal = name,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(name),
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
            Username = $"{officeCode.ToLowerInvariant()}-rental-importer",
            Role = DomainConstants.RoleAdmin,
            Permissions = [AppPermissionNames.RentalImport, AppPermissionNames.RentalEditAll]
        });
        return session;
    }

    private static Dictionary<string, string> RentalRow(
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
            ["제조사"] = "테스트 제조사",
            ["고객명"] = customerName,
            ["현재거래처ID"] = currentCustomerId,
            ["품명"] = itemName,
            ["기계번호"] = machineNumber,
            ["설치위치"] = installLocation,
            ["렌탈요금"] = "10000",
            ["렌탈1"] = rental1,
            ["회수1"] = recall1,
            ["이력1거래처ID"] = historyCustomerId1,
            ["반영검증상태"] = validationStatus
        };

    private static string CreateWorkbook(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rental-assignment-import-{Guid.NewGuid():N}.xlsx");
        var headers = rows.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).ToList();
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
              <sheets><sheet name="렌탈재고관리" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        var sheetRows = new List<IReadOnlyList<string>> { headers };
        sheetRows.AddRange(rows.Select(row => (IReadOnlyList<string>)headers.Select(header => row.GetValueOrDefault(header, string.Empty)).ToList()));
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(sheetRows));
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
        var root = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
    }

    private static void CleanupAppRoot()
    {
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
        SqliteConnection.ClearAllPools();
    }
}
