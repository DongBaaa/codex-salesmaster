using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SessionStore
{
    private const string HasSessionKey = "session.has";
    private const string TokenKey = "session.token";
    private const string UsernameKey = "session.username";
    private const string RoleKey = "session.role";
    private const string TenantCodeKey = "session.tenant";
    private const string OfficeCodeKey = "session.office";
    private const string SessionGenerationKey =
        "session.generation";
    private const string ScopeTypeKey = "session.scope";
    private const string PermissionsKey = "session.permissions";
    private const string ExpiresAtUtcKey = "session.expiresAtUtc";
    private static readonly TimeSpan ExpirationSkew = TimeSpan.FromMinutes(1);
    private static readonly object SessionGenerationSync = new();
    private readonly SemaphoreSlim _ownerMutationGate =
        new(1, 1);

    public bool HasCachedSession()
        => Preferences.Default.Get(HasSessionKey, false);

    public async Task<bool> HasUsableSessionAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var owner = CaptureOwner();
            if (!owner.IsAuthenticated)
                return false;

            var token = await ReadStoredTokenAsync();
            if (!IsOwnerCurrent(owner))
                continue;
            if (!string.IsNullOrWhiteSpace(token) &&
                !IsExpired(ResolveExpirationUtc(token)))
            {
                return true;
            }

            await ClearIfCurrentAsync(owner);
            return false;
        }

        return false;
    }

    public SessionSnapshot GetSnapshot()
    {
        lock (SessionGenerationSync)
        {
            if (!HasCachedSession())
                return SessionSnapshot.Empty;

            return new SessionSnapshot
            {
                IsAuthenticated = true,
                Username = Preferences.Default.Get(
                    UsernameKey,
                    string.Empty),
                Role = Preferences.Default.Get(
                    RoleKey,
                    string.Empty),
                TenantCode = Preferences.Default.Get(
                    TenantCodeKey,
                    string.Empty),
                OfficeCode = Preferences.Default.Get(
                    OfficeCodeKey,
                    string.Empty),
                SessionGeneration =
                    GetOrCreateSessionGeneration(),
                ScopeType = Preferences.Default.Get(
                    ScopeTypeKey,
                    string.Empty),
                Permissions = ReadStoredPermissions(),
                ExpiresAtUtc = ReadStoredExpirationUtc()
            };
        }
    }

    public MobileSessionOwner CaptureOwner()
        => MobileSessionOwner.Capture(GetSnapshot());

    public bool IsOwnerCurrent(MobileSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.Matches(GetSnapshot());
    }

    public void ThrowIfOwnerChanged(MobileSessionOwner owner)
    {
        if (!IsOwnerCurrent(owner))
        {
            throw new StaleMobileSessionOwnerException(
                "The authenticated mobile owner or session generation changed before the operation could commit.");
        }
    }

    public async Task<bool> ClearIfCurrentAsync(
        MobileSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await _ownerMutationGate.WaitAsync();
        try
        {
            if (!IsOwnerCurrent(owner))
                return false;

            await ClearCoreAsync();
            return true;
        }
        finally
        {
            _ownerMutationGate.Release();
        }
    }

    public async Task<IDisposable>
        AcquireOwnerCommitLeaseAsync(
            MobileSessionOwner owner,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await _ownerMutationGate.WaitAsync(ct);
        try
        {
            ThrowIfOwnerChanged(owner);
            return new SessionOwnerCommitLease(
                _ownerMutationGate);
        }
        catch
        {
            _ownerMutationGate.Release();
            throw;
        }
    }

    public async Task SaveAsync(LoginResponse response)
    {
        await _ownerMutationGate.WaitAsync();
        try
        {
            var token = response.Token ?? string.Empty;
            lock (SessionGenerationSync)
            {
                // HasSession is the commit marker. Remove it before replacing
                // secure-token or owner metadata so a process stop fails closed.
                Preferences.Default.Remove(HasSessionKey);
            }

            try
            {
                await SecureStorage.Default.SetAsync(
                    TokenKey,
                    token);
            }
            catch
            {
                await ClearCoreAsync();
                throw new InvalidOperationException("보안 저장소를 사용할 수 없어 로그인 정보를 안전하게 저장하지 못했습니다.");
            }

            lock (SessionGenerationSync)
            {
                Preferences.Default.Set(UsernameKey, response.User?.Username ?? string.Empty);
                Preferences.Default.Set(RoleKey, response.User?.Role ?? string.Empty);
                Preferences.Default.Set(TenantCodeKey, response.User?.TenantCode ?? string.Empty);
                Preferences.Default.Set(OfficeCodeKey, response.User?.OfficeCode ?? string.Empty);
                Preferences.Default.Set(ScopeTypeKey, response.User?.ScopeType ?? string.Empty);
                Preferences.Default.Set(PermissionsKey, string.Join("\n", response.User?.Permissions ?? new List<string>()));
                Preferences.Default.Set(ExpiresAtUtcKey, response.ExpiresAtUtc.ToUniversalTime().ToString("O"));
                Preferences.Default.Set(
                    SessionGenerationKey,
                    Guid.NewGuid().ToString("N"));
                Preferences.Default.Set(HasSessionKey, true);
            }
        }
        finally
        {
            _ownerMutationGate.Release();
        }
    }

    public async Task<bool> ReplaceIfCurrentAsync(
        MobileSessionOwner expectedOwner,
        LoginResponse response,
        bool preserveGeneration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(response);
        await _ownerMutationGate.WaitAsync(ct);
        try
        {
            if (!IsOwnerCurrent(expectedOwner))
                return false;

            var replacement = MobileSessionOwner.Capture(
                new SessionSnapshot
                {
                    IsAuthenticated = true,
                    Username = response.User?.Username ??
                               string.Empty,
                    TenantCode = response.User?.TenantCode ??
                                 string.Empty,
                    OfficeCode = response.User?.OfficeCode ??
                                 string.Empty,
                    SessionGeneration =
                        expectedOwner.SessionGeneration
                });
            if (expectedOwner.IsAuthenticated &&
                !expectedOwner.HasSameLogicalOwner(replacement))
            {
                return false;
            }

            lock (SessionGenerationSync)
                Preferences.Default.Remove(HasSessionKey);
            await SecureStorage.Default.SetAsync(
                TokenKey,
                response.Token ?? string.Empty);
            lock (SessionGenerationSync)
            {
                Preferences.Default.Set(UsernameKey, response.User?.Username ?? string.Empty);
                Preferences.Default.Set(RoleKey, response.User?.Role ?? string.Empty);
                Preferences.Default.Set(TenantCodeKey, response.User?.TenantCode ?? string.Empty);
                Preferences.Default.Set(OfficeCodeKey, response.User?.OfficeCode ?? string.Empty);
                Preferences.Default.Set(ScopeTypeKey, response.User?.ScopeType ?? string.Empty);
                Preferences.Default.Set(PermissionsKey, string.Join("\n", response.User?.Permissions ?? new List<string>()));
                Preferences.Default.Set(ExpiresAtUtcKey, response.ExpiresAtUtc.ToUniversalTime().ToString("O"));
                Preferences.Default.Set(
                    SessionGenerationKey,
                    preserveGeneration &&
                    expectedOwner.IsAuthenticated
                        ? expectedOwner.SessionGeneration
                        : Guid.NewGuid().ToString("N"));
                Preferences.Default.Set(HasSessionKey, true);
            }
            return true;
        }
        finally
        {
            _ownerMutationGate.Release();
        }
    }

    public async Task<string?> GetTokenAsync(bool clearStaleSession = true)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var owner = CaptureOwner();
            if (!owner.IsAuthenticated)
                return null;

            var token = await ReadStoredTokenAsync();
            if (!IsOwnerCurrent(owner))
                continue;
            if (!string.IsNullOrWhiteSpace(token) &&
                !IsExpired(ResolveExpirationUtc(token)))
            {
                return token;
            }

            if (clearStaleSession)
                await ClearIfCurrentAsync(owner);
            return null;
        }

        return null;
    }

    public async Task<bool> IsTokenExpiredAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var owner = CaptureOwner();
            if (!owner.IsAuthenticated)
                return true;

            var token = await ReadStoredTokenAsync();
            if (!IsOwnerCurrent(owner))
                continue;
            return string.IsNullOrWhiteSpace(token) ||
                   IsExpired(ResolveExpirationUtc(token));
        }

        return true;
    }

    private static async Task<string?> ReadStoredTokenAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(TokenKey);
        }
        catch
        {
            return null;
        }
    }

#if DEBUG
    public async Task SaveDebugSnapshotAsync(string token, string username, string role, string officeCode = "", string tenantCode = "")
    {
        await _ownerMutationGate.WaitAsync();
        try
        {
            lock (SessionGenerationSync)
            {
                Preferences.Default.Remove(HasSessionKey);
            }

            await SecureStorage.Default.SetAsync(TokenKey, token ?? string.Empty);
            lock (SessionGenerationSync)
            {
                Preferences.Default.Set(UsernameKey, username ?? string.Empty);
                Preferences.Default.Set(RoleKey, role ?? string.Empty);
                Preferences.Default.Set(TenantCodeKey, tenantCode ?? string.Empty);
                Preferences.Default.Set(OfficeCodeKey, officeCode ?? string.Empty);
                Preferences.Default.Set(ScopeTypeKey, string.Empty);
                Preferences.Default.Set(PermissionsKey, string.Empty);
                Preferences.Default.Set(
                    SessionGenerationKey,
                    Guid.NewGuid().ToString("N"));
                Preferences.Default.Set(HasSessionKey, true);
            }
        }
        finally
        {
            _ownerMutationGate.Release();
        }
    }
#endif

    public async Task ClearAsync()
    {
        await _ownerMutationGate.WaitAsync();
        try
        {
            await ClearCoreAsync();
        }
        finally
        {
            _ownerMutationGate.Release();
        }
    }

    private static Task ClearCoreAsync()
    {
        try
        {
            SecureStorage.Default.Remove(TokenKey);
        }
        catch
        {
            // ignore
        }
        lock (SessionGenerationSync)
        {
            Preferences.Default.Remove(HasSessionKey);
            Preferences.Default.Remove(UsernameKey);
            Preferences.Default.Remove(RoleKey);
            Preferences.Default.Remove(TenantCodeKey);
            Preferences.Default.Remove(OfficeCodeKey);
            Preferences.Default.Remove(SessionGenerationKey);
            Preferences.Default.Remove(ScopeTypeKey);
            Preferences.Default.Remove(PermissionsKey);
            Preferences.Default.Remove(ExpiresAtUtcKey);
        }
        return Task.CompletedTask;
    }

    private static IReadOnlyList<string> ReadStoredPermissions()
    {
        var raw = Preferences.Default.Get(PermissionsKey, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetOrCreateSessionGeneration()
    {
        lock (SessionGenerationSync)
        {
            var generation = Preferences.Default.Get(
                SessionGenerationKey,
                string.Empty);
            if (!string.IsNullOrWhiteSpace(generation))
                return generation;

            generation = Guid.NewGuid().ToString("N");
            Preferences.Default.Set(
                SessionGenerationKey,
                generation);
            return generation;
        }
    }

    private DateTime? ResolveExpirationUtc(string token)
    {
        var fromToken = TryReadJwtExpirationUtc(token);
        return fromToken ?? ReadStoredExpirationUtc();
    }

    private static DateTime? ReadStoredExpirationUtc()
    {
        var raw = Preferences.Default.Get(ExpiresAtUtcKey, string.Empty);
        return DateTime.TryParse(
            raw,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static bool IsExpired(DateTime? expiresAtUtc)
        => expiresAtUtc.HasValue && expiresAtUtc.Value <= DateTime.UtcNow.Add(ExpirationSkew);

    private static DateTime? TryReadJwtExpirationUtc(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var segments = token.Split('.');
        if (segments.Length < 2)
            return null;

        try
        {
            var payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');

            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("exp", out var expElement))
                return null;

            var expSeconds = expElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when expElement.TryGetInt64(out var numeric) => numeric,
                System.Text.Json.JsonValueKind.String when long.TryParse(expElement.GetString(), out var numeric) => numeric,
                _ => 0L
            };

            return expSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class SessionOwnerCommitLease :
        IDisposable
    {
        private SemaphoreSlim? _gate;

        public SessionOwnerCommitLease(
            SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
            => Interlocked.Exchange(
                    ref _gate,
                    null)
                ?.Release();
    }
}

public sealed record MobileSessionOwner
{
    private MobileSessionOwner(
        bool isAuthenticated,
        string username,
        string tenantCode,
        string officeCode,
        string sessionGeneration)
    {
        IsAuthenticated = isAuthenticated;
        Username = username;
        TenantCode = tenantCode;
        OfficeCode = officeCode;
        SessionGeneration = sessionGeneration;
    }

    public bool IsAuthenticated { get; }
    public string Username { get; }
    public string TenantCode { get; }
    public string OfficeCode { get; }
    public string SessionGeneration { get; }

    public static MobileSessionOwner Capture(
        SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var officeCode = string.IsNullOrWhiteSpace(
            snapshot.OfficeCode)
            ? string.Empty
            : OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                snapshot.OfficeCode);
        var tenantCode = string.IsNullOrWhiteSpace(
            snapshot.TenantCode)
            ? string.Empty
            : TenantScopeCatalog
                .NormalizeTenantCodeForOfficeOrDefault(
                    snapshot.TenantCode,
                    officeCode);
        return new MobileSessionOwner(
            snapshot.IsAuthenticated,
            snapshot.Username?.Trim() ?? string.Empty,
            tenantCode,
            officeCode,
            snapshot.SessionGeneration?.Trim() ??
            string.Empty);
    }

    public bool Matches(SessionSnapshot snapshot)
    {
        var other = Capture(snapshot);
        return IsAuthenticated == other.IsAuthenticated &&
               string.Equals(
                   Username,
                   other.Username,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   TenantCode,
                   other.TenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   OfficeCode,
                   other.OfficeCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   SessionGeneration,
                   other.SessionGeneration,
                   StringComparison.Ordinal);
    }

    public bool HasSameLogicalOwner(
        MobileSessionOwner other)
        => string.Equals(
               Username,
               other.Username,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               TenantCode,
               other.TenantCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               OfficeCode,
               other.OfficeCode,
               StringComparison.OrdinalIgnoreCase);

    public string BuildStateKey()
        => !IsAuthenticated ||
           string.IsNullOrWhiteSpace(Username) ||
           string.IsNullOrWhiteSpace(TenantCode) ||
           string.IsNullOrWhiteSpace(OfficeCode)
            ? "legacy"
            : string.Join(
                "|",
                Username.Trim().ToUpperInvariant(),
                TenantCode.Trim().ToUpperInvariant(),
                OfficeCode.Trim().ToUpperInvariant());
}

public sealed class StaleMobileSessionOwnerException :
    InvalidOperationException
{
    public StaleMobileSessionOwnerException(string message)
        : base(message)
    {
    }
}
