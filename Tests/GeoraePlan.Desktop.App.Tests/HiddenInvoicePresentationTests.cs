using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class HiddenInvoicePresentationTests
{
    [Theory]
    [InlineData(typeof(MainViewModel))]
    [InlineData(typeof(CustomerInvoiceLookupViewModel))]
    public void PreviewBindingsRefreshVisibleTotalsAndDisclosureChanges(Type viewModelType)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Exercise the actual generated property notifications without starting
                // unrelated services, loading customer data, or showing a window.
                var vm = RuntimeHelpers.GetUninitializedObject(viewModelType);
                foreach (var kind in new[] { "Total", "Supply", "Vat" })
                {
                    var text = new TextBlock { DataContext = vm };
                    BindingOperations.SetBinding(text, TextBlock.TextProperty,
                        new Binding($"Preview{kind}AmountDisplay"));
                    Assert.Equal("0", text.Text);
                    viewModelType.GetProperty($"Preview{kind}Amount")!.SetValue(vm, 55000m);
                    Assert.Equal(55000m.ToString("N0"), text.Text);
                    viewModelType.GetProperty("PreviewAmountsHidden")!.SetValue(vm, true);
                    Assert.Equal("비공개", text.Text);
                    viewModelType.GetProperty($"Preview{kind}Amount")!.SetValue(vm, 77000m);
                    Assert.Equal("비공개", text.Text);
                    viewModelType.GetProperty("PreviewAmountsHidden")!.SetValue(vm, false);
                    Assert.Equal(77000m.ToString("N0"), text.Text);
                    BindingOperations.ClearAllBindings(text);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF binding test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenEditorPreservesQuantityAndMemoWithoutPublishingMoney(bool hiddenByParent)
    {
        var source = new LocalInvoiceLine
        {
            Id = Guid.NewGuid(), ItemNameOriginal = "품목", Quantity = 2,
            UnitPrice = 1100, LineAmount = 2200, AmountsHidden = !hiddenByParent
        };
        var row = InvoiceLineEditModel.FromLocal(source, hiddenByParent);
        row.Quantity = 3;
        row.Remark = "비고 수정";
        row.EditableUnitPrice = 999;
        row.EditableLineAmount = 999;
        Assert.False(row.CanEditAmounts);
        Assert.Null(row.EditableUnitPrice);
        Assert.Null(row.EditableLineAmount);
        Assert.Equal("비공개", row.UnitPriceDisplay);
        Assert.Equal("비공개", row.LineAmountDisplay);
        var outgoing = LocalMappings.ToDto(row.ToLocal(Guid.NewGuid()));
        Assert.True(outgoing.AmountsHidden);
        Assert.Null(outgoing.UnitPrice);
        Assert.Null(outgoing.LineAmount);
        Assert.Equal(3, outgoing.Quantity);
        Assert.Equal("비고 수정", outgoing.Remark);
        Assert.Equal(1100, source.UnitPrice); // The original cached/dirty object is not edited.
    }

    private static SessionState CreateAmountViewer()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "amount-fixture", Role = DomainConstants.RoleAdmin, OfficeCode = OfficeCodeCatalog.Usenet });
        return session;
    }

    [Fact]
    public void VisibleEditorStillCalculatesAndNotifiesDisplayedPrice()
    {
        var row = InvoiceLineEditModel.FromLocal(new LocalInvoiceLine { Quantity = 2, UnitPrice = 1100, LineAmount = 2200 });
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        row.EditableUnitPrice = 1200;
        row.Quantity = 3;
        Assert.True(row.CanEditAmounts);
        Assert.Equal(3600, row.LineAmount);
        Assert.Contains(nameof(row.UnitPriceDisplay), changes);
        Assert.Contains(nameof(row.LineAmountDisplay), changes);
        Assert.False(row.ToLocal(Guid.NewGuid()).AmountsHidden);
    }

    [Theory]
    [InlineData(false, VoucherType.Sales)]
    [InlineData(true, VoucherType.Sales)]
    [InlineData(false, VoucherType.Purchase)]
    [InlineData(true, VoucherType.Purchase)]
    public void HiddenListNeverShowsMoneyOrClaimsSettlement(bool summary, VoucherType type)
    {
        var row = summary
            ? InvoiceListRow.From(new LocalInvoiceListSummary { AmountsHidden = true, VoucherType = type, TotalAmount = 1100, SettledAmount = 1100 }, "거래처", true)
            : InvoiceListRow.From(new LocalInvoice { AmountsHidden = true, VoucherType = type, TotalAmount = 1100, Payments = [new LocalPayment { Amount = 1100 }] }, "거래처", true);
        Assert.True(row.AmountsHidden);
        Assert.Null(row.TotalAmount);
        Assert.Null(row.ReceiptAmount);
        Assert.Null(row.PaymentAmount);
        Assert.Null(row.BalanceAmount);
        Assert.False(row.IsBalanceCleared);
        Assert.All(new[] { row.SupplyAmountDisplay, row.VatAmountDisplay, row.TotalAmountDisplay, row.ReceiptAmountDisplay, row.PaymentAmountDisplay, row.BalanceAmountDisplay }, text => Assert.Equal("비공개", text));
    }

    [Fact]
    public void RealZeroRemainsVisibleAndMayBeSettled()
    {
        var row = InvoiceListRow.From(new LocalInvoice { VoucherType = VoucherType.Sales }, "거래처", true);
        Assert.False(row.AmountsHidden);
        Assert.Equal(0, row.BalanceAmount);
        Assert.True(row.IsBalanceCleared);
        Assert.Equal("0", row.TotalAmountDisplay);
    }

    [Fact]
    public void ImportedHiddenInvoiceKeepsDraftTotalsUnknownAcrossQuantityChanges()
    {
        using var vm = new SalesViewModel(null!, null!, null!, CreateAmountViewer());
        vm.ImportPreviousInvoices([new LocalInvoice
        {
            AmountsHidden = true,
            Lines = [new LocalInvoiceLine { ItemNameOriginal = "품목", Quantity = 2, UnitPrice = 1100, LineAmount = 2200 }]
        }], true);
        var line = Assert.Single(vm.Lines);
        line.Quantity = 5;
        line.Remark = "수량 수정";
        Assert.True(vm.AmountsHidden);
        Assert.Equal("비공개", vm.TotalAmountDisplay);
        Assert.Equal("비공개", vm.SupplyAmountDisplay);
        Assert.Equal("비공개", vm.VatAmountDisplay);
        Assert.Null(LocalMappings.ToDto(line.ToLocal(Guid.NewGuid())).UnitPrice);
        vm.Lines.Clear();
        vm.Lines.Add(new InvoiceLineEditModel { ItemName = "공개 품목", Quantity = 2, UnitPrice = 1100 });
        Assert.False(vm.AmountsHidden);
        Assert.Equal(2200, vm.TotalAmount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ActualListProjectionRetainsHeaderOrLineDisclosure(bool headerHidden, bool lineHidden)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "시험", TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var invoice = new LocalInvoice
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, IsLatestVersion = true,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode, ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            AmountsHidden = headerHidden, InvoiceDate = new DateOnly(2026, 9, 24), TotalAmount = 1100,
            Lines = [new LocalInvoiceLine { Id = Guid.NewGuid(), ItemNameOriginal = "품목", Quantity = 1, UnitPrice = 1100, LineAmount = 1100, AmountsHidden = lineHidden }]
        };
        db.Customers.Add(customer);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "fixture-admin", Role = DomainConstants.RoleAdmin, TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode, ScopeType = TenantScopeCatalog.ScopeAdmin });
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var summary = Assert.Single(await local.GetInvoiceListSummariesAsync(null, null, customer.Id, session));
        Assert.Equal(headerHidden || lineHidden, summary.AmountsHidden);
        Assert.Equal(headerHidden || lineHidden, InvoiceListRow.From(summary, "시험", true).AmountsHidden);
        Assert.Equal(1100, (await db.Invoices.SingleAsync()).TotalAmount);
    }
}
