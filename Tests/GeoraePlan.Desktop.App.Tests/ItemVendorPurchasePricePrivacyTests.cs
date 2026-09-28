using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ItemVendorPurchasePricePrivacyTests
{
    [Theory]
    [InlineData("none", false, false, 456, false)]
    [InlineData("sales", false, false, 456, false)]
    [InlineData("purchase", false, false, 456, true)]
    [InlineData("admin", false, false, 456, true)]
    [InlineData("purchase", true, false, 456, false)]
    [InlineData("purchase", false, true, 456, false)]
    [InlineData("admin", true, false, 456, false)]
    [InlineData("admin", false, true, 456, false)]
    [InlineData("purchase", true, true, 0, false)]
    public async Task LatestHistoryPreservesMetadataAndNeverFallsBackAcrossUnknownMoney(
        string grant, bool invoiceHidden, bool lineHidden, int price, bool visible)
    {
        await using var f = await Fixture.CreateAsync(grant);
        f.Latest.AmountsHidden = invoiceHidden;
        f.Latest.Lines.Single().AmountsHidden = lineHidden;
        f.Latest.Lines.Single().UnitPrice = price;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single(await f.Local.GetItemVendorPurchasePricesAsync(f.Item.Id, f.Session));
        Assert.Equal(visible ? price : (decimal?)null, row.UnitPrice);
        Assert.Equal(visible ? price.ToString("N0") : "비공개", row.UnitPriceDisplay);
        Assert.Equal("VENDOR", row.VendorName); Assert.Equal("LATEST", row.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 9, 24), row.LastPurchaseDate); Assert.Equal("EA", row.Unit);
        var defaults = await f.Local.GetLatestPurchasePriceByItemForCustomerAsync(f.Customer.Id, f.Session);
        if (visible && price > 0) Assert.Equal(price, defaults[f.Item.Id]); else Assert.Empty(defaults);
        var dates = await f.Local.GetItemConfirmedInvoiceDatesAsync(f.Item.Id, f.Session);
        Assert.Equal(row.LastPurchaseDate, dates.LastPurchaseDate);
        Assert.Equal(price, (await f.Db.InvoiceLines.AsNoTracking().SingleAsync(x => x.InvoiceId == f.Latest.Id)).UnitPrice);
        Assert.False(f.Latest.IsDirty); Assert.False(f.Item.IsDirty);
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task KnownZeroRetainsExistingPositivePriceCandidateRule()
    {
        await using var f = await Fixture.CreateAsync("purchase");
        f.Latest.Lines.Single().UnitPrice = 0m;
        await f.Db.SaveChangesAsync();
        var row = Assert.Single(await f.Local.GetItemVendorPurchasePricesAsync(f.Item.Id, f.Session));
        Assert.Equal(123m, row.UnitPrice); Assert.Equal("OLD", row.InvoiceNumber);
        Assert.Equal(123m, (await f.Local.GetLatestPurchasePriceByItemForCustomerAsync(f.Customer.Id, f.Session))[f.Item.Id]);
        Assert.Equal(new DateOnly(2026, 9, 24), (await f.Local.GetItemConfirmedInvoiceDatesAsync(f.Item.Id, f.Session)).LastPurchaseDate);
        Assert.Equal(0m, (await f.Db.InvoiceLines.AsNoTracking().SingleAsync(x => x.InvoiceId == f.Latest.Id)).UnitPrice);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("office")]
    [InlineData("deleted")]
    [InlineData("unconfirmed")]
    [InlineData("superseded")]
    public async Task IneligibleNewerHistoryDoesNotChangeReadableLatest(string reason)
    {
        await using var f = await Fixture.CreateAsync("purchase");
        if (reason == "tenant") f.Latest.TenantCode = TenantScopeCatalog.Itworld;
        if (reason == "office") f.Latest.OfficeCode = f.Latest.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        if (reason == "deleted") f.Latest.IsDeleted = true;
        if (reason == "unconfirmed") f.Latest.IsConfirmed = false;
        if (reason == "superseded") f.Latest.IsLatestVersion = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal(123m, Assert.Single(await f.Local.GetItemVendorPurchasePricesAsync(f.Item.Id, f.Session)).UnitPrice);
        Assert.Equal(123m, (await f.Local.GetLatestPurchasePriceByItemForCustomerAsync(f.Customer.Id, f.Session))[f.Item.Id]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InFlightHistoryIsDiscardedOnRevocationOrAccountChange(bool defaults, bool switchAccount)
    {
        var gate = new ReadGate();
        await using var f = await Fixture.CreateAsync("purchase", gate);
        gate.Enabled = true;
        var task = defaults ? (Task)f.Local.GetLatestPurchasePriceByItemForCustomerAsync(f.Customer.Id, f.Session)
            : f.Local.GetItemVendorPurchasePricesAsync(f.Item.Id, f.Session);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Session.RefreshSession("changed", Fixture.User(switchAccount ? Guid.NewGuid() : f.Session.User!.UserId,
                switchAccount ? "purchase" : "none"));
        }
        finally { gate.Release.TrySetResult(); }
        await task;
        if (defaults) Assert.Empty(await (Task<IReadOnlyDictionary<Guid, decimal>>)task);
        else Assert.Empty(await (Task<IReadOnlyList<ItemVendorPurchasePriceRow>>)task);
    }

    [Fact]
    public async Task InventoryClearsRenderedPricesOnRevocationButNotTokenRefresh()
    {
        await using var f = await Fixture.CreateAsync("purchase");
        using var vm = new InventoryViewModel(f.Local, f.Session);
        foreach (var row in await f.Local.GetItemVendorPurchasePricesAsync(f.Item.Id, f.Session)) vm.SelectedItemVendorPurchasePrices.Add(row);
        f.Session.RefreshSession("token", Fixture.User(f.Session.User!.UserId, "purchase"));
        Assert.Single(vm.SelectedItemVendorPurchasePrices);
        f.Session.RefreshSession("revoked", Fixture.User(f.Session.User!.UserId, "none"));
        Assert.Empty(vm.SelectedItemVendorPurchasePrices);
        Assert.False(f.Item.IsDirty); Assert.False(f.Latest.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryCannotRepopulatePricesAfterRevocationOrDisposal(bool dispose)
    {
        var gate = new ReadGate();
        await using var f = await Fixture.CreateAsync("purchase", gate);
        using var vm = new InventoryViewModel(f.Local, f.Session);
        typeof(InventoryViewModel).GetField("_selectedItem", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm,
            new InventoryItemRow(f.Item, new Dictionary<string, decimal>(), OfficeCodeCatalog.Usenet));
        gate.Enabled = true;
        var task = (Task)typeof(InventoryViewModel).GetMethod("LoadSelectedItemVendorPurchasePricesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, new object[] { f.Item.Id, 0 })!;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) vm.Dispose(); else f.Session.RefreshSession("revoked", Fixture.User(f.Session.User!.UserId, "none"));
        }
        finally { gate.Release.TrySetResult(); }
        if (dispose) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); else await task;
        Assert.Empty(vm.SelectedItemVendorPurchasePrices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorClearsExistingVendorCacheOnRevocationOrDisposal(bool dispose)
    {
        await using var f = await Fixture.CreateAsync("purchase");
        using var vm = new SalesViewModel(f.Local, null!, null!, f.Session, VoucherType.Purchase);
        var cache = (Dictionary<Guid, decimal>)typeof(SalesViewModel).GetField("_customerPurchasePriceByItem", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        cache.Add(f.Item.Id, 456m);
        if (dispose) vm.Dispose(); else f.Session.RefreshSession("revoked", Fixture.User(f.Session.User!.UserId, "none"));
        Assert.Empty(cache);
    }

    private sealed class ReadGate : DbCommandInterceptor
    {
        internal bool Enabled;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("\"InvoiceLines\"") && command.CommandText.Contains("\"Customers\""))
            { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        internal LocalDbContext Db { get; }
        internal LocalStateService Local { get; }
        internal SessionState Session { get; }
        internal LocalCustomer Customer { get; }
        internal LocalItem Item { get; }
        internal LocalInvoice Latest { get; }
        private Fixture(SqliteConnection connection, LocalDbContext db, SessionState session, LocalCustomer customer, LocalItem item, LocalInvoice latest)
        { _connection = connection; Db = db; Session = session; Customer = customer; Item = item; Latest = latest;
            Local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session); }
        internal static UserSessionDto User(Guid id, string grant) => new()
        {
            UserId = id, Username = "vendor-fixture", Role = grant == "admin" ? DomainConstants.RoleAdmin : DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = grant == "admin" ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { AppPermissionNames.ItemEdit }
                .Concat(grant == "sales" ? new[] { AppPermissionNames.AmountViewSales } : [])
                .Concat(grant == "purchase" ? new[] { AppPermissionNames.AmountViewPurchase } : []).ToList()
        };
        internal static async Task<Fixture> CreateAsync(string grant, DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var db = new LocalDbContext(options.Options); await db.Database.EnsureCreatedAsync();
            var customer = new LocalCustomer { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "VENDOR", NameMatchKey = "VENDOR", TradeType = CustomerTradeTypes.Purchase, IsDirty = false };
            var item = new LocalItem { Id = Guid.NewGuid(), TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
                NameOriginal = "ITEM", NameMatchKey = "ITEM", IsDirty = false };
            LocalInvoice Invoice(int day, decimal price) => new() { Id = Guid.NewGuid(), CustomerId = customer.Id,
                TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode, ResponsibleOfficeCode = customer.OfficeCode,
                VoucherType = VoucherType.Purchase, InvoiceDate = new DateOnly(2026, 9, day), InvoiceNumber = day == 24 ? "LATEST" : "OLD",
                IsDirty = false, Lines = new List<LocalInvoiceLine> { new() { ItemId = item.Id, UnitPrice = price, Unit = "EA", Quantity = 2m } } };
            var latest = Invoice(24, 456m);
            db.Customers.Add(customer); db.Items.Add(item); db.Invoices.AddRange(Invoice(23, 123m), latest); await db.SaveChangesAsync();
            var session = new SessionState(); session.SetSession("fixture", User(Guid.NewGuid(), grant));
            return new Fixture(connection, db, session, customer, item, latest);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
