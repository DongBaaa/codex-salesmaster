using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Models;

public sealed class MobileSyncState
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerUsername { get; set; } = string.Empty;
    public string OwnerTenantCode { get; set; } = string.Empty;
    public string OwnerOfficeCode { get; set; } = string.Empty;
    public long LastRevision { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastBackgroundSyncUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public int ConsecutiveFailureCount { get; set; }
    public int LastPulledCustomerCount { get; set; }
    public int LastPulledItemCount { get; set; }
    public int LastPulledPriceGradeOptionCount { get; set; }
    public int LastPulledInvoiceCount { get; set; }
    public int LastPulledPaymentCount { get; set; }
    public int LastPulledTransactionCount { get; set; }
    public int LastPulledTransactionAttachmentCount { get; set; }
    public int LastPulledInventoryTransferCount { get; set; }
    public int LastPulledRentalManagementCompanyCount { get; set; }
    public int LastPulledRentalBillingProfileCount { get; set; }
    public int LastPulledRentalAssetCount { get; set; }
    public int LastPulledRentalBillingLogCount { get; set; }
    public List<TransactionDto> SyncedTransactions { get; set; } = new();
    public List<TransactionAttachmentDto> SyncedTransactionAttachments { get; set; } = new();
    public List<InventoryTransferDto> SyncedInventoryTransfers { get; set; } = new();
    public List<RentalManagementCompanyDto> SyncedRentalManagementCompanies { get; set; } = new();
    public List<RentalBillingProfileDto> SyncedRentalBillingProfiles { get; set; } = new();
    public List<RentalAssetDto> SyncedRentalAssets { get; set; } = new();
    public List<RentalBillingLogDto> SyncedRentalBillingLogs { get; set; } = new();
    public List<PriceGradeOptionDto> SyncedPriceGradeOptions { get; set; } = new();
    public SyncPushRequest PendingPush { get; set; } = new();
    public List<PendingPaymentAttachmentRecord> PendingPaymentAttachments { get; set; } = new();

    public int PendingInvoiceCount => PendingPush.Invoices?.Count ?? 0;
    public int PendingPaymentCount => PendingPush.Payments?.Count ?? 0;
    public int PendingTransactionCount => PendingPush.Transactions?.Count ?? 0;
    public int PendingTransactionAttachmentCount => PendingPush.TransactionAttachments?.Count ?? 0;
    public int PendingInventoryTransferCount => PendingPush.InventoryTransfers?.Count ?? 0;
    public int PendingRentalManagementCompanyCount => PendingPush.RentalManagementCompanies?.Count ?? 0;
    public int PendingRentalBillingProfileCount => PendingPush.RentalBillingProfiles?.Count ?? 0;
    public int PendingRentalAssetCount => PendingPush.RentalAssets?.Count ?? 0;
    public int PendingRentalBillingLogCount => PendingPush.RentalBillingLogs?.Count ?? 0;
    public int PendingPaymentAttachmentCount => PendingPaymentAttachments?.Count ?? 0;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
            DeviceId = Guid.NewGuid().ToString("N");

        PendingPush ??= new SyncPushRequest();
        PendingPush.DeviceId = DeviceId;
        PendingPush.CompanyProfiles ??= new List<CompanyProfileDto>();
        PendingPush.Units ??= new List<UnitDto>();
        PendingPush.CustomerCategories ??= new List<CustomerCategoryDto>();
        PendingPush.PriceGradeOptions ??= new List<PriceGradeOptionDto>();
        PendingPush.TradeTypeOptions ??= new List<TradeTypeOptionDto>();
        PendingPush.ItemCategoryOptions ??= new List<ItemCategoryOptionDto>();
        PendingPush.CustomerMasters ??= new List<CustomerMasterDto>();
        PendingPush.Customers ??= new List<CustomerDto>();
        PendingPush.CustomerContracts ??= new List<CustomerContractDto>();
        PendingPush.Items ??= new List<ItemDto>();
        PendingPush.ItemWarehouseStocks ??= new List<ItemWarehouseStockDto>();
        PendingPush.Transactions ??= new List<TransactionDto>();
        PendingPush.TransactionAttachments ??= new List<TransactionAttachmentDto>();
        PendingPush.InventoryTransfers ??= new List<InventoryTransferDto>();
        PendingPush.RentalManagementCompanies ??= new List<RentalManagementCompanyDto>();
        PendingPush.RentalBillingProfiles ??= new List<RentalBillingProfileDto>();
        PendingPush.RentalAssets ??= new List<RentalAssetDto>();
        PendingPush.RentalBillingLogs ??= new List<RentalBillingLogDto>();
        PendingPush.Invoices ??= new List<InvoiceDto>();
        PendingPush.Payments ??= new List<PaymentDto>();
        SyncedTransactions ??= new List<TransactionDto>();
        SyncedTransactionAttachments ??= new List<TransactionAttachmentDto>();
        SyncedInventoryTransfers ??= new List<InventoryTransferDto>();
        SyncedRentalManagementCompanies ??= new List<RentalManagementCompanyDto>();
        SyncedRentalBillingProfiles ??= new List<RentalBillingProfileDto>();
        SyncedRentalAssets ??= new List<RentalAssetDto>();
        SyncedRentalBillingLogs ??= new List<RentalBillingLogDto>();
        SyncedPriceGradeOptions ??= new List<PriceGradeOptionDto>();
        PendingPaymentAttachments ??= new List<PendingPaymentAttachmentRecord>();
    }
}
