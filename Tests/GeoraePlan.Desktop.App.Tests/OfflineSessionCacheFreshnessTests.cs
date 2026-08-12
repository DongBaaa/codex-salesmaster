using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class OfflineSessionCacheFreshnessTests
{
    private const string Username = "cached-user";
    private const string Password = "cached-password";
    private const string UserCachePrefix = "CachedSession.cached-user.";

    [Fact]
    public void AuthenticationRevocationCleanup_MissingParentIsSuccessfulNoOp()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-auth-revocation-cleanup-tests",
            Guid.NewGuid().ToString("N"),
            "missing",
            "marker.revoked");

        var deleted = LocalStateService.TryDeleteAuthenticationRevocationTombstone(
            markerPath,
            out var error);

        Assert.True(deleted);
        Assert.Null(error);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public void AuthenticationRevocationCleanup_DeletesExistingMarker()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-auth-revocation-cleanup-tests",
            Guid.NewGuid().ToString("N"));
        var markerPath = Path.Combine(root, "marker.revoked");
        Directory.CreateDirectory(root);
        File.WriteAllText(markerPath, "revoked");

        try
        {
            var deleted = LocalStateService.TryDeleteAuthenticationRevocationTombstone(
                markerPath,
                out var error);

            Assert.True(deleted);
            Assert.Null(error);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AuthenticationRevocationCleanup_DoesNotHideRealDeleteFailure()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-auth-revocation-cleanup-tests",
            Guid.NewGuid().ToString("N"));
        var directoryAtMarkerPath = Path.Combine(root, "marker.revoked");
        Directory.CreateDirectory(directoryAtMarkerPath);

        try
        {
            var deleted = LocalStateService.TryDeleteAuthenticationRevocationTombstone(
                directoryAtMarkerPath,
                out var error);

            Assert.False(deleted);
            Assert.NotNull(error);
            Assert.True(Directory.Exists(directoryAtMarkerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FreshOnlineValidatedCache_IsPersistedAndAccepted()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);

        await fixture.SaveValidCacheAsync();

        Assert.Equal(
            OfflineSessionCachePolicy.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
            await fixture.Local.GetSettingAsync(UserCachePrefix + "SchemaVersion"));
        Assert.Equal(
            nowUtc.ToString("O", CultureInfo.InvariantCulture),
            await fixture.Local.GetSettingAsync(UserCachePrefix + "CachedAtUtc"));
        Assert.Equal(
            nowUtc.ToString("O", CultureInfo.InvariantCulture),
            await fixture.Local.GetSettingAsync(UserCachePrefix + "LastOnlineValidationAtUtc"));
        Assert.Equal(
            nowUtc.ToString("O", CultureInfo.InvariantCulture),
            await fixture.Local.GetSettingAsync(UserCachePrefix + "LastAcceptedOfflineUtc"));

        var cached = await fixture.Local.GetCachedSessionAsync(Username);

        Assert.NotNull(cached);
        Assert.Equal(Username, cached.Username);
        Assert.Equal(OfficeCodeCatalog.Usenet, cached.OfficeCode);
        Assert.Contains("Customers.Read", cached.Permissions);
        Assert.True(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, "wrong-password"));
    }

    [Fact]
    public async Task IndependentSave_KeepsLegacyIndexAsAValidFallback()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        var userSettings = await fixture.Db.Settings
            .Where(setting => setting.Key.StartsWith(UserCachePrefix))
            .ToListAsync();
        fixture.Db.Settings.RemoveRange(userSettings);
        await fixture.Db.SaveChangesAsync();

        var cached = await fixture.Local.GetCachedSessionAsync(Username);

        Assert.NotNull(cached);
        Assert.Equal(Username, cached.Username);
        Assert.Equal(OfficeCodeCatalog.Usenet, cached.OfficeCode);
        Assert.True(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
    }

    [Fact]
    public async Task AcceptedOfflineUse_AdvancesWatermarkAndLaterClockRollbackIsRejected()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await fixture.Local.GetCachedSessionAsync(Username));
        var acceptedAtUtc = nowUtc.AddHours(1);
        Assert.Equal(
            acceptedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            await fixture.Local.GetSettingAsync(UserCachePrefix + "LastAcceptedOfflineUtc"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(-30));

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
    }

    [Fact]
    public async Task AnyClockRollback_IsRejectedAndCannotBeRepeatedToExtendGrace()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.NotNull(await fixture.Local.GetCachedSessionAsync(Username));
        var watermark = nowUtc.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(-4));

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.Equal(
            watermark,
            await fixture.Local.GetSettingAsync(UserCachePrefix + "LastAcceptedOfflineUtc"));

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));

        fixture.Clock.Advance(
            OfflineSessionCachePolicy.DefaultMaximumOfflineGrace
            + TimeSpan.FromSeconds(1));
        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
    }

    [Fact]
    public async Task OfflineProbe_DoesNotAdvanceWatermarkAndMinorAdjustmentBeforeClickSucceeds()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        var probe = await fixture.Local.ProbeCachedSessionAuthenticationAsync(
            Username,
            Password);
        Assert.NotNull(probe);
        Assert.Equal(
            nowUtc.ToString("O", CultureInfo.InvariantCulture),
            await fixture.Local.GetSettingAsync(UserCachePrefix + "LastAcceptedOfflineUtc"));

        fixture.Clock.Advance(TimeSpan.FromTicks(-1));
        var authentication = await fixture.Local.AuthenticateCachedSessionAsync(
            Username,
            Password);

        Assert.NotNull(authentication);
        Assert.Equal(Username, authentication.User.Username);
        Assert.Equal(OfficeCodeCatalog.Usenet, authentication.OfficeCode);
        Assert.Contains("Customers.Read", authentication.User.Permissions);
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(Username));

        fixture.Clock.Advance(TimeSpan.FromTicks(-1));
        Assert.Null(await fixture.Local.AuthenticateCachedSessionAsync(
            Username,
            Password));
    }

    [Fact]
    public async Task OfflineAuthentication_ExternalMarkerAfterReadBlocksWithoutChangingCache()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var originalWatermark =
            await fixture.Local.GetSettingAsync(UserCachePrefix + "LastAcceptedOfflineUtc");
        var originalSchema =
            await fixture.Local.GetSettingAsync(UserCachePrefix + "SchemaVersion");
        fixture.Local.OfflineAuthenticationAfterCacheReadHook = () =>
            LocalStateService.CreateExternalPrimaryAuthenticationRevocationMarkerForTests(
                Username);

        var authentication = await fixture.Local.AuthenticateCachedSessionAsync(
            Username,
            Password);

        Assert.Null(authentication);
        Assert.True(LocalStateService.HasAuthenticationRevocationTombstone(Username));
        Assert.Equal(
            originalWatermark,
            await fixture.Local.GetSettingAsync(UserCachePrefix + "LastAcceptedOfflineUtc"));
        Assert.Equal(
            originalSchema,
            await fixture.Local.GetSettingAsync(UserCachePrefix + "SchemaVersion"));

        fixture.Local.OfflineAuthenticationAfterCacheReadHook = null;
        await fixture.SaveValidCacheAsync();
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(Username));
    }

    [Fact]
    public async Task OfflineAuthentication_PathReplacementAfterNonceReadIsRetainedAndBlocksResult()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var replacementHookCalled = false;
        string? displacedMarkerPath = null;
        fixture.Local.AuthenticationOwnedMarkerBeforeDispositionHook = _ =>
        {
            replacementHookCalled = true;
            Task.Run(() =>
                    displacedMarkerPath =
                        LocalStateService.ReplacePrimaryAuthenticationRevocationMarkerForTests(
                            Username))
                .GetAwaiter()
                .GetResult();
        };

        var authentication = await fixture.Local.AuthenticateCachedSessionAsync(
            Username,
            Password);

        Assert.Null(authentication);
        Assert.True(replacementHookCalled);
        Assert.NotNull(displacedMarkerPath);
        Assert.False(File.Exists(displacedMarkerPath));
        Assert.True(LocalStateService.HasAuthenticationRevocationTombstone(Username));
        fixture.Local.AuthenticationOwnedMarkerBeforeDispositionHook = null;
        Assert.Null(await fixture.Local.AuthenticateCachedSessionAsync(
            Username,
            Password));

        await fixture.SaveValidCacheAsync();
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(Username));
    }

    [Fact]
    public async Task OfflineProbe_DoesNotAuthorizeAfterOnlinePasswordProofChanges()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        Assert.NotNull(await fixture.Local.ProbeCachedSessionAuthenticationAsync(
            Username,
            Password));

        const string newPassword = "new-cached-password";
        await fixture.Local.SaveSessionCacheAsync(
            Username,
            "User",
            ["Customers.Read", "Invoices.Read"],
            TenantScopeCatalog.UsenetGroup,
            TenantScopeCatalog.ScopeOfficeOnly,
            OfficeCodeCatalog.Usenet,
            newPassword);

        Assert.Null(await fixture.Local.AuthenticateCachedSessionAsync(
            Username,
            Password));
        Assert.NotNull(await fixture.Local.AuthenticateCachedSessionAsync(
            Username,
            newPassword));
    }

    [Theory]
    [InlineData("LastAcceptedOfflineUtc", "")]
    [InlineData("SchemaVersion", "3")]
    public async Task MissingWatermarkOrOlderSchema_IsRejected(
        string suffix,
        string value)
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        await fixture.Local.SetSettingAsync(UserCachePrefix + suffix, value);

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
    }

    [Fact]
    public async Task CacheOlderThanMaximumOfflineGrace_IsRejected()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        fixture.Clock.Advance(OfflineSessionCachePolicy.DefaultMaximumOfflineGrace + TimeSpan.FromSeconds(1));

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
        Assert.Null(await fixture.Local.GetCachedOfficeCodeAsync(Username));
    }

    [Fact]
    public async Task ConfiguredShorterGrace_IsEnforced()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc, TimeSpan.FromHours(2));
        await fixture.SaveValidCacheAsync();

        fixture.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
    }

    [Fact]
    public async Task MissingTimestampOrLegacyRecord_IsRejectedUntilAnotherOnlineValidation()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();
        var legacyPasswordProof = await fixture.Local.GetSettingAsync(UserCachePrefix + "PasswordProof");
        Assert.False(string.IsNullOrWhiteSpace(legacyPasswordProof));

        await fixture.Local.SetSettingAsync(UserCachePrefix + "LastOnlineValidationAtUtc", string.Empty);

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));

        await using var legacyFixture = await TestFixture.CreateAsync(nowUtc);
        await legacyFixture.Local.SetSettingAsync("CachedSession_Username", Username);
        await legacyFixture.Local.SetSettingAsync("CachedSession_Role", "User");
        await legacyFixture.Local.SetSettingAsync("CachedSession_Permissions", "Customers.Read");
        await legacyFixture.Local.SetSettingAsync("CachedSession_TenantCode", TenantScopeCatalog.UsenetGroup);
        await legacyFixture.Local.SetSettingAsync("CachedSession_ScopeType", TenantScopeCatalog.ScopeOfficeOnly);
        await legacyFixture.Local.SetSettingAsync("CachedSession_OfficeCode", OfficeCodeCatalog.Usenet);
        await legacyFixture.Local.SetSettingAsync("CachedSession_PasswordProof", legacyPasswordProof!);

        Assert.Null(await legacyFixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await legacyFixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
    }

    [Fact]
    public async Task CorruptDpapiPayloads_FailClosed()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        await fixture.Local.SetSettingAsync(UserCachePrefix + "PasswordProof", "not-valid-base64");

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));

        await fixture.SaveValidCacheAsync();
        await fixture.Local.SetSettingAsync(UserCachePrefix + "MetadataProof", "not-valid-base64");

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
    }

    [Fact]
    public async Task ProtectedMetadataMismatch_IsRejected()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();

        await fixture.Local.SetSettingAsync(UserCachePrefix + "Role", "Admin");

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
    }

    [Fact]
    public async Task RejectedServerSession_RemovesOfflineCacheAndMatchingOfficeSyncCredential()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();
        await fixture.Local.SaveOfficeSyncCredentialAsync(
            CreateCachedUser(Username),
            Username,
            Password);

        Assert.NotNull(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.NotNull(await fixture.Local.GetStoredSyncCredentialAsync(OfficeCodeCatalog.Usenet));

        await fixture.Local.RevokeRejectedAuthenticationCacheAsync(
            Username,
            OfficeCodeCatalog.Usenet);

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        Assert.False(await fixture.Local.VerifyCachedSessionPasswordAsync(Username, Password));
        Assert.Null(await fixture.Local.GetStoredSyncCredentialAsync(OfficeCodeCatalog.Usenet));
        Assert.DoesNotContain(
            fixture.Db.Settings,
            setting => setting.Key.StartsWith(UserCachePrefix, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectedServerSession_DoesNotRemoveAnotherUsersOfficeSyncCredential()
    {
        const string otherUsername = "other-user";
        var nowUtc = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(nowUtc);
        await fixture.SaveValidCacheAsync();
        await fixture.Local.SaveOfficeSyncCredentialAsync(
            CreateCachedUser(otherUsername),
            otherUsername,
            "other-password");

        await fixture.Local.RevokeRejectedAuthenticationCacheAsync(
            Username,
            OfficeCodeCatalog.Usenet);

        Assert.Null(await fixture.Local.GetCachedSessionAsync(Username));
        var retainedCredential =
            await fixture.Local.GetStoredSyncCredentialAsync(OfficeCodeCatalog.Usenet);
        Assert.NotNull(retainedCredential);
        Assert.Equal(otherUsername, retainedCredential.Username);
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("", 24)]
    [InlineData("invalid", 24)]
    [InlineData("-1", 24)]
    [InlineData("12", 12)]
    [InlineData("48", 24)]
    [InlineData("1e308", 24)]
    [InlineData("0", 0)]
    public void MaximumOfflineGraceConfiguration_IsFailSafeAndNeverExceedsTwentyFourHours(
        string? configuredHours,
        double expectedHours)
    {
        Assert.Equal(
            TimeSpan.FromHours(expectedHours),
            OfflineSessionCachePolicy.ResolveMaximumOfflineGrace(configuredHours));
    }

    private static UserSessionDto CreateCachedUser(string username) => new()
    {
        UserId = Guid.NewGuid(),
        Username = username,
        Role = "User",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = ["Customers.Read", "Invoices.Read"]
    };

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string _tempRoot;

        private TestFixture(
            string tempRoot,
            LocalDbContext db,
            LocalStateService local,
            MutableTimeProvider clock)
        {
            _tempRoot = tempRoot;
            Db = db;
            Local = local;
            Clock = clock;
        }

        public LocalDbContext Db { get; }

        public LocalStateService Local { get; }

        public MutableTimeProvider Clock { get; }

        public static async Task<TestFixture> CreateAsync(
            DateTimeOffset nowUtc,
            TimeSpan? maximumOfflineGrace = null)
        {
            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-offline-cache-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var dbPath = Path.Combine(tempRoot, "offline-cache.db");
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var clock = new MutableTimeProvider(nowUtc);
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                new SessionState(),
                clock,
                maximumOfflineGrace ?? OfflineSessionCachePolicy.DefaultMaximumOfflineGrace);
            return new TestFixture(tempRoot, db, local, clock);
        }

        public Task SaveValidCacheAsync()
            => Local.SaveSessionCacheAsync(
                Username,
                "User",
                ["Customers.Read", "Invoices.Read"],
                TenantScopeCatalog.UsenetGroup,
                TenantScopeCatalog.ScopeOfficeOnly,
                OfficeCodeCatalog.Usenet,
                Password);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup; SQLite can briefly retain a file handle.
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
    }
}
