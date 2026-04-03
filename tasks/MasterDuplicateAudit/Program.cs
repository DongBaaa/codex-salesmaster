using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

await using var db = new LocalDbContext();

var items = await db.Items.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
var companyProfiles = await db.CompanyProfiles.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
var customers = await db.Customers.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
var rentalBillingProfiles = await db.RentalBillingProfiles.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
var rentalAssets = await db.RentalAssets.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();

var summary = new
{
    GeneratedAtUtc = DateTime.UtcNow,
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

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ManualAudit", "outputs");
Directory.CreateDirectory(outputDirectory);
var outputPath = Path.GetFullPath(Path.Combine(outputDirectory, "master-duplicate-audit.json"));
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(outputPath);

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
        BuildStrictTextKey(current.Notes));

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
