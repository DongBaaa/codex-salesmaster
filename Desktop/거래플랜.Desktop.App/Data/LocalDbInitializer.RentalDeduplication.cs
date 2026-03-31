using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static async Task MergeDuplicateCustomerMastersAsync(LocalDbContext db)
    {
        var masters = await db.CustomerMasters.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();
        if (masters.Count == 0)
            return;

        var customers = await db.Customers.IgnoreQueryFilters().ToListAsync();
        var referenceCounts = customers
            .Where(current => current.CustomerMasterId.HasValue && current.CustomerMasterId.Value != Guid.Empty)
            .GroupBy(current => current.CustomerMasterId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var groups = masters
            .GroupBy(BuildCustomerMasterDuplicateKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (groups.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            var canonical = group
                .OrderByDescending(current => referenceCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var changed = false;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                foreach (var customer in customers.Where(current => current.CustomerMasterId == duplicate.Id))
                {
                    customer.CustomerMasterId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(customer, now);
                }

                if (!canonical.CategoryId.HasValue && duplicate.CategoryId.HasValue)
                {
                    canonical.CategoryId = duplicate.CategoryId;
                    changed = true;
                }

                db.CustomerMasters.Remove(duplicate);
            }

            if (changed)
                PreserveDirtyStateForStartupMaintenance(canonical, now);
        }
    }

    private static async Task MergeDuplicateCustomersAsync(LocalDbContext db)
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

        var contractCounts = contracts.GroupBy(current => current.CustomerId).ToDictionary(group => group.Key, group => group.Count());
        var invoiceCounts = invoices.GroupBy(current => current.CustomerId).ToDictionary(group => group.Key, group => group.Count());
        var transactionCounts = transactions.GroupBy(current => current.CustomerId).ToDictionary(group => group.Key, group => group.Count());
        var profileCounts = profiles.Where(current => current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
            .GroupBy(current => current.CustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var assetCounts = assets.Where(current => current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
            .GroupBy(current => current.CustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var groups = customers
            .GroupBy(BuildCustomerDuplicateKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (groups.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            var canonical = group
                .OrderByDescending(current => contractCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => invoiceCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => transactionCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => profileCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => assetCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => current.CustomerMasterId.HasValue && current.CustomerMasterId.Value != Guid.Empty)
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

                if ((!canonical.CustomerMasterId.HasValue || canonical.CustomerMasterId.Value == Guid.Empty) && duplicate.CustomerMasterId.HasValue && duplicate.CustomerMasterId.Value != Guid.Empty)
                {
                    canonical.CustomerMasterId = duplicate.CustomerMasterId;
                    changed = true;
                }

                db.Customers.Remove(duplicate);
            }

            if (changed)
                PreserveDirtyStateForStartupMaintenance(canonical, now);
        }
    }

    private static async Task MergeDuplicateRentalBillingProfilesAsync(LocalDbContext db)
    {
        var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();
        if (profiles.Count == 0)
            return;

        var assets = await db.RentalAssets.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();
        var invoices = await db.Invoices.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted && current.LinkedRentalBillingProfileId.HasValue && current.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .ToListAsync();
        var transactions = await db.Transactions.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted && current.LinkedRentalBillingProfileId.HasValue && current.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .ToListAsync();
        var logs = await db.RentalBillingLogs.IgnoreQueryFilters().Where(current => !current.IsDeleted).ToListAsync();

        var assetCounts = assets.Where(current => current.BillingProfileId.HasValue && current.BillingProfileId.Value != Guid.Empty)
            .GroupBy(current => current.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var invoiceCounts = invoices.GroupBy(current => current.LinkedRentalBillingProfileId!.Value).ToDictionary(group => group.Key, group => group.Count());
        var transactionCounts = transactions.GroupBy(current => current.LinkedRentalBillingProfileId!.Value).ToDictionary(group => group.Key, group => group.Count());
        var logCounts = logs.GroupBy(current => current.BillingProfileId).ToDictionary(group => group.Key, group => group.Count());

        var groups = profiles
            .GroupBy(BuildRentalBillingProfileDuplicateKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (groups.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var group in groups)
        {
            var groupIds = group.Select(current => current.Id).ToHashSet();
            var canonical = group
                .OrderByDescending(current => invoiceCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => transactionCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => !string.IsNullOrWhiteSpace(current.BusinessNumber))
                .ThenByDescending(current => assetCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => logCounts.GetValueOrDefault(current.Id))
                .ThenByDescending(current => CountJsonArrayItems(current.BillingTemplateJson))
                .ThenByDescending(current => CountJsonArrayItems(current.BillingRunsJson))
                .ThenByDescending(CountFilledProfileValues)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var changed = false;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                changed |= MergeRentalBillingProfileValues(canonical, duplicate);

                foreach (var asset in assets.Where(current => current.BillingProfileId == duplicate.Id))
                {
                    asset.BillingProfileId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(asset, now);
                }

                foreach (var invoice in invoices.Where(current => current.LinkedRentalBillingProfileId == duplicate.Id))
                {
                    invoice.LinkedRentalBillingProfileId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(invoice, now);
                }

                foreach (var transaction in transactions.Where(current => current.LinkedRentalBillingProfileId == duplicate.Id))
                {
                    transaction.LinkedRentalBillingProfileId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(transaction, now);
                }

                foreach (var log in logs.Where(current => current.BillingProfileId == duplicate.Id))
                {
                    log.BillingProfileId = canonical.Id;
                    PreserveDirtyStateForStartupMaintenance(log, now);
                }

                db.RentalBillingProfiles.Remove(duplicate);
            }

            var canonicalProfileKey = RentalDuplicateNormalizer.BuildProfileKey(
                canonical.ManagementCompanyCode,
                canonical.BusinessNumber,
                canonical.CustomerName,
                canonical.RealCustomerName,
                canonical.ItemName);
            if (!string.IsNullOrWhiteSpace(canonicalProfileKey) &&
                !string.Equals(canonical.ProfileKey, canonicalProfileKey, StringComparison.Ordinal) &&
                !profiles.Any(current => current.Id != canonical.Id && !groupIds.Contains(current.Id) && !current.IsDeleted && string.Equals(current.ProfileKey, canonicalProfileKey, StringComparison.Ordinal)))
            {
                canonical.ProfileKey = canonicalProfileKey;
                changed = true;
            }

            if (changed)
                PreserveDirtyStateForStartupMaintenance(canonical, now);
        }
    }

    private static async Task MergeDuplicateRentalAssetsAsync(LocalDbContext db)
    {
        var assets = await db.RentalAssets.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();
        if (assets.Count == 0)
            return;

        var groups = assets
            .GroupBy(BuildRentalAssetDuplicateKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();
        if (groups.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var assetIdReplacements = new Dictionary<Guid, Guid>();
        foreach (var group in groups)
        {
            var canonical = group
                .OrderByDescending(current => current.BillingProfileId.HasValue && current.BillingProfileId.Value != Guid.Empty)
                .ThenByDescending(current => current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
                .ThenByDescending(CountFilledAssetValues)
                .ThenByDescending(current => (current.Notes ?? string.Empty).Length)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            var changed = false;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
            {
                changed |= MergeRentalAssetValues(canonical, duplicate);
                assetIdReplacements[duplicate.Id] = canonical.Id;
                db.RentalAssets.Remove(duplicate);
            }

            if (changed)
                PreserveDirtyStateForStartupMaintenance(canonical, now);
        }

        if (assetIdReplacements.Count == 0)
            return;

        var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();
        foreach (var profile in profiles)
        {
            var normalizedTemplateJson = RentalDuplicateNormalizer.RemapBillingTemplateIncludedAssetIds(profile.BillingTemplateJson, assetIdReplacements);
            if (string.Equals(profile.BillingTemplateJson ?? string.Empty, normalizedTemplateJson, StringComparison.Ordinal))
                continue;

            profile.BillingTemplateJson = normalizedTemplateJson;
            PreserveDirtyStateForStartupMaintenance(profile, now);
        }
    }

    private static string BuildCustomerMasterDuplicateKey(LocalCustomerMaster current)
        => string.Join('|',
            BuildStrictTextKey(current.NameOriginal),
            BuildStrictTextKey(current.NameMatchKey),
            current.CategoryId?.ToString("D") ?? string.Empty,
            BuildStrictTextKey(current.TenantCode),
            BuildStrictTextKey(current.OfficeCode));

    private static string BuildCustomerDuplicateKey(LocalCustomer current)
        => string.Join('|',
            BuildStrictTextKey(current.NameOriginal),
            BuildStrictTextKey(current.NameMatchKey),
            current.CustomerMasterId?.ToString("D") ?? string.Empty,
            current.CategoryId?.ToString("D") ?? string.Empty,
            BuildStrictTextKey(current.TenantCode),
            BuildStrictTextKey(current.TradeType),
            BuildStrictTextKey(current.Department),
            BuildStrictTextKey(current.ContactPerson),
            BuildStrictTextKey(current.BusinessNumber),
            BuildStrictTextKey(current.Address),
            BuildStrictTextKey(current.DetailAddress),
            BuildStrictTextKey(current.Phone),
            BuildStrictTextKey(current.MobilePhone),
            BuildStrictTextKey(current.FaxNumber),
            BuildStrictTextKey(current.Email),
            BuildStrictTextKey(current.HomePage),
            BuildStrictTextKey(current.Representative),
            BuildStrictTextKey(current.BusinessType),
            BuildStrictTextKey(current.BusinessItem),
            BuildStrictTextKey(current.Recipient),
            BuildStrictTextKey(current.PriceGrade),
            BuildStrictTextKey(current.Notes),
            BuildStrictTextKey(current.ResponsibleOfficeCode));

    private static string BuildRentalBillingProfileDuplicateKey(LocalRentalBillingProfile current)
        => RentalDuplicateNormalizer.BuildRentalBillingProfileDuplicateKey(
            current.CustomerName,
            current.BillToCustomerName,
            current.RealCustomerName,
            current.InstallSiteName,
            current.ItemName,
            current.BillingDay,
            current.BillingCycleMonths,
            current.MonthlyAmount,
            current.DepositAmount,
            current.BillingType,
            current.BillingAdvanceMode,
            current.ManagementCompanyCode,
            current.BillingMethod,
            current.PaymentMethod,
            current.ResponsibleOfficeCode,
            current.AssignedUsername);

    private static string BuildRentalAssetDuplicateKey(LocalRentalAsset current)
        => RentalDuplicateNormalizer.BuildRentalAssetDuplicateKey(
            current.CustomerName,
            current.CurrentCustomerName,
            current.BillToCustomerName,
            current.InstallSiteName,
            current.InstallLocation,
            current.ItemCategoryName,
            current.ItemName,
            current.Manufacturer,
            current.MachineNumber,
            current.MonthlyFee,
            current.ContractMonths,
            current.AssignedUsername,
            current.AssetStatus);

    private static bool MergeRentalBillingProfileValues(LocalRentalBillingProfile canonical, LocalRentalBillingProfile duplicate)
    {
        var changed = false;

        if ((!canonical.CustomerId.HasValue || canonical.CustomerId.Value == Guid.Empty) && duplicate.CustomerId.HasValue && duplicate.CustomerId.Value != Guid.Empty)
        {
            canonical.CustomerId = duplicate.CustomerId;
            changed = true;
        }

        changed |= TryAssignString(() => canonical.CustomerName, value => canonical.CustomerName = value, duplicate.CustomerName);
        changed |= TryAssignString(() => canonical.BusinessNumber, value => canonical.BusinessNumber = value, duplicate.BusinessNumber);
        changed |= TryAssignString(() => canonical.RealCustomerName, value => canonical.RealCustomerName = value, duplicate.RealCustomerName);
        changed |= TryAssignString(() => canonical.BillToCustomerName, value => canonical.BillToCustomerName = value, duplicate.BillToCustomerName);
        changed |= TryAssignString(() => canonical.InstallSiteName, value => canonical.InstallSiteName = value, duplicate.InstallSiteName);
        changed |= TryAssignString(() => canonical.ManagementCompanyCode, value => canonical.ManagementCompanyCode = value, duplicate.ManagementCompanyCode);
        changed |= TryAssignString(() => canonical.BillingMethod, value => canonical.BillingMethod = value, duplicate.BillingMethod);
        changed |= TryAssignString(() => canonical.PaymentMethod, value => canonical.PaymentMethod = value, duplicate.PaymentMethod);
        changed |= TryAssignString(() => canonical.BillingStatus, value => canonical.BillingStatus = value, duplicate.BillingStatus);
        changed |= TryAssignString(() => canonical.Email, value => canonical.Email = value, duplicate.Email);
        changed |= TryAssignString(() => canonical.SubmissionDocuments, value => canonical.SubmissionDocuments = value, duplicate.SubmissionDocuments, preferLonger: true);
        changed |= TryAssignString(() => canonical.Notes, value => canonical.Notes = value, duplicate.Notes, preferLonger: true);
        changed |= TryAssignString(() => canonical.ResponsibleOfficeCode, value => canonical.ResponsibleOfficeCode = value, duplicate.ResponsibleOfficeCode);
        changed |= TryAssignString(() => canonical.AssignedUsername, value => canonical.AssignedUsername = value, duplicate.AssignedUsername);
        changed |= TryAssignString(() => canonical.FollowUpNote, value => canonical.FollowUpNote = value, duplicate.FollowUpNote, preferLonger: true);
        changed |= TryAssignString(() => canonical.ItemName, value => canonical.ItemName = value, duplicate.ItemName);

        if (!canonical.BillingAnchorDate.HasValue && duplicate.BillingAnchorDate.HasValue)
        {
            canonical.BillingAnchorDate = duplicate.BillingAnchorDate;
            changed = true;
        }

        if (!canonical.BillingStartDate.HasValue && duplicate.BillingStartDate.HasValue)
        {
            canonical.BillingStartDate = duplicate.BillingStartDate;
            changed = true;
        }

        if (!canonical.ContractDate.HasValue && duplicate.ContractDate.HasValue)
        {
            canonical.ContractDate = duplicate.ContractDate;
            changed = true;
        }

        if (!canonical.ContractStartDate.HasValue && duplicate.ContractStartDate.HasValue)
        {
            canonical.ContractStartDate = duplicate.ContractStartDate;
            changed = true;
        }

        if (!canonical.ContractEndDate.HasValue && duplicate.ContractEndDate.HasValue)
        {
            canonical.ContractEndDate = duplicate.ContractEndDate;
            changed = true;
        }

        if (!canonical.LastBilledDate.HasValue && duplicate.LastBilledDate.HasValue)
        {
            canonical.LastBilledDate = duplicate.LastBilledDate;
            changed = true;
        }

        if (!canonical.LastSettledDate.HasValue || (duplicate.LastSettledDate.HasValue && duplicate.LastSettledDate.Value > canonical.LastSettledDate.Value))
        {
            if (duplicate.LastSettledDate.HasValue)
            {
                canonical.LastSettledDate = duplicate.LastSettledDate;
                changed = true;
            }
        }

        if (canonical.BillingDay <= 0 && duplicate.BillingDay > 0)
        {
            canonical.BillingDay = duplicate.BillingDay;
            changed = true;
        }

        if (canonical.BillingCycleMonths <= 0 && duplicate.BillingCycleMonths > 0)
        {
            canonical.BillingCycleMonths = duplicate.BillingCycleMonths;
            changed = true;
        }

        if (canonical.MonthlyAmount <= 0m && duplicate.MonthlyAmount > 0m)
        {
            canonical.MonthlyAmount = duplicate.MonthlyAmount;
            changed = true;
        }

        if (canonical.DepositAmount <= 0m && duplicate.DepositAmount > 0m)
        {
            canonical.DepositAmount = duplicate.DepositAmount;
            changed = true;
        }

        if (duplicate.SettledAmount > canonical.SettledAmount)
        {
            canonical.SettledAmount = duplicate.SettledAmount;
            changed = true;
        }

        if (duplicate.OutstandingAmount > canonical.OutstandingAmount)
        {
            canonical.OutstandingAmount = duplicate.OutstandingAmount;
            changed = true;
        }

        if (duplicate.RequiresFollowUp && !canonical.RequiresFollowUp)
        {
            canonical.RequiresFollowUp = true;
            changed = true;
        }

        var mergedTemplateJson = RentalDuplicateNormalizer.MergeBillingTemplateJson(canonical.BillingTemplateJson, duplicate.BillingTemplateJson);
        if (!string.Equals(canonical.BillingTemplateJson ?? string.Empty, mergedTemplateJson, StringComparison.Ordinal))
        {
            canonical.BillingTemplateJson = mergedTemplateJson;
            changed = true;
        }

        var mergedRunsJson = RentalDuplicateNormalizer.MergeBillingRunsJson(canonical.BillingRunsJson, duplicate.BillingRunsJson);
        if (!string.Equals(canonical.BillingRunsJson ?? string.Empty, mergedRunsJson, StringComparison.Ordinal))
        {
            canonical.BillingRunsJson = mergedRunsJson;
            changed = true;
        }

        return changed;
    }

    private static bool MergeRentalAssetValues(LocalRentalAsset canonical, LocalRentalAsset duplicate)
    {
        var changed = false;

        if ((!canonical.CustomerId.HasValue || canonical.CustomerId.Value == Guid.Empty) && duplicate.CustomerId.HasValue && duplicate.CustomerId.Value != Guid.Empty)
        {
            canonical.CustomerId = duplicate.CustomerId;
            changed = true;
        }

        if ((!canonical.ItemId.HasValue || canonical.ItemId.Value == Guid.Empty) && duplicate.ItemId.HasValue && duplicate.ItemId.Value != Guid.Empty)
        {
            canonical.ItemId = duplicate.ItemId;
            changed = true;
        }

        if ((!canonical.BillingProfileId.HasValue || canonical.BillingProfileId.Value == Guid.Empty) && duplicate.BillingProfileId.HasValue && duplicate.BillingProfileId.Value != Guid.Empty)
        {
            canonical.BillingProfileId = duplicate.BillingProfileId;
            changed = true;
        }

        changed |= TryAssignString(() => canonical.ManagementCompanyCode, value => canonical.ManagementCompanyCode = value, duplicate.ManagementCompanyCode);
        changed |= TryAssignString(() => canonical.CurrentLocation, value => canonical.CurrentLocation = value, duplicate.CurrentLocation);
        changed |= TryAssignString(() => canonical.CurrentCustomerName, value => canonical.CurrentCustomerName = value, duplicate.CurrentCustomerName);
        changed |= TryAssignString(() => canonical.BillToCustomerName, value => canonical.BillToCustomerName = value, duplicate.BillToCustomerName);
        changed |= TryAssignString(() => canonical.InstallSiteName, value => canonical.InstallSiteName = value, duplicate.InstallSiteName);
        changed |= TryAssignString(() => canonical.BillingEligibilityStatus, value => canonical.BillingEligibilityStatus = value, duplicate.BillingEligibilityStatus);
        changed |= TryAssignString(() => canonical.BillingExclusionReason, value => canonical.BillingExclusionReason = value, duplicate.BillingExclusionReason, preferLonger: true);
        changed |= TryAssignString(() => canonical.ItemCategoryName, value => canonical.ItemCategoryName = value, duplicate.ItemCategoryName);
        changed |= TryAssignString(() => canonical.Manufacturer, value => canonical.Manufacturer = value, duplicate.Manufacturer);
        changed |= TryAssignString(() => canonical.ItemName, value => canonical.ItemName = value, duplicate.ItemName);
        changed |= TryAssignString(() => canonical.MachineNumber, value => canonical.MachineNumber = value, duplicate.MachineNumber);
        changed |= TryAssignString(() => canonical.PurchaseVendor, value => canonical.PurchaseVendor = value, duplicate.PurchaseVendor);
        changed |= TryAssignString(() => canonical.CustomerName, value => canonical.CustomerName = value, duplicate.CustomerName);
        changed |= TryAssignString(() => canonical.InstallLocation, value => canonical.InstallLocation = value, duplicate.InstallLocation);
        changed |= TryAssignString(() => canonical.DepositText, value => canonical.DepositText = value, duplicate.DepositText);
        changed |= TryAssignString(() => canonical.FreeSupplyItems, value => canonical.FreeSupplyItems = value, duplicate.FreeSupplyItems, preferLonger: true);
        changed |= TryAssignString(() => canonical.PaidSupplyItems, value => canonical.PaidSupplyItems = value, duplicate.PaidSupplyItems, preferLonger: true);
        changed |= TryAssignString(() => canonical.ResponsibleOfficeCode, value => canonical.ResponsibleOfficeCode = value, duplicate.ResponsibleOfficeCode);
        changed |= TryAssignString(() => canonical.AssignedUsername, value => canonical.AssignedUsername = value, duplicate.AssignedUsername);
        changed |= TryAssignString(() => canonical.AssetStatus, value => canonical.AssetStatus = value, duplicate.AssetStatus);
        changed |= TryAssignString(() => canonical.Notes, value => canonical.Notes = value, duplicate.Notes, preferLonger: true);

        if (!canonical.PurchaseDate.HasValue && duplicate.PurchaseDate.HasValue)
        {
            canonical.PurchaseDate = duplicate.PurchaseDate;
            changed = true;
        }

        if (!canonical.DisposalDate.HasValue && duplicate.DisposalDate.HasValue)
        {
            canonical.DisposalDate = duplicate.DisposalDate;
            changed = true;
        }

        if (!canonical.ContractDate.HasValue && duplicate.ContractDate.HasValue)
        {
            canonical.ContractDate = duplicate.ContractDate;
            changed = true;
        }

        if (!canonical.InstallDate.HasValue && duplicate.InstallDate.HasValue)
        {
            canonical.InstallDate = duplicate.InstallDate;
            changed = true;
        }

        if (!canonical.ContractStartDate.HasValue && duplicate.ContractStartDate.HasValue)
        {
            canonical.ContractStartDate = duplicate.ContractStartDate;
            changed = true;
        }

        if (!canonical.RentalEndDate.HasValue && duplicate.RentalEndDate.HasValue)
        {
            canonical.RentalEndDate = duplicate.RentalEndDate;
            changed = true;
        }

        if (canonical.PurchasePrice <= 0m && duplicate.PurchasePrice > 0m)
        {
            canonical.PurchasePrice = duplicate.PurchasePrice;
            changed = true;
        }

        if (canonical.SalePrice <= 0m && duplicate.SalePrice > 0m)
        {
            canonical.SalePrice = duplicate.SalePrice;
            changed = true;
        }

        if (canonical.MonthlyFee <= 0m && duplicate.MonthlyFee > 0m)
        {
            canonical.MonthlyFee = duplicate.MonthlyFee;
            changed = true;
        }

        if (canonical.ContractMonths <= 0 && duplicate.ContractMonths > 0)
        {
            canonical.ContractMonths = duplicate.ContractMonths;
            changed = true;
        }

        return changed;
    }

    private static int CountFilledProfileValues(LocalRentalBillingProfile current)
    {
        return CountFilledStrings(
            current.CustomerName,
            current.BusinessNumber,
            current.RealCustomerName,
            current.BillToCustomerName,
            current.InstallSiteName,
            current.ItemName,
            current.ManagementCompanyCode,
            current.BillingMethod,
            current.PaymentMethod,
            current.BillingStatus,
            current.Email,
            current.SubmissionDocuments,
            current.Notes,
            current.ResponsibleOfficeCode,
            current.AssignedUsername,
            current.FollowUpNote)
            + (current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty ? 1 : 0)
            + (current.BillingAnchorDate.HasValue ? 1 : 0)
            + (current.BillingStartDate.HasValue ? 1 : 0)
            + (current.ContractDate.HasValue ? 1 : 0)
            + (current.ContractStartDate.HasValue ? 1 : 0)
            + (current.ContractEndDate.HasValue ? 1 : 0)
            + (current.LastBilledDate.HasValue ? 1 : 0)
            + (current.LastSettledDate.HasValue ? 1 : 0)
            + (current.MonthlyAmount > 0m ? 1 : 0)
            + (current.DepositAmount > 0m ? 1 : 0)
            + (current.SettledAmount > 0m ? 1 : 0)
            + (current.OutstandingAmount > 0m ? 1 : 0)
            + (current.RequiresFollowUp ? 1 : 0);
    }

    private static int CountFilledAssetValues(LocalRentalAsset current)
    {
        return CountFilledStrings(
            current.ManagementCompanyCode,
            current.CurrentLocation,
            current.CurrentCustomerName,
            current.BillToCustomerName,
            current.InstallSiteName,
            current.BillingEligibilityStatus,
            current.BillingExclusionReason,
            current.ItemCategoryName,
            current.Manufacturer,
            current.ItemName,
            current.MachineNumber,
            current.PurchaseVendor,
            current.CustomerName,
            current.InstallLocation,
            current.DepositText,
            current.FreeSupplyItems,
            current.PaidSupplyItems,
            current.ResponsibleOfficeCode,
            current.AssignedUsername,
            current.AssetStatus,
            current.Notes)
            + (current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty ? 1 : 0)
            + (current.ItemId.HasValue && current.ItemId.Value != Guid.Empty ? 1 : 0)
            + (current.BillingProfileId.HasValue && current.BillingProfileId.Value != Guid.Empty ? 1 : 0)
            + (current.PurchaseDate.HasValue ? 1 : 0)
            + (current.DisposalDate.HasValue ? 1 : 0)
            + (current.ContractDate.HasValue ? 1 : 0)
            + (current.InstallDate.HasValue ? 1 : 0)
            + (current.ContractStartDate.HasValue ? 1 : 0)
            + (current.RentalEndDate.HasValue ? 1 : 0)
            + (current.PurchasePrice > 0m ? 1 : 0)
            + (current.SalePrice > 0m ? 1 : 0)
            + (current.MonthlyFee > 0m ? 1 : 0)
            + (current.ContractMonths > 0 ? 1 : 0);
    }

    private static int CountFilledStrings(params string?[] values)
        => values.Count(current => !string.IsNullOrWhiteSpace(current));

    private static int CountJsonArrayItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return 0;

        try
        {
            return JsonNode.Parse(json) is JsonArray array ? array.Count : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryAssignString(Func<string> getter, Action<string> setter, string? incoming, bool preferLonger = false)
    {
        var current = getter();
        if (string.IsNullOrWhiteSpace(incoming))
            return false;

        if (string.IsNullOrWhiteSpace(current))
        {
            setter(incoming.Trim());
            return true;
        }

        if (!preferLonger)
            return false;

        var trimmedIncoming = incoming.Trim();
        if (trimmedIncoming.Length <= current.Trim().Length)
            return false;

        setter(trimmedIncoming);
        return true;
    }

    private static string BuildStrictTextKey(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();
}
