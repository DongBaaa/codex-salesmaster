using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class RecycleBinAmountPrivacyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ListUsesAmountGrantsAndHiddenFlagsWithoutWritingBusinessRows(bool sales, bool purchase, bool hidden)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState(); session.SetSession("test", User(Guid.NewGuid(), sales, purchase));
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "보존 거래처", NameMatchKey = "CUSTOMER",
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var item = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "보존 품목", NameMatchKey = "ITEM",
            OfficeCode = OfficeCodeCatalog.Usenet, SalePrice = 123456, Notes = "품목 비고", IsDeleted = true };
        var salesInvoice = Invoice(customer.Id, VoucherType.Sales, hidden);
        var purchaseInvoice = Invoice(customer.Id, VoucherType.Purchase, hidden);
        var payment = new LocalPayment { Id = Guid.NewGuid(), InvoiceId = salesInvoice.Id, Amount = 123456, AmountsHidden = hidden, IsDeleted = true };
        var transaction = new LocalTransaction { Id = Guid.NewGuid(), CustomerId = customer.Id,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionKind = "전표지급", LinkedInvoiceId = purchaseInvoice.Id, PaymentTotal = 123456, CashPayment = 123456,
            AmountsHidden = hidden, Note = "거래 비고", IsDeleted = true };
        var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), CustomerId = customer.Id, CustomerName = "보존 거래처",
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet, ProfileKey = "fixture-profile", MonthlyAmount = 123456, IsDeleted = true };
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementNumber = "TEST-ASSET", MonthlyFee = 123456, IsDeleted = true };
        var log = new LocalRentalBillingLog { Id = Guid.NewGuid(), BillingProfileId = profile.Id,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BilledAmount = 123456, BillingYearMonth = "2026-09", IsDeleted = true };
        db.AddRange(customer, item, salesInvoice, purchaseInvoice, payment, transaction, profile, asset, log);
        await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var entries = await local.GetRecycleBinEntriesAsync(session);
        AssertMoney(entries.Single(x => x.EntityId == item.Id), sales);
        AssertMoney(entries.Single(x => x.EntityId == salesInvoice.Id), sales && !hidden);
        AssertMoney(entries.Single(x => x.EntityId == purchaseInvoice.Id), purchase && !hidden);
        AssertMoney(entries.Single(x => x.EntityId == payment.Id), sales && !hidden);
        AssertMoney(entries.Single(x => x.EntityId == transaction.Id), purchase && !hidden);
        foreach (var id in new[] { profile.Id, asset.Id, log.Id }) AssertMoney(entries.Single(x => x.EntityId == id), sales);
        Assert.Contains("품목 비고", entries.Single(x => x.EntityId == item.Id).Detail);
        Assert.Contains("거래 비고", entries.Single(x => x.EntityId == transaction.Id).Detail);
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(123456, (await db.Payments.IgnoreQueryFilters().SingleAsync()).Amount);
        Assert.Equal(123456, (await db.Transactions.IgnoreQueryFilters().SingleAsync()).PaymentTotal);

        // Cached strings cannot expose old amounts after revocation, even before UI notification runs.
        session.RefreshSession("refresh", User(session.User!.UserId, false, false));
        Assert.All(entries, entry => Assert.DoesNotContain("123,456", JsonSerializer.Serialize(entry)));
        session.Clear();
        Assert.All(entries, entry => Assert.DoesNotContain("123,456", JsonSerializer.Serialize(entry)));
    }

    [Fact]
    public void EntryFromStaleReadCannotPublishMoneyAndNotifiesAllVisibleFields()
    {
        var session = new SessionState(); session.SetSession("test", User(Guid.NewGuid(), true, true));
        var original = FinancialAmountVisibility.CaptureAccess(session);
        session.SetSession("new-account", User(Guid.NewGuid(), true, true));
        var entry = new RecycleBinEntry { Title = "123,456원", Subtitle = "123,456원", Detail = "123,456원" }
            .ProtectAmounts(session, original, true, "금액 비공개", "금액 비공개", "금액 비공개");
        var changed = new List<string?>(); entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        entry.RefreshAmountAccess();
        Assert.Equal(new[] { "Title", "Subtitle", "Detail" }, changed);
        Assert.DoesNotContain("123,456", JsonSerializer.Serialize(entry));
    }

    private static void AssertMoney(RecycleBinEntry entry, bool visible)
    {
        var text = entry.Title + entry.Subtitle + entry.Detail;
        Assert.Equal(visible, text.Contains("123,456", StringComparison.Ordinal));
        Assert.Equal(!visible, text.Contains("비공개", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowAccessSubscriptionRefreshesCachedRowsAndDetachesOnClose()
    {
        var session = new SessionState(); session.SetSession("test", User(Guid.NewGuid(), true, true));
        var entry = new RecycleBinEntry { Detail = "123,456원" }.ProtectAmounts(session,
            FinancialAmountVisibility.CaptureAccess(session), true, hiddenDetail: "금액 비공개");
        // Only the window event adapter is under test; no settings services or UI are constructed.
        var vm = (EnvironmentSettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(EnvironmentSettingsViewModel));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(EnvironmentSettingsViewModel).GetField("_session", flags)!.SetValue(vm, session);
        typeof(EnvironmentSettingsViewModel).GetField("_allRecycleBinEntries", flags)!.SetValue(vm, new List<RecycleBinEntry> { entry });
        var changes = 0; entry.PropertyChanged += (_, _) => changes++;
        vm.AttachRecycleBinAmountAccess(); vm.AttachRecycleBinAmountAccess(); changes = 0;
        session.RefreshSession("refresh", User(session.User!.UserId, false, false));
        Assert.Equal(3, changes); Assert.Equal("금액 비공개", entry.Detail);
        vm.DetachRecycleBinAmountAccess(); changes = 0;
        session.Clear(); Assert.Equal(0, changes);
    }
    private static LocalInvoice Invoice(Guid customerId, VoucherType type, bool hidden) => new()
    {
        Id = Guid.NewGuid(), CustomerId = customerId, VoucherType = type, InvoiceNumber = type.ToString(),
        OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
        TotalAmount = 123456, AmountsHidden = hidden, IsDeleted = true, IsLatestVersion = true
    };
    private static UserSessionDto User(Guid id, bool sales, bool purchase) => new()
    {
        UserId = id, Username = "recycle-fixture", Role = DomainConstants.RoleUser,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = new[] { AppPermissionNames.DataBackupRestore, AppPermissionNames.ItemEdit, AppPermissionNames.InvoiceEdit,
            AppPermissionNames.PaymentEdit, AppPermissionNames.RentalProfileEdit, AppPermissionNames.RentalAssetEdit }
            .Concat(sales ? new[] { AppPermissionNames.AmountViewSales } : []).Concat(purchase ? new[] { AppPermissionNames.AmountViewPurchase } : []).ToList()
    };
}
