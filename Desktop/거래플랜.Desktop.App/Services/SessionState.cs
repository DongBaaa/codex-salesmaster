using System.Linq;
using System.Text.Json;
using System.Threading;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Singleton: holds the authenticated JWT session for the current run.
/// IsOfflineMode = true when logged in from local cache (server unreachable).
/// </summary>
public sealed class SessionState
{
    [ThreadStatic]
    private static Dictionary<SessionState, int>?
        s_syncScopeSynchronousCallbackDepths;

    private readonly SemaphoreSlim _syncScopeMutationGate = new(1, 1);
    private long _syncScopeEpoch;

    public string? Token { get; private set; }
    public DateTime? TokenExpiresAtUtc { get; private set; }
    public UserSessionDto? User { get; private set; }
    public string TenantCode { get; private set; } = TenantScopeCatalog.UsenetGroup;
    public string AuthenticatedTenantCode { get; private set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; private set; } = DomainConstants.OfficeUsenet;
    public string BusinessOfficeCode { get; private set; } = DomainConstants.OfficeUsenet;
    public string ScopeType { get; private set; } = TenantScopeCatalog.ScopeOfficeOnly;
    public string SelectedBusinessDatabaseName { get; private set; } = TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup);
    public string SelectedBusinessDatabaseDisplayName { get; private set; } = TenantScopeCatalog.GetBusinessDatabaseDisplayName(TenantScopeCatalog.UsenetGroup);
    public string SelectedBusinessDatabaseLabel => TenantScopeCatalog.FormatBusinessDatabaseLabel(SelectedBusinessDatabaseDisplayName, SelectedBusinessDatabaseName);
    public bool IsOfflineMode { get; private set; }
    public Guid SessionId { get; private set; } = Guid.NewGuid();
    public long SyncScopeEpoch => Interlocked.Read(ref _syncScopeEpoch);
    public bool IsLoggedIn => User is not null;
    public bool IsTokenExpired => TokenExpiresAtUtc is not null && DateTime.UtcNow >= TokenExpiresAtUtc.Value;
    public bool ShouldRefreshToken(TimeSpan leadTime)
        => !IsOfflineMode
           && !string.IsNullOrWhiteSpace(Token)
           && TokenExpiresAtUtc is not null
           && DateTime.UtcNow >= TokenExpiresAtUtc.Value.Subtract(leadTime);
    public bool IsAdmin => DomainConstants.IsAdminRole(User?.Role);
    public bool IsGodMode => TryReadBooleanTokenClaim("god");
    public bool HasAdministrativePrivileges => IsAdmin || IsGodMode;
    public bool HasGlobalDataScope =>
        HasAdministrativePrivileges && string.Equals(ScopeType, TenantScopeCatalog.ScopeAdmin, StringComparison.OrdinalIgnoreCase);
    public bool HasSystemConfigurationScope => IsGodMode || HasGlobalDataScope;
    public event EventHandler? BusinessDatabaseChanged;

    public void SetSession(string token, UserSessionDto user, DateTime? expiresAtUtc = null)
    {
        bool businessDatabaseChanged;
        using (AcquireSyncScopeWriteLease())
        {
            SessionId = Guid.NewGuid();
            Interlocked.Increment(ref _syncScopeEpoch);
            businessDatabaseChanged = ApplyOnlineSession(
                token,
                user,
                expiresAtUtc,
                preserveBusinessDatabaseSelection: false);
        }

        if (businessDatabaseChanged)
            BusinessDatabaseChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSession(string token, UserSessionDto user, DateTime? expiresAtUtc = null)
    {
        bool businessDatabaseChanged;
        using (AcquireSyncScopeWriteLease())
        {
            var before = CaptureSyncScopeIdentity();
            businessDatabaseChanged = ApplyOnlineSession(
                token,
                user,
                expiresAtUtc,
                preserveBusinessDatabaseSelection: true);
            if (before != CaptureSyncScopeIdentity())
                Interlocked.Increment(ref _syncScopeEpoch);
        }

        if (businessDatabaseChanged)
            BusinessDatabaseChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ApplyOnlineSession(
        string token,
        UserSessionDto user,
        DateTime? expiresAtUtc,
        bool preserveBusinessDatabaseSelection)
    {
        var previousBusinessDatabaseName = SelectedBusinessDatabaseName;
        var previousBusinessDatabaseDisplayName = SelectedBusinessDatabaseDisplayName;

        Token = token;
        TokenExpiresAtUtc = ResolveTokenExpiresAtUtc(token, expiresAtUtc);
        User = user;
        IsOfflineMode = false;
        AuthenticatedTenantCode = ResolveTenantCode(user.TenantCode, user.OfficeCode);
        TenantCode = AuthenticatedTenantCode;
        OfficeCode = ResolveOfficeCode(user.OfficeCode, user.Role);
        BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
        ScopeType = ResolveScopeType(user.ScopeType, user.Role, OfficeCode);

        if (preserveBusinessDatabaseSelection && HasSystemConfigurationScope)
        {
            return SetBusinessDatabaseCore(
                previousBusinessDatabaseName,
                previousBusinessDatabaseDisplayName);
        }

        return ResetBusinessDatabaseSelection();
    }

    public void SetOfflineSession(UserSessionDto user)
    {
        bool businessDatabaseChanged;
        using (AcquireSyncScopeWriteLease())
        {
            SessionId = Guid.NewGuid();
            Interlocked.Increment(ref _syncScopeEpoch);
            Token = null;
            TokenExpiresAtUtc = null;
            User = user;
            IsOfflineMode = true;
            AuthenticatedTenantCode = ResolveTenantCode(user.TenantCode, user.OfficeCode);
            TenantCode = AuthenticatedTenantCode;
            OfficeCode = ResolveOfficeCode(user.OfficeCode, user.Role);
            BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
            ScopeType = ResolveScopeType(user.ScopeType, user.Role, OfficeCode);
            businessDatabaseChanged = ResetBusinessDatabaseSelection();
        }

        if (businessDatabaseChanged)
            BusinessDatabaseChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetOfficeCode(string? officeCode)
    {
        using var scopeWriteLease = AcquireSyncScopeWriteLease();
        if (string.IsNullOrWhiteSpace(officeCode))
            return;

        var before = CaptureSyncScopeIdentity();
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, OfficeCode);
        AuthenticatedTenantCode = ResolveTenantCode(AuthenticatedTenantCode, OfficeCode);
        if (!HasAdministrativePrivileges)
        {
            TenantCode = AuthenticatedTenantCode;
            BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
            _ = ResetBusinessDatabaseSelection();
        }

        if (before != CaptureSyncScopeIdentity())
            Interlocked.Increment(ref _syncScopeEpoch);
    }

    public void SetBusinessDatabase(string? databaseName, string? displayName = null)
    {
        bool businessDatabaseChanged;
        using (AcquireSyncScopeWriteLease())
        {
            var before = CaptureSyncScopeIdentity();
            businessDatabaseChanged = SetBusinessDatabaseCore(databaseName, displayName);
            if (before != CaptureSyncScopeIdentity())
                Interlocked.Increment(ref _syncScopeEpoch);
        }

        if (businessDatabaseChanged)
            BusinessDatabaseChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SetBusinessDatabaseCore(
        string? databaseName,
        string? displayName)
    {
        if (!HasSystemConfigurationScope)
            return ResetBusinessDatabaseSelection();

        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(databaseName, AuthenticatedTenantCode);
        var normalizedDatabaseName = TenantScopeCatalog.GetDatabaseName(databaseName);
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? TenantScopeCatalog.GetBusinessDatabaseDisplayName(normalizedDatabaseName)
            : displayName.Trim();

        var changed = !string.Equals(TenantCode, normalizedTenantCode, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(SelectedBusinessDatabaseName, normalizedDatabaseName, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(SelectedBusinessDatabaseDisplayName, normalizedDisplayName, StringComparison.Ordinal);

        TenantCode = normalizedTenantCode;
        BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
        SelectedBusinessDatabaseName = normalizedDatabaseName;
        SelectedBusinessDatabaseDisplayName = normalizedDisplayName;
        return changed;
    }

    public void Clear()
    {
        bool businessDatabaseChanged;
        using (AcquireSyncScopeWriteLease())
        {
            SessionId = Guid.NewGuid();
            Interlocked.Increment(ref _syncScopeEpoch);
            Token = null;
            TokenExpiresAtUtc = null;
            User = null;
            IsOfflineMode = false;
            AuthenticatedTenantCode = TenantScopeCatalog.UsenetGroup;
            TenantCode = TenantScopeCatalog.UsenetGroup;
            OfficeCode = DomainConstants.OfficeUsenet;
            BusinessOfficeCode = DomainConstants.OfficeUsenet;
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly;
            businessDatabaseChanged = ResetBusinessDatabaseSelection();
        }

        if (businessDatabaseChanged)
            BusinessDatabaseChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasPermission(string permissionName)
    {
        if (User is null) return false;
        if (HasAdministrativePrivileges) return true;
        return User.Permissions.Contains(permissionName);
    }

    public bool HasAssignedPermission(string permissionName)
    {
        if (User is null) return false;
        return User.Permissions.Contains(permissionName);
    }

    private static string ResolveOfficeCode(string? officeCode, string? role)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
            return normalizedOfficeCode;

        return DomainConstants.IsAdminRole(role)
            ? DomainConstants.OfficeUsenet
            : DomainConstants.OfficeYeonsu;
    }

    private static string ResolveTenantCode(string? tenantCode, string? officeCode)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode);

    private static string ResolveScopeType(string? scopeType, string? role, string? officeCode)
    {
        if (TenantScopeCatalog.TryNormalizeScopeType(scopeType, out var normalizedScopeType))
            return normalizedScopeType;

        return TenantScopeCatalog.NormalizeScopeTypeOrDefault(
            null,
            string.Equals(ResolveTenantCode(null, officeCode), TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase) &&
            !DomainConstants.IsAdminRole(role)
                ? TenantScopeCatalog.ScopeTenantAll
                : TenantScopeCatalog.ScopeOfficeOnly);
    }

    private static string ResolveBusinessOfficeCode(string? tenantCode)
        => TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode).FirstOrDefault()
           ?? DomainConstants.OfficeUsenet;

    private static DateTime? ResolveTokenExpiresAtUtc(string token, DateTime? explicitExpiresAtUtc)
    {
        if (explicitExpiresAtUtc is not null)
            return NormalizeDateTimeUtc(explicitExpiresAtUtc.Value);

        if (!TryReadTokenPayload(token, out var document))
            return null;

        using (document)
        {
            if (document.RootElement.TryGetProperty("exp", out var expProperty) &&
                expProperty.TryGetInt64(out var expSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
            }
        }

        return null;
    }

    private static DateTime NormalizeDateTimeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static bool TryReadTokenPayload(string? token, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var segments = token.Split('.');
        if (segments.Length < 2)
            return false;

        try
        {
            var payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');

            switch (payload.Length % 4)
            {
                case 2:
                    payload += "==";
                    break;
                case 3:
                    payload += "=";
                    break;
            }

            var bytes = Convert.FromBase64String(payload);
            document = JsonDocument.Parse(bytes);
            return true;
        }
        catch
        {
            document = null!;
            return false;
        }
    }

    internal IDisposable AcquireSyncScopeSnapshotLease()
        => AcquireSyncScopeWriteLease();

    internal async ValueTask<IDisposable> AcquireSyncScopeCommitLeaseAsync(
        CancellationToken ct = default)
    {
        await _syncScopeMutationGate.WaitAsync(ct);
        return new SyncScopeLease(_syncScopeMutationGate);
    }

    /// <summary>
    /// Allows a synchronous notification invoked while a sync-scope commit lease is held
    /// to change the session on the same thread without re-entering the non-reentrant
    /// semaphore. The caller must revalidate its captured owner immediately after each
    /// callback and stop publishing when the owner changed.
    /// </summary>
    internal IDisposable EnterSyncScopeSynchronousCallback()
    {
        var depths = s_syncScopeSynchronousCallbackDepths ??= [];
        depths.TryGetValue(this, out var depth);
        depths[this] = depth + 1;
        return new SyncScopeSynchronousCallbackLease(this);
    }

    private IDisposable AcquireSyncScopeWriteLease()
    {
        if (IsSyncScopeSynchronousCallbackActive())
            return NoopSyncScopeLease.Instance;

        _syncScopeMutationGate.Wait();
        return new SyncScopeLease(_syncScopeMutationGate);
    }

    private bool IsSyncScopeSynchronousCallbackActive()
        => s_syncScopeSynchronousCallbackDepths is { } depths
           && depths.TryGetValue(this, out var depth)
           && depth > 0;

    private void ExitSyncScopeSynchronousCallback()
    {
        if (s_syncScopeSynchronousCallbackDepths is not { } depths ||
            !depths.TryGetValue(this, out var depth))
        {
            return;
        }

        if (depth <= 1)
            depths.Remove(this);
        else
            depths[this] = depth - 1;
    }

    private SyncScopeIdentity CaptureSyncScopeIdentity()
        => new(
            SessionId,
            User?.UserId ?? Guid.Empty,
            User?.Username ?? string.Empty,
            User?.Role ?? string.Empty,
            BuildPermissionIdentity(User?.Permissions),
            AuthenticatedTenantCode,
            TenantCode,
            OfficeCode,
            BusinessOfficeCode,
            ScopeType,
            SelectedBusinessDatabaseName,
            IsOfflineMode,
            HasGlobalDataScope,
            HasSystemConfigurationScope);

    private static string BuildPermissionIdentity(
        IEnumerable<string>? permissions)
        => string.Join(
            "\u001F",
            (permissions ?? [])
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase));

    private bool ResetBusinessDatabaseSelection()
    {
        var normalizedDatabaseName = TenantScopeCatalog.GetDatabaseName(AuthenticatedTenantCode);
        var normalizedDisplayName = TenantScopeCatalog.GetBusinessDatabaseDisplayName(normalizedDatabaseName);
        var changed = !string.Equals(TenantCode, AuthenticatedTenantCode, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(SelectedBusinessDatabaseName, normalizedDatabaseName, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(SelectedBusinessDatabaseDisplayName, normalizedDisplayName, StringComparison.Ordinal);

        TenantCode = AuthenticatedTenantCode;
        BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
        SelectedBusinessDatabaseName = normalizedDatabaseName;
        SelectedBusinessDatabaseDisplayName = normalizedDisplayName;
        return changed;
    }

    private sealed record SyncScopeIdentity(
        Guid SessionId,
        Guid UserId,
        string Username,
        string Role,
        string PermissionIdentity,
        string AuthenticatedTenantCode,
        string TenantCode,
        string OfficeCode,
        string BusinessOfficeCode,
        string ScopeType,
        string BusinessDatabaseName,
        bool IsOfflineMode,
        bool HasGlobalDataScope,
        bool HasSystemConfigurationScope);

    private sealed class SyncScopeLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public SyncScopeLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed class SyncScopeSynchronousCallbackLease : IDisposable
    {
        private SessionState? _session;

        public SyncScopeSynchronousCallbackLease(SessionState session)
        {
            _session = session;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _session, null)?
                .ExitSyncScopeSynchronousCallback();
    }

    private sealed class NoopSyncScopeLease : IDisposable
    {
        public static NoopSyncScopeLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private bool TryReadBooleanTokenClaim(string claimName)
    {
        if (string.IsNullOrWhiteSpace(claimName))
            return false;

        if (!TryReadTokenPayload(Token, out var document))
            return false;

        using (document)
        {
            if (!document.RootElement.TryGetProperty(claimName, out var property))
                return false;

            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(property.GetString(), out var value) && value,
                JsonValueKind.Number => property.TryGetInt32(out var numeric) && numeric != 0,
                _ => false
            };
        }
    }
}
