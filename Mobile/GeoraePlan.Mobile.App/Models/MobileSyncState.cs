using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Models;

public sealed class MobileSyncState
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public long LastRevision { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastBackgroundSyncUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public int ConsecutiveFailureCount { get; set; }
    public int LastPulledCustomerCount { get; set; }
    public int LastPulledItemCount { get; set; }
    public int LastPulledInvoiceCount { get; set; }
    public int LastPulledPaymentCount { get; set; }
    public SyncPushRequest PendingPush { get; set; } = new();
    public List<PendingPaymentAttachmentRecord> PendingPaymentAttachments { get; set; } = new();

    public int PendingInvoiceCount => PendingPush.Invoices?.Count ?? 0;
    public int PendingPaymentCount => PendingPush.Payments?.Count ?? 0;
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
        PendingPaymentAttachments ??= new List<PendingPaymentAttachmentRecord>();
    }
}
