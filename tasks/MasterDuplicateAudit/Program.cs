using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

var summary = await BuildSummaryAsync();

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ManualAudit", "outputs");
Directory.CreateDirectory(outputDirectory);
var outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "master-duplicate-audit.json"));
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(outputPath);

static async Task<object> BuildSummaryAsync()
{
    var generatedAtUtc = DateTime.UtcNow;
    var dbPath = AppPaths.LocalDbFile;
    if (!File.Exists(dbPath))
    {
        return new
        {
            GeneratedAtUtc = generatedAtUtc,
            Status = "local_db_missing",
            LocalDbFile = dbPath
        };
    }

    var dbInfo = new FileInfo(dbPath);
    if (dbInfo.Length == 0)
    {
        return new
        {
            GeneratedAtUtc = generatedAtUtc,
            Status = "local_db_empty",
            LocalDbFile = dbPath,
            SizeBytes = dbInfo.Length
        };
    }

    await using var db = new LocalDbContext();
    if (!await db.Database.CanConnectAsync() || !await HasRequiredTablesAsync(db))
    {
        return new
        {
            GeneratedAtUtc = generatedAtUtc,
            Status = "local_db_uninitialized",
            LocalDbFile = dbPath,
            SizeBytes = dbInfo.Length
        };
    }

    var items = await db.Items.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
    var companyProfiles = await db.CompanyProfiles.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
    var customers = await db.Customers.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
    var rentalBillingProfiles = await db.RentalBillingProfiles.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
    var rentalAssets = await db.RentalAssets.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();

    return new
    {
        GeneratedAtUtc = generatedAtUtc,
        Status = "ok",
        LocalDbFile = dbPath,
        SizeBytes = dbInfo.Length,
        ItemExactDuplicateGroupCount = items
            .GroupBy(BuildItemDuplicateKey, StringComparer.Ordinal)
            .Count(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1),
        CompanyProfileDuplicateGroupCount = companyProfiles
            .GroupBy(BuildCompanyProfileDuplicateKey, StringComparer.Ordinal)
            .Count(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1),
        BusinessCustomerDuplicateGroupCount = customers
            .GroupBy(BuildBusinessDuplicateCustomerKey, StringComparer.Ordinal)
            .Count(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1),
        RentalBillingProfileDuplicateGroupCount = rentalBillingProfiles
            .GroupBy(BuildRentalBillingProfileDuplicateKey, StringComparer.Ordinal)
            .Count(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1),
        RentalAssetDuplicateGroupCount = rentalAssets
            .GroupBy(BuildRentalAssetDuplicateKey, StringComparer.Ordinal)
            .Count(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
    };
}

static async Task<bool> HasRequiredTablesAsync(LocalDbContext db)
{
    await using var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
        await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = """
                          SELECT COUNT(*)
                          FROM sqlite_master
                          WHERE type = 'table'
                            AND name IN ('Items', 'CompanyProfiles', 'Customers', 'RentalBillingProfiles', 'RentalAssets')
                          """;
    var scalar = await command.ExecuteScalarAsync();
    return Convert.ToInt32(scalar, CultureInfo.InvariantCulture) == 5;
}

static string BuildCompanyProfileDuplicateKey(LocalCompanyProfile current)
{
    if (string.IsNullOrWhiteSpace(current.BusinessNumber))
        return string.Empty;

    return string.Join('|',
        BuildStrictTextKey(current.OfficeCode),
        BuildStrictTextKey(current.BusinessNumber),
        BuildStrictTextKey(current.TradeName),
        BuildStrictTextKey(current.Representative),
        BuildStrictTextKey(current.ContactNumber));
}

static string BuildBusinessDuplicateCustomerKey(LocalCustomer current)
{
    if (string.IsNullOrWhiteSpace(current.NameOriginal) || string.IsNullOrWhiteSpace(current.BusinessNumber))
        return string.Empty;

    return string.Join('|',
        BuildStrictTextKey(current.TenantCode),
        BuildStrictTextKey(current.NameOriginal),
        BuildStrictTextKey(current.BusinessNumber),
        BuildStrictTextKey(current.ResponsibleOfficeCode),
        BuildStrictTextKey(current.TradeType));
}

static string BuildItemDuplicateKey(LocalItem current)
    => string.Join('|',
        BuildStrictTextKey(current.TenantCode),
        BuildStrictTextKey(current.OfficeCode),
        BuildStrictTextKey(current.NameOriginal),
        BuildStrictTextKey(current.NameMatchKey),
        BuildStrictTextKey(current.SpecificationOriginal),
        BuildStrictTextKey(current.SpecificationMatchKey),
        BuildStrictTextKey(current.CategoryName),
        BuildStrictTextKey(current.ItemKind),
        BuildStrictTextKey(current.TrackingType),
        BuildStrictTextKey(current.Unit),
        BuildDecimalKey(current.BoxQuantity),
        BuildStrictTextKey(current.StorageLocation),
        BuildDecimalKey(current.CurrentStock),
        BuildDecimalKey(current.SafetyStock),
        BuildDecimalKey(current.PurchasePrice),
        BuildDecimalKey(current.SalePrice),
        BuildDecimalKey(current.RetailPrice),
        BuildDecimalKey(current.PriceGradeA),
        BuildDecimalKey(current.PriceGradeB),
        BuildDecimalKey(current.PriceGradeC),
        BuildDateKey(current.LastPurchaseDate),
        BuildDateKey(current.LastSaleDate),
        BuildStrictTextKey(current.SimpleMemo),
        current.IsRental ? "1" : "0",
        current.IsSale ? "1" : "0",
        BuildStrictTextKey(current.SerialNumber),
        BuildStrictTextKey(current.MaterialNumber),
        BuildStrictTextKey(current.InstallLocation),
        BuildDateKey(current.RentalStartDate),
        BuildDateKey(current.RentalEndDate),
        string.Equals(current.SimpleMemo, RentalStateService.AutoCreatedRentalItemMemo, StringComparison.Ordinal)
            ? string.Empty
            : BuildStrictTextKey(current.Notes));

static string BuildRentalBillingProfileDuplicateKey(LocalRentalBillingProfile current)
    => RentalDuplicateNormalizer.BuildRentalBillingProfileDuplicateKey(
        current.ManagementCompanyCode,
        current.ResponsibleOfficeCode,
        current.CustomerId,
        current.BusinessNumber,
        current.CustomerName,
        current.BillingType,
        current.BillingAdvanceMode,
        current.BillingDay,
        current.BillingCycleMonths,
        current.BillingMethod);

static string BuildRentalAssetDuplicateKey(LocalRentalAsset current)
    => RentalDuplicateNormalizer.BuildRentalAssetDuplicateKey(
        current.CustomerName,
        current.CurrentCustomerName,
        current.InstallSiteName,
        current.InstallLocation,
        current.ItemCategoryName,
        current.ItemName,
        current.Manufacturer,
        current.MachineNumber,
        current.MonthlyFee,
        current.ContractMonths,
        current.AssetStatus);

static string BuildStrictTextKey(string? value)
    => (value ?? string.Empty).Trim().ToUpperInvariant();

static string BuildDecimalKey(decimal value)
    => value.ToString(CultureInfo.InvariantCulture);

static string BuildDateKey(DateOnly? value)
    => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
