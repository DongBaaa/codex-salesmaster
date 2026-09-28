using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalEditorAmountPrivacyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewProfileInitialAmountsFollowCurrentPermission(bool canRead)
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto {
            Username = "new-profile-editor", Role = DomainConstants.RoleUser, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = canRead ? [AppPermissionNames.RentalProfileEdit, AppPermissionNames.AmountViewSales] : [AppPermissionNames.RentalProfileEdit] });
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var vm = new RentalBillingViewModel(new RentalStateService(db, local), local, session);
        try
        {
            await vm.LoadAsync(); await vm.NewProfileCommand.ExecuteAsync(null);
            decimal? expected = canRead ? 0m : null;
            Assert.Equal(expected, vm.EditMonthlyAmount); Assert.Equal(expected, vm.EditDepositAmount);
            Assert.Equal(expected, vm.EditSettledAmount); Assert.Equal(expected, vm.EditOutstandingAmount);
            var line = Assert.Single(vm.TemplateItems);
            Assert.Equal(expected, line.UnitPrice); Assert.Equal(expected, line.Amount);
            line.Quantity = 3;
            Assert.Equal(expected, line.EffectiveAmount);
        }
        finally { await vm.CancelAndDrainPendingBackgroundWorkAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QuantityEditAndNormalizationPreserveUnknownAndRealZero(bool hidden)
    {
        var item = new RentalBillingTemplateEditorItem { DisplayItemName = "품목", UnitPrice = hidden ? null : 0m, Amount = hidden ? null : 0m };
        item.Quantity = 3m; item.Note = "수량과 비고 수정"; item.NormalizeCalculatedAmount();
        Assert.Equal(hidden ? null : (decimal?)0m, item.EffectiveAmount);
        Assert.Equal(hidden, item.AmountsHidden);
        Assert.Equal(hidden ? null : (decimal?)0m, item.Amount);
        var known = new RentalBillingTemplateEditorItem { UnitPrice = 120m, Quantity = 2m };
        Assert.Equal(hidden ? null : (decimal?)240m, RentalBillingTemplateEditorItem.SumKnownAmounts([known, item]));
        Assert.Equal(hidden ? "비공개" : "0원", RentalBillingTemplateEditorItem.FormatAmount(item.EffectiveAmount));
        Assert.Equal("수량과 비고 수정", item.Note);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmountEditorAccessFollowsPermissionRatherThanTemporaryBlankInput(bool canRead)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username = "editor-access", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = canRead ? [AppPermissionNames.AmountViewSales] : [] });
        var billing = new RentalBillingViewModel(null!, null!, session);
        var onboarding = new RentalCustomerOnboardingViewModel(null!, null!, session);
        try
        {
            Assert.Equal(!canRead, billing.AreRentalAmountsReadOnly);
            Assert.Equal(!canRead, onboarding.AreRentalAmountsReadOnly);
            billing.EditMonthlyAmount = null;
            Assert.Equal(!canRead, billing.AreRentalAmountsReadOnly);
            var item = new RentalBillingTemplateEditorItem { UnitPrice = 100m };
            item.UnitPrice = null; item.UnitPrice = 120m;
            Assert.Equal(120m, item.EffectiveAmount);
        }
        finally { await billing.CancelAndDrainPendingBackgroundWorkAsync(); }
    }

    [Fact]
    public void PartialUnknownDoesNotRegainAmountWhenQuantityChanges()
    {
        var item = new RentalBillingTemplateEditorItem { UnitPrice = 50m, Amount = null };
        item.Quantity = 4m; item.NormalizeCalculatedAmount();
        Assert.Null(item.EffectiveAmount); Assert.Null(item.Amount);
        Assert.True(item.AmountsHidden);
        Assert.Null(RentalBillingTemplateEditorItem.OutstandingAmount(100m, null));
        Assert.Null(RentalBillingTemplateEditorItem.OutstandingAmount(null, 20m));
        Assert.Equal(0m, RentalBillingTemplateEditorItem.OutstandingAmount(100m, 100m));
    }

    [Fact]
    public void DraftJsonPersistsExplicitNullButLegacyMissingFieldsRemainZero()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var draft = new RentalBillingEditorDraftModel { MonthlyAmount = null, DepositAmount = null, SettledAmount = null, OutstandingAmount = null };
        var raw = JsonSerializer.Serialize(draft, options);
        using var d = JsonDocument.Parse(raw);
        foreach (var name in new[] { "MonthlyAmount", "DepositAmount", "SettledAmount", "OutstandingAmount" })
            Assert.Equal(JsonValueKind.Null, d.RootElement.GetProperty(name).ValueKind);
        var restored = JsonSerializer.Deserialize<RentalBillingEditorDraftModel>(raw)!;
        Assert.Null(restored.MonthlyAmount); Assert.Null(restored.DepositAmount); Assert.Null(restored.SettledAmount); Assert.Null(restored.OutstandingAmount);
        Assert.Equal(0m, JsonSerializer.Deserialize<RentalBillingEditorDraftModel>("{}")!.MonthlyAmount);
        var onboarding = JsonSerializer.Serialize(new RentalCustomerOnboardingDraftModel { MonthlyAmount = null }, options);
        Assert.Null(JsonSerializer.Deserialize<RentalCustomerOnboardingDraftModel>(onboarding)!.MonthlyAmount);
        Assert.Equal(0m, JsonSerializer.Deserialize<RentalCustomerOnboardingDraftModel>("{}")!.MonthlyAmount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DraftRestoreEditPersistAndReloadKeepMoneyState(bool onboarding, bool hidden)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState(); session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "editor-privacy", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
        session.SetBusinessDatabase("georaeplan_usenet");
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        var id = Guid.NewGuid();
        var item = new RentalBillingTemplateItemModel { ItemId = id, DisplayItemName = "임시 품목", BillingLineMode = "묶음", Quantity = 1m,
            UnitPrice = hidden ? null : 0m, Amount = hidden ? null : 0m, Note = "기존 비고" };
        if (onboarding)
        {
            await rental.SaveOnboardingDraftAsync(new RentalCustomerOnboardingDraftModel { CustomerName = "임시 고객", MonthlyAmount = hidden ? null : 0m,
                LinkAssetsLater = true, TemplateItems = [item] }, session);
            var vm = new RentalCustomerOnboardingViewModel(rental, local, session);
            Assert.True(await vm.RestoreAutoSaveDraftAsync());
            var editor = Assert.Single(vm.TemplateItems); editor.Quantity = 3m; editor.Note = "변경 비고"; editor.DisplayItemName = "변경 품목";
            Assert.Equal(hidden ? null : (decimal?)0m, vm.MonthlyAmount);
            Assert.Equal(hidden ? "비공개" : "0원", vm.ExpectedBillingAmountText);
            await vm.FlushAutoSaveAsync();
            var stored = await rental.GetOnboardingDraftAsync(session); Assert.NotNull(stored);
            Assert.Equal(hidden ? null : (decimal?)0m, stored.MonthlyAmount); AssertItem(Assert.Single(stored.TemplateItems), id, hidden);
            var fresh = new RentalCustomerOnboardingViewModel(rental, local, session);
            Assert.True(await fresh.RestoreAutoSaveDraftAsync()); await fresh.FlushAutoSaveAsync();
            Assert.Equal(hidden ? null : (decimal?)0m, fresh.MonthlyAmount);
            Assert.Equal("변경 비고", Assert.Single(fresh.TemplateItems).Note);
        }
        else
        {
            await rental.SaveBillingEditorDraftAsync(new RentalBillingEditorDraftModel { CustomerName = "임시 고객", MonthlyAmount = hidden ? null : 0m,
                DepositAmount = hidden ? null : 0m, SettledAmount = hidden ? null : 0m, OutstandingAmount = hidden ? null : 0m,
                LinkAssetsLater = true, TemplateItems = [item] }, session);
            var vm = new RentalBillingViewModel(rental, local, session);
            try
            {
                Assert.True(await vm.RestoreAutoSaveDraftAsync());
                var editor = Assert.Single(vm.TemplateItems); editor.Quantity = 3m; editor.Note = "변경 비고"; editor.DisplayItemName = "변경 품목";
                if (hidden)
                {
                    var assetId = Guid.NewGuid();
                    vm.IncludedAssets.Add(new RentalBillingAssetOption { AssetId = assetId, MonthlyFee = 99000m });
                    editor.IncludedAssetIds.Add(assetId);
                    editor.UnitPrice = null; editor.Amount = null;
                    typeof(RentalBillingViewModel).GetMethod("ApplyIncludedAssetMonthlyFeesToTemplateItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(vm, [editor, true]);
                    Assert.Null(editor.UnitPrice); Assert.Null(editor.Amount);
                }
                Assert.Equal(hidden ? null : (decimal?)0m, vm.EditMonthlyAmount); Assert.Equal(hidden ? null : (decimal?)0m, vm.EditOutstandingAmount);
                Assert.True(await vm.FlushAutoSaveAsync());
                var stored = await rental.GetBillingEditorDraftAsync(session); Assert.NotNull(stored);
                Assert.Equal(hidden ? null : (decimal?)0m, stored.MonthlyAmount); AssertItem(Assert.Single(stored.TemplateItems), id, hidden);
                await vm.CancelAndDrainPendingBackgroundWorkAsync();
                var fresh = new RentalBillingViewModel(rental, local, session);
                try
                {
                    Assert.True(await fresh.RestoreAutoSaveDraftAsync());
                    Assert.Equal(hidden ? null : (decimal?)0m, fresh.EditMonthlyAmount);
                    Assert.Equal("변경 비고", Assert.Single(fresh.TemplateItems).Note);
                }
                finally { await fresh.CancelAndDrainPendingBackgroundWorkAsync(); }
            }
            finally { await vm.CancelAndDrainPendingBackgroundWorkAsync(); }
        }
    }

    private static void AssertItem(RentalBillingTemplateItemModel item, Guid id, bool hidden)
    {
        Assert.Equal(id, item.ItemId); Assert.Equal(3m, item.Quantity); Assert.Equal("변경 비고", item.Note); Assert.Equal("변경 품목", item.DisplayItemName);
        Assert.Equal(hidden ? null : (decimal?)0m, item.UnitPrice); Assert.Equal(hidden ? null : (decimal?)0m, item.Amount);
    }
}
