using System.Data.Common;
using System.Net;
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

public sealed class InvoiceLedgerPrivacyTests
{
    private static UserSessionDto User(Guid id, bool sales, bool purchase) => new()
    {
        UserId = id, Username = "ledger-fixture", Role = DomainConstants.RoleUser,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = new[] { AppPermissionNames.InvoiceEdit, sales ? AppPermissionNames.AmountViewSales : "", purchase ? AppPermissionNames.AmountViewPurchase : "" }.Where(p => p.Length > 0).ToList()
    };

    [Theory]
    [InlineData(VoucherType.Sales, false, true, true)]
    [InlineData(VoucherType.Sales, true, false, false)]
    [InlineData(VoucherType.Purchase, true, false, true)]
    [InlineData(VoucherType.Purchase, false, true, false)]
    [InlineData(VoucherType.Procurement, true, false, true)]
    [InlineData(VoucherType.Expense, false, true, false)]
    public void CachedInvoiceRowsRespectCurrentDirection(VoucherType type, bool sales, bool purchase, bool hidden)
    {
        var session = new SessionState(); session.SetSession("fixture", User(Guid.NewGuid(), sales, purchase));
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), VoucherType = type, TotalAmount = 500m };
        var summary = new LocalInvoiceListSummary { Id = invoice.Id, VoucherType = type, TotalAmount = 500m };
        foreach (var row in new[] { InvoiceListRow.From(invoice, "Fixture", true, session), InvoiceListRow.From(summary, "Fixture", true, session) })
        {
            Assert.Equal(hidden, row.AmountsHidden);
            Assert.Equal(hidden ? null : (decimal?)500m, row.TotalAmount);
            Assert.Equal(hidden ? "비공개" : "500", row.TotalAmountDisplay);
        }
        Assert.Equal(500m, invoice.TotalAmount);
        Assert.True(session.HasPermission(AppPermissionNames.InvoiceEdit));
    }

    [Theory]
    [InlineData("일반수금", 500, 0, true, false, false)]
    [InlineData("일반지급", 0, 500, true, false, true)]
    [InlineData("일반지급", 0, 500, false, true, false)]
    [InlineData("일반수금", 500, 1, true, false, true)]
    [InlineData("unknown", 500, 0, true, false, true)]
    public void CachedTransactionRowsDoNotDiscloseMixedOrWrongDirection(string kind, int receipt, int payment, bool sales, bool purchase, bool hidden)
    {
        var session = new SessionState(); session.SetSession("fixture", User(Guid.NewGuid(), sales, purchase));
        var tx = new LocalTransaction { TransactionKind = kind, ReceiptTotal = receipt, PaymentTotal = payment };
        var row = InvoiceListRow.From(tx, "Fixture", true, session);
        Assert.Equal(hidden, row.AmountsHidden);
        if (hidden) { Assert.Null(row.ReceiptAmount); Assert.Null(row.PaymentAmount); Assert.Null(row.BalanceAmount); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationClearsRenderedValuesAndTokenRefreshKeepsThem(bool main)
    {
        await using var f = await Fixture.CreateAsync();
        var vm = main ? (object)f.Main : f.Lookup;
        Set(vm, "PreviewTotalAmount", 12345m); Set(vm, "PreviewSupplyAmount", 11222m); Set(vm, "PreviewVatAmount", 1123m);
        if (main)
        {
            f.Main.InvoiceRows.Add(InvoiceListRow.From(f.Invoice, "Fixture", true, f.Session));
            f.Main.DashboardReceivable = 12345m;
        }
        else f.Lookup.InvoiceRows.Add(InvoiceListRow.From(f.Invoice, "Fixture", true, f.Session));
        f.Session.RefreshSession("new-token", User(f.Session.User!.UserId, true, true));
        Assert.Equal("12,345", Get<string>(vm, "PreviewTotalAmountDisplay"));
        f.Session.RefreshSession("revoke", User(f.Session.User!.UserId, false, false));
        Assert.Equal("비공개", Get<string>(vm, "PreviewTotalAmountDisplay"));
        Assert.Equal(0m, Get<decimal>(vm, "PreviewTotalAmount"));
        Assert.Empty(main ? f.Main.InvoiceRows : f.Lookup.InvoiceRows);
        if (main) { Assert.Null(f.Main.DashboardReceivable); Assert.Equal("비공개", f.Main.DashboardReceivableDisplay); }
        f.Session.RefreshSession("restore", User(f.Session.User!.UserId, true, true));
        Assert.Equal("비공개", Get<string>(vm, "PreviewTotalAmountDisplay"));
    }

    [Theory]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    [InlineData(false, "payment")]
    [InlineData(true, "payment")]
    public async Task PreviewMasksWholeDocumentButPreservesQuantityAndNotes(bool main, string reason)
    {
        await using var f = await Fixture.CreateAsync();
        if (reason == "permission") f.Session.RefreshSession("restricted", User(f.Session.User!.UserId, false, false));
        else { f.Invoice.Payments.Single().AmountsHidden = true; await f.Db.SaveChangesAsync(); }
        await f.Preview(main);
        var vm = main ? (object)f.Main : f.Lookup;
        Assert.Equal("비공개", Get<string>(vm, "PreviewTotalAmountDisplay"));
        Assert.Equal(0m, Get<decimal>(vm, "PreviewTotalAmount"));
        var line = Assert.Single(main ? f.Main.PreviewLines : f.Lookup.PreviewLines);
        Assert.Equal(2m, line.Quantity); Assert.Equal("Fixture note", line.Remark);
        Assert.Equal("비공개", line.UnitPriceDisplay); Assert.Equal("비공개", line.LineAmountDisplay);
        Assert.Equal(500m, f.Invoice.TotalAmount); Assert.False(f.Invoice.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewStartedBeforeRevocationCannotRepaint(bool main)
    {
        await using var f = await Fixture.CreateAsync();
        f.Gate.Arm(); var pending = f.Preview(main);
        try
        {
            await f.Gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Session.RefreshSession("revoke", User(f.Session.User!.UserId, false, false));
            f.Session.RefreshSession("restore", User(f.Session.User!.UserId, true, true));
        }
        finally { f.Gate.Release(); }
        await pending;
        Assert.Empty(main ? f.Main.PreviewLines : f.Lookup.PreviewLines);
        Assert.Equal("비공개", Get<string>(main ? (object)f.Main : f.Lookup, "PreviewTotalAmountDisplay"));
    }

    [Fact]
    public async Task LookupReloadUsesCurrentPermissionsAndRetainsOfficeBoundary()
    {
        await using var f = await Fixture.CreateAsync();
        var other = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = f.Customer.Id, TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld, ResponsibleOfficeCode = OfficeCodeCatalog.Itworld, VoucherType = VoucherType.Sales, IsLatestVersion = true, TotalAmount = 999m };
        f.Db.Invoices.Add(other); await f.Db.SaveChangesAsync();
        await f.Lookup.LoadAsync(f.Customer.Id);
        Assert.Equal("500", Assert.Single(f.Lookup.InvoiceRows).TotalAmountDisplay);
        f.Session.RefreshSession("revoke", User(f.Session.User!.UserId, false, false));
        await f.Lookup.RefreshRowsAsync();
        Assert.Equal("비공개", Assert.Single(f.Lookup.InvoiceRows).TotalAmountDisplay);
        Assert.Equal(500m, f.Invoice.TotalAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListStartedBeforeRevocationCannotRepaint(bool main)
    {
        await using var f = await Fixture.CreateAsync();
        f.Gate.Arm();
        var pending = main ? Invoke(f.Main, "LoadInvoiceListCoreAsync", true, CancellationToken.None, false) : f.Lookup.RefreshRowsAsync();
        try
        {
            await f.Gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Session.RefreshSession("revoke", User(f.Session.User!.UserId, false, false));
        }
        finally { f.Gate.Release(); }
        await pending;
        Assert.Empty(main ? f.Main.InvoiceRows : f.Lookup.InvoiceRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FavoritesAndDashboardDistinguishHiddenFromDisclosedZero(bool hidden)
    {
        await using var f = await Fixture.CreateAsync();
        var rows = new List<LocalInvoiceListSummary> { new() { Id = f.Invoice.Id, CustomerId = f.Customer.Id, VoucherType = VoucherType.Sales,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today), TotalAmount = 0m, SettledAmount = 0m, AmountsHidden = hidden } };
        await Invoke(f.Main, "SaveFavoriteInvoiceIdsAsync", new[] { f.Invoice.Id }, CancellationToken.None);
        await Invoke(f.Main, "LoadInvoiceFavoritesAsync", rows, CancellationToken.None, true, false);
        Assert.Contains(hidden ? "비공개" : "0원", Assert.Single(f.Main.FavoriteInvoices).DisplayText);
        await Invoke(f.Main, "RefreshDashboardMetricsAsync", rows, CancellationToken.None, true);
        Assert.Equal(hidden ? "비공개" : "0원", f.Main.DashboardReceivableDisplay);
        f.Session.RefreshSession("revoke", User(f.Session.User!.UserId, false, false));
        Assert.Empty(f.Main.FavoriteInvoices);
        await Invoke(f.Main, "LoadInvoiceFavoritesAsync", rows, CancellationToken.None, true, false);
        Assert.Contains("비공개", Assert.Single(f.Main.FavoriteInvoices).DisplayText);
        await Invoke(f.Main, "RefreshDashboardMetricsAsync", rows, CancellationToken.None, true);
        Assert.Null(f.Main.DashboardReceivable); Assert.Null(f.Main.DashboardPayable);
    }

    [Fact]
    public async Task LegacyKindNormalizationCannotGrantVisibilityToUnknownTransaction()
    {
        await using var f = await Fixture.CreateAsync();
        var tx = new LocalTransaction { Id = Guid.NewGuid(), CustomerId = f.Customer.Id, TenantCode = f.Customer.TenantCode,
            OfficeCode = f.Customer.OfficeCode, ResponsibleOfficeCode = f.Customer.OfficeCode,
            TransactionKind = "unknown", ReceiptTotal = 321m, CashReceipt = 321m, IsDirty = false };
        f.Db.Transactions.Add(tx); await f.Db.SaveChangesAsync();
        f.Session.RefreshSession("sales-only", User(f.Session.User!.UserId, true, false));
        await f.Lookup.LoadAsync(f.Customer.Id);
        var row = Assert.Single(f.Lookup.InvoiceRows, r => r.IsTransactionRow);
        Assert.True(row.AmountsHidden); Assert.Null(row.ReceiptAmount);
        Assert.False(tx.AmountsHidden); Assert.False(tx.IsDirty); Assert.Equal(321m, tx.ReceiptTotal);
        Assert.False(await f.Db.Transactions.AsNoTracking().Where(t => t.Id == tx.Id).Select(t => t.AmountsHidden).SingleAsync());
    }

    private static void Set(object vm, string name, object value) => vm.GetType().GetProperty(name)!.SetValue(vm, value);
    private static T Get<T>(object vm, string name) => (T)vm.GetType().GetProperty(name)!.GetValue(vm)!;
    private static Task Invoke(object vm, string name, params object[] args) => (Task)vm.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SyncService _sync;
        private readonly HttpClient _http;
        public LocalDbContext Db { get; }
        public SessionState Session { get; } = new();
        public LocalCustomer Customer { get; }
        public LocalInvoice Invoice { get; }
        public MainViewModel Main { get; }
        public CustomerInvoiceLookupViewModel Lookup { get; }
        public QueryGate Gate { get; }
        private Fixture(SqliteConnection connection, LocalDbContext db, QueryGate gate, LocalCustomer customer, LocalInvoice invoice)
        {
            _connection = connection; Db = db; Gate = gate; Customer = customer; Invoice = invoice;
            Session.SetSession("fixture", User(Guid.NewGuid(), true, true));
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, Session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(Session);
            _http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost/") };
            var api = new ErpApiClient(_http, Session);
            _sync = new SyncService(db, local, rental, api, Session, dispatcher, diagnostics);
            Main = new MainViewModel(local, _sync, new BackupService(), rental, diagnostics, api, Session);
            Lookup = new CustomerInvoiceLookupViewModel(local, Session);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var gate = new QueryGate();
            var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).AddInterceptors(gate).Options);
            await db.Database.EnsureCreatedAsync();
            var c = new LocalCustomer { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, NameOriginal = "Fixture", NameMatchKey = "FIXTURE", IsDirty = false };
            var i = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = c.Id, TenantCode = c.TenantCode, OfficeCode = c.OfficeCode, ResponsibleOfficeCode = c.OfficeCode,
                VoucherType = VoucherType.Sales, TotalAmount = 500m, SupplyAmount = 450m, VatAmount = 50m, IsLatestVersion = true, IsConfirmed = true, IsDirty = false };
            i.VersionGroupId = i.Id;
            i.Lines.Add(new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = i.Id, Quantity = 2m, UnitPrice = 250m, LineAmount = 500m, Remark = "Fixture note" });
            i.Payments.Add(new LocalPayment { Id = Guid.NewGuid(), InvoiceId = i.Id, Amount = 100m });
            db.Customers.Add(c); db.Invoices.Add(i); await db.SaveChangesAsync();
            return new Fixture(connection, db, gate, c, i);
        }
        public Task Preview(bool main)
        {
            var vm = main ? (object)Main : Lookup;
            var field = vm.GetType().GetField(main ? "_invoicePreviewVersion" : "_previewVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var version = (int)field.GetValue(vm)!;
            var row = InvoiceListRow.From(Invoice, "Fixture", true, Session);
            return main ? Invoke(Main, "LoadPreviewCoreAsync", row, version, CancellationToken.None) : Invoke(Lookup, "LoadPreviewAsync", row, version);
        }
        public async ValueTask DisposeAsync()
        {
            Gate.Release(); Main.CancelPendingBackgroundWorkForShutdown(); await Lookup.DisposeAsync();
            _sync.Dispose(); _http.Dispose(); await Db.DisposeAsync(); await _connection.DisposeAsync();
        }
    }
    private sealed class QueryGate : DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _released.TrySetResult();
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Invoices") && Interlocked.Exchange(ref _armed, 0) == 1)
            { Blocked.TrySetResult(); await _released.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
