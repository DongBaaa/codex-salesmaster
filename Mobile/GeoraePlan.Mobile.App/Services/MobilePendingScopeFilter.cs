using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public static class MobilePendingScopeFilter
{
    public static MobilePendingScopeSummary CreateSummary(SessionSnapshot snapshot, MobileSyncState state)
    {
        state.Normalize();
        var scopedPush = CreateScopedPushRequest(snapshot, state);
        var scopedPaymentAttachmentCount = GetScopedPaymentAttachments(snapshot, state).Count;
        var scopedServerMutationCount = CountServerMutations(scopedPush);

        return new MobilePendingScopeSummary
        {
            PendingCompanyProfileCount = scopedPush.CompanyProfiles.Count,
            PendingUnitCount = scopedPush.Units.Count,
            PendingCustomerCategoryCount = scopedPush.CustomerCategories.Count,
            PendingPriceGradeOptionCount = scopedPush.PriceGradeOptions.Count,
            PendingTradeTypeOptionCount = scopedPush.TradeTypeOptions.Count,
            PendingItemCategoryOptionCount = scopedPush.ItemCategoryOptions.Count,
            PendingCustomerMasterCount = scopedPush.CustomerMasters.Count,
            PendingCustomerCount = scopedPush.Customers.Count,
            PendingCustomerContractCount = scopedPush.CustomerContracts.Count,
            PendingItemCount = scopedPush.Items.Count,
            PendingItemWarehouseStockCount = scopedPush.ItemWarehouseStocks.Count,
            PendingInvoiceCount = scopedPush.Invoices.Count,
            PendingPaymentCount = scopedPush.Payments.Count,
            PendingTransactionCount = scopedPush.Transactions.Count,
            PendingTransactionAttachmentCount = scopedPush.TransactionAttachments.Count,
            PendingInventoryTransferCount = scopedPush.InventoryTransfers.Count,
            PendingRentalManagementCompanyCount = scopedPush.RentalManagementCompanies.Count,
            PendingRentalBillingProfileCount = scopedPush.RentalBillingProfiles.Count,
            PendingRentalAssetCount = scopedPush.RentalAssets.Count,
            PendingRentalAssetAssignmentHistoryCount = scopedPush.RentalAssetAssignmentHistories.Count,
            PendingRentalBillingLogCount = scopedPush.RentalBillingLogs.Count,
            PendingPaymentAttachmentCount = scopedPaymentAttachmentCount,
            ExcludedServerMutationCount = Math.Max(0, state.PendingServerMutationCount - scopedServerMutationCount),
            ExcludedPaymentAttachmentCount = Math.Max(0, state.PendingPaymentAttachmentCount - scopedPaymentAttachmentCount)
        };
    }

    public static SyncPushRequest CreateScopedPushRequest(SessionSnapshot snapshot, MobileSyncState state)
    {
        state.Normalize();
        var pending = state.PendingPush;
        var request = new SyncPushRequest { DeviceId = state.DeviceId };

        if (!snapshot.IsAuthenticated)
            return request;

        var canEditCompanyProfiles = snapshot.CanEditCompanyProfiles;
        var canEditSettings = snapshot.CanEditSettings;
        var canEditCustomers = snapshot.CanEditCustomers;
        var canEditItems = snapshot.CanEditItems;
        var canEditInvoices = snapshot.CanCreateInvoices;
        var canEditPayments = snapshot.CanCreatePayments;
        var canEditDelivery = snapshot.CanEditDelivery;
        var canEditRentalSettings = snapshot.CanEditRentalSettings;
        var canEditRentalProfiles = snapshot.CanEditRentalProfiles;
        var canEditRentalAssets = snapshot.CanEditRentalAssets;

        var customerMap = BuildEntityMap(state.SyncedCustomers, pending.Customers);
        var invoiceMap = BuildEntityMap(state.SyncedInvoices, pending.Invoices);
        var transactionMap = BuildEntityMap(state.SyncedTransactions, pending.Transactions);
        var itemMap = BuildEntityMap(state.SyncedItems, pending.Items);

        request.CompanyProfiles.AddRange(Filter(pending.CompanyProfiles, canEditCompanyProfiles, profile => CanAccessCompanyProfile(snapshot, profile)));
        request.Units.AddRange(Filter(pending.Units, canEditSettings, _ => true));
        request.CustomerCategories.AddRange(Filter(pending.CustomerCategories, canEditSettings, _ => true));
        request.PriceGradeOptions.AddRange(Filter(pending.PriceGradeOptions, canEditSettings, _ => true));
        request.TradeTypeOptions.AddRange(Filter(pending.TradeTypeOptions, canEditSettings, _ => true));
        request.ItemCategoryOptions.AddRange(Filter(pending.ItemCategoryOptions, canEditSettings, _ => true));
        request.CustomerMasters.AddRange(Filter(pending.CustomerMasters, canEditCustomers, master => CanAccessCustomerMaster(snapshot, master)));
        request.Customers.AddRange(Filter(pending.Customers, canEditCustomers, customer => MobileSessionScopeFilter.CanAccessCustomer(snapshot, customer)));
        request.CustomerContracts.AddRange(Filter(pending.CustomerContracts, canEditCustomers, contract => CanAccessCustomerContract(snapshot, contract, customerMap)));
        request.Items.AddRange(Filter(pending.Items, canEditItems, item => MobileSessionScopeFilter.CanAccessItem(snapshot, item)));
        request.ItemWarehouseStocks.AddRange(Filter(pending.ItemWarehouseStocks, canEditItems, stock => CanAccessItemWarehouseStock(snapshot, stock, itemMap)));
        request.Invoices.AddRange(Filter(pending.Invoices, canEditInvoices, invoice => MobileSessionScopeFilter.CanAccessInvoice(snapshot, invoice)));
        request.Payments.AddRange(Filter(pending.Payments, canEditPayments, payment => CanAccessPayment(snapshot, payment, invoiceMap)));
        request.Transactions.AddRange(Filter(pending.Transactions, canEditPayments, transaction => MobileSessionScopeFilter.CanAccessTransaction(snapshot, transaction)));
        request.TransactionAttachments.AddRange(Filter(pending.TransactionAttachments, canEditPayments, attachment => CanAccessTransactionAttachment(snapshot, attachment, transactionMap)));
        request.InventoryTransfers.AddRange(Filter(pending.InventoryTransfers, canEditDelivery, transfer => MobileSessionScopeFilter.CanAccessInventoryTransfer(snapshot, transfer)));
        request.RentalManagementCompanies.AddRange(Filter(pending.RentalManagementCompanies, canEditRentalSettings, company => CanAccessTenantScope(snapshot, company.TenantCode, company.Code)));
        request.RentalBillingProfiles.AddRange(Filter(pending.RentalBillingProfiles, canEditRentalProfiles, profile => MobileSessionScopeFilter.CanAccessRentalBillingProfile(snapshot, profile)));
        request.RentalAssets.AddRange(Filter(pending.RentalAssets, canEditRentalAssets, asset => MobileSessionScopeFilter.CanAccessRentalAsset(snapshot, asset)));
        request.RentalAssetAssignmentHistories.AddRange(Filter(pending.RentalAssetAssignmentHistories, canEditRentalAssets, history => MobileSessionScopeFilter.CanAccessRentalAssetAssignmentHistory(snapshot, history)));
        request.RentalBillingLogs.AddRange(Filter(pending.RentalBillingLogs, canEditRentalProfiles, log => MobileSessionScopeFilter.CanAccessRentalBillingLog(snapshot, log)));

        return request;
    }

    public static IReadOnlyList<PendingPaymentAttachmentRecord> GetScopedPaymentAttachments(
        SessionSnapshot snapshot,
        MobileSyncState state,
        IReadOnlySet<Guid>? additionalAllowedPaymentIds = null)
    {
        state.Normalize();
        if (!snapshot.IsAuthenticated || !snapshot.CanCreatePayments)
            return [];

        var paymentMap = BuildEntityMap(state.SyncedPayments, state.PendingPush.Payments);
        var invoiceMap = BuildEntityMap(state.SyncedInvoices, state.PendingPush.Invoices);
        var allowedPaymentIds = paymentMap.Values
            .Where(payment => CanAccessPayment(snapshot, payment, invoiceMap))
            .Select(payment => payment.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        if (additionalAllowedPaymentIds is not null)
        {
            foreach (var paymentId in additionalAllowedPaymentIds)
            {
                if (paymentId != Guid.Empty)
                    allowedPaymentIds.Add(paymentId);
            }
        }

        return state.PendingPaymentAttachments
            .Where(attachment => attachment is not null && allowedPaymentIds.Contains(attachment.PaymentId))
            .ToList();
    }

    public static bool HasScopedServerSyncPayload(SessionSnapshot snapshot, MobileSyncState state)
        => CountServerMutations(CreateScopedPushRequest(snapshot, state)) > 0;

    public static int CountServerMutations(SyncPushRequest request)
        => CountSettings(request)
           + (request.CustomerMasters?.Count ?? 0)
           + (request.Customers?.Count ?? 0)
           + (request.CustomerContracts?.Count ?? 0)
           + (request.Items?.Count ?? 0)
           + (request.ItemWarehouseStocks?.Count ?? 0)
           + (request.Invoices?.Count ?? 0)
           + (request.Payments?.Count ?? 0)
           + (request.Transactions?.Count ?? 0)
           + (request.TransactionAttachments?.Count ?? 0)
           + (request.InventoryTransfers?.Count ?? 0)
           + CountRental(request);

    private static int CountSettings(SyncPushRequest request)
        => (request.CompanyProfiles?.Count ?? 0)
           + (request.Units?.Count ?? 0)
           + (request.CustomerCategories?.Count ?? 0)
           + (request.PriceGradeOptions?.Count ?? 0)
           + (request.TradeTypeOptions?.Count ?? 0)
           + (request.ItemCategoryOptions?.Count ?? 0);

    private static int CountRental(SyncPushRequest request)
        => (request.RentalManagementCompanies?.Count ?? 0)
           + (request.RentalBillingProfiles?.Count ?? 0)
           + (request.RentalAssets?.Count ?? 0)
           + (request.RentalAssetAssignmentHistories?.Count ?? 0)
           + (request.RentalBillingLogs?.Count ?? 0);

    private static IEnumerable<T> Filter<T>(IEnumerable<T>? values, bool canPush, Func<T, bool> canAccess)
        where T : class
    {
        if (!canPush || values is null)
            return [];

        return values.Where(value => value is not null && canAccess(value));
    }

    private static Dictionary<Guid, T> BuildEntityMap<T>(params IEnumerable<T>?[] sources)
        where T : SyncEntityDto
    {
        var map = new Dictionary<Guid, T>();
        foreach (var source in sources)
        {
            if (source is null)
                continue;

            foreach (var item in source)
            {
                if (item is null || item.Id == Guid.Empty)
                    continue;

                if (item.IsDeleted)
                    map.Remove(item.Id);
                else
                    map[item.Id] = item;
            }
        }

        return map;
    }

    private static bool CanAccessCompanyProfile(SessionSnapshot snapshot, CompanyProfileDto profile)
        => MobileSessionScopeFilter.CanAccessOperationalScope(
            snapshot,
            profile.OfficeCode,
            fallbackOfficeCode: null,
            allowSharedOffice: false);

    private static bool CanAccessCustomerMaster(SessionSnapshot snapshot, CustomerMasterDto master)
        => MobileSessionScopeFilter.CanAccessOperationalScope(
            snapshot,
            master.OfficeCode,
            master.TenantCode,
            allowSharedOffice: true);

    private static bool CanAccessCustomerContract(
        SessionSnapshot snapshot,
        CustomerContractDto contract,
        IReadOnlyDictionary<Guid, CustomerDto> customerMap)
        => contract.CustomerId != Guid.Empty &&
           customerMap.TryGetValue(contract.CustomerId, out var customer) &&
           MobileSessionScopeFilter.CanAccessCustomer(snapshot, customer);

    private static bool CanAccessItemWarehouseStock(
        SessionSnapshot snapshot,
        ItemWarehouseStockDto stock,
        IReadOnlyDictionary<Guid, ItemDto> itemMap)
        => stock.ItemId != Guid.Empty &&
           itemMap.TryGetValue(stock.ItemId, out var item) &&
           MobileSessionScopeFilter.CanAccessItem(snapshot, item) &&
           MobileSessionScopeFilter.CanAccessWarehouse(snapshot, stock.WarehouseCode);

    private static bool CanAccessPayment(
        SessionSnapshot snapshot,
        PaymentDto payment,
        IReadOnlyDictionary<Guid, InvoiceDto> invoiceMap)
        => payment.InvoiceId != Guid.Empty &&
           invoiceMap.TryGetValue(payment.InvoiceId, out var invoice) &&
           MobileSessionScopeFilter.CanAccessInvoice(snapshot, invoice);

    private static bool CanAccessTransactionAttachment(
        SessionSnapshot snapshot,
        TransactionAttachmentDto attachment,
        IReadOnlyDictionary<Guid, TransactionDto> transactionMap)
        => attachment.TransactionId != Guid.Empty &&
           transactionMap.TryGetValue(attachment.TransactionId, out var transaction) &&
           MobileSessionScopeFilter.CanAccessTransaction(snapshot, transaction);

    private static bool CanAccessTenantScope(SessionSnapshot snapshot, string? tenantCode, string? fallbackOfficeCode = null)
    {
        if (!snapshot.IsAuthenticated)
            return false;

        if (MobileSessionScopeFilter.IsGlobalAdminScope(snapshot))
            return true;

        var sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode);
        var sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, sessionOfficeCode);
        var entityTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, fallbackOfficeCode);
        return string.Equals(entityTenantCode, sessionTenantCode, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class MobilePendingScopeSummary
{
    public int PendingCompanyProfileCount { get; init; }
    public int PendingUnitCount { get; init; }
    public int PendingCustomerCategoryCount { get; init; }
    public int PendingPriceGradeOptionCount { get; init; }
    public int PendingTradeTypeOptionCount { get; init; }
    public int PendingItemCategoryOptionCount { get; init; }
    public int PendingCustomerMasterCount { get; init; }
    public int PendingCustomerCount { get; init; }
    public int PendingCustomerContractCount { get; init; }
    public int PendingItemCount { get; init; }
    public int PendingItemWarehouseStockCount { get; init; }
    public int PendingInvoiceCount { get; init; }
    public int PendingPaymentCount { get; init; }
    public int PendingTransactionCount { get; init; }
    public int PendingTransactionAttachmentCount { get; init; }
    public int PendingInventoryTransferCount { get; init; }
    public int PendingRentalManagementCompanyCount { get; init; }
    public int PendingRentalBillingProfileCount { get; init; }
    public int PendingRentalAssetCount { get; init; }
    public int PendingRentalAssetAssignmentHistoryCount { get; init; }
    public int PendingRentalBillingLogCount { get; init; }
    public int PendingPaymentAttachmentCount { get; init; }
    public int ExcludedServerMutationCount { get; init; }
    public int ExcludedPaymentAttachmentCount { get; init; }
    public int ExcludedTotalCount => ExcludedServerMutationCount + ExcludedPaymentAttachmentCount;
    public int PendingSettingCount =>
        PendingCompanyProfileCount +
        PendingUnitCount +
        PendingCustomerCategoryCount +
        PendingPriceGradeOptionCount +
        PendingTradeTypeOptionCount +
        PendingItemCategoryOptionCount;
    public int PendingRentalCount =>
        PendingRentalManagementCompanyCount +
        PendingRentalBillingProfileCount +
        PendingRentalAssetCount +
        PendingRentalAssetAssignmentHistoryCount +
        PendingRentalBillingLogCount;
    public int PendingServerMutationCount =>
        PendingSettingCount +
        PendingCustomerMasterCount +
        PendingCustomerCount +
        PendingCustomerContractCount +
        PendingItemCount +
        PendingItemWarehouseStockCount +
        PendingInvoiceCount +
        PendingPaymentCount +
        PendingTransactionCount +
        PendingTransactionAttachmentCount +
        PendingInventoryTransferCount +
        PendingRentalCount;
    public int PendingTotalCount => PendingServerMutationCount + PendingPaymentAttachmentCount;
}
