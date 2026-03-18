using System.Text.Json;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
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
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<CustomerCategory> CustomerCategories => Set<CustomerCategory>();
    public DbSet<CustomerMaster> CustomerMasters => Set<CustomerMaster>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ConflictLog> ConflictLogs => Set<ConflictLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserPermission>().HasKey(x => new { x.UserId, x.Permission });
        modelBuilder.Entity<UserPermission>()
            .HasOne(x => x.User).WithMany(x => x.Permissions)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Username).IsUnique();

        modelBuilder.Entity<Invoice>()
            .HasMany(x => x.Lines).WithOne(x => x.Invoice)
            .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Invoice>()
            .HasMany(x => x.Payments).WithOne(x => x.Invoice)
            .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Invoice>().Property(x => x.TotalAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Invoice>().Property(x => x.SupplyAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Invoice>().Property(x => x.VatAmount).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLine>().Property(x => x.Quantity).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLine>().Property(x => x.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLine>().Property(x => x.LineAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Payment>().Property(x => x.Amount).HasPrecision(18, 2);

        modelBuilder.Entity<Customer>().HasIndex(x => x.NameMatchKey);
        modelBuilder.Entity<CustomerMaster>().HasIndex(x => x.NameMatchKey);
        modelBuilder.Entity<Item>().HasIndex(x => x.NameMatchKey);
        modelBuilder.Entity<Item>().HasIndex(x => x.SpecificationMatchKey);

        ApplySoftDeleteFilter<UserAccount>(modelBuilder);
        ApplySoftDeleteFilter<CompanyProfile>(modelBuilder);
        ApplySoftDeleteFilter<Unit>(modelBuilder);
        ApplySoftDeleteFilter<CustomerCategory>(modelBuilder);
        ApplySoftDeleteFilter<CustomerMaster>(modelBuilder);
        ApplySoftDeleteFilter<Customer>(modelBuilder);
        ApplySoftDeleteFilter<Item>(modelBuilder);
        ApplySoftDeleteFilter<Invoice>(modelBuilder);
        ApplySoftDeleteFilter<Payment>(modelBuilder);
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

        return entry.Entity is not AuditLog and not ConflictLog;
    }

    private AuditLog? BuildAuditLog(EntityEntry entry, DateTime now)
    {
        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey() || property.Metadata.Name == nameof(UserAccount.PasswordHash))
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
