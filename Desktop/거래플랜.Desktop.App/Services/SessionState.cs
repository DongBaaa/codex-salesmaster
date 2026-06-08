using System.Linq;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Singleton: holds the authenticated JWT session for the current run.
/// IsOfflineMode = true when logged in from local cache (server unreachable).
/// </summary>
public sealed class SessionState
{
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
    public event EventHandler? BusinessDatabaseChanged;

    public void SetSession(string token, UserSessionDto user, DateTime? expiresAtUtc = null)
    {
        SessionId = Guid.NewGuid();
        ApplyOnlineSession(token, user, expiresAtUtc, preserveBusinessDatabaseSelection: false);
    }

    public void RefreshSession(string token, UserSessionDto user, DateTime? expiresAtUtc = null)
        => ApplyOnlineSession(token, user, expiresAtUtc, preserveBusinessDatabaseSelection: true);

    private void ApplyOnlineSession(
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

        if (preserveBusinessDatabaseSelection && HasAdministrativePrivileges)
        {
            SetBusinessDatabase(previousBusinessDatabaseName, previousBusinessDatabaseDisplayName);
            return;
        }

        ResetBusinessDatabaseSelection();
    }

    public void SetOfflineSession(UserSessionDto user)
    {
        SessionId = Guid.NewGuid();
        Token = null;
        TokenExpiresAtUtc = null;
        User = user;
        IsOfflineMode = true;
        AuthenticatedTenantCode = ResolveTenantCode(user.TenantCode, user.OfficeCode);
        TenantCode = AuthenticatedTenantCode;
        OfficeCode = ResolveOfficeCode(user.OfficeCode, user.Role);
        BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
        ScopeType = ResolveScopeType(user.ScopeType, user.Role, OfficeCode);
        ResetBusinessDatabaseSelection();
    }

    public void SetOfficeCode(string? officeCode)
    {
        if (string.IsNullOrWhiteSpace(officeCode))
            return;

        OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, OfficeCode);
        AuthenticatedTenantCode = ResolveTenantCode(AuthenticatedTenantCode, OfficeCode);
        if (!HasAdministrativePrivileges)
        {
            TenantCode = AuthenticatedTenantCode;
            BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
            ResetBusinessDatabaseSelection(raiseChanged: false);
        }
    }

    public void SetBusinessDatabase(string? databaseName, string? displayName = null)
    {
        if (!HasAdministrativePrivileges)
        {
            ResetBusinessDatabaseSelection();
            return;
        }

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

        if (changed)
            BusinessDatabaseChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        SessionId = Guid.NewGuid();
        Token = null;
        TokenExpiresAtUtc = null;
        User = null;
        IsOfflineMode = false;
        AuthenticatedTenantCode = TenantScopeCatalog.UsenetGroup;
        TenantCode = TenantScopeCatalog.UsenetGroup;
        OfficeCode = DomainConstants.OfficeUsenet;
        BusinessOfficeCode = DomainConstants.OfficeUsenet;
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly;
        SelectedBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup);
        SelectedBusinessDatabaseDisplayName = TenantScopeCatalog.GetBusinessDatabaseDisplayName(TenantScopeCatalog.UsenetGroup);
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

    private void ResetBusinessDatabaseSelection(bool raiseChanged = true)
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

        if (raiseChanged && changed)
            BusinessDatabaseChanged?.Invoke(this, EventArgs.Empty);
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
