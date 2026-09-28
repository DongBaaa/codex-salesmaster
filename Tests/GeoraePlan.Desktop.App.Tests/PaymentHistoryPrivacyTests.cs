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

public sealed class PaymentHistoryPrivacyTests
{
    [Theory]
    [InlineData("일반수금", "sales", false, false, true)]
    [InlineData("일반수금", "purchase", false, false, false)]
    [InlineData("일반수금", "none", false, false, false)]
    [InlineData("일반지급", "purchase", false, false, true)]
    [InlineData("일반지급", "sales", false, false, false)]
    [InlineData("선수금환불", "sales", false, false, true)]
    [InlineData("선수금차감", "sales", false, false, true)]
    [InlineData("렌탈수금", "sales", false, false, true)]
    [InlineData("일반수금", "both", true, false, false)]
    [InlineData("일반수금", "sales", false, true, false)]
    [InlineData("일반지급", "purchase", false, true, false)]
    [InlineData("일반지급", "both", false, true, true)]
    [InlineData("알수없음", "sales", false, false, false)]
    [InlineData("알수없음", "both", false, false, true)]
    public async Task HistoryAndEditRequireKnownAmountsAndCorrectGrant(string kind, string grants, bool hidden, bool mixed, bool visible)
    {
        await using var f = await Fixture.CreateAsync(grants);
        var tx = f.Transaction;
        tx.TransactionKind = kind; tx.AmountsHidden = hidden;
        tx.CashReceipt = tx.ReceiptTotal = kind is "일반지급" or "선수금환불" ? 0m : 123m;
        tx.CashPayment = tx.PaymentTotal = kind is "일반지급" or "선수금환불" ? 123m : 0m;
        if (mixed) { tx.CashReceipt = tx.ReceiptTotal = 123m; tx.CashPayment = tx.PaymentTotal = 45m; }
        await f.Db.SaveChangesAsync();
        var row = Assert.Single(await f.Local.GetPaymentHistoryAsync(f.Customer.Id, f.Session));
        Assert.Equal(!visible, row.AmountsHidden);
        Assert.Equal(visible ? tx.ReceiptTotal : (decimal?)null, row.ReceiptTotal);
        Assert.Equal(visible ? tx.PaymentTotal : (decimal?)null, row.PaymentTotal);
        Assert.Equal(visible ? tx.ReceiptTotal.ToString("N0") : "비공개", row.ReceiptTotalDisplay);
        Assert.Equal(visible, await f.Local.GetTransactionForPaymentEditingAsync(tx.Id, f.Session) is not null);
        Assert.Equal(hidden, (await f.Db.Transactions.AsNoTracking().SingleAsync()).AmountsHidden);
        Assert.False(tx.IsDirty);
    }

    [Theory]
    [InlineData(VoucherType.Sales, "sales", true)]
    [InlineData(VoucherType.Collection, "sales", true)]
    [InlineData(VoucherType.Purchase, "sales", false)]
    [InlineData(VoucherType.Purchase, "purchase", false)]
    [InlineData((VoucherType)99, "sales", false)]
    public async Task LinkedInvoiceTypeCannotBeBypassedByReceiptKind(VoucherType type, string grants, bool visible)
    {
        await using var f = await Fixture.CreateAsync(grants);
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = f.Customer.Id, VoucherType = type,
            TenantCode = f.Customer.TenantCode, OfficeCode = f.Customer.OfficeCode, ResponsibleOfficeCode = f.Customer.OfficeCode };
        f.Transaction.LinkedInvoiceId = invoice.Id; f.Db.Invoices.Add(invoice); await f.Db.SaveChangesAsync();
        Assert.Equal(!visible, Assert.Single(await f.Local.GetPaymentHistoryAsync(f.Customer.Id, f.Session)).AmountsHidden);
        Assert.Equal(visible, await f.Local.GetTransactionForPaymentEditingAsync(f.Transaction.Id, f.Session) is not null);
    }

    [Fact]
    public async Task MissingLinkedInvoiceAndOtherOfficeDoNotExposeHistory()
    {
        await using var f = await Fixture.CreateAsync("sales");
        f.Transaction.LinkedInvoiceId = Guid.NewGuid(); await f.Db.SaveChangesAsync();
        Assert.True(Assert.Single(await f.Local.GetPaymentHistoryAsync(f.Customer.Id, f.Session)).AmountsHidden);
        Assert.Null(await f.Local.GetTransactionForPaymentEditingAsync(f.Transaction.Id, f.Session));
        f.Transaction.OfficeCode = f.Transaction.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Local.GetPaymentHistoryAsync(f.Customer.Id, f.Session));
    }

    [Theory]
    [InlineData("일반수금", false)]
    [InlineData("알수없음", true)]
    public async Task HistoricalDisplayNormalizationDoesNotGrantAmountAccess(string storedKind, bool hidden)
    {
        await using var f = await Fixture.CreateAsync("sales");
        f.Transaction.TransactionKind = storedKind;
        f.Transaction.LinkedRentalBillingProfileId = Guid.NewGuid();
        f.Transaction.Note = "렌탈수금 - 이력 메모";
        await f.Db.SaveChangesAsync();
        var row = Assert.Single(await f.Local.GetPaymentHistoryAsync(f.Customer.Id, f.Session));
        Assert.Equal("렌탈수금", row.TransactionKindDisplay);
        Assert.Equal("이력 메모", row.Note);
        Assert.Equal(hidden, row.AmountsHidden);
        Assert.Equal(storedKind, (await f.Db.Transactions.AsNoTracking().SingleAsync()).TransactionKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreshCandidateCannotOverwritePersistedHiddenMoney(bool withoutSession, bool deleted)
    {
        await using var f = await Fixture.CreateAsync("both");
        f.Transaction.AmountsHidden = true; f.Transaction.IsDeleted = deleted;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var candidate = f.Candidate();
        Assert.False(candidate.AmountsHidden);
        if (withoutSession)
            Assert.Contains("비공개", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Local.SaveTransactionAsync(candidate))).Message);
        else
        {
            var result = await f.Local.SaveTransactionAsync(candidate, f.Session);
            Assert.False(result.Success); Assert.Contains("비공개", result.Message);
        }
        var stored = await f.Db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.True(stored.AmountsHidden); Assert.Equal(deleted, stored.IsDeleted);
        Assert.Equal(123m, stored.ReceiptTotal); Assert.Equal("original", stored.Memo);
        Assert.False(stored.IsDirty); Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task RevokedReadGrantBlocksExistingEditButAllowsNewExplicitReceipt()
    {
        await using var f = await Fixture.CreateAsync("none");
        var edit = await f.Local.SaveTransactionAsync(f.Candidate(), f.Session);
        Assert.False(edit.Success); Assert.Contains("비공개", edit.Message);
        var fresh = f.Candidate(); fresh.Id = Guid.NewGuid();
        var create = await f.Local.SaveTransactionAsync(fresh, f.Session);
        Assert.True(create.Success, create.Message);
        Assert.Equal(123m, (await f.Db.Transactions.AsNoTracking().SingleAsync(row => row.Id == f.Transaction.Id)).ReceiptTotal);
    }

    [Fact]
    public async Task UnfilteredInternalReadDoesNotRestoreADeletedDisclosedTransaction()
    {
        await using var f = await Fixture.CreateAsync("both");
        f.Transaction.IsDeleted = true; await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        Assert.Contains("삭제된", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Local.SaveTransactionAsync(f.Candidate()))).Message);
        var stored = await f.Db.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.True(stored.IsDeleted); Assert.False(stored.IsDirty); Assert.Equal(123m, stored.ReceiptTotal);
    }

    [Fact]
    public async Task EditViewModelRechecksStoredHiddenStateInsteadOfTrustingSelectedRow()
    {
        await using var f = await Fixture.CreateAsync("sales");
        using var vm = new PaymentViewModel(f.Local, f.Session);
        var row = Assert.Single(await f.Local.GetPaymentHistoryAsync(f.Customer.Id, f.Session));
        vm.History.Add(row); vm.SelectedHistory = row;
        Assert.True(vm.CanEditHistory);
        f.Transaction.AmountsHidden = true; await f.Db.SaveChangesAsync();
        await vm.EditHistoryCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditingHistory); Assert.Equal(0m, vm.CashReceipt);
        Assert.Contains("비공개", vm.StatusMessage);
    }

    [Fact]
    public async Task ReopeningNowHiddenEntryClearsPreviouslyLoadedAmounts()
    {
        await using var f = await Fixture.CreateAsync("sales");
        using var vm = new PaymentViewModel(f.Local, f.Session);
        typeof(PaymentViewModel).GetField("_allCustomers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, new List<LocalCustomer> { f.Customer });
        await vm.LoadTransactionForEditingAsync(f.Transaction.Id);
        Assert.Equal(123m, vm.CashReceipt);
        f.Transaction.AmountsHidden = true; await f.Db.SaveChangesAsync();
        await vm.LoadTransactionForEditingAsync(f.Transaction.Id);
        Assert.False(vm.IsEditingHistory); Assert.Equal(0m, vm.CashReceipt);
        Assert.False(vm.SaveCommand.CanExecute(null)); Assert.Contains("비공개", vm.StatusMessage);
    }

    [Fact]
    public async Task RevocationClearsRenderedHistoryAndEditorAndCannotSaveAsNewTransaction()
    {
        await using var f = await Fixture.CreateAsync("sales");
        using var vm = new PaymentViewModel(f.Local, f.Session);
        typeof(PaymentViewModel).GetField("_allCustomers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, new List<LocalCustomer> { f.Customer });
        await vm.LoadTransactionForEditingAsync(f.Transaction.Id);
        Assert.True(vm.IsEditingHistory); Assert.Equal(123m, vm.CashReceipt);
        f.Session.RefreshSession("token-only", Fixture.User(f.Session.User!.UserId, "sales"));
        Assert.True(vm.IsEditingHistory); Assert.Equal(123m, vm.CashReceipt);
        f.Session.RefreshSession("revoked", Fixture.User(f.Session.User!.UserId, "none"));
        Assert.Empty(vm.History); Assert.Null(vm.SelectedHistory);
        Assert.False(vm.IsEditingHistory); Assert.Equal(0m, vm.CashReceipt);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(await f.Db.Transactions.ToListAsync());
        Assert.Equal(123m, f.Transaction.ReceiptTotal); Assert.False(f.Transaction.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessChangeDuringReadCannotReturnOldAmounts(bool editing)
    {
        var gate = new ReadGate();
        await using var f = await Fixture.CreateAsync("sales", gate);
        gate.Enabled = true;
        var read = editing ? (Task)f.Local.GetTransactionForPaymentEditingAsync(f.Transaction.Id, f.Session)
            : f.Local.GetPaymentHistoryAsync(f.Customer.Id, f.Session);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Session.RefreshSession("revoked", Fixture.User(f.Session.User!.UserId, "none"));
        gate.Release.TrySetResult(); await read;
        if (editing) Assert.Null(await (Task<LocalTransaction?>)read);
        else Assert.Empty(await (Task<List<PaymentHistoryRow>>)read);
    }

    private sealed class ReadGate : DbCommandInterceptor
    {
        internal bool Enabled;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("FROM \"Transactions\""))
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
        internal LocalTransaction Transaction { get; }
        private Fixture(SqliteConnection connection, LocalDbContext db, SessionState session, LocalCustomer customer, LocalTransaction tx)
        { _connection = connection; Db = db; Session = session; Customer = customer; Transaction = tx;
            Local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session); }
        internal static UserSessionDto User(Guid id, string grants) => new()
        {
            UserId = id, Username = "history-fixture", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { AppPermissionNames.PaymentEdit }
                .Concat(grants is "sales" or "both" ? new[] { AppPermissionNames.AmountViewSales } : [])
                .Concat(grants is "purchase" or "both" ? new[] { AppPermissionNames.AmountViewPurchase } : []).ToList()
        };
        internal static async Task<Fixture> CreateAsync(string grants, DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var db = new LocalDbContext(options.Options); await db.Database.EnsureCreatedAsync();
            var customer = new LocalCustomer { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Fixture", NameMatchKey = "FIXTURE", IsDirty = false };
            var tx = new LocalTransaction { Id = Guid.NewGuid(), CustomerId = customer.Id, TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode, ResponsibleOfficeCode = customer.OfficeCode, TransactionKind = "일반수금",
                CashReceipt = 123m, ReceiptTotal = 123m, Memo = "original", IsDirty = false };
            db.Customers.Add(customer); db.Transactions.Add(tx); await db.SaveChangesAsync();
            var session = new SessionState(); session.SetSession("fixture", User(Guid.NewGuid(), grants));
            return new Fixture(connection, db, session, customer, tx);
        }
        internal LocalTransaction Candidate() => new() { Id = Transaction.Id, Revision = Transaction.Revision, CustomerId = Customer.Id,
            TenantCode = Customer.TenantCode, OfficeCode = Customer.OfficeCode, ResponsibleOfficeCode = Customer.OfficeCode,
            TransactionKind = "일반수금", CashReceipt = 50m, ReceiptTotal = 50m, Memo = "changed" };
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
