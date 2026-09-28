using System.Data.Common;
using System.Reflection;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PeriodLedgerPrivacyTests
{
    private static readonly DateOnly Day = new(2026, 9, 24);
    private static UserSessionDto User(Guid id, bool sales = true, bool purchase = true) => new()
    {
        UserId = id, Username = "period-fixture", Role = DomainConstants.RoleUser,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = new[] { AppPermissionNames.InvoiceEdit, sales ? AppPermissionNames.AmountViewSales : "", purchase ? AppPermissionNames.AmountViewPurchase : "" }.Where(p => p.Length > 0).ToList()
    };
    private static PeriodLedgerQuery Query(PeriodLedgerType type = PeriodLedgerType.SalesOnly) => new()
    { From = Day.AddDays(-1), To = Day, LedgerType = type, Scope = PeriodLedgerScope.AllCustomers, IncludeProfit = true };

    [Theory]
    [InlineData(VoucherType.Sales, PeriodLedgerType.SalesOnly, false, true)]
    [InlineData(VoucherType.Purchase, PeriodLedgerType.PurchaseOnly, true, false)]
    public async Task RestrictedLedgerPreservesMetadataButMasksEveryMoneySurface(VoucherType type, PeriodLedgerType ledger, bool sales, bool purchase)
    {
        await using var f = await Fixture.CreateAsync(type); f.SetAccess(sales, purchase);
        var result = await f.Aggregation.BuildAsync(Query(ledger), f.Session);
        var row = Assert.Single(Assert.Single(result.Blocks).Rows, r => r.IsInvoiceSummary);
        Assert.Null(row.TradeAmount); Assert.Null(row.RunningBalance); Assert.Null(result.Totals.TradeAmount);
        var item = Assert.Single(row.Items); Assert.Equal(3m, item.Quantity); Assert.Equal("fixture item", item.ItemName);
        Assert.Null(item.UnitPrice); Assert.Null(item.LineAmount); Assert.Null(item.SupplyAmount); Assert.Null(item.VatAmount);
        Apply(f.Vm, result); Assert.Equal("비공개", f.Vm.SummaryTradeAmountText);
        Assert.Equal(3m, Assert.Single(f.Vm.SelectedLedgerItems).Quantity);
        Assert.Null(Assert.Single(f.Vm.SelectedLedgerItems).UnitPrice);
        Assert.Equal(12345m, f.Invoice.TotalAmount); Assert.False(f.Invoice.IsDirty);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("line")]
    [InlineData("payment")]
    [InlineData("transaction")]
    public async Task StoredHiddenSourcesNeverBecomeZeroOrPartialTotals(string source)
    {
        await using var f = await Fixture.CreateAsync();
        if (source == "invoice") { f.Invoice.AmountsHidden = true; f.Invoice.TotalAmount = 0; }
        if (source == "line") f.Invoice.Lines.Single().AmountsHidden = true;
        if (source == "payment") { f.Invoice.Payments.Single().AmountsHidden = true; f.Invoice.Payments.Single().Amount = 0; }
        if (source == "transaction") f.Db.Transactions.Add(f.Transaction(hidden: true, amount: 0));
        await f.Db.SaveChangesAsync();
        var result = await f.Aggregation.BuildAsync(Query(source == "transaction" ? PeriodLedgerType.ReceiptPayment : PeriodLedgerType.SalesOnly), f.Session);
        Assert.Null(result.Totals.TradeAmount); Assert.Null(result.Totals.RunningBalance);
        if (source == "transaction") Assert.Contains(result.PaymentRows, r => r.TransactionId.HasValue && !r.ReceiptAmount.HasValue);
        if (source == "payment") Assert.Contains(result.Blocks.Single().Rows, r => r.PaymentId.HasValue && !r.ReceiptAmount.HasValue);
    }

    [Fact]
    public async Task SalesOnlyGrantCannotInferPurchaseCostThroughProfit()
    {
        await using var f = await Fixture.CreateAsync(); f.SetAccess(true, false);
        var purchase = f.NewInvoice(VoucherType.Purchase, 6000m); f.Db.Invoices.Add(purchase); await f.Db.SaveChangesAsync();
        var result = await f.Aggregation.BuildAsync(Query(), f.Session);
        Assert.Equal(12345m, result.Totals.TradeAmount); Assert.Null(result.Totals.ProfitAmount); Assert.True(result.ProfitAmountsHidden);
        Assert.All(result.Blocks.SelectMany(b => b.Rows), r => Assert.Null(r.ProfitAmount));
    }

    [Fact]
    public async Task UnknownMonthDoesNotRevealChartScaleOrPartialTotal()
    {
        await using var f = await Fixture.CreateAsync(); f.Invoice.AmountsHidden = true; await f.Db.SaveChangesAsync();
        var result = await f.Aggregation.BuildAsync(Query(), f.Session); var point = Assert.Single(result.MonthlySalesChartPoints);
        Assert.Null(point.SalesAmount); Assert.Equal("비공개", point.SalesAmountText); Assert.Equal(0d, point.BarHeight);
        Apply(f.Vm, result); Assert.Contains("비공개", f.Vm.MonthlySalesChartSummaryText); Assert.DoesNotContain("12,345", f.Vm.MonthlySalesChartSummaryText);
    }

    [Fact]
    public async Task KnownZeroAndHiddenAreDistinct()
    {
        await using var f = await Fixture.CreateAsync(); f.Invoice.TotalAmount = 0; f.Invoice.Lines.Single().LineAmount = 0; await f.Db.SaveChangesAsync();
        var result = await f.Aggregation.BuildAsync(Query(), f.Session); Assert.Equal(0m, result.Totals.TradeAmount);
        Apply(f.Vm, result); Assert.Equal("0", f.Vm.SummaryTradeAmountText);
        f.SetAccess(false, false); result = await f.Aggregation.BuildAsync(Query(), f.Session); Assert.Null(result.Totals.TradeAmount);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mixed")]
    public async Task TransactionOriginalDirectionIsCheckedBeforeDisplayNormalization(string kind)
    {
        await using var f = await Fixture.CreateAsync(); f.SetAccess(true, false);
        var tx = f.Transaction(); tx.TransactionKind = kind == "unknown" ? "legacy-unknown" : "일반수금";
        if (kind == "mixed") { tx.PaymentTotal = 77; tx.CashPayment = 77; }
        f.Db.Transactions.Add(tx); await f.Db.SaveChangesAsync();
        var result = await f.Aggregation.BuildAsync(Query(PeriodLedgerType.ReceiptPayment), f.Session);
        var rows = result.PaymentRows.Where(r => r.TransactionId == tx.Id).ToList(); Assert.NotEmpty(rows);
        Assert.All(rows, r => { Assert.Null(r.ReceiptAmount); Assert.Null(r.PaymentAmount); });
        Assert.Equal(kind == "unknown" ? "legacy-unknown" : "일반수금", tx.TransactionKind); Assert.False(tx.IsDirty);
    }

    [Fact]
    public async Task RevocationClearsAllDisplaysAndTokenRefreshKeepsThem()
    {
        await using var f = await Fixture.CreateAsync(); var result = await f.Aggregation.BuildAsync(Query(), f.Session); Apply(f.Vm, result);
        Assert.Equal("12,345", f.Vm.SummaryTradeAmountText);
        f.SetAccess(true, true); Assert.NotEmpty(f.Vm.LedgerRows);
        f.SetAccess(false, false);
        Assert.Empty(f.Vm.LedgerRows); Assert.Empty(f.Vm.SelectedLedgerItems); Assert.Empty(f.Vm.MonthlySalesChartPoints);
        Assert.Null(f.Vm.SelectedLedgerRow); Assert.Equal("비공개", f.Vm.SummaryTradeAmountText); Assert.Equal("", f.Vm.LastExportPath);
        f.SetAccess(true, true); Apply(f.Vm, result); Assert.Empty(f.Vm.LedgerRows);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("regrant")]
    [InlineData("dispose")]
    public async Task DelayedAggregationCannotRepopulateOrExportAfterAccessChanges(string action)
    {
        await using var f = await Fixture.CreateAsync(); f.Vm.IsAllCustomers = true; f.Vm.FromDate = Day.ToDateTime(TimeOnly.MinValue); f.Vm.ToDate = f.Vm.FromDate;
        f.Gate.Arm(); var pending = f.Vm.StartAggregationCommand.ExecuteAsync(null);
        try
        {
            await f.Gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (action == "dispose") f.Vm.Dispose();
            else { f.SetAccess(false, false); if (action == "regrant") f.SetAccess(true, true); }
        }
        finally { f.Gate.Release(); }
        await pending; Assert.Empty(f.Vm.LedgerRows); Assert.Equal("", f.Vm.LastExportPath); Assert.False(f.Vm.IsBusy);
    }

    [Theory]
    [InlineData(PeriodLedgerType.SalesOnly)]
    [InlineData(PeriodLedgerType.ReceiptPayment)]
    [InlineData(PeriodLedgerType.YeonsuDelivery)]
    public async Task RestrictedWorkbookContainsMetadataAndPrivateMarkersInsteadOfNumbers(PeriodLedgerType type)
    {
        await using var f = await Fixture.CreateAsync(); f.SetAccess(false, false);
        var result = await f.Aggregation.BuildAsync(Query(type), f.Session);
        var path = await f.Exporter.ExportAsync(result, f.ExportDirectory);
        using var workbook = new XLWorkbook(path); var cells = workbook.Worksheets.SelectMany(s => s.CellsUsed()).ToList();
        Assert.Contains(cells, c => c.GetString() == "비공개"); Assert.Contains(cells, c => c.GetString().Contains("fixture"));
        Assert.DoesNotContain(cells, c => c.DataType == XLDataType.Number && new[] {12345d, 4115d, 2345d}.Contains(c.GetDouble()));
        Assert.Equal(1, f.OpenCount);
        if (type == PeriodLedgerType.SalesOnly)
        {
            var subtotal = cells.Single(c => c.GetString() == "(소계)").Address.RowNumber;
            Assert.Equal("비공개", workbook.Worksheet("원장").Cell(subtotal, 7).GetString());
        }
    }

    [Theory]
    [InlineData(PeriodLedgerType.SalesOnly)]
    [InlineData(PeriodLedgerType.PurchaseOnly)]
    [InlineData(PeriodLedgerType.ReceiptPayment)]
    [InlineData(PeriodLedgerType.YeonsuDelivery)]
    public async Task AuthorizedWorkbooksKeepNumericAmounts(PeriodLedgerType type)
    {
        await using var f = await Fixture.CreateAsync(type == PeriodLedgerType.PurchaseOnly ? VoucherType.Purchase : VoucherType.Sales);
        var result = await f.Aggregation.BuildAsync(Query(type), f.Session);
        var path = await f.Exporter.ExportAsync(result, f.ExportDirectory);
        using var workbook = new XLWorkbook(path);
        Assert.Contains(workbook.Worksheet("원장").CellsUsed(), c => c.DataType == XLDataType.Number &&
            c.GetDouble() == (type == PeriodLedgerType.ReceiptPayment ? 2345d : type == PeriodLedgerType.PurchaseOnly ? -12345d : 12345d));
        Assert.Equal(1, f.OpenCount);
    }

    [Fact]
    public async Task PartialAccessKeepsAuthorizedRowsWithoutPublishingCombinedTotals()
    {
        await using var f = await Fixture.CreateAsync(); f.SetAccess(true, false);
        var purchase = f.NewInvoice(VoucherType.Purchase, 999999m); f.Db.Invoices.Add(purchase); await f.Db.SaveChangesAsync();
        var result = await f.Aggregation.BuildAsync(Query(PeriodLedgerType.SalesPurchase), f.Session);
        var rows = result.Blocks.SelectMany(b => b.Rows).Where(r => r.IsInvoiceSummary).ToList();
        Assert.Equal(12345m, rows.Single(r => r.InvoiceId == f.Invoice.Id).TradeAmount);
        Assert.Null(rows.Single(r => r.InvoiceId == purchase.Id).TradeAmount); Assert.Null(result.Totals.TradeAmount);
        Assert.Equal(12345m, Assert.Single(result.MonthlySalesChartPoints).SalesAmount);
    }

    [Fact]
    public async Task HiddenMonthCannotBeInferredFromOtherMonthBarHeight()
    {
        await using var f = await Fixture.CreateAsync();
        var hidden = f.NewInvoice(VoucherType.Sales, 999999m); hidden.InvoiceDate = new(2026, 8, 24); hidden.AmountsHidden = true;
        f.Db.Invoices.Add(hidden); await f.Db.SaveChangesAsync();
        var result = await f.Aggregation.BuildAsync(Query() with { From = new(2026, 8, 1) }, f.Session);
        Assert.Null(result.MonthlySalesChartPoints[0].SalesAmount); Assert.Equal(12345m, result.MonthlySalesChartPoints[1].SalesAmount);
        Assert.All(result.MonthlySalesChartPoints, p => Assert.Equal(0d, p.BarHeight));
        Apply(f.Vm, result); Assert.Contains("비공개", f.Vm.MonthlySalesChartSummaryText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedExportDoesNotCreateOrOpenWorkbook(bool duringExport)
    {
        await using var f = await Fixture.CreateAsync(); var result = await f.Aggregation.BuildAsync(Query(), f.Session);
        if (!duringExport) f.SetAccess(false, false);
        var progress = new SynchronousProgress(message => { if (duringExport && message == "저장 중...") { f.SetAccess(false, false); f.SetAccess(true, true); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Exporter.ExportAsync(result, f.ExportDirectory, progress));
        Assert.Empty(Directory.GetFiles(f.ExportDirectory)); Assert.Equal(0, f.OpenCount);
    }

    [Fact]
    public async Task RestrictedUserCanStillSaveOnlyInvoiceAndItemNotes()
    {
        await using var f = await Fixture.CreateAsync(); f.SetAccess(false, false);
        Apply(f.Vm, await f.Aggregation.BuildAsync(Query(), f.Session));
        Assert.True((await f.Vm.UpdateSelectedLedgerMemoAsync("changed note")).Success);
        Assert.True((await f.Vm.UpdateSelectedItemMemoAsync("changed item note")).Success);
        Assert.Equal(12345m, f.Invoice.TotalAmount); Assert.Equal(4115m, f.Invoice.Lines.Single().UnitPrice); Assert.Equal(3m, f.Invoice.Lines.Single().Quantity);
        Assert.Equal("changed note", f.Invoice.Memo); Assert.Equal("changed item note", f.Invoice.Lines.Single().Remark);
    }

    [Fact]
    public async Task CrossTenantDataDoesNotEnterLedger()
    {
        await using var f = await Fixture.CreateAsync(); var other = f.NewInvoice(VoucherType.Sales, 999999m);
        other.TenantCode = TenantScopeCatalog.Itworld; other.OfficeCode = other.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        f.Db.Invoices.Add(other); await f.Db.SaveChangesAsync(); var result = await f.Aggregation.BuildAsync(Query(), f.Session);
        Assert.DoesNotContain(result.Blocks.SelectMany(b => b.Rows), r => r.InvoiceId == other.Id); Assert.Equal(12345m, result.Totals.TradeAmount);
    }

    private static void Apply(PeriodLedgerViewModel vm, PeriodLedgerBuildResult result)
        => typeof(PeriodLedgerViewModel).GetMethod("ApplyResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [result]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectionRateUsesKnownAmountsAndNeverTreatsHiddenAsZero(bool hidden)
    {
        await using var f = await Fixture.CreateAsync();
        var result = await f.Aggregation.BuildAsync(Query(), f.Session);
        Apply(f.Vm, result with { Totals = result.Totals with { ReceiptAmount = 25m, ReceivableBalance = hidden ? null : 75m } });
        Assert.Equal(hidden ? "비공개" : 25m.ToString("0.0'%' ", System.Globalization.CultureInfo.CurrentCulture).Trim(), f.Vm.SummaryCollectionRateText);
    }
    private sealed class SynchronousProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public LocalDbContext Db { get; }
        public SessionState Session { get; } = new();
        public LocalInvoice Invoice { get; }
        public LocalCustomer Customer { get; }
        public LocalStateService Local { get; }
        public PeriodLedgerAggregationService Aggregation { get; }
        public PeriodLedgerExcelExportService Exporter { get; }
        public PeriodLedgerViewModel Vm { get; }
        public QueryGate Gate { get; }
        public int OpenCount { get; private set; }
        public string ExportDirectory { get; } = Path.Combine(Path.GetTempPath(), "period-privacy-" + Guid.NewGuid().ToString("N"));
        private Fixture(SqliteConnection connection, LocalDbContext db, QueryGate gate)
        {
            _connection = connection; Db = db; Gate = gate; Session.SetSession("fixture", User(Guid.NewGuid()));
            Customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "fixture customer", NameMatchKey = "FIXTURE", TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, IsDirty = false };
            Invoice = NewInvoice(VoucherType.Sales, 12345m);
            Local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), Session);
            Aggregation = new(Local); Exporter = new(_ => OpenCount++); Vm = new(Local, Aggregation, Exporter, Session);
            Directory.CreateDirectory(ExportDirectory);
        }
        public void SetAccess(bool sales, bool purchase) => Session.RefreshSession("refresh", User(Session.User!.UserId, sales, purchase));
        public LocalInvoice NewInvoice(VoucherType type, decimal amount)
        {
            var i = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = Customer.Id, TenantCode = Customer.TenantCode, OfficeCode = Customer.OfficeCode, ResponsibleOfficeCode = Customer.OfficeCode, VoucherType = type, InvoiceDate = Day, TotalAmount = amount, IsLatestVersion = true, IsConfirmed = true, IsDirty = false, Memo = "fixture memo" }; i.VersionGroupId = i.Id;
            i.Lines.Add(new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = i.Id, ItemNameOriginal = "fixture item", Quantity = 3, UnitPrice = amount / 3, LineAmount = amount, Remark = "fixture line note" });
            return i;
        }
        public LocalTransaction Transaction(bool hidden = false, decimal amount = 2345m) => new()
        { Id = Guid.NewGuid(), CustomerId = Customer.Id, TenantCode = Customer.TenantCode, OfficeCode = Customer.OfficeCode, ResponsibleOfficeCode = Customer.OfficeCode, TransactionDate = Day, TransactionKind = "일반수금", ReceiptTotal = amount, CashReceipt = amount, AmountsHidden = hidden, IsDirty = false };
        public static async Task<Fixture> CreateAsync(VoucherType type = VoucherType.Sales)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var gate = new QueryGate();
            var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).AddInterceptors(gate).Options); await db.Database.EnsureCreatedAsync();
            var f = new Fixture(connection, db, gate); f.Invoice.VoucherType = type;
            f.Invoice.Payments.Add(new LocalPayment { Id = Guid.NewGuid(), InvoiceId = f.Invoice.Id, PaymentDate = Day, Amount = 2345m, IsDirty = false });
            db.Customers.Add(f.Customer); db.Invoices.Add(f.Invoice); await db.SaveChangesAsync(); return f;
        }
        public async ValueTask DisposeAsync()
        { Gate.Release(); Vm.Dispose(); await Db.DisposeAsync(); await _connection.DisposeAsync(); Directory.Delete(ExportDirectory, true); }
    }
    private sealed class QueryGate : DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _released.TrySetResult();
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("Invoices") && Interlocked.Exchange(ref _armed, 0) == 1) { Blocked.TrySetResult(); await _released.Task.WaitAsync(cancellationToken); } return result; }
    }
}
