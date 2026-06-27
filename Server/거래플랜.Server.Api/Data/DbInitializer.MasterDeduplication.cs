using Microsoft.EntityFrameworkCore;
using System.Globalization;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private const string AutoCreatedRentalItemMemo = "렌탈 자산/설치현황 자동 동기화 생성";

    private static async Task MergeDuplicateCompanyProfilesAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var profiles = await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync(cancellationToken);
        if (profiles.Count == 0)
            return;

        var groups = profiles
            .GroupBy(BuildCompanyProfileDuplicateKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (groups.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            var canonical = group
                .OrderByDescending(CountFilledCompanyProfileValues)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var changed = false;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                changed |= MergeCompanyProfileValues(canonical, duplicate);
                dbContext.CompanyProfiles.Remove(duplicate);
            }

            if (changed)
                TouchTrackedEntity(canonical, now);
        }
    }

    private static async Task MergeDuplicateItemsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var items = await dbContext.Items.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
            return;

        var invoiceLines = await dbContext.InvoiceLines.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var rentalAssets = await dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var rentalBillingProfiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var itemWarehouseStocks = await dbContext.ItemWarehouseStocks.ToListAsync(cancellationToken);
        var inventoryTransferLines = await dbContext.InventoryTransferLines.IgnoreQueryFilters().ToListAsync(cancellationToken);

        var invoiceLineCounts = invoiceLines.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var rentalAssetCounts = rentalAssets.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var warehouseStockCounts = itemWarehouseStocks.GroupBy(current => current.ItemId)
            .ToDictionary(group => group.Key, group => group.Count());
        var inventoryTransferLineCounts = inventoryTransferLines.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var rentalBillingTemplateCounts = CountRentalBillingTemplateItemReferences(rentalBillingProfiles);

        var groups = items
            .GroupBy(BuildItemDuplicateKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (groups.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            var canonical = group
                .OrderByDescending(current => CountItemReferenceScore(
                    current.Id,
                    invoiceLineCounts,
                    rentalAssetCounts,
                    warehouseStockCounts,
                    inventoryTransferLineCounts,
                    rentalBillingTemplateCounts))
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var warehouseLookup = itemWarehouseStocks
                .Where(current => current.ItemId == canonical.Id)
                .GroupBy(current => BuildStrictTextKey(current.WarehouseCode), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                foreach (var line in invoiceLines.Where(current => current.ItemId == duplicate.Id))
                    line.ItemId = canonical.Id;

                foreach (var asset in rentalAssets.Where(current => current.ItemId == duplicate.Id))
                {
                    asset.ItemId = canonical.Id;
                    TouchTrackedEntity(asset, now);
                }

                RemapStartupRentalBillingTemplateItemReferences(rentalBillingProfiles, canonical, duplicate, now);

                foreach (var line in inventoryTransferLines.Where(current => current.ItemId == duplicate.Id))
                    line.ItemId = canonical.Id;

                foreach (var stock in itemWarehouseStocks.Where(current => current.ItemId == duplicate.Id).ToList())
                {
                    var warehouseKey = BuildStrictTextKey(stock.WarehouseCode);
                    if (warehouseLookup.TryGetValue(warehouseKey, out var canonicalStock))
                    {
                        canonicalStock.Quantity += stock.Quantity;
                        if (stock.UpdatedAtUtc > canonicalStock.UpdatedAtUtc)
                            canonicalStock.UpdatedAtUtc = stock.UpdatedAtUtc;
                        dbContext.ItemWarehouseStocks.Remove(stock);
                        continue;
                    }

                    var migratedStock = new ItemWarehouseStock
                    {
                        ItemId = canonical.Id,
                        WarehouseCode = stock.WarehouseCode,
                        Quantity = stock.Quantity,
                        UpdatedAtUtc = stock.UpdatedAtUtc
                    };
                    warehouseLookup[warehouseKey] = migratedStock;
                    itemWarehouseStocks.Add(migratedStock);
                    dbContext.ItemWarehouseStocks.Add(migratedStock);
                    dbContext.ItemWarehouseStocks.Remove(stock);
                }

                dbContext.Items.Remove(duplicate);
            }
        }
    }

    private static async Task MergeBusinessDuplicateCustomersAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var customers = await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync(cancellationToken);
        if (customers.Count == 0)
            return;

        var contracts = await dbContext.CustomerContracts.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var invoices = await dbContext.Invoices.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var transactions = await dbContext.Transactions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var assets = await dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var assignmentHistories = await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().ToListAsync(cancellationToken);

        var groups = customers
            .GroupBy(BuildBusinessDuplicateCustomerKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Where(group => CanAutoMergeBusinessCustomerGroup(group.ToList()))
            .ToList();
        if (groups.Count == 0)
            return;

        var contractCounts = contracts.GroupBy(current => current.CustomerId).ToDictionary(group => group.Key, group => group.Count());
        var invoiceCounts = invoices.GroupBy(current => current.CustomerId).ToDictionary(group => group.Key, group => group.Count());
        var transactionCounts = transactions.GroupBy(current => current.CustomerId).ToDictionary(group => group.Key, group => group.Count());
        var profileCounts = profiles.Where(current => current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
            .GroupBy(current => current.CustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var assetCounts = assets.Where(current => current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
            .GroupBy(current => current.CustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var assignmentHistoryCounts = assignmentHistories.Where(current => current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
            .GroupBy(current => current.CustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            var canonical = group
                .OrderByDescending(current => CountCustomerReferenceScore(current.Id, contractCounts, invoiceCounts, transactionCounts, profileCounts, assetCounts, assignmentHistoryCounts))
                .ThenByDescending(CountFilledCustomerValues)
                .ThenByDescending(current => (current.Notes ?? string.Empty).Length)
                .ThenByDescending(current => NormalizeComparableText(current.Address).Length)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var changed = false;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                foreach (var contract in contracts.Where(current => current.CustomerId == duplicate.Id))
                {
                    contract.CustomerId = canonical.Id;
                    TouchTrackedEntity(contract, now);
                }

                foreach (var invoice in invoices.Where(current => current.CustomerId == duplicate.Id))
                {
                    invoice.CustomerId = canonical.Id;
                    TouchTrackedEntity(invoice, now);
                }

                foreach (var transaction in transactions.Where(current => current.CustomerId == duplicate.Id))
                {
                    transaction.CustomerId = canonical.Id;
                    TouchTrackedEntity(transaction, now);
                }

                foreach (var profile in profiles.Where(current => current.CustomerId == duplicate.Id))
                {
                    profile.CustomerId = canonical.Id;
                    TouchTrackedEntity(profile, now);
                }

                foreach (var asset in assets.Where(current => current.CustomerId == duplicate.Id))
                {
                    asset.CustomerId = canonical.Id;
                    TouchTrackedEntity(asset, now);
                }

                foreach (var history in assignmentHistories.Where(current => current.CustomerId == duplicate.Id))
                {
                    history.CustomerId = canonical.Id;
                    TouchTrackedEntity(history, now);
                }

                changed |= MergeBusinessCustomerValues(canonical, duplicate);
                dbContext.Customers.Remove(duplicate);
            }

            if (changed)
                TouchTrackedEntity(canonical, now);
        }
    }

    private static string BuildCompanyProfileDuplicateKey(CompanyProfile current)
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

    private static string BuildItemDuplicateKey(Item current)
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
            BuildDecimalKey(current.CurrentStock),
            BuildDecimalKey(current.SafetyStock),
            BuildDecimalKey(current.PurchasePrice),
            BuildDecimalKey(current.SalePrice),
            BuildDecimalKey(current.RetailPrice),
            BuildDecimalKey(current.PriceGradeA),
            BuildDecimalKey(current.PriceGradeB),
            BuildDecimalKey(current.PriceGradeC),
            BuildStrictTextKey(current.SimpleMemo),
            BuildBooleanKey(current.IsRental),
            BuildBooleanKey(current.IsSale),
            BuildStrictTextKey(current.SerialNumber),
            BuildStrictTextKey(current.MaterialNumber),
            BuildStrictTextKey(current.InstallLocation),
            BuildDateKey(current.RentalStartDate),
            BuildDateKey(current.RentalEndDate),
            string.Equals(current.SimpleMemo, AutoCreatedRentalItemMemo, StringComparison.Ordinal)
                ? string.Empty
                : BuildStrictTextKey(current.Notes));

    private static string BuildBusinessDuplicateCustomerKey(Customer current)
    {
        if (string.IsNullOrWhiteSpace(current.NameOriginal) || string.IsNullOrWhiteSpace(current.BusinessNumber))
            return string.Empty;

        return string.Join('|',
            BuildStrictTextKey(current.TenantCode),
            BuildStrictTextKey(current.NameOriginal),
            BuildStrictTextKey(current.BusinessNumber),
            BuildBusinessCustomerLocationKey(current),
            BuildStrictTextKey(current.ResponsibleOfficeCode),
            BuildStrictTextKey(current.TradeType));
    }

    private static int CountFilledCompanyProfileValues(CompanyProfile current)
    {
        return CountFilledStrings(
                   current.ProfileName,
                   current.OfficeCode,
                   current.TradeName,
                   current.Representative,
                   current.BusinessNumber,
                   current.BusinessType,
                   current.BusinessItem,
                   current.Address,
                   current.ContactNumber,
                   current.FaxNumber,
                   current.Email,
                   current.BankAccountText)
               + (current.StampImage?.Length > 0 ? 1 : 0)
               + (current.IsDefaultForOffice ? 1 : 0)
               + (current.IsActive ? 1 : 0);
    }

    private static int CountCustomerReferenceScore(
        Guid customerId,
        IReadOnlyDictionary<Guid, int> contractCounts,
        IReadOnlyDictionary<Guid, int> invoiceCounts,
        IReadOnlyDictionary<Guid, int> transactionCounts,
        IReadOnlyDictionary<Guid, int> profileCounts,
        IReadOnlyDictionary<Guid, int> assetCounts,
        IReadOnlyDictionary<Guid, int> assignmentHistoryCounts)
        => contractCounts.GetValueOrDefault(customerId)
           + invoiceCounts.GetValueOrDefault(customerId)
           + transactionCounts.GetValueOrDefault(customerId)
           + profileCounts.GetValueOrDefault(customerId)
           + assetCounts.GetValueOrDefault(customerId)
           + assignmentHistoryCounts.GetValueOrDefault(customerId);

    private static int CountItemReferenceScore(
        Guid itemId,
        IReadOnlyDictionary<Guid, int> invoiceLineCounts,
        IReadOnlyDictionary<Guid, int> rentalAssetCounts,
        IReadOnlyDictionary<Guid, int> warehouseStockCounts,
        IReadOnlyDictionary<Guid, int> inventoryTransferLineCounts,
        IReadOnlyDictionary<Guid, int> rentalBillingTemplateCounts)
        => invoiceLineCounts.GetValueOrDefault(itemId)
           + rentalAssetCounts.GetValueOrDefault(itemId)
           + warehouseStockCounts.GetValueOrDefault(itemId)
           + inventoryTransferLineCounts.GetValueOrDefault(itemId)
           + rentalBillingTemplateCounts.GetValueOrDefault(itemId);

    private static Dictionary<Guid, int> CountRentalBillingTemplateItemReferences(
        IReadOnlyList<RentalBillingProfile> profiles)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var profile in profiles)
        {
            foreach (var item in DeserializeBillingTemplateItems(profile.BillingTemplateJson))
            {
                var catalogItemId = item.CatalogItemId.GetValueOrDefault();
                if (catalogItemId == Guid.Empty)
                    continue;

                counts[catalogItemId] = counts.GetValueOrDefault(catalogItemId) + 1;
            }
        }

        return counts;
    }

    private static void RemapStartupRentalBillingTemplateItemReferences(
        IReadOnlyList<RentalBillingProfile> profiles,
        Item canonical,
        Item duplicate,
        DateTime now)
    {
        foreach (var profile in profiles)
        {
            var templateItems = DeserializeBillingTemplateItems(profile.BillingTemplateJson);
            if (templateItems.Count == 0)
                continue;

            var changed = false;
            foreach (var item in templateItems)
            {
                if (item.CatalogItemId.GetValueOrDefault() != duplicate.Id)
                    continue;

                item.CatalogItemId = canonical.Id;
                if (string.IsNullOrWhiteSpace(item.DisplayItemName) ||
                    string.Equals(
                        RentalCatalogValueNormalizer.NormalizeLooseKey(item.DisplayItemName),
                        RentalCatalogValueNormalizer.NormalizeLooseKey(duplicate.NameOriginal),
                        StringComparison.OrdinalIgnoreCase))
                {
                    item.DisplayItemName = canonical.NameOriginal;
                }

                changed = true;
            }

            if (!changed)
                continue;

            profile.BillingTemplateJson = SerializeBillingTemplateItems(templateItems);
            TouchTrackedEntity(profile, now);
        }
    }

    private static int CountFilledCustomerValues(Customer current)
    {
        return CountFilledStrings(
                   current.NameOriginal,
                   current.NameMatchKey,
                   current.TenantCode,
                   current.OfficeCode,
                   current.TradeType,
                   current.Department,
                   current.ContactPerson,
                   current.Representative,
                   current.BusinessNumber,
                   current.BusinessType,
                   current.BusinessItem,
                   current.Address,
                   current.Phone,
                   current.Email,
                   current.Notes)
               + (current.CustomerMasterId.HasValue && current.CustomerMasterId.Value != Guid.Empty ? 1 : 0)
               + (current.CategoryId.HasValue && current.CategoryId.Value != Guid.Empty ? 1 : 0);
    }

    private static bool MergeCompanyProfileValues(CompanyProfile canonical, CompanyProfile duplicate)
    {
        var changed = false;
        changed |= TryAssignString(() => canonical.ProfileName, value => canonical.ProfileName = value, duplicate.ProfileName, preferLonger: true);
        changed |= TryAssignString(() => canonical.OfficeCode, value => canonical.OfficeCode = value, duplicate.OfficeCode);
        changed |= TryAssignString(() => canonical.TradeName, value => canonical.TradeName = value, duplicate.TradeName);
        changed |= TryAssignString(() => canonical.Representative, value => canonical.Representative = value, duplicate.Representative);
        changed |= TryAssignString(() => canonical.BusinessNumber, value => canonical.BusinessNumber = value, duplicate.BusinessNumber);
        changed |= TryAssignString(() => canonical.BusinessType, value => canonical.BusinessType = value, duplicate.BusinessType, preferLonger: true);
        changed |= TryAssignString(() => canonical.BusinessItem, value => canonical.BusinessItem = value, duplicate.BusinessItem, preferLonger: true);
        changed |= TryAssignString(() => canonical.Address, value => canonical.Address = value, duplicate.Address, preferLonger: true);
        changed |= TryAssignString(() => canonical.ContactNumber, value => canonical.ContactNumber = value, duplicate.ContactNumber);
        changed |= TryAssignString(() => canonical.FaxNumber, value => canonical.FaxNumber = value, duplicate.FaxNumber);
        changed |= TryAssignString(() => canonical.Email, value => canonical.Email = value, duplicate.Email);
        changed |= TryAssignString(() => canonical.BankAccountText, value => canonical.BankAccountText = value, duplicate.BankAccountText, preferLonger: true);

        if ((canonical.StampImage is null || canonical.StampImage.Length == 0) &&
            duplicate.StampImage is { Length: > 0 })
        {
            canonical.StampImage = duplicate.StampImage;
            changed = true;
        }

        if (!canonical.IsDefaultForOffice && duplicate.IsDefaultForOffice)
        {
            canonical.IsDefaultForOffice = true;
            changed = true;
        }

        if (!canonical.IsActive && duplicate.IsActive)
        {
            canonical.IsActive = true;
            changed = true;
        }

        return changed;
    }

    private static bool MergeBusinessCustomerValues(Customer canonical, Customer duplicate)
    {
        var changed = false;

        if ((!canonical.CustomerMasterId.HasValue || canonical.CustomerMasterId.Value == Guid.Empty) &&
            duplicate.CustomerMasterId.HasValue &&
            duplicate.CustomerMasterId.Value != Guid.Empty)
        {
            canonical.CustomerMasterId = duplicate.CustomerMasterId;
            changed = true;
        }

        if ((!canonical.CategoryId.HasValue || canonical.CategoryId.Value == Guid.Empty) &&
            duplicate.CategoryId.HasValue &&
            duplicate.CategoryId.Value != Guid.Empty)
        {
            canonical.CategoryId = duplicate.CategoryId;
            changed = true;
        }

        changed |= TryAssignString(() => canonical.NameOriginal, value => canonical.NameOriginal = value, duplicate.NameOriginal);
        changed |= TryAssignString(() => canonical.NameMatchKey, value => canonical.NameMatchKey = value, duplicate.NameMatchKey);
        changed |= TryAssignString(() => canonical.TenantCode, value => canonical.TenantCode = value, duplicate.TenantCode);
        changed |= TryAssignString(() => canonical.OfficeCode, value => canonical.OfficeCode = value, duplicate.OfficeCode);
        changed |= TryAssignString(() => canonical.TradeType, value => canonical.TradeType = value, duplicate.TradeType);
        changed |= TryAssignString(() => canonical.Department, value => canonical.Department = value, duplicate.Department, preferLonger: true);
        changed |= TryAssignString(() => canonical.ContactPerson, value => canonical.ContactPerson = value, duplicate.ContactPerson, preferLonger: true);
        changed |= TryAssignString(() => canonical.Representative, value => canonical.Representative = value, duplicate.Representative, preferLonger: true);
        changed |= TryAssignString(() => canonical.BusinessNumber, value => canonical.BusinessNumber = value, duplicate.BusinessNumber);
        changed |= TryAssignString(() => canonical.BusinessType, value => canonical.BusinessType = value, duplicate.BusinessType, preferLonger: true);
        changed |= TryAssignString(() => canonical.BusinessItem, value => canonical.BusinessItem = value, duplicate.BusinessItem, preferLonger: true);
        changed |= TryAssignString(() => canonical.Address, value => canonical.Address = value, duplicate.Address, preferLonger: true);
        changed |= TryAssignString(() => canonical.Phone, value => canonical.Phone = value, duplicate.Phone);
        changed |= TryAssignString(() => canonical.Email, value => canonical.Email = value, duplicate.Email);
        changed |= TryAssignString(() => canonical.Notes, value => canonical.Notes = value, duplicate.Notes, preferLonger: true);

        return changed;
    }

    private static bool CanAutoMergeBusinessCustomerGroup(IReadOnlyCollection<Customer> group)
    {
        if (group.Count <= 1 || group.Count > 2)
            return false;

        if (group.Any(current => string.IsNullOrWhiteSpace(current.NameOriginal) || string.IsNullOrWhiteSpace(current.BusinessNumber)))
            return false;

        return HasAtMostOneDistinctNonEmpty(group.Select(current => NormalizeComparableText(current.Phone)))
               && HasAtMostOneDistinctNonEmpty(group.Select(current => NormalizeComparableText(current.Email)))
               && HasAtMostOneDistinctNonEmpty(group.Select(current => NormalizeComparableText(current.Representative)))
               && HasAtMostOneDistinctNonEmpty(group.Select(current => NormalizeComparableText(current.BusinessType)))
               && HasAtMostOneDistinctNonEmpty(group.Select(current => NormalizeComparableText(current.BusinessItem)))
               && HasAtMostOneDistinctNonEmpty(group.Select(current => current.CustomerMasterId?.ToString("D") ?? string.Empty))
               && HasAtMostOneDistinctNonEmpty(group.Select(current => current.CategoryId?.ToString("D") ?? string.Empty))
               && AreBusinessCustomerAddressesCompatible(group);
    }

    private static bool HasAtMostOneDistinctNonEmpty(IEnumerable<string?> values)
        => values
            .Select(current => (current ?? string.Empty).Trim())
            .Where(current => !string.IsNullOrWhiteSpace(current))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() <= 1;

    private static bool AreBusinessCustomerAddressesCompatible(IEnumerable<Customer> customers)
    {
        var addresses = customers
            .Select(current => NormalizeComparableText(current.Address))
            .Where(current => !string.IsNullOrWhiteSpace(current))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (addresses.Count <= 1)
            return true;

        for (var index = 0; index < addresses.Count; index++)
        {
            for (var compareIndex = index + 1; compareIndex < addresses.Count; compareIndex++)
            {
                var left = addresses[index];
                var right = addresses[compareIndex];
                if (string.Equals(left, right, StringComparison.Ordinal))
                    continue;

                if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
                    continue;

                return false;
            }
        }

        return true;
    }

    private static string NormalizeComparableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Concat(value.Trim().ToUpperInvariant().Where(current => !char.IsWhiteSpace(current)));
    }

    private static string BuildBusinessCustomerLocationKey(Customer current)
        => string.Join('|',
            BuildStrictTextKey(TryExtractCustomerNoteValue(current.Notes, "종사업장")),
            BuildStrictTextKey(NormalizeComparableText(current.Address)));

    private static string TryExtractCustomerNoteValue(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes) || string.IsNullOrWhiteSpace(label))
            return string.Empty;

        foreach (var line in notes
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(current => current.Trim()))
        {
            if (line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
                return line[(label.Length + 1)..].Trim();
        }

        return string.Empty;
    }

    private static string BuildDecimalKey(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static string BuildDateKey(DateOnly? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string BuildBooleanKey(bool value)
        => value ? "1" : "0";
}
