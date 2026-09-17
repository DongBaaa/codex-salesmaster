using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceVersionRefreshTests
{
    [Theory]
    [InlineData("USENET", false)]
    [InlineData("USENET", true)]
    [InlineData("ITWORLD", true)]
    [InlineData("YEONSU", true)]
    public async Task ReceivingInheritsAcknowledgedRevision(string office, bool keepTracked)
    {
        await using var fixture = await Fixture.CreateAsync(office);
        var original = await fixture.Db.Invoices.SingleAsync();
        await fixture.AcknowledgeAsync(123456789);
        Assert.Equal(123456789, await fixture.Db.Invoices.AsNoTracking().Select(x => x.Revision).SingleAsync());
        if (!keepTracked) fixture.Db.ChangeTracker.Clear();

        var result = await fixture.Local.SaveInvoiceAsync(fixture.Draft(), fixture.Context(), fixture.Session);

        Assert.True(result.Success, result.Message);
        var saved = await fixture.Db.Invoices.AsNoTracking().SingleAsync(x => x.Id == result.SavedInvoiceId);
        Assert.Equal(123456789, saved.Revision);
        Assert.Equal(original.Id, saved.PreviousVersionId);
        Assert.Equal(2, saved.VersionNumber);
        Assert.Equal(InvoiceReceivingStatuses.Confirmed, saved.PurchaseReceivingStatus);
        Assert.Equal(office, saved.ResponsibleOfficeCode);
        Assert.Equal(office == "YEONSU" ? "USENET" : office, saved.OfficeCode);
    }

    [Fact]
    public async Task OtherWriterStampIsCheckedAfterRefresh()
    {
        await using var fixture = await Fixture.CreateAsync("USENET");
        await fixture.AcknowledgeAsync(200, "other-writer");

        var result = await fixture.Local.SaveInvoiceAsync(fixture.Draft(), fixture.Context(), fixture.Session);

        Assert.False(result.Success);
        Assert.True(result.ConcurrencyConflict);
        Assert.Equal(1, await fixture.Db.Invoices.CountAsync());
        Assert.Equal("other-writer", (await fixture.Db.Invoices.AsNoTracking().SingleAsync()).ConcurrencyStamp);
    }

    [Fact]
    public async Task TrackedDraftAndUnrelatedPendingEditArePreserved()
    {
        await using var fixture = await Fixture.CreateAsync("USENET");
        var original = await fixture.Db.Invoices.SingleAsync();
        original.Memo = "unsaved draft";
        original.PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed;
        var unrelated = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "unrelated pending item", TenantCode = "USENET_GROUP", OfficeCode = "USENET" };
        fixture.Db.Items.Add(unrelated);

        var result = await fixture.Local.SaveInvoiceAsync(original, fixture.Context(), fixture.Session);

        Assert.True(result.Success, result.Message);
        var saved = await fixture.Db.Invoices.AsNoTracking().SingleAsync(x => x.Id == result.SavedInvoiceId);
        Assert.Equal("unsaved draft", saved.Memo);
        Assert.Equal(InvoiceReceivingStatuses.Confirmed, saved.PurchaseReceivingStatus);
        Assert.Equal("unrelated pending item", (await fixture.Db.Items.SingleAsync()).NameOriginal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<LocalDbContext> options;
        private readonly string office;
        public LocalDbContext Db { get; }
        public LocalStateService Local { get; }
        public SessionState Session { get; }
        private Guid invoiceId;
        private Guid customerId;
        private string stamp = "";
        private Fixture(SqliteConnection connection, DbContextOptions<LocalDbContext> options, string office)
        {
            this.connection = connection; this.options = options; this.office = office;
            Db = new LocalDbContext(options);
            Session = new SessionState();
            Session.SetOfflineSession(new UserSessionDto { Username = "admin", Role = DomainConstants.RoleAdmin, TenantCode = Tenant, OfficeCode = office, ScopeType = TenantScopeCatalog.ScopeAdmin });
            Local = new LocalStateService(Db, new OfficeAccessService(), new SyncRequestDispatcher(), Session);
        }
        private string Tenant => office == "ITWORLD" ? "ITWORLD" : "USENET_GROUP";
        private string Owner => office == "YEONSU" ? "USENET" : office;
        public static async Task<Fixture> CreateAsync(string office)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
            var fixture = new Fixture(connection, options, office);
            await fixture.Db.Database.EnsureCreatedAsync();
            fixture.customerId = Guid.NewGuid(); fixture.invoiceId = Guid.NewGuid();
            fixture.Db.Customers.Add(new LocalCustomer { Id = fixture.customerId, NameOriginal = "revision test", TenantCode = fixture.Tenant, OfficeCode = fixture.Owner, ResponsibleOfficeCode = office });
            fixture.Db.Invoices.Add(new LocalInvoice { Id = fixture.invoiceId, CustomerId = fixture.customerId, TenantCode = fixture.Tenant, OfficeCode = fixture.Owner, ResponsibleOfficeCode = office, SourceWarehouseCode = office + "_MAIN", VersionGroupId = fixture.invoiceId, VersionNumber = 1, IsLatestVersion = true, VoucherType = VoucherType.Purchase, InvoiceDate = new DateOnly(2026, 9, 12), PurchaseReceivingRequired = true, PurchaseReceivingStatus = InvoiceReceivingStatuses.Pending, Revision = 0, CreatedByUsername = "admin", LastSavedByUsername = "admin", ConcurrencyStamp = "original" });
            await fixture.Db.SaveChangesAsync();
            fixture.stamp = (await fixture.Db.Invoices.SingleAsync()).ConcurrencyStamp;
            return fixture;
        }
        public async Task AcknowledgeAsync(long revision, string? newStamp = null)
        {
            await using var sync = new LocalDbContext(options);
            Assert.Equal(1, await sync.Database.ExecuteSqlInterpolatedAsync($"UPDATE Invoices SET Revision={revision}, IsDirty={false}, ConcurrencyStamp={newStamp ?? stamp} WHERE Id={invoiceId}"));
        }
        public InvoiceSaveContext Context() => new() { Username = "admin", Role = DomainConstants.RoleAdmin, OfficeCode = office, ExpectedConcurrencyStamp = stamp };
        public LocalInvoice Draft() => new() { Id = invoiceId, CustomerId = customerId, TenantCode = Tenant, OfficeCode = Owner, ResponsibleOfficeCode = office, SourceWarehouseCode = office + "_MAIN", VersionGroupId = invoiceId, VoucherType = VoucherType.Purchase, InvoiceDate = new DateOnly(2026, 9, 12), PurchaseReceivingRequired = true, PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed, PurchaseReceivingOfficeCode = office, PurchaseReceivingWarehouseCode = office + "_MAIN", PurchaseReceivedByUsername = "admin", PurchaseReceivedAtUtc = DateTime.UtcNow, Memo = "receiving draft" };
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
