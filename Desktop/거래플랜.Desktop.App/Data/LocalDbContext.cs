using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public sealed class LocalDbContext : DbContext
{
    public DbSet<LocalCompanyProfile> CompanyProfiles => Set<LocalCompanyProfile>();
    public DbSet<LocalUnit> Units => Set<LocalUnit>();
    public DbSet<LocalCustomerCategory> CustomerCategories => Set<LocalCustomerCategory>();
    public DbSet<LocalPriceGradeOption> PriceGradeOptions => Set<LocalPriceGradeOption>();
    public DbSet<LocalTradeTypeOption> TradeTypeOptions => Set<LocalTradeTypeOption>();
    public DbSet<LocalItemCategoryOption> ItemCategoryOptions => Set<LocalItemCategoryOption>();
    public DbSet<LocalCustomerMaster> CustomerMasters => Set<LocalCustomerMaster>();
    public DbSet<LocalCustomer> Customers => Set<LocalCustomer>();
    public DbSet<LocalCustomerContract> CustomerContracts => Set<LocalCustomerContract>();
    public DbSet<LocalItem> Items => Set<LocalItem>();
    public DbSet<LocalInvoice> Invoices => Set<LocalInvoice>();
    public DbSet<LocalInvoiceLine> InvoiceLines => Set<LocalInvoiceLine>();
    public DbSet<LocalPayment> Payments => Set<LocalPayment>();
    public DbSet<LocalSetting> Settings => Set<LocalSetting>();
    public DbSet<LocalRecentSelection> RecentSelections => Set<LocalRecentSelection>();
    public DbSet<LocalAttachmentSelection> AttachmentSelections => Set<LocalAttachmentSelection>();
    public DbSet<LocalSyncDiagnosticEvent> SyncDiagnosticEvents => Set<LocalSyncDiagnosticEvent>();
    public DbSet<LocalSyncOutboxEntry> SyncOutboxEntries => Set<LocalSyncOutboxEntry>();
    public DbSet<LocalTransaction> Transactions => Set<LocalTransaction>();
    public DbSet<LocalTransactionAttachment> TransactionAttachments => Set<LocalTransactionAttachment>();
    public DbSet<LocalOffice> Offices => Set<LocalOffice>();
    public DbSet<LocalWarehouse> Warehouses => Set<LocalWarehouse>();
    public DbSet<LocalInvoiceLineSerial> InvoiceLineSerials => Set<LocalInvoiceLineSerial>();
    public DbSet<LocalInventoryMovement> InventoryMovements => Set<LocalInventoryMovement>();
    public DbSet<LocalStockLayer> StockLayers => Set<LocalStockLayer>();
    public DbSet<LocalCostAllocation> CostAllocations => Set<LocalCostAllocation>();
    public DbSet<LocalItemWarehouseStock> ItemWarehouseStocks => Set<LocalItemWarehouseStock>();
    public DbSet<LocalSerialLedger> SerialLedgers => Set<LocalSerialLedger>();
    public DbSet<LocalAuditLog> AuditLogs => Set<LocalAuditLog>();
    public DbSet<LocalInventoryTransfer> InventoryTransfers => Set<LocalInventoryTransfer>();
    public DbSet<LocalInventoryTransferLine> InventoryTransferLines => Set<LocalInventoryTransferLine>();
    public DbSet<LocalRentalManagementCompany> RentalManagementCompanies => Set<LocalRentalManagementCompany>();
    public DbSet<LocalRentalBillingProfile> RentalBillingProfiles => Set<LocalRentalBillingProfile>();
    public DbSet<LocalRentalAsset> RentalAssets => Set<LocalRentalAsset>();
    public DbSet<LocalRentalAssetAssignmentHistory> RentalAssetAssignmentHistories => Set<LocalRentalAssetAssignmentHistory>();
    public DbSet<LocalRentalBillingLog> RentalBillingLogs => Set<LocalRentalBillingLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={AppPaths.LocalDbFile}");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Soft-delete query filters
        model.Entity<LocalCompanyProfile>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalUnit>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomerCategory>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalPriceGradeOption>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalTradeTypeOption>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalItemCategoryOption>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomerMaster>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomer>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomerContract>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalItem>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalInvoice>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalPayment>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalOffice>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalWarehouse>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalInventoryTransfer>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalTransactionAttachment>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalManagementCompany>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalBillingProfile>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalAsset>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalAssetAssignmentHistory>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalBillingLog>().HasQueryFilter(e => !e.IsDeleted);

        // InvoiceLine: no ILocalSyncEntity, filter inline
        model.Entity<LocalInvoiceLine>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalInventoryTransferLine>().HasQueryFilter(e => !e.IsDeleted);

        // Settings: key is PK
        model.Entity<LocalSetting>().HasKey(s => s.Key);
        model.Entity<LocalSyncDiagnosticEvent>().HasKey(e => e.Id);

        // Indexes for sync pull efficiency
        model.Entity<LocalCompanyProfile>().HasIndex(e => e.Revision);
        model.Entity<LocalUnit>().HasIndex(e => e.Revision);
        model.Entity<LocalPriceGradeOption>().HasIndex(e => e.Revision);
        model.Entity<LocalTradeTypeOption>().HasIndex(e => e.Revision);
        model.Entity<LocalItemCategoryOption>().HasIndex(e => e.Revision);
        model.Entity<LocalCustomer>().HasIndex(e => e.Revision);
        model.Entity<LocalCustomerContract>().HasIndex(e => e.Revision);
        model.Entity<LocalItem>().HasIndex(e => e.Revision);
        model.Entity<LocalInvoice>().HasIndex(e => e.Revision);
        model.Entity<LocalPayment>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalManagementCompany>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalBillingProfile>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalAsset>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalAssetAssignmentHistory>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalBillingLog>().HasIndex(e => e.Revision);

        // InvoiceLine owned by Invoice
        model.Entity<LocalInvoice>()
            .HasMany(i => i.Lines)
            .WithOne(l => l.Invoice)
            .HasForeignKey(l => l.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<LocalInvoice>()
            .HasMany(i => i.Payments)
            .WithOne(p => p.Invoice)
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<LocalTransaction>()
            .HasMany(transaction => transaction.Attachments)
            .WithOne(attachment => attachment.Transaction)
            .HasForeignKey(attachment => attachment.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<LocalInventoryTransfer>()
            .HasMany(t => t.Lines)
            .WithOne(l => l.Transfer)
            .HasForeignKey(l => l.TransferId)
            .OnDelete(DeleteBehavior.Cascade);

        // RecentSelections index
        model.Entity<LocalRecentSelection>()
            .HasIndex(r => new { r.EntityType, r.EntityId })
            .IsUnique();

        model.Entity<LocalAttachmentSelection>()
            .HasKey(s => new { s.CustomerKey, s.DocCode });
        model.Entity<LocalAttachmentSelection>()
            .HasIndex(s => s.CustomerKey);
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => e.LastOccurredAtUtc);
        model.Entity<LocalSyncOutboxEntry>().HasKey(entry => entry.Id);
        model.Entity<LocalSyncOutboxEntry>()
            .HasIndex(entry => entry.MutationId)
            .IsUnique();
        model.Entity<LocalSyncOutboxEntry>()
            .HasIndex(entry => new { entry.Status, entry.PreparedAtUtc });
        model.Entity<LocalSyncOutboxEntry>()
            .HasIndex(entry => new { entry.TenantCode, entry.OfficeCode, entry.ResponsibleOfficeCode, entry.Status, entry.PreparedAtUtc });
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => new { e.Status, e.LastOccurredAtUtc });
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => new { e.Category, e.Subcategory });
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => new { e.SyncPhase, e.Status });
        model.Entity<LocalCustomerContract>()
            .HasIndex(contract => contract.CustomerId);
        model.Entity<LocalCustomerContract>()
            .HasIndex(contract => new { contract.CustomerId, contract.IsPrimary });

        model.Entity<LocalPriceGradeOption>()
            .HasIndex(option => option.Name);
        model.Entity<LocalTradeTypeOption>()
            .HasIndex(option => option.Name);
        model.Entity<LocalItemCategoryOption>()
            .HasIndex(option => option.Name);
        model.Entity<LocalCompanyProfile>()
            .HasIndex(profile => new { profile.OfficeCode, profile.ProfileName });
        model.Entity<LocalCompanyProfile>()
            .HasIndex(profile => new { profile.OfficeCode, profile.IsDefaultForOffice });
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.OfficeCode);
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.ResponsibleOfficeCode);
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.NameOriginal);
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.NameMatchKey);
        model.Entity<LocalItem>()
            .HasIndex(item => new { item.TenantCode, item.OfficeCode });

        model.Entity<LocalOffice>()
            .HasIndex(o => o.Code)
            .IsUnique();
        model.Entity<LocalWarehouse>()
            .HasIndex(w => w.Code)
            .IsUnique();
        model.Entity<LocalWarehouse>()
            .HasIndex(w => w.OfficeCode);

        model.Entity<LocalInvoice>()
            .HasIndex(i => i.VersionGroupId);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.IsLatestVersion);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.LinkedRentalBillingProfileId);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.LinkedRentalBillingRunId);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.SourceWarehouseCode);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.PurchaseReceivingStatus);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.ResponsibleOfficeCode);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.OfficeCode);
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.TenantCode, i.ResponsibleOfficeCode, i.IsLatestVersion, i.InvoiceDate });
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.CustomerId, i.IsLatestVersion, i.InvoiceDate });
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.VoucherType, i.IsLatestVersion, i.InvoiceDate });
        model.Entity<LocalInvoice>()
            .Property(i => i.VatMode)
            .HasMaxLength(20)
            .HasDefaultValue(InvoiceVatModes.Included);

        model.Entity<LocalInvoiceLineSerial>()
            .HasIndex(s => new { s.InvoiceId, s.InvoiceLineId });
        model.Entity<LocalInvoiceLineSerial>()
            .HasIndex(s => s.SerialNumber);

        model.Entity<LocalInventoryMovement>()
            .HasIndex(m => new { m.ItemId, m.WarehouseCode, m.OccurredDate });
        model.Entity<LocalInventoryMovement>()
            .HasIndex(m => m.InvoiceId);

        model.Entity<LocalStockLayer>()
            .HasIndex(l => new { l.ItemId, l.WarehouseCode, l.ReceiptDate });
        model.Entity<LocalCostAllocation>()
            .HasIndex(a => new { a.SalesInvoiceId, a.SalesInvoiceLineId });
        model.Entity<LocalItemWarehouseStock>()
            .HasKey(s => new { s.ItemId, s.WarehouseCode });
        model.Entity<LocalSerialLedger>()
            .HasIndex(s => s.SerialNumber)
            .IsUnique();

        model.Entity<LocalRentalManagementCompany>()
            .HasIndex(company => company.Code)
            .IsUnique();
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => profile.ProfileKey)
            .IsUnique();
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => profile.OfficeCode);
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => profile.ResponsibleOfficeCode);
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.TenantCode, profile.ResponsibleOfficeCode, profile.IsDeleted, profile.IsActive });
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.TenantCode, profile.ManagementCompanyCode, profile.IsDeleted, profile.IsActive });
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.CustomerId, profile.IsDeleted });
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.BillingStatus, profile.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.AssetKey })
            .HasDatabaseName("IX_RentalAssets_AssetKey")
            .HasFilter("COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(TRIM(\"AssetKey\"), '') <> ''")
            .IsUnique();
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementId })
            .HasDatabaseName("IX_RentalAssets_ManagementId")
            .HasFilter("COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(TRIM(\"ManagementId\"), '') <> ''")
            .IsUnique();
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_ManagementNumber")
            .HasFilter("COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(TRIM(\"ManagementNumber\"), '') <> ''")
            .IsUnique();
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => asset.OfficeCode);
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => asset.ResponsibleOfficeCode);
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ResponsibleOfficeCode, asset.IsDeleted, asset.AssetStatus });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementCompanyCode, asset.IsDeleted, asset.AssetStatus });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ResponsibleOfficeCode, asset.IsDeleted, asset.CustomerName, asset.ManagementNumber });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementCompanyCode, asset.IsDeleted, asset.CustomerName, asset.ManagementNumber });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.CustomerName, asset.ManagementNumber });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.BillingProfileId, asset.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.CustomerId, asset.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.ItemCategoryName, asset.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.BillingEligibilityStatus, asset.IsDeleted });
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => new { history.AssetId, history.IsCurrent });
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => history.BillingProfileId);
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => history.LinkedAtUtc);
        model.Entity<LocalRentalBillingLog>()
            .HasIndex(log => new { log.BillingProfileId, log.BillingYearMonth })
            .IsUnique();
        model.Entity<LocalRentalBillingLog>()
            .HasIndex(log => log.OfficeCode);
        model.Entity<LocalRentalBillingLog>()
            .HasIndex(log => log.ResponsibleOfficeCode);
        model.Entity<LocalAuditLog>()
            .HasIndex(a => new { a.EntityName, a.EntityId, a.CreatedAtUtc });
        model.Entity<LocalInventoryTransfer>()
            .HasIndex(t => t.TransferDate);
        model.Entity<LocalInventoryTransfer>()
            .HasIndex(t => t.TransferNumber);
        model.Entity<LocalInventoryTransfer>()
            .HasIndex(t => new { t.FromWarehouseCode, t.ToWarehouseCode });
        model.Entity<LocalInventoryTransferLine>()
            .HasIndex(l => new { l.TransferId, l.ItemId });
        model.Entity<LocalTransactionAttachment>()
            .HasIndex(attachment => attachment.TransactionId);
        model.Entity<LocalTransactionAttachment>()
            .HasIndex(attachment => new { attachment.TransactionId, attachment.VerificationStatus });

        // Transactions
        model.Entity<LocalTransaction>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalTransaction>().HasIndex(e => e.CustomerId);
        model.Entity<LocalTransaction>().HasIndex(e => e.TransactionDate);
        model.Entity<LocalTransaction>().HasIndex(e => e.OfficeCode);
        model.Entity<LocalTransaction>().HasIndex(e => e.ResponsibleOfficeCode);
        model.Entity<LocalTransaction>().HasIndex(e => e.LinkedRentalBillingRunId);
        model.Entity<LocalTransactionAttachment>().HasIndex(e => e.Revision);
    }
}
