using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace GeoraePlan.Mobile.Session.Tests;

public sealed class SessionAmountNotificationTests
{
    public SessionAmountNotificationTests()
    {
        Preferences.Default.Reset(); SecureStorage.Default.Reset();
    }

    [Fact]
    public async Task RefreshWithSameGeneration_NotifiesRevocationAndHidesPrices()
    {
        var store = new SessionStore();
        await store.SaveAsync(Login(true));
        var owner = store.CaptureOwner();
        var seen = new List<MobileItemAmountAccess>();
        store.SessionChanged += (_, _) =>
        {
            var snapshot = store.GetSnapshot();
            seen.Add(MobileItemAmountAccess.Capture(snapshot.IsAuthenticated, snapshot.Role,
                snapshot.Permissions, snapshot.CanEditItems));
            // Subscribers run after the commit gate is released and can observe committed state.
            var lease = store.AcquireOwnerCommitLeaseAsync(store.CaptureOwner());
            Assert.True(lease.IsCompletedSuccessfully);
            lease.Result.Dispose();
        };
        Assert.True(await store.ReplaceIfCurrentAsync(owner, Login(false), preserveGeneration: true));
        Assert.True(store.IsOwnerCurrent(owner));
        var access = Assert.Single(seen);
        Assert.False(access.Purchase); Assert.False(access.Sales); Assert.True(access.Edit);
        Assert.DoesNotContain("4321", access.Summary(new ItemDto { PurchasePrice = 4321 }));
        await store.ClearAsync();
        Assert.False(seen.Last().Edit);
        Assert.False(store.IsOwnerCurrent(owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutAndConditionalLogout_NotifyCommittedEmptyState(bool conditional)
    {
        var store = new SessionStore(); await store.SaveAsync(Login(true));
        var owner = store.CaptureOwner(); var calls = 0;
        store.SessionChanged += (_, _) => { calls++; Assert.False(store.GetSnapshot().IsAuthenticated); };
        if (conditional) Assert.True(await store.ClearIfCurrentAsync(owner)); else await store.ClearAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NewLogin_InvalidatesEditorOwnerAndPublishesNewAccess()
    {
        var store = new SessionStore(); await store.SaveAsync(Login(true));
        var owner = store.CaptureOwner(); var called = false;
        store.SessionChanged += (_, _) => { called = true; Assert.False(store.IsOwnerCurrent(owner)); };
        await store.SaveAsync(Login(false));
        Assert.True(called);
        Assert.Throws<StaleMobileSessionOwnerException>(() => store.ThrowIfOwnerChanged(owner));
    }

    [Fact]
    public async Task FailedSecureStorage_LoginNotifiesThatOldSessionIsGone()
    {
        var store = new SessionStore(); await store.SaveAsync(Login(true));
        SecureStorage.Default.FailWrites = true; bool? authenticated = null;
        store.SessionChanged += (_, _) => authenticated = store.GetSnapshot().IsAuthenticated;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Login(false)));
        Assert.Equal(false, authenticated);
    }

    [Fact]
    public async Task StaleRefresh_DoesNotRestoreAnotherOwnersGrants()
    {
        var store = new SessionStore(); await store.SaveAsync(Login(true));
        var owner = store.CaptureOwner(); await store.SaveAsync(Login(false));
        Assert.False(await store.ReplaceIfCurrentAsync(owner, Login(true), true));
        Assert.DoesNotContain("Amount.ViewSales", store.GetSnapshot().Permissions);
    }

    private static LoginResponse Login(bool prices) => new()
    {
        Token = "synthetic-session", ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        User = new UserSessionDto { Username = "session-test", Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            Permissions = prices ? ["Item.Edit", "Amount.ViewPurchase", "Amount.ViewSales"] : ["Item.Edit"] }
    };
}
