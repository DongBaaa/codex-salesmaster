using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetAmountPrivacyTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var access in new[] { "none", "purchase", "sales", "both", "admin", "god" })
        foreach (var hidden in new[] { false, true })
        foreach (var amount in new[] { 0, 23456 }) yield return [access, hidden, amount];
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task ListDetailAndEditorRespectBothDirectionsWithoutChangingStoredMoney(string access, bool hidden, int amount)
    {
        await using var db = await Database(); var asset = await Seed(db, hidden, amount);
        var session = Session(access); var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        var purchase = !hidden && access is "purchase" or "both" or "admin" or "god";
        var sales = !hidden && access is "sales" or "both" or "admin" or "god";
        foreach (var search in new[] { "", "PRIVACY-ASSET" })
        {
            var row = Assert.Single(await rental.GetAssetRowsAsync(new RentalAssetFilter { SearchText = search }, session));
            Assert.Equal(!purchase, row.Source.PurchaseAmountsHidden); Assert.Equal(!sales, row.Source.SalesAmountsHidden);
            Assert.Equal(sales ? amount : 0m, row.Source.MonthlyFee);
        }
        var detail = (await rental.GetAssetRowAsync(asset.Id, session))!;
        Assert.Equal(purchase ? amount : 0m, detail.Source.PurchasePrice);
        Assert.Equal(sales ? "deposit" : "", detail.Source.DepositText);
        Assert.Equal(sales ? amount : (decimal?)null, detail.Source.BlackOverageUnitPrice);
        var vm = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);
        try
        {
            await vm.LoadAndSelectAssetAsync(asset.Id);
            Assert.Equal(purchase ? amount : (decimal?)null, (decimal?)vm.EditPurchasePrice);
            Assert.Equal(sales ? amount : (decimal?)null, (decimal?)vm.EditSalePrice);
            Assert.Equal(sales ? amount : (decimal?)null, (decimal?)vm.EditMonthlyFee);
            Assert.Equal(sales ? "deposit" : null, vm.EditDepositText);
            Assert.False(vm.HasPendingChanges);
        }
        finally { await vm.CancelPendingBackgroundWorkAsync(); }
        db.ChangeTracker.Clear(); var stored = await db.RentalAssets.SingleAsync();
        Assert.Equal(amount, stored.PurchasePrice); Assert.Equal(amount, stored.MonthlyFee);
        Assert.Equal(hidden, stored.PurchaseAmountsHidden); Assert.Equal(hidden, stored.SalesAmountsHidden);
        Assert.False(stored.IsDirty); Assert.Equal("before", stored.Notes);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task EditorNoteSavePreservesMoneyProfileAndHistoricalSnapshot(string access, bool hidden, int amount)
    {
        await using var db = await Database(); var asset = await Seed(db, hidden, amount);
        var session = Session(access); var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        var vm = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);
        try
        {
            await vm.LoadAndSelectAssetAsync(asset.Id);
            typeof(RentalAssetViewModel).GetField("_suppressEditAutoSave", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
            vm.EditNotes = "nonfinancial edit";
            var capture = typeof(RentalAssetViewModel).GetMethod("CaptureEditSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var snapshot = capture.Invoke(vm, null)!;
            var candidate = (LocalRentalAsset)typeof(RentalAssetViewModel).GetMethod("BuildAsset", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [snapshot])!;
            var result = await rental.SaveAssetAsync(candidate, session);
            Assert.True(result.Success, result.Message);
        }
        finally { await vm.CancelPendingBackgroundWorkAsync(); }
        db.ChangeTracker.Clear(); var stored = await db.RentalAssets.SingleAsync();
        Assert.Equal("nonfinancial edit", stored.Notes); Assert.True(stored.IsDirty);
        Assert.Equal(amount, stored.PurchasePrice); Assert.Equal(amount, stored.SalePrice); Assert.Equal(amount, stored.MonthlyFee);
        Assert.Equal("deposit", stored.DepositText); Assert.Equal(amount, stored.BlackOverageUnitPrice);
        var dto = LocalMappings.ToDto(stored);
        var purchase = !hidden && access is "purchase" or "both" or "admin" or "god";
        var sales = !hidden && access is "sales" or "both" or "admin" or "god";
        Assert.Equal(purchase ? amount : (decimal?)null, dto.PurchasePrice); Assert.Equal(sales ? amount : (decimal?)null, dto.MonthlyFee);
        var profile = await db.RentalBillingProfiles.SingleAsync(); Assert.Equal(98765m, profile.MonthlyAmount); Assert.False(profile.IsDirty);
        var history = await db.RentalAssetAssignmentHistories.SingleAsync(); Assert.Equal(13579m, history.MonthlyFee); Assert.False(history.IsDirty);
        Assert.Null(history.ContractStartDate); Assert.Null(history.ContractEndDate);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    [InlineData("sales")]
    public async Task ForgedHiddenMoneyCannotOverwritePersistedSlots(string access)
    {
        await using var db = await Database(); var asset = await Seed(db, false, 12345);
        var candidate = await db.RentalAssets.AsNoTracking().SingleAsync();
        candidate.PurchasePrice = candidate.SalePrice = candidate.MonthlyFee = 99999;
        candidate.DepositText = "forged"; candidate.BlackOverageUnitPrice = 999;
        candidate.Notes = "allowed";
        var result = await new RentalStateService(db).SaveAssetAsync(candidate, Session(access));
        Assert.True(result.Success, result.Message); db.ChangeTracker.Clear();
        var stored = await db.RentalAssets.SingleAsync();
        Assert.Equal(access == "purchase" ? 99999m : 12345m, stored.PurchasePrice);
        Assert.Equal(access == "sales" ? 99999m : 12345m, stored.MonthlyFee);
        Assert.Equal(access == "sales" ? "forged" : "deposit", stored.DepositText);
    }

    private static async Task<LocalDbContext> Database()
    {
        var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); return db;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateAssetReadCannotRestoreEditorAfterAccessChangeOrClose(bool close)
    {
        var gate = new QueryGate();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").AddInterceptors(gate).Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); var asset = await Seed(db, false, 23456);
        var session = Session("both"); var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var vm = new RentalAssetViewModel(new RentalStateService(db, local), local, new RentalDocumentService(), null!, session);
        try
        {
            gate.Arm(); var load = vm.LoadAndSelectAssetAsync(asset.Id);
            try
            {
                await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (close) vm.CancelPendingBackgroundWork(); else session.SetOfflineSession(Session("none").User!);
            }
            finally { gate.Release(); }
            await load; Assert.Empty(vm.Rows); Assert.Null(vm.SelectedRow); Assert.Empty(vm.AssignmentHistories);
        }
        finally { await vm.CancelPendingBackgroundWorkAsync(); }
    }

    private sealed class QueryGate : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _released.TrySetResult();
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("RentalAssets") && Interlocked.Exchange(ref _armed, 0) == 1)
            { Blocked.TrySetResult(); await _released.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    [Fact]
    public async Task AccessChangeClearsDraftAndOldSnapshotCannotRestoreOrSaveIt()
    {
        await using var db = await Database(); var asset = await Seed(db, false, 23456);
        var session = Session("both"); var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var vm = new RentalAssetViewModel(new RentalStateService(db, local), local, new RentalDocumentService(), null!, session);
        try
        {
            await vm.LoadAndSelectAssetAsync(asset.Id);
            var snapshot = typeof(RentalAssetViewModel).GetMethod("CaptureEditSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
            session.SetOfflineSession(Session("none").User!);
            Assert.Empty(vm.Rows); Assert.Null(vm.SelectedRow); Assert.Null(vm.EditMonthlyFee); Assert.Null(vm.EditPurchasePrice);
            Assert.Null(vm.EditDepositText); Assert.Empty(vm.AssignmentHistories); Assert.False(vm.HasPendingChanges);
            typeof(RentalAssetViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [snapshot, false]);
            Assert.Null(vm.EditMonthlyFee);
            object?[] args = [snapshot, false];
            Assert.False((bool)typeof(RentalAssetViewModel).GetMethod("CanSaveSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!);
            await vm.LoadAndSelectAssetAsync(asset.Id); Assert.Null(vm.EditMonthlyFee);
            session.SetOfflineSession(Session("both").User!); Assert.Empty(vm.Rows);
            await vm.LoadAndSelectAssetAsync(asset.Id); Assert.Equal(23456m, vm.EditMonthlyFee);
        }
        finally { await vm.CancelPendingBackgroundWorkAsync(); }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    [InlineData("sales")]
    public async Task LegacySharedOwnerNoteSaveKeepsOwnerAndCanComplete(string access)
    {
        await using var db = await Database(); var asset = await Seed(db, false, 23456);
        var existing = await db.RentalAssets.SingleAsync(); existing.OfficeCode = OfficeCodeCatalog.Shared;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var session = Session(access); var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var vm = new RentalAssetViewModel(new RentalStateService(db, local), local, new RentalDocumentService(), null!, session);
        try
        {
            await vm.LoadAndSelectAssetAsync(asset.Id);
            typeof(RentalAssetViewModel).GetField("_suppressEditAutoSave", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
            vm.EditNotes = "legacy note"; vm.SuppressExplicitSaveConflictDialog = true;
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Contains("저장했습니다", vm.StatusMessage);
            db.ChangeTracker.Clear(); existing = await db.RentalAssets.SingleAsync();
            Assert.Equal("legacy note", existing.Notes); Assert.Equal(OfficeCodeCatalog.Shared, existing.OfficeCode);
            Assert.Equal(23456m, existing.MonthlyFee);
        }
        finally { await vm.CancelPendingBackgroundWorkAsync(); }
    }

    [Fact]
    public async Task ExistingActualZeroIsNotReplacedByItemPrices()
    {
        await using var db = await Database(); var asset = await Seed(db, false, 0);
        var session = Session("both"); var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var vm = new RentalAssetViewModel(new RentalStateService(db, local), local, new RentalDocumentService(), null!, session);
        try
        {
            await vm.LoadAndSelectAssetAsync(asset.Id);
            typeof(RentalAssetViewModel).GetField("_suppressEditAutoSave", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
            await vm.ApplySelectedItemAsync(new LocalItem { NameOriginal = "different item", PurchasePrice = 12345m, SalePrice = 34567m });
            Assert.Equal(0m, vm.EditPurchasePrice); Assert.Equal(0m, vm.EditSalePrice);
        }
        finally { await vm.CancelPendingBackgroundWorkAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquipmentDocumentQueryPreservesDirectionalPrivacy(bool purchaseHidden)
    {
        await using var db = await Database(); var asset = await Seed(db, false, 23456);
        var tracked = await db.RentalAssets.SingleAsync(); tracked.PurchaseAmountsHidden = purchaseHidden; tracked.SalesAmountsHidden = !purchaseHidden;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var row = Assert.Single(await new RentalStateService(db).GetAssetsForEquipmentDetailAsync(asset, Session("both")));
        Assert.Equal(purchaseHidden ? 0m : 23456m, row.PurchasePrice);
        Assert.Equal(purchaseHidden ? 23456m : 0m, row.MonthlyFee);
        Assert.Equal(!purchaseHidden, row.SalesAmountsHidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContractPreviewKeepsPrivateMoneyUnknownAcrossRefresh(bool hidden)
    {
        Exception? error = null;
        var thread = new Thread(() => {
            try
            {
                var docs = new RentalDocumentService();
                var asset = new LocalRentalAsset { CustomerName = "Fixture", ItemName = "Printer", MonthlyFee = 23456m, DepositText = "SecretDeposit", SalesAmountsHidden = hidden };
                var model = docs.CreateContractDocumentModel(asset, null, new LocalCompanyProfile());
                Assert.Equal(hidden ? null : (decimal?)23456m, model.MonthlyFee);
                Assert.Equal(hidden ? "비공개" : "SecretDeposit", model.DepositText);
                var vm = new RentalContractEditorViewModel(model, docs);
                Assert.Equal(hidden, vm.AreAmountsReadOnly);
                if (hidden) { vm.MonthlyFee = 99999; vm.DepositText = "ForgedDeposit"; }
                vm.RefreshPreviewCommand.Execute(null);
                Assert.Equal(hidden ? null : (decimal?)23456m, vm.BuildModel().MonthlyFee);
                var xaml = System.Windows.Markup.XamlWriter.Save(vm.PreviewDocument);
                if (hidden) { Assert.Contains("비공개", xaml); Assert.DoesNotContain("SecretDeposit", xaml); Assert.DoesNotContain("23,456", xaml); Assert.DoesNotContain("99,999", xaml); }
                else Assert.Contains("23,456", xaml);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    [InlineData("sales")]
    public async Task NewAssetWithoutServerPricingBasisAndChangedContractCannotBeSilentlySaved(string access)
    {
        await using var db = await Database(); var asset = await Seed(db, false, 12345); var service = new RentalStateService(db);
        var candidate = await db.RentalAssets.AsNoTracking().SingleAsync(); candidate.Id = Guid.NewGuid();
        var result = await service.SaveAssetAsync(candidate, Session(access)); Assert.False(result.Success);
        candidate = await db.RentalAssets.AsNoTracking().SingleAsync(); candidate.ItemId = Guid.NewGuid();
        result = await service.SaveAssetAsync(candidate, Session(access)); Assert.False(result.Success);
        Assert.Equal(1, await db.RentalAssets.CountAsync());
        Assert.All(db.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Unchanged, e.State));
    }

    private static async Task<LocalRentalAsset> Seed(LocalDbContext db, bool hidden, int amount)
    {
        var customer = new LocalCustomer { NameOriginal = "Privacy Customer", NameMatchKey = "PRIVACYCUSTOMER", TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var asset = new LocalRentalAsset { AssetKey = "PRIVACY-ASSET", ManagementId = "PRIVACY-ASSET", ManagementNumber = "PRIVACY-ASSET",
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode, ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            ManagementCompanyCode = customer.OfficeCode, CustomerId = customer.Id, CustomerName = customer.NameOriginal, CurrentCustomerName = customer.NameOriginal,
            AssetStatus = "임대진행중", BillingEligibilityStatus = "청구대상", Notes = "before", PurchasePrice = amount, SalePrice = amount, MonthlyFee = amount,
            DepositText = "deposit", BlackOverageUnitPrice = amount, PurchaseAmountsHidden = hidden, SalesAmountsHidden = hidden,
            InstallLocation = "desk", InstallSiteName = "desk", ContractStartDate = new(2026, 1, 1), RentalEndDate = new(2027, 1, 1), IsDirty = false };
        var profile = new LocalRentalBillingProfile { ProfileKey = "PRIVACY-PROFILE", TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.OfficeCode, ManagementCompanyCode = customer.OfficeCode, CustomerId = customer.Id, CustomerName = customer.NameOriginal,
            MonthlyAmount = 98765m, IsDirty = false, BillingTemplateJson = JsonSerializer.Serialize(new[] { new RentalBillingTemplateItemModel
            { DisplayItemName = "Rental", Quantity = 1, UnitPrice = 98765, Amount = 98765, RepresentativeAssetId = asset.Id, IncludedAssetIds = [asset.Id] } }) };
        asset.BillingProfileId = profile.Id;
        var history = new LocalRentalAssetAssignmentHistory { AssetId = asset.Id, CustomerId = customer.Id, BillingProfileId = profile.Id,
            CustomerName = customer.NameOriginal, InstallLocation = "desk", TenantCode = asset.TenantCode, ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            MonthlyFee = 13579m, IsCurrent = true, IsDirty = false, LinkedAtUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.AddRange(customer, asset, profile, history); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); return asset;
    }

    private static SessionState Session(string access)
    {
        var permissions = new List<string> { AppPermissionNames.RentalViewAll, AppPermissionNames.RentalAssetEdit, AppPermissionNames.RentalProfileEdit };
        if (access is "purchase" or "both") permissions.Add(AppPermissionNames.AmountViewPurchase);
        if (access is "sales" or "both") permissions.Add(AppPermissionNames.AmountViewSales);
        var user = new UserSessionDto { Username = "asset-privacy", Role = access == "admin" ? DomainConstants.RoleAdmin : DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = access is "admin" or "god" ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions };
        var session = new SessionState();
        if (access == "god") session.SetSession("e30.eyJnb2QiOnRydWV9.test", user); else session.SetOfflineSession(user);
        return session;
    }
}
