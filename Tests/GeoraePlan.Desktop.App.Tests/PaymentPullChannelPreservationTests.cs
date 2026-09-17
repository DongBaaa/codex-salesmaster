using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PaymentPullChannelPreservationTests
{
    [Theory]
    [InlineData("USENET", "split")]
    [InlineData("YEONSU", "split")]
    [InlineData("ITWORLD", "split")]
    [InlineData("USENET", "deleted")]
    [InlineData("USENET", "zero")]
    [InlineData("USENET", "unlinked")]
    public async Task TransactionOnlyPull_PreservesPaymentOwnRevision(string office, string change)
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase, office);
        await f.PullAsync(f.Payment(), f.Transaction("cash"));
        var incoming = f.Transaction("split"); incoming.Revision = 99;
        incoming.IsDeleted = change == "deleted";
        if (change == "zero") incoming.SettlementAmount = 0;
        if (change == "unlinked") incoming.LinkedInvoiceId = null;
        await f.PullAsync(null, incoming);
        var payment = await f.Db.Payments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(18, payment.Revision);
        Assert.False(payment.IsDirty);
        Assert.Equal(change != "split", payment.IsDeleted);
        Assert.Equal(99, (await f.StoredAsync()).Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionPull_DoesNotFabricateRevision_ForMissingOrDirtyPayment(bool dirty)
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase);
        if (dirty)
        {
            var local = LocalMappings.ToLocal(f.Payment()); local.IsDirty = true;
            local.Amount = 42; local.Note = "unsubmitted payment";
            f.Db.Payments.Add(local); await f.Db.SaveChangesAsync();
        }
        var incoming = f.Transaction("split"); incoming.Revision = 99;
        await f.PullAsync(null, incoming);
        var payment = await f.Db.Payments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(dirty ? 18 : 0, payment.Revision);
        Assert.Equal(dirty, payment.IsDirty);
        Assert.Equal(dirty ? 42 : 100, payment.Amount);
        if (dirty) Assert.Equal("unsubmitted payment", payment.Note);
        else
        {
            await f.PullAsync(f.Payment());
            Assert.Equal(18, (await f.Db.Payments.SingleAsync()).Revision);
            Assert.Equal(99, (await f.StoredAsync()).Revision);
        }
    }

    [Fact]
    public async Task SameBatch_UsesEachCanonicalEntityRevision_EvenWhenTransactionRevisionIsHigher()
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase);
        var transaction = f.Transaction("split"); transaction.Revision = 99;
        await f.PullAsync(f.Payment(), transaction);
        Assert.Equal(18, (await f.Db.Payments.SingleAsync()).Revision);
        Assert.Equal(99, (await f.StoredAsync()).Revision);
        await f.AssertChannelsAsync("split", 100);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentDeletionProjection_PreservesTransactionRevision(bool deletedInvoice)
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase);
        await f.StoreAsync(f.Transaction("cash"));
        var payment = f.Payment(); payment.Revision = 99;
        payment.IsDeleted = !deletedInvoice;
        if (deletedInvoice)
        {
            var invoice = await f.Db.Invoices.SingleAsync(); invoice.IsDeleted = true;
            await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        }
        await f.PullAsync(payment);
        var transaction = await f.StoredAsync();
        Assert.True(transaction.IsDeleted); Assert.False(transaction.IsDirty);
        Assert.Equal(17, transaction.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceDeletionProjection_PreservesChildRevisionsAndDirtyChanges(bool dirty)
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase);
        await f.PullAsync(f.Payment(), f.Transaction("cash"));
        var payment = await f.Db.Payments.SingleAsync(); payment.IsDirty = dirty;
        var transaction = await f.Db.Transactions.SingleAsync(); transaction.IsDirty = dirty;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var invoice = LocalMappings.ToDto(f.Invoice); invoice.IsDeleted = true; invoice.Revision = 99;
        await f.PullResponseAsync(new SyncPullResponse { CurrentServerRevision = 100, Invoices = [invoice] });
        payment = await f.Db.Payments.IgnoreQueryFilters().SingleAsync();
        transaction = await f.StoredAsync();
        Assert.Equal(18, payment.Revision); Assert.Equal(17, transaction.Revision);
        Assert.Equal(dirty, payment.IsDirty); Assert.Equal(dirty, transaction.IsDirty);
        Assert.Equal(!dirty, payment.IsDeleted);
        Assert.Equal(dirty ? f.Invoice.Id : (Guid?)null, transaction.LinkedInvoiceId);
    }

    [Theory]
    [InlineData("USENET")]
    [InlineData("YEONSU")]
    [InlineData("ITWORLD")]
    public async Task TransactionOnlyPull_ThenNextPaymentEdit_PushesIndependentExpectedRevisions(string office)
    {
        using var handler = new RevisionCheckingHandler();
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase, office, handler);
        await f.PullAsync(f.Payment(), f.Transaction("cash"));
        var incoming = f.Transaction("split"); incoming.Revision = 99;
        await f.PullAsync(null, incoming);
        var localTransaction = await f.Db.Transactions.SingleAsync();
        localTransaction.CashPayment = 100; localTransaction.CardPayment = 0;
        localTransaction.BankPayment = 0; localTransaction.DiscountReceived = 0;
        localTransaction.IsDirty = true; localTransaction.UpdatedAtUtc = DateTime.UtcNow;
        var localPayment = await f.Db.Payments.SingleAsync();
        localPayment.IsDirty = true; localPayment.UpdatedAtUtc = localTransaction.UpdatedAtUtc;
        f.Db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "100" });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var ok = await f.SyncAsync();
        var error = await f.Db.Settings.Where(x => x.Key == "Sync.LastError").Select(x => x.Value).FirstOrDefaultAsync();
        Assert.True(ok, error);
        Assert.Single(handler.Pushes);
        Assert.Equal(18, Assert.Single(handler.Pushes[0].Payments).ExpectedRevision);
        Assert.Equal(99, Assert.Single(handler.Pushes[0].Transactions).ExpectedRevision);
        Assert.False((await f.Db.Payments.SingleAsync()).IsDirty);
        Assert.False((await f.StoredAsync()).IsDirty);
        Assert.Equal(101, (await f.Db.Payments.SingleAsync()).Revision);
        Assert.Equal(102, (await f.StoredAsync()).Revision);
        await f.AssertChannelsAsync("cash", 100);
    }

    [Theory]
    [InlineData("equivalent")]
    [InlineData("server-edit")]
    [InlineData("different-invoice")]
    [InlineData("different-actor")]
    public async Task CorruptedPaymentRevision_RetriesOnlyEquivalentCanonicalPayload_AndPreservesPendingPair(string mode)
    {
        using var handler = new RevisionCheckingHandler(mode);
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase, "USENET", handler);
        var transaction = f.Transaction("split"); transaction.Revision = 99;
        await f.PullAsync(f.Payment(), transaction);
        var localTransaction = await f.Db.Transactions.SingleAsync();
        localTransaction.CashPayment = 100; localTransaction.CardPayment = 0;
        localTransaction.BankPayment = 0; localTransaction.DiscountReceived = 0;
        localTransaction.IsDirty = true; localTransaction.UpdatedAtUtc = DateTime.UtcNow;
        var localPayment = await f.Db.Payments.SingleAsync();
        localPayment.Revision = 99; localPayment.IsDirty = true;
        localPayment.UpdatedAtUtc = localTransaction.UpdatedAtUtc;
        f.Db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "100" });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        Assert.False(await f.SyncAsync());
        f.Db.ChangeTracker.Clear();
        localPayment = await f.Db.Payments.SingleAsync(); localTransaction = await f.Db.Transactions.SingleAsync();
        Assert.True(localPayment.IsDirty); Assert.True(localTransaction.IsDirty);
        var canRetry = mode is "equivalent" or "different-actor";
        Assert.Equal(canRetry ? 18 : 99, localPayment.Revision);
        Assert.Equal(99, localTransaction.Revision); Assert.Equal(100, localPayment.Amount);
        Assert.Equal(100, localTransaction.CashPayment); Assert.Equal(0, localTransaction.CardPayment);
        Assert.DoesNotContain(await f.Db.SyncOutboxEntries.ToListAsync(), x => x.Status == "Acknowledged");
        if (!canRetry) return;
        Assert.True(await f.SyncAsync());
        Assert.Equal(2, handler.Pushes.Count);
        Assert.NotEqual(Assert.Single(handler.Pushes[0].Payments).MutationId, Assert.Single(handler.Pushes[1].Payments).MutationId);
        Assert.Equal(18, Assert.Single(handler.Pushes[1].Payments).ExpectedRevision);
        Assert.Equal(99, Assert.Single(handler.Pushes[1].Transactions).ExpectedRevision);
        f.Db.ChangeTracker.Clear();
        Assert.False((await f.Db.Payments.SingleAsync()).IsDirty);
        Assert.False((await f.StoredAsync()).IsDirty);
        await f.AssertChannelsAsync("cash", 100);
    }

    private sealed class RevisionCheckingHandler(string? conflictMode = null) : HttpMessageHandler
    {
        public List<SyncPushRequest> Pushes { get; } = [];
        private PaymentDto? conflictPayment;
        private TransactionDto? conflictTransaction;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/sync/pull")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(
                    Pushes.Count == 1 && conflictPayment != null
                        ? new SyncPullResponse { CurrentServerRevision = 100, Payments = [conflictPayment], Transactions = [conflictTransaction!] }
                        : new SyncPullResponse { CurrentServerRevision = 102 }) };
            if (request.RequestUri.AbsolutePath != "/sync/push") return new(HttpStatusCode.NotFound);
            var push = (await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: ct))!;
            Pushes.Add(push);
            var payment = Assert.Single(push.Payments); var transaction = Assert.Single(push.Transactions);
            if (conflictMode != null && Pushes.Count == 1)
            {
                Assert.Equal(99, payment.ExpectedRevision);
                var serverPayment = JsonSerializer.Deserialize<PaymentDto>(JsonSerializer.Serialize(payment))!;
                serverPayment.Revision = 18;
                serverPayment.UpdatedAtUtc = serverPayment.UpdatedAtUtc.AddMinutes(-1);
                if (conflictMode == "server-edit") serverPayment.Amount = 150;
                if (conflictMode == "different-invoice") serverPayment.InvoiceId = Guid.NewGuid();
                conflictPayment = serverPayment;
                conflictTransaction = JsonSerializer.Deserialize<TransactionDto>(JsonSerializer.Serialize(transaction))!;
                conflictTransaction.CashPayment = 20; conflictTransaction.CardPayment = 30;
                conflictTransaction.BankPayment = 40; conflictTransaction.DiscountReceived = 10;
                var conflict = new ConflictLogDto { EntityName = "Payment", EntityId = payment.Id.ToString(),
                    Reason = "Expected revision mismatch. client=99, server=18", ClientJson = JsonSerializer.Serialize(payment), ServerJson = JsonSerializer.Serialize(serverPayment) };
                if (conflictMode == "different-actor") conflict.ServerUsername = "another-user";
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new SyncPushResult { ConflictCount = 2,
                    CurrentServerRevision = 100, Conflicts = [conflict,
                        new ConflictLogDto { EntityName = "TransactionRecord", EntityId = transaction.Id.ToString(),
                            Reason = "The paired Payment cannot be committed atomically: Expected revision mismatch. client=99, server=18", ClientJson = JsonSerializer.Serialize(transaction) }
                    ]
                }) };
            }
            Assert.Equal(18, payment.ExpectedRevision); Assert.Equal(99, transaction.ExpectedRevision);
            Assert.Equal(100, transaction.CashPayment); Assert.Equal(0, transaction.CardPayment);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new SyncPushResult {
                CurrentServerRevision = 102, AcceptedCount = 2,
                AcceptedRevisions = [
                    new SyncAcceptedRevisionDto { EntityName = "Payment", EntityId = payment.Id, Revision = 101, UpdatedAtUtc = payment.UpdatedAtUtc },
                    new SyncAcceptedRevisionDto { EntityName = "TransactionRecord", EntityId = transaction.Id, Revision = 102, UpdatedAtUtc = transaction.UpdatedAtUtc }
                ]
            }) };
        }
    }
    public static IEnumerable<object[]> CanonicalCases()
    {
        foreach (var voucher in new[] { VoucherType.Purchase, VoucherType.Procurement, VoucherType.Sales })
        foreach (var channel in new[] { "cash", "card", "bank", "split" })
        foreach (var sameBatch in new[] { false, true })
            yield return [voucher, channel, sameBatch];
    }

    [Theory, MemberData(nameof(CanonicalCases))]
    public async Task FullPull_PreservesCanonicalChannels_InSameBatchAndLaterPaymentEcho(
        VoucherType voucher, string channel, bool sameBatch)
    {
        await using var f = await Fixture.CreateAsync(voucher);
        var canonical = f.Transaction(channel);
        canonical.Memo = "canonical memo";
        if (!sameBatch) await f.StoreAsync(canonical);
        await f.PullAsync(f.Payment(), sameBatch ? canonical : null);
        await f.AssertChannelsAsync(channel, 100m);
        var actual = await f.StoredAsync();
        Assert.Equal("canonical memo", actual.Memo);
        Assert.Equal(17, actual.Revision);
        await f.PullAsync(f.Payment());
        await f.AssertChannelsAsync(channel, 100m);
        Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());
    }

    [Theory]
    [InlineData(VoucherType.Purchase, "cash")]
    [InlineData(VoucherType.Purchase, "card")]
    [InlineData(VoucherType.Purchase, "bank")]
    [InlineData(VoucherType.Sales, "cash")]
    [InlineData(VoucherType.Sales, "card")]
    [InlineData(VoucherType.Sales, "bank")]
    public async Task PaymentOnlyAmountChange_MatchesServerSingleChannelPolicy(VoucherType voucher, string channel)
    {
        await using var f = await Fixture.CreateAsync(voucher);
        await f.StoreAsync(f.Transaction(channel));
        var payment = f.Payment(); payment.Amount = 150m; payment.Revision++;
        await f.PullAsync(payment);
        await f.AssertChannelsAsync(channel, 150m);
    }

    [Theory]
    [InlineData(VoucherType.Purchase)]
    [InlineData(VoucherType.Procurement)]
    [InlineData(VoucherType.Sales)]
    public async Task PaymentWithoutTransaction_CreatesCleanMirrorWithCorrectDirection(VoucherType voucher)
    {
        await using var f = await Fixture.CreateAsync(voucher);
        await f.PullAsync(f.Payment());
        await f.AssertChannelsAsync("bank", 100m);
        Assert.Equal(0, (await f.StoredAsync()).Revision);
    }

    [Theory]
    [InlineData("USENET")]
    [InlineData("YEONSU")]
    [InlineData("ITWORLD")]
    public async Task PaymentMovesToNewInvoiceVersion_UpdatesLinkAndPreservesSplit(string office)
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase, office);
        await f.StoreAsync(f.Transaction("split"));
        var next = LocalMappings.ToLocal(LocalMappings.ToDto(f.Invoice));
        next.Id = Guid.NewGuid(); next.VersionNumber = 2; next.InvoiceNumber = "NEXT-002";
        next.IsDirty = false; f.Db.Invoices.Add(next); await f.Db.SaveChangesAsync();
        var payment = f.Payment(); payment.InvoiceId = next.Id;
        await f.PullAsync(payment);
        await f.AssertChannelsAsync("split", 100m, next.Id);
        Assert.Equal("NEXT-002", (await f.StoredAsync()).LinkedInvoiceNumber);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task DirtyPaymentOrTransaction_IsNotOverwrittenByPaymentPull(bool dirtyPayment, bool deleted)
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase);
        await f.StoreAsync(f.Transaction("cash"));
        var transaction = await f.Db.Transactions.SingleAsync();
        transaction.IsDirty = !dirtyPayment; transaction.Note = "local note";
        var localPayment = LocalMappings.ToLocal(f.Payment()); localPayment.IsDirty = dirtyPayment;
        f.Db.Payments.Add(localPayment); await f.Db.SaveChangesAsync();
        var incoming = f.Payment(); incoming.Amount = 150m; incoming.IsDeleted = deleted;
        await f.PullAsync(incoming);
        var actual = await f.StoredAsync();
        Assert.False(actual.IsDeleted); Assert.Equal(!dirtyPayment, actual.IsDirty);
        Assert.Equal("local note", actual.Note);
        Assert.Equal(100m, actual.CashPayment); Assert.Equal(0m, actual.BankPayment);
        if (dirtyPayment)
        {
            var preserved = await f.Db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.True(preserved.IsDirty); Assert.False(preserved.IsDeleted); Assert.Equal(100m, preserved.Amount);
        }
    }

    [Fact]
    public async Task DeletedPayment_DeletesCleanMirrorAndRetainsHistoricalChannels()
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Purchase);
        await f.StoreAsync(f.Transaction("cash"));
        var payment = f.Payment(); payment.IsDeleted = true;
        await f.PullAsync(payment);
        var actual = await f.StoredAsync();
        Assert.True(actual.IsDeleted); Assert.False(actual.IsDirty);
        Assert.Equal(100m, actual.CashPayment); Assert.Equal(0m, actual.BankPayment);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly HttpClient http;
        private readonly SyncService sync;
        private readonly Guid paymentId = Guid.NewGuid();
        private static readonly DateTime Timestamp = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        public LocalDbContext Db { get; }
        public LocalInvoice Invoice { get; }
        private bool IsPayment => Invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement;
        private Fixture(SqliteConnection connection, LocalDbContext db, LocalInvoice invoice, HttpMessageHandler? handler)
        {
            this.connection = connection; Db = db; Invoice = invoice;
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto { Username = "admin", Role = "Admin", TenantCode = invoice.TenantCode, OfficeCode = invoice.ResponsibleOfficeCode, ScopeType = TenantScopeCatalog.ScopeAdmin });
            if (handler != null) session.SetSession("isolated-test-token", new UserSessionDto { UserId = Guid.NewGuid(), Username = "admin", Role = "Admin", TenantCode = invoice.TenantCode, OfficeCode = invoice.ResponsibleOfficeCode, ScopeType = TenantScopeCatalog.ScopeAdmin }, DateTime.UtcNow.AddDays(1));
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            http.BaseAddress = new Uri("http://127.0.0.1/");
            sync = new SyncService(db, local, new RentalStateService(db), new ErpApiClient(http, session), session, dispatcher, new SyncDiagnosticsService(session));
        }
        public static async Task<Fixture> CreateAsync(VoucherType voucher, string office = "USENET", HttpMessageHandler? handler = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid(); var invoiceId = Guid.NewGuid();
            var tenant = office == "ITWORLD" ? "ITWORLD" : "USENET_GROUP";
            var owner = office == "YEONSU" ? "USENET" : office;
            db.Customers.Add(new LocalCustomer { Id = customerId, NameOriginal = "payment pull fixture", TenantCode = tenant, OfficeCode = owner, ResponsibleOfficeCode = office, IsDirty = false });
            var invoice = new LocalInvoice { Id = invoiceId, CustomerId = customerId, TenantCode = tenant, OfficeCode = owner, ResponsibleOfficeCode = office, VoucherType = voucher, VersionGroupId = invoiceId, VersionNumber = 1, IsLatestVersion = true, InvoiceDate = new DateOnly(2026, 9, 12), InvoiceNumber = "TEST-001", TotalAmount = 100m, Revision = 10, IsDirty = false, CreatedAtUtc = Timestamp, UpdatedAtUtc = Timestamp };
            db.Invoices.Add(invoice); await db.SaveChangesAsync();
            return new Fixture(connection, db, invoice, handler);
        }
        public PaymentDto Payment() => new() { Id = paymentId, InvoiceId = Invoice.Id, Amount = 100m, PaymentDate = Invoice.InvoiceDate, Note = "payment note", Revision = 18, CreatedAtUtc = Timestamp, UpdatedAtUtc = Timestamp };
        public TransactionDto Transaction(string channel)
        {
            var t = new TransactionDto { Id = paymentId, CustomerId = Invoice.CustomerId, TenantCode = Invoice.TenantCode, OfficeCode = Invoice.OfficeCode, ResponsibleOfficeCode = Invoice.ResponsibleOfficeCode, TransactionDate = Invoice.InvoiceDate, TransactionKind = IsPayment ? "전표지급" : "전표수금", LinkedInvoiceId = Invoice.Id, LinkedInvoiceNumber = Invoice.InvoiceNumber, SettlementAmount = 100m, Revision = 17, CreatedAtUtc = Timestamp, UpdatedAtUtc = Timestamp };
            var amounts = Amounts(channel, 100m);
            if (IsPayment) { t.CashPayment = amounts[0]; t.CardPayment = amounts[1]; t.BankPayment = amounts[2]; t.DiscountReceived = amounts[3]; t.PaymentTotal = 100m; }
            else { t.CashReceipt = amounts[0]; t.CardReceipt = amounts[1]; t.BankReceipt = amounts[2]; t.DiscountApplied = amounts[3]; t.ReceiptTotal = 100m; }
            return t;
        }
        public async Task StoreAsync(TransactionDto dto) { var local = LocalMappings.ToLocal(dto); local.IsDirty = false; Db.Transactions.Add(local); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); }
        public async Task PullAsync(PaymentDto? payment, TransactionDto? transaction = null)
        {
            var pull = new SyncPullResponse { CurrentServerRevision = 100 };
            if (payment != null) pull.Payments.Add(payment);
            if (transaction != null) pull.Transactions.Add(transaction);
            await PullResponseAsync(pull);
        }
        public async Task PullResponseAsync(SyncPullResponse pull)
        {
            var method = typeof(SyncService).GetMethod("ApplyPullAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await ((Task)method.Invoke(sync, [pull, 0L, CancellationToken.None, false])!).WaitAsync(TimeSpan.FromSeconds(20));
            Db.ChangeTracker.Clear();
        }
        public Task<bool> SyncAsync() => sync.TrySyncAsync().WaitAsync(TimeSpan.FromSeconds(20));
        public Task<LocalTransaction> StoredAsync() => Db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == paymentId);
        public async Task AssertChannelsAsync(string channel, decimal amount, Guid? invoiceId = null)
        {
            var t = await StoredAsync();
            Assert.False(t.IsDirty); Assert.False(t.IsDeleted);
            Assert.Equal(invoiceId ?? Invoice.Id, t.LinkedInvoiceId); Assert.Equal(Invoice.CustomerId, t.CustomerId);
            Assert.Equal(Invoice.TenantCode, t.TenantCode); Assert.Equal(Invoice.OfficeCode, t.OfficeCode); Assert.Equal(Invoice.ResponsibleOfficeCode, t.ResponsibleOfficeCode);
            Assert.Equal(Invoice.InvoiceDate, t.TransactionDate); Assert.Equal(amount, t.SettlementAmount);
            Assert.Equal(IsPayment ? "전표지급" : "전표수금", t.TransactionKind);
            Assert.Equal(amount, IsPayment ? t.PaymentTotal : t.ReceiptTotal);
            Assert.Equal(0m, IsPayment ? t.ReceiptTotal : t.PaymentTotal);
            Assert.Equal(Amounts(channel, amount), IsPayment ? new[] { t.CashPayment, t.CardPayment, t.BankPayment, t.DiscountReceived } : [t.CashReceipt, t.CardReceipt, t.BankReceipt, t.DiscountApplied]);
            Assert.Equal(new decimal[4], IsPayment ? new[] { t.CashReceipt, t.CardReceipt, t.BankReceipt, t.DiscountApplied } : [t.CashPayment, t.CardPayment, t.BankPayment, t.DiscountReceived]);
        }
        private static decimal[] Amounts(string channel, decimal amount) => channel switch { "cash" => [amount, 0, 0, 0], "card" => [0, amount, 0, 0], "split" => [20, 30, 40, 10], _ => [0, 0, amount, 0] };
        public async ValueTask DisposeAsync() { sync.Dispose(); http.Dispose(); await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
