using Microsoft.EntityFrameworkCore;
using System.Globalization;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private const string CompanyProfileAssignmentSettingPrefix = "CompanyProfile.Assigned.";

    private static async Task MergeDuplicateCompanyProfilesAsync(LocalDbContext db)
    {
        var profiles = await db.CompanyProfiles.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();
        if (profiles.Count == 0)
            return;

        var groups = profiles
            .GroupBy(BuildCompanyProfileDuplicateKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (groups.Count == 0)
            return;

        var settings = await db.Settings.ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            var canonical = group
                .OrderByDescending(current => current.IsDefaultForOffice)
                .ThenByDescending(current => !string.IsNullOrWhiteSpace(current.ProfileName))
                .ThenByDescending(CountFilledCompanyProfileValues)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var changed = false;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                changed |= MergeCompanyProfileValues(canonical, duplicate);

                var duplicateIdText = duplicate.Id.ToString("D");
                foreach (var setting in settings.Where(current =>
                             current.Key.StartsWith(CompanyProfileAssignmentSettingPrefix, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals((current.Value ?? string.Empty).Trim(), duplicateIdText, StringComparison.OrdinalIgnoreCase)))
                {
                    setting.Value = canonical.Id.ToString("D");
                }

                db.CompanyProfiles.Remove(duplicate);
            }

            if (changed)
                PreserveDirtyStateForStartupMaintenance(canonical, now);
        }
    }

    private static async Task MergeDuplicateItemsAsync(LocalDbContext db)
    {
        var items = await db.Items.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();
        if (items.Count == 0)
            return;

        var invoiceLines = await db.InvoiceLines.IgnoreQueryFilters().ToListAsync();
        var invoiceLineSerials = await db.InvoiceLineSerials.ToListAsync();
        var rentalAssets = await db.RentalAssets.IgnoreQueryFilters().ToListAsync();
        var itemWarehouseStocks = await db.ItemWarehouseStocks.ToListAsync();
        var serialLedgers = await db.SerialLedgers.ToListAsync();
        var inventoryTransferLines = await db.InventoryTransferLines.IgnoreQueryFilters().ToListAsync();
        var inventoryMovements = await db.InventoryMovements.ToListAsync();
        var stockLayers = await db.StockLayers.ToListAsync();
        var rentalBillingProfiles = await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();

        var invoiceLineCounts = invoiceLines.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var invoiceLineSerialCounts = invoiceLineSerials.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var rentalAssetCounts = rentalAssets.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var warehouseStockCounts = itemWarehouseStocks.GroupBy(current => current.ItemId)
            .ToDictionary(group => group.Key, group => group.Count());
        var serialLedgerCounts = serialLedgers.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var inventoryTransferLineCounts = inventoryTransferLines.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var inventoryMovementCounts = inventoryMovements.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
            .GroupBy(current => current.ItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var stockLayerCounts = stockLayers.Where(current => current.ItemId.HasValue && current.ItemId.Value != Guid.Empty)
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
                    invoiceLineSerialCounts,
                    rentalAssetCounts,
                    warehouseStockCounts,
                    serialLedgerCounts,
                    inventoryTransferLineCounts,
                    inventoryMovementCounts,
                    stockLayerCounts,
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

                foreach (var serial in invoiceLineSerials.Where(current => current.ItemId == duplicate.Id))
                    serial.ItemId = canonical.Id;

                foreach (var asset in rentalAssets.Where(current => current.ItemId == duplicate.Id))
                {
                    asset.ItemId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(asset, now);
                }

                RemapStartupRentalBillingTemplateItemReferences(rentalBillingProfiles, canonical, duplicate, now);

                foreach (var ledger in serialLedgers.Where(current => current.ItemId == duplicate.Id))
                    ledger.ItemId = canonical.Id;

                foreach (var line in inventoryTransferLines.Where(current => current.ItemId == duplicate.Id))
                    line.ItemId = canonical.Id;

                foreach (var movement in inventoryMovements.Where(current => current.ItemId == duplicate.Id))
                    movement.ItemId = canonical.Id;

                foreach (var layer in stockLayers.Where(current => current.ItemId == duplicate.Id))
                    layer.ItemId = canonical.Id;

                foreach (var stock in itemWarehouseStocks.Where(current => current.ItemId == duplicate.Id).ToList())
                {
                    var warehouseKey = BuildStrictTextKey(stock.WarehouseCode);
                    if (warehouseLookup.TryGetValue(warehouseKey, out var canonicalStock))
                    {
                        canonicalStock.Quantity += stock.Quantity;
                        if (stock.UpdatedAtUtc > canonicalStock.UpdatedAtUtc)
                            canonicalStock.UpdatedAtUtc = stock.UpdatedAtUtc;
                        db.ItemWarehouseStocks.Remove(stock);
                        continue;
                    }

                    var migratedStock = new LocalItemWarehouseStock
                    {
                        ItemId = canonical.Id,
                        WarehouseCode = stock.WarehouseCode,
                        Quantity = stock.Quantity,
                        UpdatedAtUtc = stock.UpdatedAtUtc
                    };
                    warehouseLookup[warehouseKey] = migratedStock;
                    itemWarehouseStocks.Add(migratedStock);
                    db.ItemWarehouseStocks.Add(migratedStock);
                    db.ItemWarehouseStocks.Remove(stock);
                }

                db.Items.Remove(duplicate);
            }
        }
    }

    private static async Task MergeBusinessDuplicateCustomersAsync(LocalDbContext db)
    {
        var customers = await db.Customers.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();
        if (customers.Count == 0)
            return;

        var contracts = await db.CustomerContracts.IgnoreQueryFilters().ToListAsync();
        var invoices = await db.Invoices.IgnoreQueryFilters().ToListAsync();
        var transactions = await db.Transactions.IgnoreQueryFilters().ToListAsync();
        var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();
        var assets = await db.RentalAssets.IgnoreQueryFilters().ToListAsync();
        var assignmentHistories = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().ToListAsync();

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
                .ThenByDescending(current => BuildCustomerFullAddress(current).Length)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var changed = false;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                foreach (var contract in contracts.Where(current => current.CustomerId == duplicate.Id))
                {
                    contract.CustomerId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(contract, now);
                }

                foreach (var invoice in invoices.Where(current => current.CustomerId == duplicate.Id))
                {
                    invoice.CustomerId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(invoice, now);
                }

                foreach (var transaction in transactions.Where(current => current.CustomerId == duplicate.Id))
                {
                    transaction.CustomerId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(transaction, now);
                }

                foreach (var profile in profiles.Where(current => current.CustomerId == duplicate.Id))
                {
                    profile.CustomerId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(profile, now);
                }

                foreach (var asset in assets.Where(current => current.CustomerId == duplicate.Id))
                {
                    asset.CustomerId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(asset, now);
                }

                foreach (var history in assignmentHistories.Where(current => current.CustomerId == duplicate.Id))
                {
                    history.CustomerId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(history, now);
                }

                changed |= MergeBusinessCustomerValues(canonical, duplicate);
                db.Customers.Remove(duplicate);
            }

            if (changed)
                PreserveDirtyStateForStartupMaintenance(canonical, now);
        }
    }

    private static string BuildCompanyProfileDuplicateKey(LocalCompanyProfile current)
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

    private static string BuildItemDuplicateKey(LocalItem current)
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
            BuildBooleanKey(current.IsRental),
            BuildBooleanKey(current.IsSale),
            BuildStrictTextKey(current.SerialNumber),
            BuildStrictTextKey(current.MaterialNumber),
            BuildStrictTextKey(current.InstallLocation),
            BuildDateKey(current.RentalStartDate),
            BuildDateKey(current.RentalEndDate),
            BuildItemNotesDuplicateKey(current));

    private static string BuildItemNotesDuplicateKey(LocalItem current)
        => string.Equals(current.SimpleMemo, RentalStateService.AutoCreatedRentalItemMemo, StringComparison.Ordinal)
            ? string.Empty
            : BuildStrictTextKey(current.Notes);

    private static string BuildBusinessDuplicateCustomerKey(LocalCustomer current)
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

    private static int CountFilledCompanyProfileValues(LocalCompanyProfile current)
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
        IReadOnlyDictionary<Guid, int> invoiceLineSerialCounts,
        IReadOnlyDictionary<Guid, int> rentalAssetCounts,
        IReadOnlyDictionary<Guid, int> warehouseStockCounts,
        IReadOnlyDictionary<Guid, int> serialLedgerCounts,
        IReadOnlyDictionary<Guid, int> inventoryTransferLineCounts,
        IReadOnlyDictionary<Guid, int> inventoryMovementCounts,
        IReadOnlyDictionary<Guid, int> stockLayerCounts,
        IReadOnlyDictionary<Guid, int> rentalBillingTemplateCounts)
        => invoiceLineCounts.GetValueOrDefault(itemId)
           + invoiceLineSerialCounts.GetValueOrDefault(itemId)
           + rentalAssetCounts.GetValueOrDefault(itemId)
           + warehouseStockCounts.GetValueOrDefault(itemId)
           + serialLedgerCounts.GetValueOrDefault(itemId)
           + inventoryTransferLineCounts.GetValueOrDefault(itemId)
           + inventoryMovementCounts.GetValueOrDefault(itemId)
           + stockLayerCounts.GetValueOrDefault(itemId)
           + rentalBillingTemplateCounts.GetValueOrDefault(itemId);

    private static Dictionary<Guid, int> CountRentalBillingTemplateItemReferences(
        IReadOnlyList<LocalRentalBillingProfile> profiles)
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
        IReadOnlyList<LocalRentalBillingProfile> profiles,
        LocalItem canonical,
        LocalItem duplicate,
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
            PreserveDirtyStateForStartupMaintenance(profile, now);
        }
    }

    private static int CountFilledCustomerValues(LocalCustomer current)
    {
        return CountFilledStrings(
                   current.NameOriginal,
                   current.NameMatchKey,
                   current.TenantCode,
                   current.TradeType,
                   current.Department,
                   current.ContactPerson,
                   current.BusinessNumber,
                   current.Address,
                   current.DetailAddress,
                   current.Phone,
                   current.MobilePhone,
                   current.FaxNumber,
                   current.Email,
                   current.HomePage,
                   current.Representative,
                   current.BusinessType,
                   current.BusinessItem,
                   current.Recipient,
                   current.PriceGrade,
                   current.Notes,
                   current.ResponsibleOfficeCode)
               + (current.CustomerMasterId.HasValue && current.CustomerMasterId.Value != Guid.Empty ? 1 : 0)
               + (current.CategoryId.HasValue && current.CategoryId.Value != Guid.Empty ? 1 : 0);
    }

    private static bool MergeCompanyProfileValues(LocalCompanyProfile canonical, LocalCompanyProfile duplicate)
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

    private static bool MergeBusinessCustomerValues(LocalCustomer canonical, LocalCustomer duplicate)
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
        changed |= TryAssignString(() => canonical.TradeType, value => canonical.TradeType = value, duplicate.TradeType);
        changed |= TryAssignString(() => canonical.Department, value => canonical.Department = value, duplicate.Department, preferLonger: true);
        changed |= TryAssignString(() => canonical.ContactPerson, value => canonical.ContactPerson = value, duplicate.ContactPerson, preferLonger: true);
        changed |= TryAssignString(() => canonical.BusinessNumber, value => canonical.BusinessNumber = value, duplicate.BusinessNumber);
        changed |= TryAssignString(() => canonical.Address, value => canonical.Address = value, duplicate.Address, preferLonger: true);
        changed |= TryAssignString(() => canonical.DetailAddress, value => canonical.DetailAddress = value, duplicate.DetailAddress, preferLonger: true);
        changed |= TryAssignString(() => canonical.Phone, value => canonical.Phone = value, duplicate.Phone);
        changed |= TryAssignString(() => canonical.MobilePhone, value => canonical.MobilePhone = value, duplicate.MobilePhone);
        changed |= TryAssignString(() => canonical.FaxNumber, value => canonical.FaxNumber = value, duplicate.FaxNumber);
        changed |= TryAssignString(() => canonical.Email, value => canonical.Email = value, duplicate.Email);
        changed |= TryAssignString(() => canonical.HomePage, value => canonical.HomePage = value, duplicate.HomePage);
        changed |= TryAssignString(() => canonical.Representative, value => canonical.Representative = value, duplicate.Representative, preferLonger: true);
        changed |= TryAssignString(() => canonical.BusinessType, value => canonical.BusinessType = value, duplicate.BusinessType, preferLonger: true);
        changed |= TryAssignString(() => canonical.BusinessItem, value => canonical.BusinessItem = value, duplicate.BusinessItem, preferLonger: true);
        changed |= TryAssignString(() => canonical.Recipient, value => canonical.Recipient = value, duplicate.Recipient, preferLonger: true);
        changed |= TryAssignString(() => canonical.PriceGrade, value => canonical.PriceGrade = value, duplicate.PriceGrade);
        changed |= TryAssignString(() => canonical.Notes, value => canonical.Notes = value, duplicate.Notes, preferLonger: true);
        changed |= TryAssignString(() => canonical.ResponsibleOfficeCode, value => canonical.ResponsibleOfficeCode = value, duplicate.ResponsibleOfficeCode);

        return changed;
    }

    private static bool CanAutoMergeBusinessCustomerGroup(IReadOnlyCollection<LocalCustomer> group)
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

    private static bool AreBusinessCustomerAddressesCompatible(IEnumerable<LocalCustomer> customers)
    {
        var addresses = customers
            .Select(BuildCustomerFullAddress)
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

    private static string BuildCustomerFullAddress(LocalCustomer current)
        => NormalizeComparableText($"{current.Address} {current.DetailAddress}");

    private static string BuildBusinessCustomerLocationKey(LocalCustomer current)
        => string.Join('|',
            BuildStrictTextKey(TryExtractCustomerNoteValue(current.Notes, "종사업장")),
            BuildStrictTextKey(BuildCustomerFullAddress(current)));

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

    private static string NormalizeComparableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Concat(value.Trim().ToUpperInvariant().Where(current => !char.IsWhiteSpace(current)));
    }

    private static string BuildDecimalKey(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static string BuildDateKey(DateOnly? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string BuildBooleanKey(bool value)
        => value ? "1" : "0";
}
