using Microsoft.EntityFrameworkCore;
using SalesMaster.Desktop.App.Infrastructure;

namespace SalesMaster.Desktop.App.Data;

public sealed class LocalDbContext : DbContext
{
    public DbSet<LocalCompanyProfile> CompanyProfiles => Set<LocalCompanyProfile>();
    public DbSet<LocalUnit> Units => Set<LocalUnit>();
    public DbSet<LocalCustomerCategory> CustomerCategories => Set<LocalCustomerCategory>();
    public DbSet<LocalCustomerMaster> CustomerMasters => Set<LocalCustomerMaster>();
    public DbSet<LocalCustomer> Customers => Set<LocalCustomer>();
    public DbSet<LocalItem> Items => Set<LocalItem>();
    public DbSet<LocalInvoice> Invoices => Set<LocalInvoice>();
    public DbSet<LocalInvoiceLine> InvoiceLines => Set<LocalInvoiceLine>();
    public DbSet<LocalPayment> Payments => Set<LocalPayment>();
    public DbSet<LocalPrintTemplate> PrintTemplates => Set<LocalPrintTemplate>();
    public DbSet<LocalPrintTemplateVersion> PrintTemplateVersions => Set<LocalPrintTemplateVersion>();
    public DbSet<LocalSetting> Settings => Set<LocalSetting>();
    public DbSet<LocalRecentSelection> RecentSelections => Set<LocalRecentSelection>();
    public DbSet<LocalAttachmentSelection> AttachmentSelections => Set<LocalAttachmentSelection>();
    public DbSet<LocalTransaction> Transactions => Set<LocalTransaction>();

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
        model.Entity<LocalCustomerMaster>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomer>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalItem>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalInvoice>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalPayment>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalPrintTemplate>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalPrintTemplateVersion>().HasQueryFilter(e => !e.IsDeleted);

        // InvoiceLine: no ILocalSyncEntity, filter inline
        model.Entity<LocalInvoiceLine>().HasQueryFilter(e => !e.IsDeleted);

        // Settings: key is PK
        model.Entity<LocalSetting>().HasKey(s => s.Key);

        // Indexes for sync pull efficiency
        model.Entity<LocalCompanyProfile>().HasIndex(e => e.Revision);
        model.Entity<LocalUnit>().HasIndex(e => e.Revision);
        model.Entity<LocalCustomer>().HasIndex(e => e.Revision);
        model.Entity<LocalItem>().HasIndex(e => e.Revision);
        model.Entity<LocalInvoice>().HasIndex(e => e.Revision);
        model.Entity<LocalPayment>().HasIndex(e => e.Revision);

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

        // RecentSelections index
        model.Entity<LocalRecentSelection>()
            .HasIndex(r => new { r.EntityType, r.EntityId })
            .IsUnique();

        model.Entity<LocalAttachmentSelection>()
            .HasKey(s => new { s.CustomerKey, s.DocCode });
        model.Entity<LocalAttachmentSelection>()
            .HasIndex(s => s.CustomerKey);

        // Transactions
        model.Entity<LocalTransaction>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalTransaction>().HasIndex(e => e.CustomerId);
        model.Entity<LocalTransaction>().HasIndex(e => e.TransactionDate);
    }
}
