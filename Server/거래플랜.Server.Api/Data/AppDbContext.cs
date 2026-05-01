using System.Text.Json;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace 거래플랜.Server.Api.Data;

public sealed class AppDbContext : DbContext
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new() { WriteIndented = false };

    private readonly ICurrentUserContext _currentUserContext;
    private readonly RevisionClock _revisionClock;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        ICurrentUserContext currentUserContext,
        RevisionClock revisionClock)
        : base(options)
    {
        _currentUserContext = currentUserContext;
        _revisionClock = revisionClock;
    }

    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();
    public DbSet<TenantDefinition> TenantDefinitions => Set<TenantDefinition>();
    public DbSet<TenantOfficeDefinition> TenantOfficeDefinitions => Set<TenantOfficeDefinition>();
    public DbSet<DataSharingPolicy> DataSharingPolicies => Set<DataSharingPolicy>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<CustomerCategory> CustomerCategories => Set<CustomerCategory>();
    public DbSet<PriceGradeOption> PriceGradeOptions => Set<PriceGradeOption>();
    public DbSet<TradeTypeOption> TradeTypeOptions => Set<TradeTypeOption>();
    public DbSet<ItemCategoryOption> ItemCategoryOptions => Set<ItemCategoryOption>();
    public DbSet<CustomerMaster> CustomerMasters => Set<CustomerMaster>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerContract> CustomerContracts => Set<CustomerContract>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemWarehouseStock> ItemWarehouseStocks => Set<ItemWarehouseStock>();
    public DbSet<TransactionRecord> Transactions => Set<TransactionRecord>();
    public DbSet<TransactionAttachment> TransactionAttachments => Set<TransactionAttachment>();
    public DbSet<InventoryTransfer> InventoryTransfers => Set<InventoryTransfer>();
    public DbSet<InventoryTransferLine> InventoryTransferLines => Set<InventoryTransferLine>();
    public DbSet<RentalManagementCompany> RentalManagementCompanies => Set<RentalManagementCompany>();
    public DbSet<RentalBillingProfile> RentalBillingProfiles => Set<RentalBillingProfile>();
    public DbSet<RentalAsset> RentalAssets => Set<RentalAsset>();
    public DbSet<RentalBillingLog> RentalBillingLogs => Set<RentalBillingLog>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAttachment> PaymentAttachments => Set<PaymentAttachment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ConflictLog> ConflictLogs => Set<ConflictLog>();
    public DbSet<RecycleBinPurgeRecord> RecycleBinPurgeRecords => Set<RecycleBinPurgeRecord>();
    public DbSet<ProcessedSyncMutation> ProcessedSyncMutations => Set<ProcessedSyncMutation>();
    public DbSet<InventoryLedgerEntry> InventoryLedgerEntries => Set<InventoryLedgerEntry>();
    public DbSet<RentalAssetAssignmentHistory> RentalAssetAssignmentHistories => Set<RentalAssetAssignmentHistory>();
    public DbSet<ActiveEditSession> ActiveEditSessions => Set<ActiveEditSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserPermission>().HasKey(x => new { x.UserId, x.Permission });
        modelBuilder.Entity<UserPermission>()
            .HasOne(x => x.User).WithMany(x => x.Permissions)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<UserAccount>().HasIndex(x => new { x.TenantCode, x.OfficeCode });
        modelBuilder.Entity<CompanyProfile>().HasIndex(x => new { x.OfficeCode, x.ProfileName });
        modelBuilder.Entity<CompanyProfile>().HasIndex(x => new { x.OfficeCode, x.IsDefaultForOffice });
        modelBuilder.Entity<TenantDefinition>().HasIndex(x => x.TenantCode).IsUnique();
        modelBuilder.Entity<TenantOfficeDefinition>().HasIndex(x => x.OfficeCode).IsUnique();
        modelBuilder.Entity<TenantOfficeDefinition>().HasIndex(x => new { x.TenantCode, x.OfficeCode }).IsUnique();
        modelBuilder.Entity<PriceGradeOption>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<TradeTypeOption>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<ItemCategoryOption>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<DataSharingPolicy>().HasIndex(x => new
        {
            x.SourceTenantCode,
            x.SourceOfficeCode,
            x.TargetTenantCode,
            x.TargetOfficeCode
        });

        modelBuilder.Entity<Invoice>()
            .HasMany(x => x.Lines).WithOne(x => x.Invoice)
            .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Invoice>()
            .HasMany(x => x.Payments).WithOne(x => x.Invoice)
            .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Customer>()
            .HasMany(x => x.Contracts).WithOne(x => x.Customer)
            .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ItemWarehouseStock>()
            .HasOne(x => x.Item).WithMany()
            .HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Payment>()
            .HasMany(x => x.Attachments).WithOne(x => x.Payment)
            .HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TransactionRecord>()
            .HasMany(x => x.Attachments).WithOne(x => x.Transaction)
            .HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InventoryTransfer>()
            .HasMany(x => x.Lines).WithOne(x => x.Transfer)
            .HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Invoice>().Property(x => x.TotalAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Invoice>().Property(x => x.SupplyAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Invoice>().Property(x => x.VatAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Invoice>().Property(x => x.VatMode).HasMaxLength(20).HasDefaultValue(InvoiceVatModes.Included);
        modelBuilder.Entity<Item>().Property(x => x.CurrentStock).HasPrecision(18, 2);
        modelBuilder.Entity<Item>().Property(x => x.SafetyStock).HasPrecision(18, 2);
        modelBuilder.Entity<Item>().Property(x => x.PurchasePrice).HasPrecision(18, 2);
        modelBuilder.Entity<Item>().Property(x => x.SalePrice).HasPrecision(18, 2);
        modelBuilder.Entity<Item>().Property(x => x.RetailPrice).HasPrecision(18, 2);
        modelBuilder.Entity<Item>().Property(x => x.PriceGradeA).HasPrecision(18, 2);
        modelBuilder.Entity<Item>().Property(x => x.PriceGradeB).HasPrecision(18, 2);
        modelBuilder.Entity<Item>().Property(x => x.PriceGradeC).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLine>().Property(x => x.Quantity).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLine>().Property(x => x.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLine>().Property(x => x.LineAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Payment>().Property(x => x.Amount).HasPrecision(18, 2);
        modelBuilder.Entity<ItemWarehouseStock>().Property(x => x.Quantity).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.SettlementAmount).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.AdvanceDelta).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.PrepaidDelta).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.CashReceipt).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.CardReceipt).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.BankReceipt).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.DiscountApplied).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.ReceiptTotal).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.CashPayment).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.CardPayment).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.BankPayment).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.DiscountReceived).HasPrecision(18, 2);
        modelBuilder.Entity<TransactionRecord>().Property(x => x.PaymentTotal).HasPrecision(18, 2);
        modelBuilder.Entity<InventoryTransferLine>().Property(x => x.Quantity).HasPrecision(18, 2);
        modelBuilder.Entity<InventoryTransferLine>().Property(x => x.ReceivedQuantity).HasPrecision(18, 2);
        modelBuilder.Entity<InventoryTransferLine>().Property(x => x.QuantityDifference).HasPrecision(18, 2);
        modelBuilder.Entity<RentalBillingProfile>().Property(x => x.MonthlyAmount).HasPrecision(18, 2);
        modelBuilder.Entity<RentalBillingProfile>().Property(x => x.DepositAmount).HasPrecision(18, 2);
        modelBuilder.Entity<RentalBillingProfile>().Property(x => x.SettledAmount).HasPrecision(18, 2);
        modelBuilder.Entity<RentalBillingProfile>().Property(x => x.OutstandingAmount).HasPrecision(18, 2);
        modelBuilder.Entity<RentalAsset>().Property(x => x.PurchasePrice).HasPrecision(18, 2);
        modelBuilder.Entity<RentalAsset>().Property(x => x.SalePrice).HasPrecision(18, 2);
        modelBuilder.Entity<RentalAsset>().Property(x => x.MonthlyFee).HasPrecision(18, 2);
        modelBuilder.Entity<RentalAssetAssignmentHistory>().Property(x => x.MonthlyFee).HasPrecision(18, 2);
        modelBuilder.Entity<RentalBillingLog>().Property(x => x.BilledAmount).HasPrecision(18, 2);
        modelBuilder.Entity<InventoryLedgerEntry>().Property(x => x.QuantityDelta).HasPrecision(18, 2);

        modelBuilder.Entity<Customer>().HasIndex(x => x.NameMatchKey);
        modelBuilder.Entity<Customer>().HasIndex(x => x.TenantCode);
        modelBuilder.Entity<Customer>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<Customer>().HasIndex(x => x.ResponsibleOfficeCode);
        modelBuilder.Entity<CustomerContract>().HasIndex(x => x.CustomerId);
        modelBuilder.Entity<CustomerContract>().HasIndex(x => new { x.CustomerId, x.IsPrimary });
        modelBuilder.Entity<CustomerMaster>().HasIndex(x => x.NameMatchKey);
        modelBuilder.Entity<CustomerMaster>().HasIndex(x => x.TenantCode);
        modelBuilder.Entity<CustomerMaster>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<Item>().HasIndex(x => x.NameMatchKey);
        modelBuilder.Entity<Item>().HasIndex(x => x.SpecificationMatchKey);
        modelBuilder.Entity<Item>().HasIndex(x => x.CategoryName);
        modelBuilder.Entity<Item>().HasIndex(x => x.TenantCode);
        modelBuilder.Entity<Item>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.TenantCode);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.ResponsibleOfficeCode);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.SourceWarehouseCode);
        modelBuilder.Entity<ItemWarehouseStock>().HasKey(x => new { x.ItemId, x.WarehouseCode });
        modelBuilder.Entity<ItemWarehouseStock>().HasIndex(x => x.WarehouseCode);
        modelBuilder.Entity<PaymentAttachment>().HasIndex(x => x.PaymentId);
        modelBuilder.Entity<TransactionRecord>().HasIndex(x => x.CustomerId);
        modelBuilder.Entity<TransactionRecord>().HasIndex(x => x.TenantCode);
        modelBuilder.Entity<TransactionRecord>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<TransactionRecord>().HasIndex(x => x.ResponsibleOfficeCode);
        modelBuilder.Entity<TransactionAttachment>().HasIndex(x => x.TransactionId);
        modelBuilder.Entity<InventoryTransfer>().HasIndex(x => x.TenantCode);
        modelBuilder.Entity<InventoryTransfer>().HasIndex(x => x.SourceOfficeCode);
        modelBuilder.Entity<InventoryTransfer>().HasIndex(x => x.TargetOfficeCode);
        modelBuilder.Entity<InventoryTransfer>().HasIndex(x => x.TransferNumber);
        modelBuilder.Entity<InventoryTransferLine>().HasIndex(x => x.TransferId);
        modelBuilder.Entity<RentalManagementCompany>().HasIndex(x => new { x.TenantCode, x.Code }).IsUnique();
        modelBuilder.Entity<RentalBillingProfile>().HasIndex(x => new { x.TenantCode, x.ProfileKey }).IsUnique();
        modelBuilder.Entity<RentalBillingProfile>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<RentalBillingProfile>().HasIndex(x => x.ResponsibleOfficeCode);
        modelBuilder.Entity<RentalAsset>().HasIndex(x => new { x.TenantCode, x.AssetKey }).IsUnique();
        modelBuilder.Entity<RentalAsset>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<RentalAsset>().HasIndex(x => x.ResponsibleOfficeCode);
        modelBuilder.Entity<RentalBillingLog>().HasIndex(x => new { x.BillingProfileId, x.BillingYearMonth }).IsUnique();
        modelBuilder.Entity<RentalBillingLog>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<RentalBillingLog>().HasIndex(x => x.ResponsibleOfficeCode);
        modelBuilder.Entity<RecycleBinPurgeRecord>().HasIndex(x => new { x.Kind, x.EntityId }).IsUnique();
        modelBuilder.Entity<RecycleBinPurgeRecord>().HasIndex(x => x.TenantCode);
        modelBuilder.Entity<RecycleBinPurgeRecord>().HasIndex(x => x.OfficeCode);
        modelBuilder.Entity<ProcessedSyncMutation>().HasIndex(x => x.MutationId).IsUnique();
        modelBuilder.Entity<InventoryLedgerEntry>().HasIndex(x => new { x.ItemId, x.OccurredDate });
        modelBuilder.Entity<InventoryLedgerEntry>().HasIndex(x => x.WarehouseCode);
        modelBuilder.Entity<RentalAssetAssignmentHistory>().HasIndex(x => new { x.AssetId, x.IsCurrent });
        modelBuilder.Entity<RentalAssetAssignmentHistory>().HasIndex(x => x.BillingProfileId);
        modelBuilder.Entity<RentalAssetAssignmentHistory>().HasIndex(x => x.Revision);
        modelBuilder.Entity<ActiveEditSession>().HasIndex(x => new { x.EntityType, x.EntityId, x.ExpiresAtUtc });
        modelBuilder.Entity<ActiveEditSession>().HasIndex(x => x.LastHeartbeatUtc);
        modelBuilder.Entity<ActiveEditSession>().HasIndex(x => x.Username);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.VersionGroupId);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.IsLatestVersion);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.LinkedRentalBillingProfileId);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.LinkedRentalBillingRunId);

        ApplySoftDeleteFilter<UserAccount>(modelBuilder);
        ApplySoftDeleteFilter<CompanyProfile>(modelBuilder);
        ApplySoftDeleteFilter<TenantDefinition>(modelBuilder);
        ApplySoftDeleteFilter<TenantOfficeDefinition>(modelBuilder);
        ApplySoftDeleteFilter<DataSharingPolicy>(modelBuilder);
        ApplySoftDeleteFilter<Unit>(modelBuilder);
        ApplySoftDeleteFilter<CustomerCategory>(modelBuilder);
        ApplySoftDeleteFilter<PriceGradeOption>(modelBuilder);
        ApplySoftDeleteFilter<TradeTypeOption>(modelBuilder);
        ApplySoftDeleteFilter<ItemCategoryOption>(modelBuilder);
        ApplySoftDeleteFilter<CustomerMaster>(modelBuilder);
        ApplySoftDeleteFilter<Customer>(modelBuilder);
        ApplySoftDeleteFilter<CustomerContract>(modelBuilder);
        ApplySoftDeleteFilter<Item>(modelBuilder);
        ApplySoftDeleteFilter<TransactionRecord>(modelBuilder);
        ApplySoftDeleteFilter<TransactionAttachment>(modelBuilder);
        ApplySoftDeleteFilter<InventoryTransfer>(modelBuilder);
        ApplySoftDeleteFilter<RentalManagementCompany>(modelBuilder);
        ApplySoftDeleteFilter<RentalBillingProfile>(modelBuilder);
        ApplySoftDeleteFilter<RentalAsset>(modelBuilder);
        ApplySoftDeleteFilter<RentalAssetAssignmentHistory>(modelBuilder);
        ApplySoftDeleteFilter<RentalBillingLog>(modelBuilder);
        ApplySoftDeleteFilter<Invoice>(modelBuilder);
        ApplySoftDeleteFilter<Payment>(modelBuilder);
        ApplySoftDeleteFilter<PaymentAttachment>(modelBuilder);
    }

    public override int SaveChanges()
    {
        PrepareTrackedEntityState();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        PrepareTrackedEntityState();
        return base.SaveChangesAsync(cancellationToken);
    }

    private static void ApplySoftDeleteFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : TrackedEntity
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(x => !x.IsDeleted);
    }

    private void PrepareTrackedEntityState()
    {
        var now = DateTime.UtcNow;
        var auditLogs = new List<AuditLog>();

        foreach (var entry in ChangeTracker.Entries().Where(IsAuditableEntry))
        {
            if (entry.Entity is TrackedEntity trackedEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    trackedEntity.CreatedAtUtc = now;
                }

                trackedEntity.UpdatedAtUtc = now;
                trackedEntity.Revision = _revisionClock.NextRevision();
            }

            var audit = BuildAuditLog(entry, now);
            if (audit is not null)
            {
                auditLogs.Add(audit);
            }
        }

        if (auditLogs.Count > 0)
        {
            AuditLogs.AddRange(auditLogs);
        }
    }

    private static bool IsAuditableEntry(EntityEntry entry)
    {
        if (entry.State != EntityState.Added &&
            entry.State != EntityState.Modified &&
            entry.State != EntityState.Deleted)
        {
            return false;
        }

        return entry.Entity is not AuditLog
               and not ConflictLog
               and not ProcessedSyncMutation
               and not InventoryLedgerEntry
               and not ActiveEditSession;
    }

    private AuditLog? BuildAuditLog(EntityEntry entry, DateTime now)
    {
        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey() ||
                property.Metadata.Name == nameof(UserAccount.PasswordHash) ||
                property.Metadata.ClrType == typeof(byte[]))
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    after[property.Metadata.Name] = property.CurrentValue;
                    break;
                case EntityState.Deleted:
                    before[property.Metadata.Name] = property.OriginalValue;
                    break;
                case EntityState.Modified:
                    if (property.IsModified)
                    {
                        before[property.Metadata.Name] = property.OriginalValue;
                        after[property.Metadata.Name] = property.CurrentValue;
                    }

                    break;
            }
        }

        if (before.Count == 0 && after.Count == 0)
        {
            return null;
        }

        return new AuditLog
        {
            UserId = _currentUserContext.UserId,
            Username = _currentUserContext.Username,
            EntityName = entry.Metadata.ClrType.Name,
            EntityId = entry.Properties.FirstOrDefault(x => x.Metadata.IsPrimaryKey())?.CurrentValue?.ToString() ?? string.Empty,
            Action = entry.State.ToString(),
            BeforeJson = JsonSerializer.Serialize(before, AuditJsonOptions),
            AfterJson = JsonSerializer.Serialize(after, AuditJsonOptions),
            CreatedAtUtc = now
        };
    }
}
