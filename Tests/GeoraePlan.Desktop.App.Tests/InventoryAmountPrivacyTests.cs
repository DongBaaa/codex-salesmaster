using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Converters;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryAmountPrivacyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EditorSeparatesPurchaseSalesAndGradeVisibilityWithoutChangingSnapshots(bool sales, bool purchase)
    {
        await using var f = await Fixture.CreateAsync(sales, purchase);
        using var vm = f.Editor();
        Assert.Equal(purchase ? 110m : (decimal?)null, vm.EditablePurchasePrice);
        Assert.Equal(sales ? 220m : (decimal?)null, vm.EditableSalePrice);
        Assert.Equal(sales ? 330m : (decimal?)null, vm.EditableRetailPrice);
        Assert.Equal(purchase ? 330m : (decimal?)null, vm.AssetValue);
        Assert.Equal(sales ? 777m : (decimal?)null, Assert.Single(vm.PriceGradeRows).EditableUnitPrice);
        Assert.False(vm.HasPendingChanges); Assert.True(vm.CanSaveItems);
        vm.EditablePurchasePrice = 901; vm.EditableSalePrice = 902; vm.EditableRetailPrice = 903;
        vm.PriceGradeRows[0].EditableUnitPrice = 904;
        Assert.Equal(purchase ? 901m : 110m, vm.EditPurchasePrice);
        Assert.Equal(sales ? 902m : 220m, vm.EditSalePrice);
        Assert.Equal(sales ? 903m : 330m, vm.EditRetailPrice);
        Assert.Equal(sales ? 904m : 777m, vm.PriceGradeRows[0].UnitPrice);
        Assert.Equal(sales || purchase, vm.HasPendingChanges);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SessionAwareWritesPreserveUnreadablePricesAndPreparedGradeMutation(bool sales, bool purchase)
    {
        await using var f = await Fixture.CreateAsync(sales, purchase);
        var candidate = f.Candidate();
        candidate.PurchasePrice = candidate.SalePrice = candidate.RetailPrice = candidate.PriceGradeA = candidate.PriceGradeB = candidate.PriceGradeC = 999m;
        await f.Local.SaveInventoryItemAsync(candidate, f.Session, OfficeCodeCatalog.Usenet,
            [new LocalItemPriceGrade { Id = f.Grade.Id, ItemId = f.Item.Id, PriceGradeOptionId = f.Option.Id, PriceGradeName = f.Option.Name, UnitPrice = 888m, IsActive = true }]);
        var stored = await f.Db.Items.AsNoTracking().SingleAsync();
        Assert.Equal("changed memo", stored.SimpleMemo);
        Assert.Equal(purchase ? 999m : 110m, stored.PurchasePrice);
        Assert.Equal(sales ? 999m : 220m, stored.SalePrice);
        Assert.Equal(sales ? 999m : 330m, stored.RetailPrice);
        Assert.Equal(sales ? 999m : 440m, stored.PriceGradeA);
        Assert.Equal(sales ? 999m : 550m, stored.PriceGradeB);
        Assert.Equal(sales ? 999m : 660m, stored.PriceGradeC);
        Assert.Equal(3m, stored.CurrentStock);
        var grade = await f.Db.ItemPriceGrades.AsNoTracking().SingleAsync();
        Assert.Equal(sales ? 888m : 777m, grade.UnitPrice); Assert.True(grade.IsDirty);
        var pending = await f.Db.SyncOutboxEntries.AsNoTracking().SingleAsync();
        Assert.Equal("Prepared", pending.Status); Assert.Equal("keep-price-mutation", pending.MutationId);
        Assert.Equal(f.Grade.Id, pending.EntityId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenNewItemCannotIntroduceMoneyOrGradeChanges(bool inventoryApi)
    {
        await using var f = await Fixture.CreateAsync(false, false);
        var item = f.Candidate(); item.Id = Guid.NewGuid(); item.NameOriginal = "NEW"; item.Revision = 0;
        item.PurchasePrice = item.SalePrice = item.RetailPrice = item.PriceGradeA = item.PriceGradeB = item.PriceGradeC = 999m;
        var grades = new[] { new LocalItemPriceGrade { Id = Guid.NewGuid(), PriceGradeOptionId = f.Option.Id, PriceGradeName = f.Option.Name, UnitPrice = 999m } };
        if (inventoryApi) await f.Local.SaveInventoryItemAsync(item, f.Session, OfficeCodeCatalog.Usenet, grades);
        else await f.Local.UpsertItemAsync(item, f.Session, OfficeCodeCatalog.Usenet, grades);
        var stored = await f.Db.Items.AsNoTracking().SingleAsync(x => x.Id == item.Id);
        Assert.Equal(0m, stored.PurchasePrice + stored.SalePrice + stored.RetailPrice + stored.PriceGradeA + stored.PriceGradeB + stored.PriceGradeC);
        Assert.Empty(await f.Db.ItemPriceGrades.Where(x => x.ItemId == item.Id).ToListAsync());
        Assert.Equal("changed memo", stored.SimpleMemo);
    }

    [Fact]
    public async Task MetadataAutoSavePreservesPricesWhenRightsWereRevokedAfterOpening()
    {
        await using var f = await Fixture.CreateAsync(true, true);
        using var vm = f.Editor();
        vm.EditablePurchasePrice = 900m; vm.EditableSalePrice = 901m; vm.PriceGradeRows[0].EditableUnitPrice = 902m;
        vm.EditSimpleMemo = "allowed note";
        f.Refresh(false, false);
        Assert.Null(vm.EditablePurchasePrice); Assert.Null(vm.EditableSalePrice); Assert.Null(vm.AssetValue);
        Assert.Null(vm.PriceGradeRows[0].EditableUnitPrice);
        Assert.True(await vm.TryAutoSaveOnCloseAsync());
        var saved = await f.Db.Items.AsNoTracking().SingleAsync();
        Assert.Equal("allowed note", saved.SimpleMemo); Assert.Equal(110m, saved.PurchasePrice); Assert.Equal(220m, saved.SalePrice);
        Assert.Equal(777m, (await f.Db.ItemPriceGrades.AsNoTracking().SingleAsync()).UnitPrice);
        Assert.Equal("Prepared", (await f.Db.SyncOutboxEntries.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ReadOnlyItemOwnerCanSeeGrantedPricesButCannotChangeTheirValues()
    {
        await using var f = await Fixture.CreateAsync(true, true);
        f.Item.OfficeCode = OfficeCodeCatalog.Yeonsu; await f.Db.SaveChangesAsync();
        using var vm = f.Editor();
        Assert.False(vm.CanSaveItems); Assert.True(vm.IsPurchasePriceReadOnly); Assert.True(vm.IsSalesPriceReadOnly);
        Assert.Equal(110m, vm.EditablePurchasePrice);
        vm.EditablePurchasePrice = 9m; vm.EditableSalePrice = 9m; vm.EditableRetailPrice = 9m; vm.PriceGradeRows[0].EditableUnitPrice = 9m;
        Assert.Equal(110m, vm.EditPurchasePrice); Assert.Equal(220m, vm.EditSalePrice); Assert.Equal(330m, vm.EditRetailPrice);
        Assert.Equal(777m, vm.PriceGradeRows[0].UnitPrice); Assert.False(vm.HasPendingChanges);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("account")]
    [InlineData("role")]
    [InlineData("dispose")]
    public async Task PrivacyTransitionDoesNotCreateAnAutoSaveOrLeaveBoundPrices(string change)
    {
        await using var f = await Fixture.CreateAsync(true, true);
        using var vm = f.Editor();
        f.Refresh(true, true); Assert.False(vm.HasPendingChanges);
        if (change == "dispose") vm.Dispose();
        else if (change == "role")
        {
            var user = Fixture.User(f.Session.User!.UserId, true, true); user.Role = DomainConstants.RoleAdmin;
            f.Session.RefreshSession("role", user);
        }
        else f.Session.RefreshSession("changed", Fixture.User(change == "account" ? Guid.NewGuid() : f.Session.User!.UserId, change == "account", change == "account"));
        Assert.Null(vm.EditablePurchasePrice); Assert.Null(vm.EditableSalePrice); Assert.Null(vm.EditableRetailPrice); Assert.Null(vm.AssetValue);
        Assert.Null(vm.PriceGradeRows[0].EditableUnitPrice); Assert.False(vm.HasPendingChanges);
        Assert.Equal(110m, vm.EditPurchasePrice); Assert.Equal(777m, vm.PriceGradeRows[0].UnitPrice);
        if (change != "revoke") Assert.False(vm.SaveItemCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionOrAccountChangeDuringWriteRollsBackItemAndPrices(bool switchAccount)
    {
        var gate = new SaveGate();
        await using var f = await Fixture.CreateAsync(true, true, gate);
        gate.Enabled = true;
        var candidate = f.Candidate(); candidate.PurchasePrice = 999m;
        var save = f.Local.SaveInventoryItemAsync(candidate, f.Session, OfficeCodeCatalog.Usenet, []);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Session.RefreshSession("changed", Fixture.User(switchAccount ? Guid.NewGuid() : f.Session.User!.UserId, switchAccount, switchAccount));
        }
        finally { gate.Release.TrySetResult(); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => save);
        var stored = await f.Db.Items.AsNoTracking().SingleAsync();
        Assert.Equal("original", stored.SimpleMemo); Assert.Equal(110m, stored.PurchasePrice); Assert.False(stored.IsDirty);
        Assert.Equal(777m, (await f.Db.ItemPriceGrades.AsNoTracking().SingleAsync()).UnitPrice);
    }

    [Fact]
    public void WpfPriceBindingsRevokeVisibleTextWithoutWritingZeroBack()
    {
        RunSta(() =>
        {
            var f = Fixture.CreateAsync(true, true).GetAwaiter().GetResult();
            try
            {
                using var vm = f.Editor();
                var box = new TextBox();
                box.SetBinding(TextBox.TextProperty, new Binding(nameof(vm.EditablePurchasePrice)) { Source = vm, Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, Converter = new DecimalToStringConverter(), ConverterParameter = "hiddenWhenNull" });
                Assert.Equal("110", box.Text);
                f.Refresh(false, false);
                box.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
                Assert.Equal("비공개", box.Text);
                box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Equal(110m, vm.EditPurchasePrice); Assert.False(vm.HasPendingChanges);
                f.Refresh(true, true); vm.EditablePurchasePrice = 0m;
                box.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget(); Assert.Equal("0", box.Text);
                var converter = new DecimalToStringConverter();
                Assert.Same(Binding.DoNothing, converter.ConvertBack("비공개", typeof(decimal?), "hiddenWhenNull", CultureInfo.InvariantCulture));
                Assert.Equal(string.Empty, converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture));
            }
            finally { f.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class SaveGate : SaveChangesInterceptor
    {
        public bool Enabled;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<LocalItem>().Any(x => x.State == EntityState.Modified))
            { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public LocalDbContext Db { get; }
        public SessionState Session { get; }
        public LocalStateService Local { get; }
        public LocalItem Item { get; }
        public LocalPriceGradeOption Option { get; }
        public LocalItemPriceGrade Grade { get; }
        private Fixture(SqliteConnection connection, LocalDbContext db, SessionState session, LocalItem item, LocalPriceGradeOption option, LocalItemPriceGrade grade)
        { _connection = connection; Db = db; Session = session; Item = item; Option = option; Grade = grade;
            Local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session); }
        public static UserSessionDto User(Guid id, bool sales, bool purchase) => new() { UserId = id, Username = "inventory-fixture", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { AppPermissionNames.ItemEdit }.Concat(sales ? new[] { AppPermissionNames.AmountViewSales } : []).Concat(purchase ? new[] { AppPermissionNames.AmountViewPurchase } : []).ToList() };
        public void Refresh(bool sales, bool purchase) => Session.RefreshSession("refresh", User(Session.User!.UserId, sales, purchase));
        public static async Task<Fixture> CreateAsync(bool sales, bool purchase, IInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var db = new LocalDbContext(options.Options); await db.Database.EnsureCreatedAsync();
            var session = new SessionState(); session.SetSession("fixture", User(Guid.NewGuid(), sales, purchase));
            var item = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "ITEM", NameMatchKey = "ITEM", TenantCode = session.TenantCode, OfficeCode = session.OfficeCode,
                PurchasePrice = 110m, SalePrice = 220m, RetailPrice = 330m, PriceGradeA = 440m, PriceGradeB = 550m, PriceGradeC = 660m,
                CurrentStock = 3m, IsDirty = false, SimpleMemo = "original", Revision = 1 };
            var option = new LocalPriceGradeOption { Id = Guid.NewGuid(), Name = "fixture grade", PriceSource = SelectionOptionDefaults.PriceSourceA, IsActive = true };
            var grade = new LocalItemPriceGrade { Id = Guid.NewGuid(), ItemId = item.Id, PriceGradeOptionId = option.Id, PriceGradeName = option.Name, UnitPrice = 777m, IsActive = true, IsDirty = true, Revision = 1 };
            db.Items.Add(item); db.PriceGradeOptions.Add(option); db.ItemPriceGrades.Add(grade);
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry { MutationId = "keep-price-mutation", EntityName = "ItemPriceGrade", EntityId = grade.Id,
                ExpectedRevision = 1, TenantCode = session.TenantCode, OfficeCode = session.OfficeCode, UserId = session.User!.UserId, Status = "Prepared" });
            await db.SaveChangesAsync();
            return new Fixture(connection, db, session, item, option, grade);
        }
        public InventoryViewModel Editor()
        {
            var vm = new InventoryViewModel(Local, Session);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(InventoryViewModel).GetField("_priceGradeOptions", flags)!.SetValue(vm, new List<LocalPriceGradeOption> { Option });
            var cache = (Dictionary<Guid, List<LocalItemPriceGrade>>)typeof(InventoryViewModel).GetField("_itemPriceGradesByItemId", flags)!.GetValue(vm)!;
            cache[Item.Id] = [Grade];
            typeof(InventoryViewModel).GetMethod("LoadFormFromItem", flags)!.Invoke(vm, [new InventoryItemRow(Item, new Dictionary<string, decimal> { [Session.OfficeCode] = 3m }, Session.OfficeCode)]);
            return vm;
        }
        public LocalItem Candidate() => new() { Id = Item.Id, Revision = Item.Revision, NameOriginal = Item.NameOriginal, NameMatchKey = Item.NameMatchKey,
            TenantCode = Item.TenantCode, OfficeCode = Item.OfficeCode, CurrentStock = 3m, SimpleMemo = "changed memo" };
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
