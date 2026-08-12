using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private const int AuthenticationRevocationDbTimeoutSeconds = 2;
    private const string AuthenticationRevocationDirectoryName = "authentication-revocations";
    private const string SecondaryAuthenticationRevocationDirectoryName = "authentication-revocations-backup";
    private const string EmergencyAuthenticationRevocationDirectoryName =
        "GeoraePlan.AuthenticationRevocations";

    private enum AuthenticationMarkerChannel
    {
        Primary,
        Secondary,
        Emergency
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AuthenticationCacheGates =
        new(StringComparer.Ordinal);

    private static int _authenticationFatalFailureLatched;

    private static readonly string[] CachedSessionSettingSuffixes =
    [
        CachedSessionUsernameSuffix,
        CachedSessionRoleSuffix,
        CachedSessionPermissionsSuffix,
        CachedSessionTenantCodeSuffix,
        CachedSessionScopeTypeSuffix,
        CachedSessionOfficeCodeSuffix,
        CachedSessionPasswordProofSuffix,
        CachedSessionSchemaVersionSuffix,
        CachedSessionCachedAtUtcSuffix,
        CachedSessionLastOnlineValidationAtUtcSuffix,
        CachedSessionLastAcceptedOfflineUtcSuffix,
        CachedSessionMetadataProofSuffix
    ];

    internal Func<string, Exception?>? AuthenticationTombstoneWriteFailureFactory { get; set; }

    internal Func<string, Exception?>? AuthenticationSchemaInvalidationFailureFactory { get; set; }

    internal Func<string, Exception?>? SecondaryAuthenticationTombstoneWriteFailureFactory { get; set; }

    internal Func<string, Exception?>? EmergencyAuthenticationTombstoneWriteFailureFactory { get; set; }

    internal Action? OfflineAuthenticationAfterCacheReadHook { get; set; }

    internal Func<Task>? AuthenticationWatermarkBeforeCommitHook { get; set; }

    internal Action<string>? AuthenticationOwnedMarkerBeforeDispositionHook { get; set; }

    internal sealed record CachedOfflineAuthentication(
        UserSessionDto User,
        string OfficeCode);

    internal sealed class AuthenticationCachePersistenceException : Exception
    {
        internal AuthenticationCachePersistenceException(
            string message,
            bool offlineFallbackBlocked,
            Exception innerException)
            : base(message, innerException)
        {
            OfflineFallbackBlocked = offlineFallbackBlocked;
        }

        internal bool OfflineFallbackBlocked { get; }
    }

    internal static bool IsAuthenticationFatalFailureLatched
        => Volatile.Read(ref _authenticationFatalFailureLatched) != 0;

    internal static void ResetAuthenticationFatalFailureForTests()
        => Volatile.Write(ref _authenticationFatalFailureLatched, 0);

    private enum AuthenticationMutationBarrierKind
    {
        OwnedPrimaryMarker,
        ExistingPrimaryMarker,
        DatabaseSchema,
        OwnedSecondaryMarker,
        ExistingSecondaryMarker,
        OwnedEmergencyMarker,
        ExistingEmergencyMarker
    }

    private readonly record struct AuthenticationMutationBarrier(
        AuthenticationMutationBarrierKind Kind,
        AuthenticationMarkerLease? Lease = null)
    {
        internal bool CacheRecordPreserved
            => Kind is not AuthenticationMutationBarrierKind.DatabaseSchema;

        internal bool OwnsMarker => Lease is not null;
    }

    private readonly record struct AuthenticationMarkerIdentity(
        uint VolumeSerialNumber,
        ulong FileId);

    private sealed record AuthenticationMarkerLease(
        string Path,
        string Nonce,
        AuthenticationMarkerIdentity? Identity);

    private readonly record struct AuthenticationMarkerCreation(
        AuthenticationMarkerLease? Lease,
        bool AlreadyExisted);

    private static SemaphoreSlim GetAuthenticationCacheGate(string normalizedUsername)
        => AuthenticationCacheGates.GetOrAdd(normalizedUsername, static _ => new SemaphoreSlim(1, 1));

    private LocalDbContext CreateIndependentAuthenticationDb()
    {
        var connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = _db.Database.GetDbConnection().ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("인증 캐시 저장을 위한 로컬 데이터베이스 연결 정보를 확인할 수 없습니다.");

        var connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString)
        {
            DefaultTimeout = AuthenticationRevocationDbTimeoutSeconds
        };
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionStringBuilder.ConnectionString)
            .Options;
        var db = new LocalDbContext(options);
        db.Database.SetCommandTimeout(AuthenticationRevocationDbTimeoutSeconds);
        return db;
    }

    internal async Task SaveSettingsIndependentAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default,
        string? authenticationUsername = null)
    {
        if (values.Count == 0)
            return;

        var normalizedUsername = NormalizeUsername(authenticationUsername);
        SemaphoreSlim? gate = null;
        if (!string.IsNullOrWhiteSpace(normalizedUsername))
        {
            gate = GetAuthenticationCacheGate(normalizedUsername);
            await gate.WaitAsync(ct);
        }

        try
        {
            var keys = values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var settingsDb = CreateIndependentAuthenticationDb();
            await using var transaction =
                await settingsDb.BeginRuntimeMutationTransactionAsync(ct);
            try
            {
                var settings = await settingsDb.Settings
                    .Where(setting => keys.Contains(setting.Key))
                    .ToDictionaryAsync(
                        setting => setting.Key,
                        StringComparer.OrdinalIgnoreCase,
                        ct);
                foreach (var pair in values)
                    UpsertSetting(settingsDb, settings, pair.Key, pair.Value);

                await settingsDb.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }

            DetachAuthenticationSettings(keys);
        }
        finally
        {
            gate?.Release();
        }
    }

    private async Task<AuthenticationMutationBarrier> EstablishAuthenticationMutationBarrierAsync(
        string normalizedUsername)
    {
        if (IsAuthenticationFatalFailureLatched)
        {
            throw new AuthenticationCachePersistenceException(
                "이 프로세스의 인증 영속 차단 채널이 이미 치명적으로 실패했습니다. 애플리케이션을 종료해야 합니다.",
                offlineFallbackBlocked: false,
                new InvalidOperationException("Authentication fail-closed latch is active."));
        }

        try
        {
            var primary = CreateAuthenticationRevocationTombstone(
                normalizedUsername,
                AuthenticationMarkerChannel.Primary);
            return primary.AlreadyExisted
                ? new AuthenticationMutationBarrier(
                    AuthenticationMutationBarrierKind.ExistingPrimaryMarker)
                : new AuthenticationMutationBarrier(
                    AuthenticationMutationBarrierKind.OwnedPrimaryMarker,
                    primary.Lease);
        }
        catch (Exception markerException)
        {
            try
            {
                await InvalidateCachedSessionSchemaIndependentAsync(normalizedUsername);
                return new AuthenticationMutationBarrier(AuthenticationMutationBarrierKind.DatabaseSchema);
            }
            catch (Exception databaseException)
            {
                try
                {
                    var secondary = CreateAuthenticationRevocationTombstone(
                        normalizedUsername,
                        AuthenticationMarkerChannel.Secondary);
                    return secondary.AlreadyExisted
                        ? new AuthenticationMutationBarrier(
                            AuthenticationMutationBarrierKind.ExistingSecondaryMarker)
                        : new AuthenticationMutationBarrier(
                            AuthenticationMutationBarrierKind.OwnedSecondaryMarker,
                            secondary.Lease);
                }
                catch (Exception secondaryMarkerException)
                {
                    try
                    {
                        var emergency = CreateAuthenticationRevocationTombstone(
                            normalizedUsername,
                            AuthenticationMarkerChannel.Emergency);
                        return emergency.AlreadyExisted
                            ? new AuthenticationMutationBarrier(
                                AuthenticationMutationBarrierKind.ExistingEmergencyMarker)
                            : new AuthenticationMutationBarrier(
                                AuthenticationMutationBarrierKind.OwnedEmergencyMarker,
                                emergency.Lease);
                    }
                    catch (Exception emergencyMarkerException)
                    {
                        Interlocked.Exchange(ref _authenticationFatalFailureLatched, 1);
                        throw new AuthenticationCachePersistenceException(
                            "오프라인 인증 기본·DB·보조·비상 차단 채널이 모두 실패했습니다. 인증을 계속할 수 없습니다.",
                            offlineFallbackBlocked: false,
                            new AggregateException(
                                markerException,
                                databaseException,
                                secondaryMarkerException,
                                emergencyMarkerException));
                    }
                }
            }
        }
    }

    private async Task InvalidateCachedSessionSchemaIndependentAsync(string normalizedUsername)
    {
        var injectedFailure = AuthenticationSchemaInvalidationFailureFactory?.Invoke(normalizedUsername);
        if (injectedFailure is not null)
            throw injectedFailure;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GetCachedSessionSettingKey(normalizedUsername, CachedSessionSchemaVersionSuffix)
        };
        await using var cacheDb = CreateIndependentAuthenticationDb();
        await using var transaction =
            await cacheDb.BeginRuntimeMutationTransactionAsync(CancellationToken.None);
        try
        {
            var legacyUsername = await cacheDb.Settings
                .AsNoTracking()
                .Where(setting => setting.Key == "CachedSession_Username")
                .Select(setting => setting.Value)
                .SingleOrDefaultAsync(CancellationToken.None);
            if (string.Equals(
                    NormalizeUsername(legacyUsername),
                    normalizedUsername,
                    StringComparison.Ordinal))
            {
                keys.Add("CachedSession_" + CachedSessionSchemaVersionSuffix);
            }

            var settings = await cacheDb.Settings
                .Where(setting => keys.Contains(setting.Key))
                .ToDictionaryAsync(
                    setting => setting.Key,
                    StringComparer.OrdinalIgnoreCase,
                    CancellationToken.None);
            foreach (var key in keys)
                UpsertSetting(cacheDb, settings, key, string.Empty);

            await cacheDb.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        DetachAuthenticationSettings(keys);
    }

    private async Task SaveSessionCacheIndependentAsync(
        string username,
        string role,
        IEnumerable<string> permissions,
        string? tenantCode,
        string? scopeType,
        string? officeCode,
        string? password,
        CancellationToken ct)
    {
        var displayUsername = (username ?? string.Empty).Trim();
        var normalizedUsername = NormalizeUsername(displayUsername);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return;

        await GetAuthenticationCacheGate(normalizedUsername).WaitAsync(ct);
        try
        {
            var normalizedTenantCode =
                TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode);
            var normalizedScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(
                scopeType,
                DomainConstants.IsAdminRole(role) ? "Admin" : "OfficeOnly");
            var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
            var normalizedRole = role ?? string.Empty;
            var permissionsText = string.Join(',', permissions ?? []);
            var passwordProof = !string.IsNullOrEmpty(password)
                ? ProtectOfflinePasswordProof(password)
                : string.Empty;
            var validatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var barrier = await EstablishAuthenticationMutationBarrierAsync(normalizedUsername);
            if (string.IsNullOrWhiteSpace(passwordProof))
                return;

            var values = CreateCachedSessionValues(
                displayUsername,
                normalizedUsername,
                normalizedRole,
                permissionsText,
                normalizedTenantCode,
                normalizedScopeType,
                normalizedOfficeCode,
                passwordProof,
                validatedAtUtc,
                validatedAtUtc,
                validatedAtUtc);
            var keysToWrite = values.Keys
                .Select(suffix => GetCachedSessionSettingKey(normalizedUsername, suffix))
                .Concat(values.Keys.Select(suffix => "CachedSession_" + suffix))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            try
            {
                await using var cacheDb = CreateIndependentAuthenticationDb();
                await using var transaction =
                    await cacheDb.BeginRuntimeMutationTransactionAsync(CancellationToken.None);
                try
                {
                    var settings = await cacheDb.Settings
                        .Where(setting => keysToWrite.Contains(setting.Key))
                        .ToDictionaryAsync(
                            setting => setting.Key,
                            StringComparer.OrdinalIgnoreCase,
                            CancellationToken.None);
                    foreach (var pair in values)
                    {
                        UpsertSetting(
                            cacheDb,
                            settings,
                            GetCachedSessionSettingKey(normalizedUsername, pair.Key),
                            pair.Value);
                        UpsertSetting(cacheDb, settings, "CachedSession_" + pair.Key, pair.Value);
                    }

                    await cacheDb.SaveChangesAsync(CancellationToken.None);
                    await transaction.CommitAsync(CancellationToken.None);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
            catch (Exception ex)
            {
                throw new AuthenticationCachePersistenceException(
                    "온라인 로그인 인증 캐시 저장에 실패했습니다.",
                    offlineFallbackBlocked: true,
                    ex);
            }

            DetachAuthenticationSettings(keysToWrite);
            ClearAllAuthenticationRevocationTombstones(normalizedUsername);
        }
        finally
        {
            GetAuthenticationCacheGate(normalizedUsername).Release();
        }
    }

    public async Task RefreshCachedSessionAfterOnlineValidationAsync(
        string? previousUsername,
        UserSessionDto refreshedUser,
        CancellationToken ct = default)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(refreshedUser);

        var normalizedUsername = NormalizeUsername(previousUsername);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return;
        if (!string.Equals(
                normalizedUsername,
                NormalizeUsername(refreshedUser.Username),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("세션 갱신 응답의 사용자가 기존 로그인 사용자와 일치하지 않습니다.");
        }

        await GetAuthenticationCacheGate(normalizedUsername).WaitAsync(CancellationToken.None);
        try
        {
            if (HasAuthenticationRevocationTombstone(normalizedUsername))
                return;

            var barrier = await EstablishAuthenticationMutationBarrierAsync(normalizedUsername);
            if (!barrier.OwnsMarker)
                return;
            var role = refreshedUser.Role ?? string.Empty;
            var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                refreshedUser.TenantCode,
                refreshedUser.OfficeCode);
            var scopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(
                refreshedUser.ScopeType,
                DomainConstants.IsAdminRole(role) ? "Admin" : "OfficeOnly");
            var officeCode = NormalizeOfficeCode(refreshedUser.OfficeCode, DomainConstants.OfficeUsenet);
            var permissionsText = string.Join(',', refreshedUser.Permissions ?? []);
            var validatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();

            try
            {
                await using var cacheDb = CreateIndependentAuthenticationDb();
                await using var transaction =
                    await cacheDb.BeginRuntimeMutationTransactionAsync(CancellationToken.None);
                try
                {
                    var latest = await ReadCachedSessionRecordAsync(
                        cacheDb,
                        previousUsername!,
                        normalizedUsername,
                        requireFresh: false,
                        validatedAtUtc);
                    if (latest is null)
                        return;

                    if (validatedAtUtc < latest.CachedAtUtc)
                        validatedAtUtc = latest.CachedAtUtc;
                    var lastAcceptedOfflineUtc = latest.LastAcceptedOfflineUtc > validatedAtUtc
                        ? latest.LastAcceptedOfflineUtc
                        : validatedAtUtc;
                    var values = CreateCachedSessionValues(
                        refreshedUser.Username.Trim(),
                        normalizedUsername,
                        role,
                        permissionsText,
                        tenantCode,
                        scopeType,
                        officeCode,
                        latest.PasswordProof,
                        latest.CachedAtUtc,
                        validatedAtUtc,
                        lastAcceptedOfflineUtc);
                    await WriteCachedSessionValuesAsync(
                        cacheDb,
                        normalizedUsername,
                        values,
                        updateLegacyOnlyWhenOwnedByUser: true);
                    await cacheDb.SaveChangesAsync(CancellationToken.None);
                    await transaction.CommitAsync(CancellationToken.None);
                    DetachAuthenticationSettings(BuildCachedSessionKeys(normalizedUsername, values.Keys, includeLegacy: true));
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
            catch (Exception ex)
            {
                throw new AuthenticationCachePersistenceException(
                    "갱신된 온라인 권한의 오프라인 인증 캐시 저장에 실패했습니다.",
                    offlineFallbackBlocked: true,
                    ex);
            }

            ClearOwnedAuthenticationRevocationTombstone(barrier.Lease);
        }
        finally
        {
            GetAuthenticationCacheGate(normalizedUsername).Release();
        }
    }

    private async Task<CachedSessionRecord?> ReadFreshCachedSessionIndependentAsync(
        string username,
        CancellationToken ct)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return null;

        await GetAuthenticationCacheGate(normalizedUsername).WaitAsync(ct);
        try
        {
            var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            return await ReadFreshCachedSessionUnderGateAsync(
                username,
                normalizedUsername,
                nowUtc,
                requiredPassword: null,
                invokeOfflineAuthenticationHook: false);
        }
        finally
        {
            GetAuthenticationCacheGate(normalizedUsername).Release();
        }
    }

    internal async Task<CachedOfflineAuthentication?> AuthenticateCachedSessionAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrEmpty(password))
            return null;

        await GetAuthenticationCacheGate(normalizedUsername).WaitAsync(ct);
        try
        {
            var capturedNowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var cached = await ReadFreshCachedSessionUnderGateAsync(
                username,
                normalizedUsername,
                capturedNowUtc,
                password,
                invokeOfflineAuthenticationHook: true);
            if (cached is null)
                return null;

            return CreateCachedOfflineAuthentication(cached);
        }
        finally
        {
            GetAuthenticationCacheGate(normalizedUsername).Release();
        }
    }

    internal async Task<CachedOfflineAuthentication?> ProbeCachedSessionAuthenticationAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrEmpty(password))
            return null;

        await GetAuthenticationCacheGate(normalizedUsername).WaitAsync(ct);
        try
        {
            if (HasAuthenticationRevocationTombstone(normalizedUsername))
                return null;

            var capturedNowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            await using var readDb = CreateIndependentAuthenticationDb();
            var cached = await ReadCachedSessionRecordAsync(
                readDb,
                username,
                normalizedUsername,
                requireFresh: true,
                capturedNowUtc);
            if (cached is null
                || !VerifyOfflinePasswordProof(password, cached.PasswordProof)
                || HasAuthenticationRevocationTombstone(normalizedUsername))
            {
                return null;
            }

            return CreateCachedOfflineAuthentication(cached);
        }
        finally
        {
            GetAuthenticationCacheGate(normalizedUsername).Release();
        }
    }

    private static CachedOfflineAuthentication CreateCachedOfflineAuthentication(
        CachedSessionRecord cached)
    {
        var user = new UserSessionDto
        {
            UserId = Guid.Empty,
            Username = cached.Username,
            Role = cached.Role,
            TenantCode = cached.TenantCode,
            OfficeCode = cached.OfficeCode,
            ScopeType = cached.ScopeType,
            Permissions = cached.PermissionsText
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .ToList()
        };
        return new CachedOfflineAuthentication(user, cached.OfficeCode);
    }

    private async Task<CachedSessionRecord?> ReadFreshCachedSessionUnderGateAsync(
        string username,
        string normalizedUsername,
        DateTimeOffset nowUtc,
        string? requiredPassword,
        bool invokeOfflineAuthenticationHook)
    {
        if (HasAuthenticationRevocationTombstone(normalizedUsername))
            return null;

        CachedSessionRecord? cached;
        await using (var readDb = CreateIndependentAuthenticationDb())
        {
            cached = await ReadCachedSessionRecordAsync(
                readDb,
                username,
                normalizedUsername,
                requireFresh: true,
                nowUtc);
        }
        if (cached is null)
            return null;

        if (invokeOfflineAuthenticationHook)
            OfflineAuthenticationAfterCacheReadHook?.Invoke();
        if (HasAuthenticationRevocationTombstone(normalizedUsername))
            return null;
        if (requiredPassword is not null
            && !VerifyOfflinePasswordProof(requiredPassword, cached.PasswordProof))
        {
            return null;
        }

        if (nowUtc <= cached.LastAcceptedOfflineUtc)
            return HasAuthenticationRevocationTombstone(normalizedUsername) ? null : cached;

        AuthenticationMutationBarrier barrier;
        try
        {
            barrier = await EstablishAuthenticationMutationBarrierAsync(normalizedUsername);
        }
        catch (AuthenticationCachePersistenceException)
        {
            return null;
        }

        if (!barrier.OwnsMarker)
            return null;

        try
        {
            await using var cacheDb = CreateIndependentAuthenticationDb();
            await using var transaction =
                await cacheDb.BeginRuntimeMutationTransactionAsync(CancellationToken.None);
            try
            {
                var latest = await ReadCachedSessionRecordAsync(
                    cacheDb,
                    username,
                    normalizedUsername,
                    requireFresh: true,
                    nowUtc);
                if (latest is null
                    || requiredPassword is not null
                    && !VerifyOfflinePasswordProof(requiredPassword, latest.PasswordProof))
                {
                    return null;
                }

                var lastAcceptedOfflineUtc = nowUtc > latest.LastAcceptedOfflineUtc
                    ? nowUtc
                    : latest.LastAcceptedOfflineUtc;
                var lastAcceptedText =
                    lastAcceptedOfflineUtc.ToString("O", CultureInfo.InvariantCulture);
                var metadataProof = ProtectOfflineSessionMetadata(CreateOfflineSessionMetadataEnvelope(
                    latest.Username,
                    normalizedUsername,
                    latest.Role,
                    latest.PermissionsText,
                    latest.TenantCode,
                    latest.ScopeType,
                    latest.OfficeCode,
                    latest.CachedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    latest.LastOnlineValidationAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    lastAcceptedText,
                    latest.PasswordProof));
                if (string.IsNullOrWhiteSpace(metadataProof))
                    return null;

                var updateLegacy = await LegacyCacheBelongsToUserAsync(
                    cacheDb,
                    normalizedUsername);
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [CachedSessionLastAcceptedOfflineUtcSuffix] = lastAcceptedText,
                    [CachedSessionMetadataProofSuffix] = metadataProof
                };
                await WriteCachedSessionValuesAsync(
                    cacheDb,
                    normalizedUsername,
                    values,
                    updateLegacyOnlyWhenOwnedByUser: updateLegacy);
                if (AuthenticationWatermarkBeforeCommitHook is not null)
                    await AuthenticationWatermarkBeforeCommitHook();
                await cacheDb.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);

                var keys = BuildCachedSessionKeys(
                    normalizedUsername,
                    values.Keys,
                    includeLegacy: updateLegacy);
                DetachAuthenticationSettings(keys);
                ClearOwnedAuthenticationRevocationTombstone(barrier.Lease);
                if (HasAuthenticationRevocationTombstone(normalizedUsername))
                    return null;
                return latest with { LastAcceptedOfflineUtc = lastAcceptedOfflineUtc };
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AUTH", "오프라인 인증 캐시 시간 워터마크 저장에 실패했습니다.", ex);
            return null;
        }
    }

    private async Task<bool> DoesCachedSessionPasswordProofMatchIndependentAsync(
        string username,
        string password,
        CancellationToken ct)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrEmpty(password))
            return false;

        await GetAuthenticationCacheGate(normalizedUsername).WaitAsync(ct);
        try
        {
            if (HasAuthenticationRevocationTombstone(normalizedUsername))
                return false;

            await using var cacheDb = CreateIndependentAuthenticationDb();
            var userProofKey =
                GetCachedSessionSettingKey(normalizedUsername, CachedSessionPasswordProofSuffix);
            var passwordProof = await cacheDb.Settings
                .AsNoTracking()
                .Where(setting => setting.Key == userProofKey)
                .Select(setting => setting.Value)
                .SingleOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(passwordProof)
                && await LegacyCacheBelongsToUserAsync(cacheDb, normalizedUsername))
            {
                passwordProof = await cacheDb.Settings
                    .AsNoTracking()
                    .Where(setting => setting.Key == "CachedSession_" + CachedSessionPasswordProofSuffix)
                    .Select(setting => setting.Value)
                    .SingleOrDefaultAsync(ct);
            }

            return VerifyOfflinePasswordProof(password, passwordProof);
        }
        finally
        {
            GetAuthenticationCacheGate(normalizedUsername).Release();
        }
    }

    public async Task RevokeRejectedAuthenticationCacheAsync(
        string? username,
        string? officeCode,
        CancellationToken ct = default)
    {
        _ = ct;
        _ = officeCode;

        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return;

        await GetAuthenticationCacheGate(normalizedUsername).WaitAsync(CancellationToken.None);
        try
        {
            await RevokeRejectedAuthenticationCacheCoreAsync(normalizedUsername);
        }
        finally
        {
            GetAuthenticationCacheGate(normalizedUsername).Release();
        }
    }

    private async Task RevokeRejectedAuthenticationCacheCoreAsync(string normalizedUsername)
    {
        var barrier = await EstablishAuthenticationMutationBarrierAsync(normalizedUsername);
        var keysToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suffix in CachedSessionSettingSuffixes)
            keysToRemove.Add(GetCachedSessionSettingKey(normalizedUsername, suffix));

        try
        {
            await using var revocationDb = CreateIndependentAuthenticationDb();
            await using var transaction =
                await revocationDb.BeginRuntimeMutationTransactionAsync(CancellationToken.None);
            try
            {
                var legacyUsername = await revocationDb.Settings
                    .AsNoTracking()
                    .Where(setting => setting.Key == "CachedSession_Username")
                    .Select(setting => setting.Value)
                    .SingleOrDefaultAsync(CancellationToken.None);
                if (string.Equals(
                        NormalizeUsername(legacyUsername),
                        normalizedUsername,
                        StringComparison.Ordinal))
                {
                    foreach (var suffix in CachedSessionSettingSuffixes)
                        keysToRemove.Add("CachedSession_" + suffix);
                }

                var syncUsernameSettings = await revocationDb.Settings
                    .AsNoTracking()
                    .Where(setting =>
                        setting.Key.StartsWith(SyncOfficeCredentialPrefix) &&
                        setting.Key.EndsWith(SyncOfficeCredentialUsernameSuffix))
                    .ToListAsync(CancellationToken.None);
                foreach (var syncUsernameSetting in syncUsernameSettings)
                {
                    if (!string.Equals(
                            NormalizeUsername(syncUsernameSetting.Value),
                            normalizedUsername,
                            StringComparison.Ordinal)
                        || !TryParseSyncCredentialSetting(
                            syncUsernameSetting.Key,
                            out var matchingOfficeCode,
                            out var suffix)
                        || !string.Equals(
                            suffix,
                            SyncOfficeCredentialUsernameSuffix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    keysToRemove.Add(syncUsernameSetting.Key);
                    keysToRemove.Add(GetSyncCredentialSettingKey(
                        matchingOfficeCode,
                        SyncOfficeCredentialTenantSuffix));
                    keysToRemove.Add(GetSyncCredentialSettingKey(
                        matchingOfficeCode,
                        SyncOfficeCredentialPasswordSuffix));
                    keysToRemove.Add(GetSyncCredentialSettingKey(
                        matchingOfficeCode,
                        SyncOfficeCredentialSavedAtSuffix));
                }

                var settings = await revocationDb.Settings
                    .Where(setting => keysToRemove.Contains(setting.Key))
                    .ToListAsync(CancellationToken.None);
                if (settings.Count > 0)
                    revocationDb.Settings.RemoveRange(settings);
                await revocationDb.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex)
        {
            throw new AuthenticationCachePersistenceException(
                "서버에서 거부된 인증 캐시 제거에 실패했습니다.",
                offlineFallbackBlocked: true,
                ex);
        }

        DetachAuthenticationSettings(keysToRemove);
        ClearOwnedAuthenticationRevocationTombstone(barrier.Lease);
    }

    private async Task<CachedSessionRecord?> ReadCachedSessionRecordAsync(
        LocalDbContext db,
        string requestedUsername,
        string normalizedUsername,
        bool requireFresh,
        DateTimeOffset nowUtc)
    {
        var userKeys = CachedSessionSettingSuffixes
            .Select(suffix => GetCachedSessionSettingKey(normalizedUsername, suffix));
        var legacyKeys = CachedSessionSettingSuffixes
            .Select(suffix => "CachedSession_" + suffix);
        var keys = userKeys.Concat(legacyKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = await db.Settings
            .AsNoTracking()
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(
                setting => setting.Key,
                setting => setting.Value,
                StringComparer.OrdinalIgnoreCase,
                CancellationToken.None);

        string? Read(string suffix, bool legacy)
            => settings.GetValueOrDefault(
                legacy
                    ? "CachedSession_" + suffix
                    : GetCachedSessionSettingKey(normalizedUsername, suffix));

        var cachedUsername = Read(CachedSessionUsernameSuffix, legacy: false);
        var useLegacy = false;
        if (!string.Equals(cachedUsername, requestedUsername, StringComparison.OrdinalIgnoreCase))
        {
            cachedUsername = Read(CachedSessionUsernameSuffix, legacy: true);
            if (!string.Equals(cachedUsername, requestedUsername, StringComparison.OrdinalIgnoreCase))
                return null;
            useLegacy = true;
        }

        var schemaVersionRaw = Read(CachedSessionSchemaVersionSuffix, useLegacy);
        if (!int.TryParse(
                schemaVersionRaw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var schemaVersion)
            || schemaVersion != OfflineSessionCachePolicy.CurrentSchemaVersion)
        {
            return null;
        }

        var role = Read(CachedSessionRoleSuffix, useLegacy);
        var permissionsText = Read(CachedSessionPermissionsSuffix, useLegacy);
        var tenantCode = Read(CachedSessionTenantCodeSuffix, useLegacy);
        var scopeType = Read(CachedSessionScopeTypeSuffix, useLegacy);
        var officeCode = Read(CachedSessionOfficeCodeSuffix, useLegacy);
        var passwordProof = Read(CachedSessionPasswordProofSuffix, useLegacy);
        var metadataProof = Read(CachedSessionMetadataProofSuffix, useLegacy);
        var cachedAtRaw = Read(CachedSessionCachedAtUtcSuffix, useLegacy);
        var lastOnlineRaw = Read(CachedSessionLastOnlineValidationAtUtcSuffix, useLegacy);
        var lastAcceptedRaw = Read(CachedSessionLastAcceptedOfflineUtcSuffix, useLegacy);
        if (cachedUsername is null
            || role is null
            || permissionsText is null
            || tenantCode is null
            || scopeType is null
            || officeCode is null
            || passwordProof is null
            || metadataProof is null
            || cachedAtRaw is null
            || lastOnlineRaw is null
            || lastAcceptedRaw is null
            || !TryParseCacheTimestamp(cachedAtRaw, out var cachedAtUtc)
            || !TryParseCacheTimestamp(lastOnlineRaw, out var lastOnlineValidationAtUtc)
            || !TryParseCacheTimestamp(lastAcceptedRaw, out var lastAcceptedOfflineUtc))
        {
            return null;
        }

        var expectedMetadata = CreateOfflineSessionMetadataEnvelope(
            cachedUsername,
            normalizedUsername,
            role,
            permissionsText,
            tenantCode,
            scopeType,
            officeCode,
            cachedAtRaw,
            lastOnlineRaw,
            lastAcceptedRaw,
            passwordProof);
        if (!VerifyOfflineSessionMetadata(metadataProof, expectedMetadata)
            || !IsOfflinePasswordProofReadable(passwordProof)
            || requireFresh
            && !OfflineSessionCachePolicy.IsFresh(
                cachedAtUtc,
                lastOnlineValidationAtUtc,
                lastAcceptedOfflineUtc,
                nowUtc,
                _maximumOfflineGrace))
        {
            return null;
        }

        return new CachedSessionRecord(
            cachedUsername,
            role,
            permissionsText,
            tenantCode,
            scopeType,
            officeCode,
            passwordProof,
            cachedAtUtc,
            lastOnlineValidationAtUtc,
            lastAcceptedOfflineUtc,
            useLegacy);
    }

    private Dictionary<string, string> CreateCachedSessionValues(
        string displayUsername,
        string normalizedUsername,
        string role,
        string permissionsText,
        string tenantCode,
        string scopeType,
        string officeCode,
        string passwordProof,
        DateTimeOffset cachedAtUtc,
        DateTimeOffset lastOnlineValidationAtUtc,
        DateTimeOffset lastAcceptedOfflineUtc)
    {
        var cachedAtText = cachedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var lastOnlineText =
            lastOnlineValidationAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var lastAcceptedText =
            lastAcceptedOfflineUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var metadataProof = ProtectOfflineSessionMetadata(CreateOfflineSessionMetadataEnvelope(
            displayUsername,
            normalizedUsername,
            role,
            permissionsText,
            tenantCode,
            scopeType,
            officeCode,
            cachedAtText,
            lastOnlineText,
            lastAcceptedText,
            passwordProof));
        if (string.IsNullOrWhiteSpace(metadataProof))
            throw new InvalidOperationException("오프라인 인증 캐시 보호 데이터 생성에 실패했습니다.");

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CachedSessionUsernameSuffix] = displayUsername,
            [CachedSessionRoleSuffix] = role,
            [CachedSessionPermissionsSuffix] = permissionsText,
            [CachedSessionTenantCodeSuffix] = tenantCode,
            [CachedSessionScopeTypeSuffix] = scopeType,
            [CachedSessionOfficeCodeSuffix] = officeCode,
            [CachedSessionPasswordProofSuffix] = passwordProof,
            [CachedSessionCachedAtUtcSuffix] = cachedAtText,
            [CachedSessionLastOnlineValidationAtUtcSuffix] = lastOnlineText,
            [CachedSessionLastAcceptedOfflineUtcSuffix] = lastAcceptedText,
            [CachedSessionMetadataProofSuffix] = metadataProof,
            [CachedSessionSchemaVersionSuffix] =
                OfflineSessionCachePolicy.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static async Task WriteCachedSessionValuesAsync(
        LocalDbContext db,
        string normalizedUsername,
        IReadOnlyDictionary<string, string> values,
        bool updateLegacyOnlyWhenOwnedByUser)
    {
        var updateLegacy = updateLegacyOnlyWhenOwnedByUser
            && await LegacyCacheBelongsToUserAsync(db, normalizedUsername);
        var keys = BuildCachedSessionKeys(normalizedUsername, values.Keys, updateLegacy);
        var settings = await db.Settings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(
                setting => setting.Key,
                StringComparer.OrdinalIgnoreCase,
                CancellationToken.None);
        foreach (var pair in values)
        {
            UpsertSetting(
                db,
                settings,
                GetCachedSessionSettingKey(normalizedUsername, pair.Key),
                pair.Value);
            if (updateLegacy)
                UpsertSetting(db, settings, "CachedSession_" + pair.Key, pair.Value);
        }
    }

    private static async Task<bool> LegacyCacheBelongsToUserAsync(
        LocalDbContext db,
        string normalizedUsername)
    {
        var legacyUsername = await db.Settings
            .AsNoTracking()
            .Where(setting => setting.Key == "CachedSession_Username")
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(CancellationToken.None);
        return string.Equals(
            NormalizeUsername(legacyUsername),
            normalizedUsername,
            StringComparison.Ordinal);
    }

    private static HashSet<string> BuildCachedSessionKeys(
        string normalizedUsername,
        IEnumerable<string> suffixes,
        bool includeLegacy)
    {
        var keys = suffixes
            .Select(suffix => GetCachedSessionSettingKey(normalizedUsername, suffix))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (includeLegacy)
        {
            foreach (var suffix in suffixes)
                keys.Add("CachedSession_" + suffix);
        }
        return keys;
    }

    private void DetachAuthenticationSettings(IEnumerable<string> keys)
    {
        var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var trackedSetting in _db.ChangeTracker.Entries<LocalSetting>()
                     .Where(entry => keySet.Contains(entry.Entity.Key))
                     .ToList())
        {
            trackedSetting.State = EntityState.Detached;
        }
    }

    private static void UpsertSetting(
        LocalDbContext db,
        IDictionary<string, LocalSetting> settings,
        string key,
        string value)
    {
        if (settings.TryGetValue(key, out var setting))
        {
            setting.Value = value;
            return;
        }

        setting = new LocalSetting { Key = key, Value = value };
        db.Settings.Add(setting);
        settings[key] = setting;
    }

    internal static bool HasAuthenticationRevocationTombstone(string? username)
    {
        if (IsAuthenticationFatalFailureLatched)
            return true;

        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return false;

        return HasAuthenticationRevocationTombstoneAtPath(
                   GetAuthenticationRevocationTombstonePath(normalizedUsername))
               || HasAuthenticationRevocationTombstoneAtPath(
                   GetSecondaryAuthenticationRevocationTombstonePath(normalizedUsername))
               || HasAuthenticationRevocationTombstoneAtPath(
                   GetEmergencyAuthenticationRevocationTombstonePath(normalizedUsername));
    }

    private static bool HasAuthenticationRevocationTombstoneAtPath(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    internal static string GetAuthenticationRevocationTombstonePath(string username)
    {
        var normalizedUsername = NormalizeUsername(username);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUsername));
        return Path.Combine(
            AppPaths.DataDir,
            AuthenticationRevocationDirectoryName,
            Convert.ToHexString(digest).ToLowerInvariant() + ".revoked");
    }

    internal static string GetSecondaryAuthenticationRevocationTombstonePath(string username)
    {
        var normalizedUsername = NormalizeUsername(username);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUsername));
        return Path.Combine(
            AppPaths.DiagnosticsDir,
            SecondaryAuthenticationRevocationDirectoryName,
            Convert.ToHexString(digest).ToLowerInvariant() + ".revoked");
    }

    internal static string GetEmergencyAuthenticationRevocationTombstonePath(string username)
    {
        var normalizedUsername = NormalizeUsername(username);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUsername));
        var root = AppPaths.IsTestEnvironment
            ? Path.Combine(AppPaths.TempRoot, EmergencyAuthenticationRevocationDirectoryName)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                EmergencyAuthenticationRevocationDirectoryName);
        return Path.Combine(
            root,
            Convert.ToHexString(digest).ToLowerInvariant() + ".revoked");
    }

    private AuthenticationMarkerCreation CreateAuthenticationRevocationTombstone(
        string normalizedUsername,
        AuthenticationMarkerChannel channel)
    {
        var injectedFailure = channel switch
        {
            AuthenticationMarkerChannel.Primary =>
                AuthenticationTombstoneWriteFailureFactory?.Invoke(normalizedUsername),
            AuthenticationMarkerChannel.Secondary =>
                SecondaryAuthenticationTombstoneWriteFailureFactory?.Invoke(normalizedUsername),
            AuthenticationMarkerChannel.Emergency =>
                EmergencyAuthenticationTombstoneWriteFailureFactory?.Invoke(normalizedUsername),
            _ => null
        };
        if (injectedFailure is not null)
            throw injectedFailure;

        var path = channel switch
        {
            AuthenticationMarkerChannel.Primary =>
                GetAuthenticationRevocationTombstonePath(normalizedUsername),
            AuthenticationMarkerChannel.Secondary =>
                GetSecondaryAuthenticationRevocationTombstonePath(normalizedUsername),
            AuthenticationMarkerChannel.Emergency =>
                GetEmergencyAuthenticationRevocationTombstonePath(normalizedUsername),
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("인증 폐기 표식 디렉터리를 확인할 수 없습니다.");
        Directory.CreateDirectory(directory);

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            AuthenticationMarkerIdentity? identity;
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.WriteLine("v2");
                writer.WriteLine(nonce);
                writer.WriteLine(DateTimeOffset.UtcNow.ToString("O"));
                writer.Flush();
                stream.Flush(flushToDisk: true);
                identity = OperatingSystem.IsWindows()
                    && TryGetAuthenticationMarkerIdentity(stream.SafeFileHandle, out var windowsIdentity)
                        ? windowsIdentity
                        : null;
            }

            if (OperatingSystem.IsWindows() && identity is null)
                throw new InvalidOperationException(
                    "The authentication revocation marker identity could not be verified.");

            return new AuthenticationMarkerCreation(
                new AuthenticationMarkerLease(path, nonce, identity),
                AlreadyExisted: false);
        }
        catch (IOException) when (HasAuthenticationRevocationTombstoneAtPath(path))
        {
            return new AuthenticationMarkerCreation(
                Lease: null,
                AlreadyExisted: true);
        }
    }

    private void ClearOwnedAuthenticationRevocationTombstone(
        AuthenticationMarkerLease? lease)
    {
        if (lease is null
            || !OperatingSystem.IsWindows()
            || lease.Identity is null)
            return;

        try
        {
            using var handle = OpenOwnedAuthenticationMarkerHandle(lease.Path);
            if (handle.IsInvalid
                || !TryGetAuthenticationMarkerIdentity(handle, out var openedIdentity)
                || openedIdentity != lease.Identity.Value
                || !TryReadAuthenticationMarkerHeader(
                    handle,
                    out var markerVersion,
                    out var markerNonce))
            {
                return;
            }

            if (!string.Equals(markerVersion, "v2", StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(markerNonce),
                    Encoding.ASCII.GetBytes(lease.Nonce)))
            {
                return;
            }

            AuthenticationOwnedMarkerBeforeDispositionHook?.Invoke(lease.Path);

            if (!TryGetAuthenticationMarkerIdentity(handle, out var dispositionIdentity)
                || dispositionIdentity != openedIdentity)
            {
                return;
            }

            var disposition = new AuthenticationFileDispositionInfo
            {
                DeleteFile = true
            };
            if (!SetAuthenticationMarkerFileInformationByHandle(
                    handle,
                    AuthenticationFileInformationClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf<AuthenticationFileDispositionInfo>()))
            {
                throw new IOException(
                    "The verified authentication revocation marker could not be deleted.",
                    Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AUTH", "소유한 인증 폐기 표식 정리에 실패했습니다.", ex);
        }
    }

    private static SafeFileHandle OpenOwnedAuthenticationMarkerHandle(string path)
        => CreateAuthenticationMarkerFile(
            path,
            AuthenticationGenericRead | AuthenticationDeleteAccess,
            FileShare.Read | FileShare.Delete,
            IntPtr.Zero,
            AuthenticationOpenExisting,
            AuthenticationFileFlagOpenReparsePoint,
            IntPtr.Zero);

    private static bool TryGetAuthenticationMarkerIdentity(
        SafeFileHandle handle,
        out AuthenticationMarkerIdentity identity)
    {
        identity = default;
        if (handle.IsInvalid
            || !GetAuthenticationMarkerFileInformation(handle, out var fileInformation)
            || (fileInformation.FileAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        identity = new AuthenticationMarkerIdentity(
            fileInformation.VolumeSerialNumber,
            ((ulong)fileInformation.FileIndexHigh << 32)
            | fileInformation.FileIndexLow);
        return true;
    }

    private static bool TryReadAuthenticationMarkerHeader(
        SafeFileHandle handle,
        out string version,
        out string nonce)
    {
        version = string.Empty;
        nonce = string.Empty;
        try
        {
            var length = RandomAccess.GetLength(handle);
            if (length <= 0 || length > AuthenticationMarkerMaximumBytes)
                return false;

            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = RandomAccess.Read(
                    handle,
                    bytes.AsSpan(offset),
                    offset);
                if (count == 0)
                    return false;

                offset += count;
            }

            var markerText = Encoding.UTF8.GetString(bytes);
            if (markerText.Length > 0 && markerText[0] == '\uFEFF')
                markerText = markerText[1..];

            using var reader = new StringReader(markerText);
            version = reader.ReadLine() ?? string.Empty;
            nonce = reader.ReadLine() ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int AuthenticationMarkerMaximumBytes = 4096;
    private const uint AuthenticationGenericRead = 0x80000000;
    private const uint AuthenticationDeleteAccess = 0x00010000;
    private const uint AuthenticationOpenExisting = 3;
    private const uint AuthenticationFileFlagOpenReparsePoint = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthenticationNativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthenticationByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public AuthenticationNativeFileTime CreationTime;
        public AuthenticationNativeFileTime LastAccessTime;
        public AuthenticationNativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthenticationFileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    private enum AuthenticationFileInformationClass
    {
        FileDispositionInfo = 4
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateAuthenticationMarkerFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAuthenticationMarkerFileInformation(
        SafeFileHandle file,
        out AuthenticationByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetAuthenticationMarkerFileInformationByHandle(
        SafeFileHandle file,
        AuthenticationFileInformationClass fileInformationClass,
        ref AuthenticationFileDispositionInfo fileInformation,
        uint bufferSize);

    private static void ClearAllAuthenticationRevocationTombstones(string normalizedUsername)
    {
        foreach (var path in new[]
                 {
                     GetAuthenticationRevocationTombstonePath(normalizedUsername),
                     GetSecondaryAuthenticationRevocationTombstonePath(normalizedUsername),
                     GetEmergencyAuthenticationRevocationTombstonePath(normalizedUsername)
                 })
        {
            if (TryDeleteAuthenticationRevocationTombstone(path, out var error))
                continue;

            AppLogger.Error("AUTH", "서버 거부 인증 폐기 표식 정리에 실패했습니다.", error);
        }
    }

    internal static bool TryDeleteAuthenticationRevocationTombstone(
        string path,
        out Exception? error)
    {
        try
        {
            File.Delete(path);
            error = null;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // An optional fallback directory that has never been created is
            // already in the desired "no revocation marker" state.
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    internal static void CreateExternalPrimaryAuthenticationRevocationMarkerForTests(
        string username)
        => WriteExternalAuthenticationRevocationMarkerForTests(
            GetAuthenticationRevocationTombstonePath(username));

    internal static string ReplacePrimaryAuthenticationRevocationMarkerForTests(
        string username)
    {
        var path = GetAuthenticationRevocationTombstonePath(username);
        var displacedPath = path + ".displaced-" + Guid.NewGuid().ToString("N");
        File.Move(path, displacedPath);
        try
        {
            WriteExternalAuthenticationRevocationMarkerForTests(path);
            return displacedPath;
        }
        catch
        {
            File.Move(displacedPath, path);
            throw;
        }
    }

    private static void WriteExternalAuthenticationRevocationMarkerForTests(string path)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Missing marker directory."));
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("v2");
        writer.WriteLine(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        writer.WriteLine(DateTimeOffset.UtcNow.ToString("O"));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}
