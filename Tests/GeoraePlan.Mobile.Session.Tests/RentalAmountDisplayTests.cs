using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Mobile.Session.Tests;

public sealed class RentalAmountDisplayTests
{
    public RentalAmountDisplayTests()
    {
        Preferences.Default.Reset(); SecureStorage.Default.Reset();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedRead_RedactsCachedAndPendingMoneyWithoutChangingSource(bool pending)
    {
        var session = new SessionStore(); await session.SaveAsync(Login(false));
        var state = Fixture();
        if (pending)
        {
            state.PendingPush.RentalBillingProfiles = state.SyncedRentalBillingProfiles;
            state.SyncedRentalBillingProfiles = [];
        }
        state.Normalize();
        var before = JsonSerializer.Serialize(state);
        var vm = new RentalsViewModel(new(state), new(state), session);
        await vm.RefreshAsync();
        var profile = Assert.Single(vm.BillingProfiles);
        Assert.Null(profile.MonthlyAmount);
        Assert.Null(Assert.Single(vm.RentalAssets).MonthlyFee);
        Assert.Contains("비공개", Assert.Single(vm.AssignmentHistories).Meta);
        Assert.Equal(2, vm.BillingLogs.Count);
        Assert.All(vm.BillingLogs, row => Assert.Contains("비공개", row.Subtitle));
        Assert.Equal(before, JsonSerializer.Serialize(state));
    }

    [Fact]
    public async Task KnownZeroRun_DoesNotFallBackToMonthlyPrice()
    {
        var session = new SessionStore(); await session.SaveAsync(Login(true));
        var state = Fixture();
        var run = Assert.Single(MobileRentalRunSnapshot.Parse(state.SyncedRentalBillingProfiles[0].BillingRunsJson));
        run.BilledAmount = 0; run.SettledAmount = 0;
        state.SyncedRentalBillingProfiles[0].BillingRunsJson = JsonSerializer.Serialize(new[] { run });
        var vm = new RentalsViewModel(new(state), new(state), session);
        await vm.RefreshAsync();
        var row = Assert.Single(vm.BillingLogs, x => x.UniqueKey.StartsWith("run:"));
        Assert.Contains("청구 0원", row.Subtitle);
        Assert.DoesNotContain("123,456", row.Subtitle);
        Assert.Contains("미수 0원", MobileRentalAmountAccess.ProfileNote(state.SyncedRentalBillingProfiles[0]));
    }

    [Fact]
    public async Task SameGenerationRevocation_ClearsVisibleMoneyAndReloadsUnderCurrentAccess()
    {
        var session = new SessionStore(); await session.SaveAsync(Login(true));
        var state = Fixture(); var vm = new RentalsViewModel(new(state), new(state), session);
        await vm.RefreshAsync();
        Assert.Equal(123456m, Assert.Single(vm.BillingProfiles).MonthlyAmount);
        var owner = session.CaptureOwner();
        session.SessionChanged += (_, _) => vm.RefreshAmountAccess();
        Assert.True(await session.ReplaceIfCurrentAsync(owner, Login(false), preserveGeneration: true));
        Assert.True(session.IsOwnerCurrent(owner));
        Assert.Empty(vm.BillingProfiles); Assert.Empty(vm.BillingLogs); Assert.Empty(vm.RentalAssets);
        Assert.True(vm.NeedsRefresh(TimeSpan.FromDays(1)));
        await vm.RefreshAsync();
        Assert.Null(Assert.Single(vm.BillingProfiles).MonthlyAmount);
        Assert.Equal(123456m, state.SyncedRentalBillingProfiles[0].MonthlyAmount);
        await session.ClearAsync(); Assert.Empty(vm.BillingProfiles); Assert.Empty(vm.AssignmentHistories);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionOrOwnerChangeDuringRead_CannotPublishOldMoney(bool replaceOwner)
    {
        var session = new SessionStore(); await session.SaveAsync(Login(true));
        var state = Fixture(); var store = new JsonSyncStateStore(state);
        store.BeforeLoad = async () =>
        {
            if (replaceOwner) await session.SaveAsync(Login(false));
            else await session.ReplaceIfCurrentAsync(session.CaptureOwner(), Login(false), preserveGeneration: true);
        };
        var vm = new RentalsViewModel(store, new(state), session);
        await vm.RefreshAsync();
        if (replaceOwner) Assert.Empty(vm.BillingProfiles);
        else Assert.Null(Assert.Single(vm.BillingProfiles).MonthlyAmount);
    }

    public static IEnumerable<object[]> AccessCases()
    {
        foreach (var role in new[] { "User", "Admin" })
        foreach (var grants in Enumerable.Range(0, 4))
        foreach (var hidden in new[] { false, true })
        foreach (var zero in new[] { false, true })
            yield return [role, grants, hidden, zero];
    }

    [Fact]
    public async Task SameGenerationOfficeScopeReduction_ClearsOtherOfficeAndPreservesTenantBoundary()
    {
        var session = new SessionStore(); var login = Login(true);
        login.User.ScopeType = TenantScopeCatalog.ScopeTenantAll;
        await session.SaveAsync(login);
        var state = Fixture();
        state.SyncedRentalBillingProfiles.Add(new() { Id = Guid.NewGuid(), ResponsibleOfficeCode = "YEONSU", CustomerName = "연수" });
        state.SyncedRentalBillingProfiles.Add(new() { Id = Guid.NewGuid(), TenantCode = "ITWORLD", ResponsibleOfficeCode = "ITWORLD", CustomerName = "타업체" });
        var vm = new RentalsViewModel(new(state), new(state), session);
        await vm.RefreshAsync(); Assert.Equal(2, vm.BillingProfiles.Count);
        session.SessionChanged += (_, _) => vm.RefreshAmountAccess();
        var owner = session.CaptureOwner();
        Assert.True(await session.ReplaceIfCurrentAsync(owner, Login(true), preserveGeneration: true));
        Assert.Empty(vm.BillingProfiles);
        await vm.RefreshAsync(); Assert.Equal("격리 거래처", Assert.Single(vm.BillingProfiles).CustomerName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrantDoesNotInventMoneyFromRedactedCache_OrCopyPricesFromAnotherDirection(bool purchaseOnly)
    {
        var session = new SessionStore(); var login = Login(!purchaseOnly);
        if (purchaseOnly) login.User.Permissions = ["Amount.ViewPurchase"];
        await session.SaveAsync(login);
        var state = Fixture();
        state.SyncedRentalBillingProfiles[0].MonthlyAmount = null;
        state.SyncedRentalAssets[0].MonthlyFee = null;
        state.SyncedRentalAssetAssignmentHistories[0].MonthlyFee = null;
        var vm = new RentalsViewModel(new(state), new(state), session);
        await vm.RefreshAsync();
        Assert.Null(Assert.Single(vm.BillingProfiles).MonthlyAmount);
        var asset = Assert.Single(vm.RentalAssets);
        Assert.Null(asset.MonthlyFee);
        Assert.Equal(purchaseOnly ? 765432m : (decimal?)null, asset.PurchasePrice);
        Assert.Contains("비공개", Assert.Single(vm.AssignmentHistories).Meta);
        Assert.Contains("비공개", Assert.Single(vm.BillingLogs, x => x.UniqueKey.StartsWith("run:")).Subtitle);
    }

    [Theory]
    [MemberData(nameof(AccessCases))]
    public void Projection_SeparatesSalesPurchaseAndUnknownAndKeepsCache(string role, int grants, bool hidden, bool zero)
    {
        var access = MobileRentalAmountAccess.Capture(new SessionSnapshot { IsAuthenticated = true, Role = role,
            Permissions = [(grants & 1) != 0 ? "Amount.ViewSales" : "", (grants & 2) != 0 ? "Amount.ViewPurchase" : ""] });
        var state = Fixture(); var profile = state.SyncedRentalBillingProfiles[0]; var asset = state.SyncedRentalAssets[0];
        profile.MonthlyAmount = asset.MonthlyFee = zero ? 0 : 123456;
        if (hidden) { profile.OutstandingAmount = null; asset.DepositText = null; }
        var before = JsonSerializer.Serialize(state);
        var shownProfile = access.Display(profile); var shownAsset = access.Display(asset);
        var sales = role == "Admin" || (grants & 1) != 0;
        var purchase = role == "Admin" || (grants & 2) != 0;
        Assert.Equal(sales && !hidden ? profile.MonthlyAmount : null, shownProfile.MonthlyAmount);
        Assert.Equal(sales && !hidden ? asset.MonthlyFee : null, shownAsset.MonthlyFee);
        Assert.Equal(purchase ? asset.PurchasePrice : null, shownAsset.PurchasePrice);
        Assert.Equal(sales && !hidden ? (zero ? "0원" : "123,456원") : "비공개",
            MobileRentalAmountAccess.Money(shownProfile.MonthlyAmount, !shownProfile.AmountsHidden));
        Assert.Equal(before, JsonSerializer.Serialize(state));
        if (!sales || hidden)
        {
            Assert.Contains("비공개", MobileRentalAmountAccess.ProfileNote(shownProfile));
            Assert.DoesNotContain("123456", shownProfile.BillingRunsJson);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public async Task RedactedOrMissingRunMoney_KeepsHistoryUnknownInsteadOfZero(string value)
    {
        var session = new SessionStore(); await session.SaveAsync(Login(true));
        var state = Fixture();
        var profile = state.SyncedRentalBillingProfiles[0];
        profile.BillingRunsJson = "[{\"RunId\":\"" + Guid.NewGuid() + "\",\"ScheduledDate\":\"2026-09-25\"" +
            (value.Length == 0 ? "" : ",\"BilledAmount\":" + value + ",\"SettledAmount\":" + value) + "}]";
        var vm = new RentalsViewModel(new(state), new(state), session);
        await vm.RefreshAsync();
        var row = Assert.Single(vm.BillingLogs, x => x.UniqueKey.StartsWith("run:"));
        Assert.Contains("비공개", row.Subtitle);
        Assert.DoesNotContain("0원", row.Subtitle);
    }

    private static LoginResponse Login(bool sales) => new()
    {
        Token = "synthetic-session-only", ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        User = new() { Username = "rental-reader", Role = "User", TenantCode = "USENET_GROUP",
            OfficeCode = "USENET", ScopeType = "OfficeOnly",
            Permissions = sales ? ["Amount.ViewSales"] : [] }
    };

    private static MobileSyncState Fixture()
    {
        var profile = new RentalBillingProfileDto { Id = Guid.NewGuid(), CustomerName = "격리 거래처",
            MonthlyAmount = 123456, DepositAmount = 345678, OutstandingAmount = 456789,
            BillingRunsJson = JsonSerializer.Serialize(new[] { new { RunId = Guid.NewGuid(),
                ScheduledDate = new DateOnly(2026,9,25), BilledAmount = 123456, SettledAmount = 0 } }) };
        var asset = new RentalAssetDto { Id = Guid.NewGuid(), BillingProfileId = profile.Id,
            MonthlyFee = 123456, SalePrice = 234567, PurchasePrice = 765432, DepositText = "345678" };
        return new MobileSyncState
        {
            SyncedRentalBillingProfiles = [profile], SyncedRentalAssets = [asset],
            SyncedRentalAssetAssignmentHistories = [new() { Id = Guid.NewGuid(), AssetId = asset.Id,
                BillingProfileId = profile.Id, MonthlyFee = 123456 }],
            SyncedRentalBillingLogs = [new() { Id = Guid.NewGuid(), BillingProfileId = profile.Id,
                BilledAmount = 123456, ScheduledDate = new(2026,9,25) }]
        };
    }
}
