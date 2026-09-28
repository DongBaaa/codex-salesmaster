using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalHiddenProfileSaveTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NonMoneySavePreservesFinancialHistoryAndQueuesUnknownAmounts(bool hiddenCache, bool viewModel)
    {
        await using var f = await Fixture.Create(hiddenCache);
        if (viewModel)
        {
            var vm = new RentalBillingViewModel(f.Rental, f.Local, f.Session);
            try
            {
                await vm.LoadAndSelectProfileAsync(f.Profile.Id);
                vm.LinkAssetsLater = true;
                Assert.Null(vm.EditMonthlyAmount);
                var item = Assert.Single(vm.TemplateItems);
                item.Quantity = 3m; item.Note = "edited line";
                vm.EditNotes = "edited profile";
                Assert.True(vm.SaveCommand.CanExecute(null));
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Contains("저장", vm.StatusMessage);
            }
            finally { await vm.CancelAndDrainPendingBackgroundWorkAsync(); }
        }
        else
        {
            var candidate = await f.Db.RentalBillingProfiles.AsNoTracking().SingleAsync();
            candidate.MonthlyAmount = 999999m; candidate.DepositAmount = 999999m;
            candidate.SettledAmount = 999999m; candidate.OutstandingAmount = 999999m;
            candidate.Notes = "edited profile";
            var items = f.Rental.GetBillingTemplateItems(candidate, []);
            items[0].Quantity = 3m; items[0].Note = "edited line";
            candidate.BillingTemplateJson = f.Rental.SerializeBillingTemplateItems(items);
            var result = await f.Rental.SaveBillingProfileAsync(candidate, f.Session);
            Assert.True(result.Success, result.Message);
        }
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.RentalBillingProfiles.SingleAsync();
        Assert.True(saved.IsDirty); Assert.True(saved.AmountsHidden);
        Assert.Equal(12345m, saved.MonthlyAmount); Assert.Equal(50m, saved.DepositAmount);
        Assert.Equal(345m, saved.SettledAmount); Assert.Equal(12000m, saved.OutstandingAmount);
        Assert.Equal(f.OriginalRuns, saved.BillingRunsJson);
        Assert.Equal("edited profile", saved.Notes);
        Assert.Equal(f.Profile.CustomerId, saved.CustomerId);
        Assert.Equal(f.Profile.TenantCode, saved.TenantCode);
        var wire = LocalMappings.ToDto(saved);
        Assert.Null(wire.MonthlyAmount); Assert.Null(wire.DepositAmount);
        Assert.Null(wire.SettledAmount); Assert.Null(wire.OutstandingAmount);
        var row = Assert.Single(JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(wire.BillingTemplateJson)!);
        Assert.Equal(3m, row.Quantity); Assert.Equal("edited line", row.Note);
        Assert.Null(row.UnitPrice); Assert.Null(row.Amount);
        Assert.Empty(await f.Db.Invoices.ToListAsync());
        Assert.Empty(await f.Db.RentalAssets.ToListAsync());
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("revision")]
    [InlineData("scope")]
    public async Task NonMoneySaveRetainsAuthorizationAndConcurrencyGuards(string failure)
    {
        await using var f = await Fixture.Create(true);
        var candidate = await f.Db.RentalBillingProfiles.AsNoTracking().SingleAsync();
        candidate.Notes = "must not save";
        if (failure == "revision") candidate.Revision = 10;
        if (failure == "scope") candidate.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        if (failure == "permission") f.Session.SetOfflineSession(new UserSessionDto {
            Username = "readonly", Role = DomainConstants.RoleUser, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.RentalViewAll] });
        var result = await f.Rental.SaveBillingProfileAsync(candidate, f.Session);
        Assert.False(result.Success);
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.RentalBillingProfiles.SingleAsync();
        Assert.NotEqual("must not save", saved.Notes); Assert.False(saved.IsDirty);
        Assert.Equal(f.OriginalRuns, saved.BillingRunsJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenProfileSaveDoesNotDirtyLinkedAssetOrCreateAssignmentHistory(bool hiddenCache)
    {
        await using var f = await Fixture.Create(hiddenCache);
        f.Session.SetOfflineSession(new UserSessionDto { Username = "asset-editor", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.RentalViewAll, AppPermissionNames.RentalProfileEdit, AppPermissionNames.RentalAssetEdit] });
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), AssetKey = "preserved-asset", ManagementNumber = "PRESERVE-001",
            BillingProfileId = f.Profile.Id, CustomerId = f.Profile.CustomerId,
            TenantCode = f.Profile.TenantCode, OfficeCode = f.Profile.OfficeCode, ResponsibleOfficeCode = f.Profile.ResponsibleOfficeCode,
            ManagementCompanyCode = f.Profile.ManagementCompanyCode, CustomerName = f.Profile.CustomerName,
            MonthlyFee = 500m, IsDirty = false, Revision = 7 };
        var stored = await f.Db.RentalBillingProfiles.SingleAsync();
        var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(stored.BillingTemplateJson)!;
        items[0].IncludedAssetIds = [asset.Id]; stored.BillingTemplateJson = f.Rental.SerializeBillingTemplateItems(items);
        f.Db.Add(asset); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var candidate = await f.Db.RentalBillingProfiles.AsNoTracking().SingleAsync();
        items[0].Quantity = 3m; items[0].Note = "quantity only";
        candidate.BillingTemplateJson = f.Rental.SerializeBillingTemplateItems(items);
        var result = await f.Rental.SaveBillingProfileAsync(candidate, f.Session);
        Assert.True(result.Success, result.Message);
        f.Db.ChangeTracker.Clear();
        var savedAsset = await f.Db.RentalAssets.SingleAsync();
        Assert.Equal(500m, savedAsset.MonthlyFee); Assert.Equal(7, savedAsset.Revision); Assert.False(savedAsset.IsDirty);
        Assert.Equal(f.Profile.Id, savedAsset.BillingProfileId);
        Assert.Empty(await f.Db.RentalAssetAssignmentHistories.ToListAsync());
        Assert.Equal(f.OriginalRuns, (await f.Db.RentalBillingProfiles.SingleAsync()).BillingRunsJson);
    }

    [Fact]
    public async Task NewNonMoneyProfileQueuesUnknownPriceInsteadOfClaimingZero()
    {
        await using var f = await Fixture.Create(false);
        var candidate = await f.Db.RentalBillingProfiles.AsNoTracking().SingleAsync();
        f.Db.RentalBillingProfiles.Remove(await f.Db.RentalBillingProfiles.SingleAsync());
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        candidate.Id = Guid.NewGuid(); candidate.Revision = 0; candidate.BillingRunsJson = "[]";
        var result = await f.Rental.SaveBillingProfileAsync(candidate, f.Session);
        Assert.True(result.Success, result.Message);
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.RentalBillingProfiles.SingleAsync();
        Assert.True(saved.AmountsHidden); Assert.True(saved.IsDirty);
        Assert.Null(LocalMappings.ToDto(saved).MonthlyAmount);
        Assert.Equal("[]", saved.BillingRunsJson);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public LocalDbContext Db = null!;
        public SessionState Session = null!;
        public LocalStateService Local = null!;
        public RentalStateService Rental = null!;
        public LocalRentalBillingProfile Profile = null!;
        public string OriginalRuns = "";
        public static async Task<Fixture> Create(bool hidden)
        {
            var f = new Fixture();
            f.Db = new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
            await f.Db.Database.OpenConnectionAsync(); await f.Db.Database.EnsureCreatedAsync();
            f.Session = new();
            f.Session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "profile-edit", Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions = [AppPermissionNames.RentalViewAll, AppPermissionNames.RentalProfileEdit] });
            f.Session.SetBusinessDatabase("georaeplan_usenet");
            f.Local = new(f.Db, new OfficeAccessService(), new SyncRequestDispatcher(), f.Session);
            f.Rental = new(f.Db, f.Local);
            f.Profile = RentalReadAmountPrivacyTests.CreateProfile(hidden ? "profile" : "known");
            f.Profile.CustomerId = Guid.NewGuid(); f.Profile.ContractDate = new(2026, 9, 1);
            f.Profile.Revision = 11;
            f.Profile.ContractStartDate = new(2026, 9, 1);
            f.Profile.DepositAmount = 50m; f.Profile.SettledAmount = 345m; f.Profile.OutstandingAmount = 12000m;
            f.OriginalRuns = f.Profile.BillingRunsJson;
            f.Db.Add(new LocalCustomer { Id = f.Profile.CustomerId.Value, NameOriginal = f.Profile.CustomerName, NameMatchKey = "PRIVACY-CUSTOMER",
                TenantCode = f.Profile.TenantCode, OfficeCode = f.Profile.OfficeCode, ResponsibleOfficeCode = f.Profile.ResponsibleOfficeCode });
            f.Db.Add(f.Profile); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
            return f;
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
