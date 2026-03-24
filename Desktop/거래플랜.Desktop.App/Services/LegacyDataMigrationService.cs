using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.Sqlite;
using 거래플랜.Shared.Contracts;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
namespace 거래플랜.Desktop.App.Services;

public sealed partial class LegacyDataMigrationService
{
    private const string AutoLegacyLocalDbFingerprintSettingKey = "LegacyMigration.Auto.LocalDbFingerprint";
    private const string AutoLegacyLocalDbPathSettingKey = "LegacyMigration.Auto.LocalDbPath";
    private const string AutoLegacyExcelFingerprintSettingKey = "LegacyMigration.Auto.ExcelFingerprint";
    private const string LegacyCustomerExcelPathSettingKey = "LegacyMigration.CustomerExcelPath";
    private const string LegacyItemExcelPathSettingKey = "LegacyMigration.ItemExcelPath";
    private static readonly string[] CustomerHeaders =[
        "거래처명",
        "고객분류",
        "",
        "등록일자",
        "업체직원",
        "대표전화",
        "휴대폰",
        "팩스번호",
        "참고사항",
        "사업자번호",
        "대표자명",
        "업태",
        "종목",
        "종사업장",
        "홈페이지",
        "E-메일",
        "우편번호",
        "업체주소",
        "상세주소",
        "받는이",
        "존칭어",
        "관리번호",
        "총미수금"
    ];

    private static readonly string[] ItemHeaders =
    [
        "품목분류",
        "상품/제품명",
        "규   격",
        "품목바코드",
        "현재재고",
        "부족재고",
        "안전재고",
        "주매입처",
        "색상",
        "매입단가",
        "매출단가",
        "소매단가",
        "A_단가적용",
        "B_단가적용",
        "C_단가적용",
        "단위",
        "등록일자"
    ];

    private readonly LocalStateService _local;

        public LegacyDataMigrationService(LocalStateService local)
    {
        _local = local;
    }

    public async Task<LegacyAutoMigrationResult> TryAutoMigrateLocalDataAsync(CancellationToken ct = default)
    {
        var legacyLocalDbPath = ResolveLegacyLocalDbPath();
        if (!string.IsNullOrWhiteSpace(legacyLocalDbPath))
        {
            var fingerprint = BuildFileFingerprint(legacyLocalDbPath);
            var processedFingerprint = await _local.GetSettingAsync(AutoLegacyLocalDbFingerprintSettingKey, ct);
            var hasCurrentData = (await _local.GetCustomersAsync(ct)).Count > 0 || (await _local.GetItemsAsync(ct)).Count > 0;
            if (hasCurrentData && string.Equals(processedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new LegacyAutoMigrationResult(
                    false,
                    "local-sqlite",
                    legacyLocalDbPath,
                    null,
                    "이전에 처리한 로컬 DB와 동일해서 자동 마이그레이션을 건너뛰었습니다.");
            }

            var result = await ImportFromLegacyLocalDbAsync(legacyLocalDbPath, ct);
            await _local.SetSettingAsync(AutoLegacyLocalDbFingerprintSettingKey, fingerprint, ct);
            await _local.SetSettingAsync(AutoLegacyLocalDbPathSettingKey, legacyLocalDbPath, ct);
            return new LegacyAutoMigrationResult(
                true,
                "local-sqlite",
                legacyLocalDbPath,
                result,
                $"로컬 DB 거래처 {result.CreatedCustomers + result.UpdatedCustomers:N0}건, 품목 {result.CreatedItems + result.UpdatedItems:N0}건을 반영했습니다.");
        }

        var excelPaths = await ResolveConfiguredLegacyExcelPathsAsync(ct);
        if (!string.IsNullOrWhiteSpace(excelPaths.CustomerExcelPath) &&
            !string.IsNullOrWhiteSpace(excelPaths.ItemExcelPath) &&
            File.Exists(excelPaths.CustomerExcelPath) &&
            File.Exists(excelPaths.ItemExcelPath))
        {
            var fingerprint = BuildFileFingerprint(excelPaths.CustomerExcelPath, excelPaths.ItemExcelPath);
            var processedFingerprint = await _local.GetSettingAsync(AutoLegacyExcelFingerprintSettingKey, ct);
            var hasCurrentData = (await _local.GetCustomersAsync(ct)).Count > 0 || (await _local.GetItemsAsync(ct)).Count > 0;
            if (hasCurrentData && string.Equals(processedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new LegacyAutoMigrationResult(
                    false,
                    "excel",
                    $"{excelPaths.CustomerExcelPath} | {excelPaths.ItemExcelPath}",
                    null,
                    "이전에 처리한 엑셀 데이터와 동일해서 자동 마이그레이션을 건너뛰었습니다.");
            }

            var result = await ImportFromExcelAsync(excelPaths.CustomerExcelPath, excelPaths.ItemExcelPath, ct);
            await _local.SetSettingAsync(AutoLegacyExcelFingerprintSettingKey, fingerprint, ct);
            return new LegacyAutoMigrationResult(
                true,
                "excel",
                $"{excelPaths.CustomerExcelPath} | {excelPaths.ItemExcelPath}",
                result,
                $"엑셀 거래처 {result.CreatedCustomers + result.UpdatedCustomers:N0}건, 품목 {result.CreatedItems + result.UpdatedItems:N0}건을 반영했습니다.");
        }

        return new LegacyAutoMigrationResult(false, string.Empty, string.Empty, null, "자동 마이그레이션 대상 로컬 데이터가 없습니다.");
    }

    public async Task<LegacyImportResult> ImportFromLegacyLocalDbAsync(
        string sourceDbPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDbPath) || !File.Exists(sourceDbPath))
            throw new FileNotFoundException("기존 로컬 DB 파일을 찾을 수 없습니다.", sourceDbPath);

        var sourceCustomers = await Task.Run(() => ReadCustomersFromLegacyLocalDb(sourceDbPath), ct);
        var sourceItems = await Task.Run(() => ReadItemsFromLegacyLocalDb(sourceDbPath), ct);

        var existingCustomers = await _local.GetCustomersAsync(ct);
        var existingItems = await _local.GetItemsAsync(ct);
        var customerLookup = BuildCustomerLookup(existingCustomers);
        var itemLookup = BuildItemLookup(existingItems);

        var createdCustomers = 0;
        var updatedCustomers = 0;
        var skippedCustomers = 0;
        foreach (var source in sourceCustomers)
        {
            if (string.IsNullOrWhiteSpace(source.NameOriginal))
            {
                skippedCustomers++;
                continue;
            }

            var target = FindMatchingCustomer(customerLookup, source);
            var isNew = target is null;
            if (isNew)
                target = CreateNewCustomerShell(source);

            var preferIncoming = isNew || source.UpdatedAtUtc >= target!.UpdatedAtUtc;
            var changed = MergeCustomer(target!, source, preferIncoming);
            if (isNew)
            {
                await _local.UpsertCustomerAsync(target!, ct);
                RegisterCustomerLookup(customerLookup, target!);
                createdCustomers++;
            }
            else if (changed)
            {
                await _local.UpsertCustomerAsync(target!, ct);
                RegisterCustomerLookup(customerLookup, target!);
                updatedCustomers++;
            }
            else
            {
                skippedCustomers++;
            }
        }

        var createdItems = 0;
        var updatedItems = 0;
        var skippedItems = 0;
        foreach (var source in sourceItems)
        {
            if (string.IsNullOrWhiteSpace(source.NameOriginal))
            {
                skippedItems++;
                continue;
            }

            var target = FindMatchingItem(itemLookup, source);
            var isNew = target is null;
            if (isNew)
                target = CreateNewItemShell(source);

            var preferIncoming = isNew || source.UpdatedAtUtc >= target!.UpdatedAtUtc;
            var changed = MergeItem(target!, source, preferIncoming);
            if (isNew)
            {
                await _local.UpsertItemAsync(target!, ct);
                RegisterItemLookup(itemLookup, target!);
                createdItems++;
            }
            else if (changed)
            {
                await _local.UpsertItemAsync(target!, ct);
                RegisterItemLookup(itemLookup, target!);
                updatedItems++;
            }
            else
            {
                skippedItems++;
            }
        }

        return new LegacyImportResult(
            createdCustomers,
            updatedCustomers,
            skippedCustomers,
            createdItems,
            updatedItems,
            skippedItems);
    }

    public async Task<LegacyExportResult> ExportFromOriginalAsync(
        string sourceFdbPath,
        string customerExcelPath,
        string itemExcelPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFdbPath))
            throw new ArgumentException("원본 DB 경로가 비어 있습니다.", nameof(sourceFdbPath));
        if (!File.Exists(sourceFdbPath))
            throw new FileNotFoundException("원본 DB 파일을 찾을 수 없습니다.", sourceFdbPath);
        if (string.IsNullOrWhiteSpace(customerExcelPath))
            throw new ArgumentException("거래처 엑셀 경로가 비어 있습니다.", nameof(customerExcelPath));
        if (string.IsNullOrWhiteSpace(itemExcelPath))
            throw new ArgumentException("제품 엑셀 경로가 비어 있습니다.", nameof(itemExcelPath));

        LegacyExtractData extracted;
        try
        {
            extracted = await Task.Run(() => ReadLegacyData(sourceFdbPath), ct);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidOperationException(
                "오리지널 DB 추출에 실패했습니다. Firebird 클라이언트 DLL의 32/64비트가 현재 앱과 맞지 않습니다. " +
                "x64 환경에서는 64비트 fbclient.dll/fbembed.dll이 필요합니다.",
                ex);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "오리지널 DB 추출에 실패했습니다. Firebird 클라이언트 DLL(fbclient.dll 또는 fbembed.dll)을 찾을 수 없습니다.",
                ex);
        }

        EnsureParentDirectory(customerExcelPath);
        EnsureParentDirectory(itemExcelPath);

        await Task.Run(() => WriteCustomerWorkbook(customerExcelPath, extracted.Customers), ct);
        await Task.Run(() => WriteItemWorkbook(itemExcelPath, extracted.Items), ct);

        return new LegacyExportResult(
            extracted.Customers.Count,
            extracted.Items.Count,
            customerExcelPath,
            itemExcelPath);
    }

        public async Task<LegacyImportResult> ImportFromExcelAsync(
        string customerExcelPath,
        string itemExcelPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerExcelPath) || !File.Exists(customerExcelPath))
            throw new FileNotFoundException("거래처 엑셀 파일을 찾을 수 없습니다.", customerExcelPath);
        if (string.IsNullOrWhiteSpace(itemExcelPath) || !File.Exists(itemExcelPath))
            throw new FileNotFoundException("제품 엑셀 파일을 찾을 수 없습니다.", itemExcelPath);

        var customerRows = await Task.Run(() => ReadCustomersFromWorkbook(customerExcelPath), ct);
        var itemRows = await Task.Run(() => ReadItemsFromWorkbook(itemExcelPath), ct);

        var existingCustomers = await _local.GetCustomersAsync(ct);
        var existingItems = await _local.GetItemsAsync(ct);
        var customerLookup = BuildCustomerLookup(existingCustomers);
        var itemLookup = BuildItemLookup(existingItems);

        var createdCustomers = 0;
        var updatedCustomers = 0;
        var skippedCustomers = 0;
        foreach (var row in customerRows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                skippedCustomers++;
                continue;
            }

            var source = BuildImportedCustomer(row);
            var target = FindMatchingCustomer(customerLookup, source);
            var isNew = target is null;
            if (isNew)
                target = CreateNewCustomerShell(source);

            var changed = MergeCustomer(target!, source, preferIncoming: true);
            if (isNew)
            {
                await _local.UpsertCustomerAsync(target!, ct);
                RegisterCustomerLookup(customerLookup, target!);
                createdCustomers++;
            }
            else if (changed)
            {
                await _local.UpsertCustomerAsync(target!, ct);
                RegisterCustomerLookup(customerLookup, target!);
                updatedCustomers++;
            }
            else
            {
                skippedCustomers++;
            }
        }

        var createdItems = 0;
        var updatedItems = 0;
        var skippedItems = 0;
        foreach (var row in itemRows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                skippedItems++;
                continue;
            }

            var source = BuildImportedItem(row);
            var target = FindMatchingItem(itemLookup, source);
            var isNew = target is null;
            if (isNew)
                target = CreateNewItemShell(source);

            var changed = MergeItem(target!, source, preferIncoming: true);
            if (isNew)
            {
                await _local.UpsertItemAsync(target!, ct);
                RegisterItemLookup(itemLookup, target!);
                createdItems++;
            }
            else if (changed)
            {
                await _local.UpsertItemAsync(target!, ct);
                RegisterItemLookup(itemLookup, target!);
                updatedItems++;
            }
            else
            {
                skippedItems++;
            }
        }

        return new LegacyImportResult(
            createdCustomers,
            updatedCustomers,
            skippedCustomers,
            createdItems,
            updatedItems,
            skippedItems);
    }

    public async Task<CustomerWorkbookImportResult> ImportCustomerWorkbookAsync(
        string customerExcelPath,
        string responsibleOfficeCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerExcelPath) || !File.Exists(customerExcelPath))
            throw new FileNotFoundException("거래처 엑셀 파일을 찾을 수 없습니다.", customerExcelPath);

        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(responsibleOfficeCode, DomainConstants.OfficeUsenet);
        var customerRows = await Task.Run(() => ReadCustomersFromWorkbook(customerExcelPath), ct);
        var result = new CustomerWorkbookImportResult
        {
            SourcePath = customerExcelPath,
            ResponsibleOfficeCode = normalizedOfficeCode,
            TotalCount = customerRows.Count
        };

        using var suppressSync = _local.SuppressSyncDispatch();
        var existingCustomers = (await _local.GetCustomersAsync(ct))
            .Where(customer => string.Equals(
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
                normalizedOfficeCode,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var customerLookup = BuildCustomerLookup(existingCustomers);

        foreach (var row in customerRows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                result.FailureCount++;
                result.Messages.Add("거래처명 누락 행은 가져오지 않았습니다.");
                continue;
            }

            var source = BuildImportedCustomer(row);
            source.ResponsibleOfficeCode = normalizedOfficeCode;
            var duplicate = FindMatchingCustomer(customerLookup, source);
            if (duplicate is not null)
            {
                result.DuplicateCount++;
                continue;
            }

            await _local.UpsertCustomerAsync(source, ct);
            RegisterCustomerLookup(customerLookup, source);
            result.CreatedCount++;
        }

        return result;
    }

    private static void EnsureParentDirectory(string path){
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
    }

    private static LegacyExtractData ReadLegacyData(string sourceFdbPath)
    {
        var csb = BuildConnectionString(sourceFdbPath);
        using var connection = new FbConnection(csb.ToString());
        connection.Open();

        var customers = ReadLegacyCustomers(connection);
        var items = ReadLegacyItems(connection);
        return new LegacyExtractData(customers, items);
    }

    private static FbConnectionStringBuilder BuildConnectionString(string sourceFdbPath)
    {
        var csb = new FbConnectionStringBuilder
        {
            Database = sourceFdbPath,
            UserID = "SYSDBA",
            Password = "masterkey",
            Charset = "NONE",
            Dialect = 3,
            ServerType = FbServerType.Embedded
        };

        var clientLibrary = ResolveClientLibrary(sourceFdbPath);
        if (!string.IsNullOrWhiteSpace(clientLibrary))
            csb.ClientLibrary = clientLibrary;

        return csb;
    }

    private static string? ResolveClientLibrary(string sourceFdbPath)
    {
        var dataDir = Path.GetDirectoryName(sourceFdbPath);
        var rootDir = dataDir is null ? null : Directory.GetParent(dataDir)?.FullName;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(rootDir))
        {
            candidates.Add(Path.Combine(rootDir, "fbembed.dll"));
            candidates.Add(Path.Combine(rootDir, "fbclient.dll"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "fbembed.dll"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "fbclient.dll"));

        return candidates.FirstOrDefault(path => File.Exists(path) && IsDllBitnessCompatible(path));
    }

    private static bool IsDllBitnessCompatible(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset > stream.Length - 6)
                return false;

            stream.Seek(peOffset, SeekOrigin.Begin);
            var pe = reader.ReadUInt32();
            if (pe != 0x00004550)
                return false;

            var machine = reader.ReadUInt16();
            var is64 = machine == 0x8664;
            var is32 = machine == 0x014c;

            return Environment.Is64BitProcess ? is64 : is32;
        }
        catch
        {
            return false;
        }
    }

    private static List<LegacyCustomerRow> ReadLegacyCustomers(FbConnection connection)
    {
        const string sql = """
SELECT
    GOGAEKNAME,
    GOGUBUN,
    GOGAEKDATE,
    DAMDANGNAME,
    TEL1,
    HANDPHON,
    FAX,
    GITA1,
    MEMOTOP,
    SAUPNO,
    SAUPNAME,
    UPTAE,
    JONGMUK,
    DONG,
    DDD,
    INTE1,
    INTE2,
    POST,
    ADDR1,
    ADDR2,
    NAME1,
    NAME2,
    H_CODE,
    TOTALMISUPAY
FROM GOGAEK
ORDER BY GOGAEKNAME
""";

        using var command = new FbCommand(sql, connection);
        using var reader = command.ExecuteReader();

        var rows = new List<LegacyCustomerRow>();
        while (reader.Read())
        {
            var date = ReadDate(reader, "GOGAEKDATE");
            var note1 = ReadText(reader, "GITA1");
            var note2 = ReadText(reader, "MEMOTOP");
            var note = string.IsNullOrWhiteSpace(note2) ? note1 : note2;
            var email1 = ReadText(reader, "INTE1");
            var email2 = ReadText(reader, "INTE2");
            var email = ChooseEmail(email1, email2);

            rows.Add(new LegacyCustomerRow
            {
                Name = ReadText(reader, "GOGAEKNAME"),
                Category = ReadText(reader, "GOGUBUN"),
                RegisterDate = date,
                Staff = ReadText(reader, "DAMDANGNAME"),
                Phone = ReadText(reader, "TEL1"),
                Mobile = ReadText(reader, "HANDPHON"),
                Fax = ReadText(reader, "FAX"),
                Note = note,
                BusinessNumber = ReadText(reader, "SAUPNO"),
                Representative = ReadText(reader, "SAUPNAME"),
                BusinessType = ReadText(reader, "UPTAE"),
                BusinessItem = ReadText(reader, "JONGMUK"),
                BranchOffice = ReadText(reader, "DONG"),
                HomePage = ReadText(reader, "DDD"),
                Email = email,
                ZipCode = ReadText(reader, "POST"),
                Address1 = ReadText(reader, "ADDR1"),
                Address2 = ReadText(reader, "ADDR2"),
                Recipient = ReadText(reader, "NAME1"),
                Honorific = ReadText(reader, "NAME2"),
                ManageNumber = ReadText(reader, "H_CODE"),
                TotalReceivable = ReadDecimal(reader, "TOTALMISUPAY")
            });
        }

        return rows;
    }

    private static List<LegacyItemRow> ReadLegacyItems(FbConnection connection)
    {
        const string sql = """
SELECT
    JAEGUBUN3,
    JAEPUMNAME,
    GUGAEK,
    JAEPUMCODE,
    HEUNJAEGO,
    BUJOKJAEGO,
    ANJUNGJAEGO,
    JAEGUBUNK1,
    COLOR,
    MAEIBDANGA,
    MAECHULDANGA,
    SOBIJAGA,
    PANMAEPAY1,
    PANMAEPAY2,
    PANMAEPAY3,
    DANWI,
    JAEPUMDATE
FROM JAEPUM
ORDER BY JAEPUMNAME
""";

        using var command = new FbCommand(sql, connection);
        using var reader = command.ExecuteReader();

        var rows = new List<LegacyItemRow>();
        while (reader.Read())
        {
            rows.Add(new LegacyItemRow
            {
                Category = ReadText(reader, "JAEGUBUN3"),
                Name = ReadText(reader, "JAEPUMNAME"),
                Specification = ReadText(reader, "GUGAEK"),
                Barcode = ReadText(reader, "JAEPUMCODE"),
                CurrentStock = ReadDecimal(reader, "HEUNJAEGO"),
                LowStock = ReadDecimal(reader, "BUJOKJAEGO"),
                SafetyStock = ReadDecimal(reader, "ANJUNGJAEGO"),
                MainSupplier = ReadText(reader, "JAEGUBUNK1"),
                Color = ReadText(reader, "COLOR"),
                PurchasePrice = ReadDecimal(reader, "MAEIBDANGA"),
                SalePrice = ReadDecimal(reader, "MAECHULDANGA"),
                RetailPrice = ReadDecimal(reader, "SOBIJAGA"),
                PriceA = ReadDecimal(reader, "PANMAEPAY1"),
                PriceB = ReadDecimal(reader, "PANMAEPAY2"),
                PriceC = ReadDecimal(reader, "PANMAEPAY3"),
                Unit = ReadText(reader, "DANWI"),
                RegisterDate = ReadDate(reader, "JAEPUMDATE")
            });
        }

        return rows;
    }

    private static string ReadText(FbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return string.Empty;

        return (reader.GetValue(ordinal)?.ToString() ?? string.Empty).Trim();
    }

    private static decimal ReadDecimal(FbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return 0m;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal d => d,
            double d => Convert.ToDecimal(d),
            float f => Convert.ToDecimal(f),
            int i => i,
            long l => l,
            short s => s,
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static DateOnly? ReadDate(FbDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            string s when DateOnly.TryParse(s, out var d) => d,
            _ => null
        };
    }

    private static string ChooseEmail(string email1, string email2)
    {
        if (email1.Contains('@'))
            return email1;
        if (email2.Contains('@'))
            return email2;
        return email1;
    }

    private static void WriteCustomerWorkbook(string path, IReadOnlyList<LegacyCustomerRow> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Sheet1");

        for (var i = 0; i < CustomerHeaders.Length; i++)
            ws.Cell(1, i + 1).Value = CustomerHeaders[i];

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var r = i + 2;

            ws.Cell(r, 1).Value = row.Name;
            ws.Cell(r, 2).Value = row.Category;
            ws.Cell(r, 3).Value = string.Empty;
            ws.Cell(r, 4).Value = row.RegisterDate?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(r, 5).Value = row.Staff;
            ws.Cell(r, 6).Value = row.Phone;
            ws.Cell(r, 7).Value = row.Mobile;
            ws.Cell(r, 8).Value = row.Fax;
            ws.Cell(r, 9).Value = row.Note;
            ws.Cell(r, 10).Value = row.BusinessNumber;
            ws.Cell(r, 11).Value = row.Representative;
            ws.Cell(r, 12).Value = row.BusinessType;
            ws.Cell(r, 13).Value = row.BusinessItem;
            ws.Cell(r, 14).Value = row.BranchOffice;
            ws.Cell(r, 15).Value = row.HomePage;
            ws.Cell(r, 16).Value = row.Email;
            ws.Cell(r, 17).Value = row.ZipCode;
            ws.Cell(r, 18).Value = row.Address1;
            ws.Cell(r, 19).Value = row.Address2;
            ws.Cell(r, 20).Value = row.Recipient;
            ws.Cell(r, 21).Value = row.Honorific;
            ws.Cell(r, 22).Value = row.ManageNumber;
            ws.Cell(r, 23).Value = row.TotalReceivable;
        }

        ws.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    private static void WriteItemWorkbook(string path, IReadOnlyList<LegacyItemRow> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Sheet1");

        for (var i = 0; i < ItemHeaders.Length; i++)
            ws.Cell(1, i + 1).Value = ItemHeaders[i];

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var r = i + 2;

            ws.Cell(r, 1).Value = row.Category;
            ws.Cell(r, 2).Value = row.Name;
            ws.Cell(r, 3).Value = row.Specification;
            ws.Cell(r, 4).Value = row.Barcode;
            ws.Cell(r, 5).Value = row.CurrentStock;
            ws.Cell(r, 6).Value = row.LowStock;
            ws.Cell(r, 7).Value = row.SafetyStock;
            ws.Cell(r, 8).Value = row.MainSupplier;
            ws.Cell(r, 9).Value = row.Color;
            ws.Cell(r, 10).Value = row.PurchasePrice;
            ws.Cell(r, 11).Value = row.SalePrice;
            ws.Cell(r, 12).Value = row.RetailPrice;
            ws.Cell(r, 13).Value = row.PriceA;
            ws.Cell(r, 14).Value = row.PriceB;
            ws.Cell(r, 15).Value = row.PriceC;
            ws.Cell(r, 16).Value = row.Unit;
            ws.Cell(r, 17).Value = row.RegisterDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        }

        ws.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    private static List<ImportedCustomerRow> ReadCustomersFromWorkbook(string path)
    {
        using var workbook = new XLWorkbook(path);
        var ws = workbook.Worksheet(1);
        var maxRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        var headerMap = BuildHeaderMap(ws);

        var rows = new List<ImportedCustomerRow>();
        for (var rowIndex = 2; rowIndex <= maxRow; rowIndex++)
        {
            var row = new ImportedCustomerRow
            {
                Name = ReadString(ws, rowIndex, headerMap, "거래처명", "상호명/고객", "상호명", "고객"),
                Category = ReadString(ws, rowIndex, headerMap, "고객분류", "고객구분", "거래구분"),
                RegisterDate = ReadDate(ws, rowIndex, headerMap, "등록일자", "등록일"),
                Manager = ReadString(ws, rowIndex, headerMap, "담당자"),
                Staff = ReadString(ws, rowIndex, headerMap, "업체직원", "업체담당자", "담당직원"),
                Phone = ReadString(ws, rowIndex, headerMap, "대표전화", "전화번호"),
                Mobile = ReadString(ws, rowIndex, headerMap, "휴대폰", "휴대전화"),
                Fax = ReadString(ws, rowIndex, headerMap, "팩스번호", "팩스"),
                Note = ReadString(ws, rowIndex, headerMap, "참고사항", "메모사항", "메모"),
                BusinessNumber = ReadString(ws, rowIndex, headerMap, "사업자번호"),
                Representative = ReadString(ws, rowIndex, headerMap, "대표자명", "대표자"),
                BusinessType = ReadString(ws, rowIndex, headerMap, "업태"),
                BusinessItem = ReadString(ws, rowIndex, headerMap, "종목"),
                BranchOffice = ReadString(ws, rowIndex, headerMap, "종사업장"),
                HomePage = ReadString(ws, rowIndex, headerMap, "홈페이지", "홈페이지주소"),
                Email = ReadString(ws, rowIndex, headerMap, "E-메일", "E-MAIL", "EMAIL", "이메일"),
                ZipCode = ReadString(ws, rowIndex, headerMap, "우편번호"),
                Address1 = ReadString(ws, rowIndex, headerMap, "업체주소", "주소"),
                Address2 = ReadString(ws, rowIndex, headerMap, "상세주소"),
                Recipient = ReadString(ws, rowIndex, headerMap, "받는이"),
                Honorific = ReadString(ws, rowIndex, headerMap, "존칭어", "존칭"),
                ManageNumber = ReadString(ws, rowIndex, headerMap, "관리번호"),
                TotalReceivable = ReadDecimal(ws, rowIndex, headerMap, "총미수금")
            };

            if (!string.IsNullOrWhiteSpace(row.Name))
                rows.Add(row);
        }

        return rows;
    }

    private static List<ImportedItemRow> ReadItemsFromWorkbook(string path)
    {
        using var workbook = new XLWorkbook(path);
        var ws = workbook.Worksheet(1);
        var maxRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        var headerMap = BuildHeaderMap(ws);

        var rows = new List<ImportedItemRow>();
        for (var rowIndex = 2; rowIndex <= maxRow; rowIndex++)
        {
            var row = new ImportedItemRow
            {
                Category = ReadString(ws, rowIndex, headerMap, "품목분류"),
                Name = ReadString(ws, rowIndex, headerMap, "상품/제품명"),
                Specification = ReadString(ws, rowIndex, headerMap, "규   격"),
                Barcode = ReadString(ws, rowIndex, headerMap, "품목바코드"),
                CurrentStock = ReadDecimal(ws, rowIndex, headerMap, "현재재고"),
                LowStock = ReadDecimal(ws, rowIndex, headerMap, "부족재고"),
                SafetyStock = ReadDecimal(ws, rowIndex, headerMap, "안전재고"),
                MainSupplier = ReadString(ws, rowIndex, headerMap, "주매입처"),
                Color = ReadString(ws, rowIndex, headerMap, "색상"),
                PurchasePrice = ReadDecimal(ws, rowIndex, headerMap, "매입단가"),
                SalePrice = ReadDecimal(ws, rowIndex, headerMap, "매출단가"),
                RetailPrice = ReadDecimal(ws, rowIndex, headerMap, "소매단가"),
                PriceA = ReadDecimal(ws, rowIndex, headerMap, "A_단가적용"),
                PriceB = ReadDecimal(ws, rowIndex, headerMap, "B_단가적용"),
                PriceC = ReadDecimal(ws, rowIndex, headerMap, "C_단가적용"),
                Unit = ReadString(ws, rowIndex, headerMap, "단위"),
                RegisterDate = ReadDate(ws, rowIndex, headerMap, "등록일자")
            };

            if (!string.IsNullOrWhiteSpace(row.Name))
                rows.Add(row);
        }

        return rows;
    }

    private static Dictionary<string, int> BuildHeaderMap(IXLWorksheet ws)
    {
        var maxCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var col = 1; col <= maxCol; col++)
        {
            var raw = ws.Cell(1, col).GetString();
            var key = NormalizeKey(raw);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!map.ContainsKey(key))
                map[key] = col;
        }

        return map;
    }

    private static string ReadString(IXLWorksheet ws, int row, IReadOnlyDictionary<string, int> headerMap, params string[] headerAliases)
    {
        var cell = FindCell(ws, row, headerMap, headerAliases);
        if (cell is null)
            return string.Empty;

        return cell.GetString().Trim();
    }

    private static decimal ReadDecimal(IXLWorksheet ws, int row, IReadOnlyDictionary<string, int> headerMap, params string[] headerAliases)
    {
        var cell = FindCell(ws, row, headerMap, headerAliases);
        if (cell is null || cell.IsEmpty())
            return 0m;

        if (cell.TryGetValue<decimal>(out var decimalValue))
            return decimalValue;
        if (cell.TryGetValue<double>(out var doubleValue))
            return Convert.ToDecimal(doubleValue);

        var text = cell.GetString().Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var current))
            return current;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariant))
            return invariant;

        return 0m;
    }

    private static DateOnly? ReadDate(IXLWorksheet ws, int row, IReadOnlyDictionary<string, int> headerMap, params string[] headerAliases)
    {
        var cell = FindCell(ws, row, headerMap, headerAliases);
        if (cell is null || cell.IsEmpty())
            return null;

        if (cell.TryGetValue<DateTime>(out var dateTime))
            return DateOnly.FromDateTime(dateTime);

        var text = cell.GetString().Trim();
        if (DateOnly.TryParse(text, out var date))
            return date;
        if (DateTime.TryParse(text, out dateTime))
            return DateOnly.FromDateTime(dateTime);

        return null;
    }

    private static IXLCell? FindCell(IXLWorksheet ws, int row, IReadOnlyDictionary<string, int> headerMap, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            var key = NormalizeKey(alias);
            if (headerMap.TryGetValue(key, out var col))
                return ws.Cell(row, col);
        }

        return null;
    }

    private static void ApplyCustomer(LocalCustomer target, ImportedCustomerRow row)
    {
        target.NameOriginal = row.Name.Trim();
        target.NameMatchKey = target.NameOriginal.ToUpperInvariant();
        target.TradeType = CustomerTradeTypes.Normalize(row.Category.Trim());
        target.Department = string.Empty;
        target.ContactPerson = row.Staff.Trim();
        target.BusinessNumber = row.BusinessNumber.Trim();
        target.Address = row.Address1.Trim();
        target.DetailAddress = row.Address2.Trim();
        target.Phone = row.Phone.Trim();
        target.MobilePhone = row.Mobile.Trim();
        target.FaxNumber = row.Fax.Trim();
        target.Email = row.Email.Trim();
        target.HomePage = row.HomePage.Trim();
        target.Representative = row.Representative.Trim();
        target.BusinessType = row.BusinessType.Trim();
        target.BusinessItem = row.BusinessItem.Trim();
        target.Recipient = row.Recipient.Trim();

        if (string.IsNullOrWhiteSpace(target.PriceGrade))
            target.PriceGrade = "매출단가";

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Manager))
            notes.Add($"담당자: {row.Manager.Trim()}");
        if (!string.IsNullOrWhiteSpace(row.Note))
            notes.Add(row.Note.Trim());
        if (!string.IsNullOrWhiteSpace(row.BranchOffice))
            notes.Add($"종사업장: {row.BranchOffice.Trim()}");
        if (!string.IsNullOrWhiteSpace(row.Honorific))
            notes.Add($"존칭어: {row.Honorific.Trim()}");
        if (!string.IsNullOrWhiteSpace(row.ManageNumber))
            notes.Add($"관리번호: {row.ManageNumber.Trim()}");
        if (row.TotalReceivable != 0)
            notes.Add($"총미수금: {row.TotalReceivable:N0}");
        if (row.RegisterDate.HasValue)
            notes.Add($"등록일자: {row.RegisterDate:yyyy-MM-dd}");

        target.Notes = string.Join(Environment.NewLine, notes).Trim();
    }

    private static void ApplyItem(LocalItem target, ImportedItemRow row)
    {
        target.NameOriginal = row.Name.Trim();
        target.NameMatchKey = target.NameOriginal.ToUpperInvariant();
        target.SpecificationOriginal = row.Specification.Trim();
        target.SpecificationMatchKey = target.SpecificationOriginal.ToUpperInvariant();
        target.CategoryName = row.Category.Trim();
        target.Unit = row.Unit.Trim();
        target.CurrentStock = row.CurrentStock;
        target.SafetyStock = row.SafetyStock;
        target.PurchasePrice = row.PurchasePrice;
        target.SalePrice = row.SalePrice;
        target.RetailPrice = row.RetailPrice;
        target.PriceGradeA = row.PriceA;
        target.PriceGradeB = row.PriceB;
        target.PriceGradeC = row.PriceC;
        target.MaterialNumber = row.Barcode.Trim();
        target.IsSale = true;

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.MainSupplier))
            notes.Add($"주매입처: {row.MainSupplier.Trim()}");
        if (!string.IsNullOrWhiteSpace(row.Color))
            notes.Add($"색상: {row.Color.Trim()}");
        if (row.LowStock != 0)
            notes.Add($"부족재고: {row.LowStock:N0}");
        if (row.RegisterDate.HasValue)
            notes.Add($"등록일자: {row.RegisterDate:yyyy-MM-dd}");

        target.Notes = string.Join(Environment.NewLine, notes).Trim();
    }

    private static string BuildItemKey(string? name, string? spec)
        => $"{NormalizeKey(name)}|{NormalizeKey(spec)}";

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value
            .Trim()
            .Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-' && c != '/' && c != '(' && c != ')' && c != '[' && c != ']' && c != '.')
            .ToArray();
        return new string(chars).ToUpperInvariant();
    }

    private sealed record LegacyExtractData(List<LegacyCustomerRow> Customers, List<LegacyItemRow> Items);

    private sealed class LegacyCustomerRow
    {
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public DateOnly? RegisterDate { get; init; }
        public string Staff { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string Mobile { get; init; } = string.Empty;
        public string Fax { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
        public string BusinessNumber { get; init; } = string.Empty;
        public string Representative { get; init; } = string.Empty;
        public string BusinessType { get; init; } = string.Empty;
        public string BusinessItem { get; init; } = string.Empty;
        public string BranchOffice { get; init; } = string.Empty;
        public string HomePage { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string ZipCode { get; init; } = string.Empty;
        public string Address1 { get; init; } = string.Empty;
        public string Address2 { get; init; } = string.Empty;
        public string Recipient { get; init; } = string.Empty;
        public string Honorific { get; init; } = string.Empty;
        public string ManageNumber { get; init; } = string.Empty;
        public decimal TotalReceivable { get; init; }
    }

    private sealed class LegacyItemRow
    {
        public string Category { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Specification { get; init; } = string.Empty;
        public string Barcode { get; init; } = string.Empty;
        public decimal CurrentStock { get; init; }
        public decimal LowStock { get; init; }
        public decimal SafetyStock { get; init; }
        public string MainSupplier { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
        public decimal PurchasePrice { get; init; }
        public decimal SalePrice { get; init; }
        public decimal RetailPrice { get; init; }
        public decimal PriceA { get; init; }
        public decimal PriceB { get; init; }
        public decimal PriceC { get; init; }
        public string Unit { get; init; } = string.Empty;
        public DateOnly? RegisterDate { get; init; }
    }

    private sealed class ImportedCustomerRow
    {
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public DateOnly? RegisterDate { get; init; }
        public string Manager { get; init; } = string.Empty;
        public string Staff { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string Mobile { get; init; } = string.Empty;
        public string Fax { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
        public string BusinessNumber { get; init; } = string.Empty;
        public string Representative { get; init; } = string.Empty;
        public string BusinessType { get; init; } = string.Empty;
        public string BusinessItem { get; init; } = string.Empty;
        public string BranchOffice { get; init; } = string.Empty;
        public string HomePage { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string ZipCode { get; init; } = string.Empty;
        public string Address1 { get; init; } = string.Empty;
        public string Address2 { get; init; } = string.Empty;
        public string Recipient { get; init; } = string.Empty;
        public string Honorific { get; init; } = string.Empty;
        public string ManageNumber { get; init; } = string.Empty;
        public decimal TotalReceivable { get; init; }
    }

    private sealed class ImportedItemRow
    {
        public string Category { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Specification { get; init; } = string.Empty;
        public string Barcode { get; init; } = string.Empty;
        public decimal CurrentStock { get; init; }
        public decimal LowStock { get; init; }
        public decimal SafetyStock { get; init; }
        public string MainSupplier { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
        public decimal PurchasePrice { get; init; }
        public decimal SalePrice { get; init; }
        public decimal RetailPrice { get; init; }
        public decimal PriceA { get; init; }
        public decimal PriceB { get; init; }
        public decimal PriceC { get; init; }
        public string Unit { get; init; } = string.Empty;
        public DateOnly? RegisterDate { get; init; }
    }
}

public sealed record LegacyExportResult(
    int CustomerCount,
    int ItemCount,
    string CustomerExcelPath,
    string ItemExcelPath);

public sealed class CustomerWorkbookImportResult
{
    public string SourcePath { get; init; } = string.Empty;
    public string ResponsibleOfficeCode { get; init; } = DomainConstants.OfficeUsenet;
    public int TotalCount { get; set; }
    public int CreatedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int FailureCount { get; set; }
    public List<string> Messages { get; } = new();
}

public sealed record LegacyImportResult(
    int CreatedCustomers,
    int UpdatedCustomers,
    int SkippedCustomers,
    int CreatedItems,
    int UpdatedItems,
    int SkippedItems);
