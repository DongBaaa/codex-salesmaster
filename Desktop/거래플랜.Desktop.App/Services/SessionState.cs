using System.Linq;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Singleton: holds the authenticated JWT session for the current run.
/// IsOfflineMode = true when logged in from local cache (server unreachable).
/// </summary>
public sealed class SessionState
{
    public string? Token { get; private set; }
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
    public bool IsAdmin => DomainConstants.IsAdminRole(User?.Role);
    public event EventHandler? BusinessDatabaseChanged;

    public void SetSession(string token, UserSessionDto user)
    {
        SessionId = Guid.NewGuid();
        Token = token;
        User = user;
        IsOfflineMode = false;
        AuthenticatedTenantCode = ResolveTenantCode(user.TenantCode, user.OfficeCode);
        TenantCode = AuthenticatedTenantCode;
        OfficeCode = ResolveOfficeCode(user.OfficeCode, user.Role);
        BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
        ScopeType = ResolveScopeType(user.ScopeType, user.Role, OfficeCode);
        ResetBusinessDatabaseSelection();
    }

    public void SetOfflineSession(UserSessionDto user)
    {
        SessionId = Guid.NewGuid();
        Token = null;
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
        if (!IsAdmin)
        {
            TenantCode = AuthenticatedTenantCode;
            BusinessOfficeCode = ResolveBusinessOfficeCode(TenantCode);
            ResetBusinessDatabaseSelection(raiseChanged: false);
        }
    }

    public void SetBusinessDatabase(string? databaseName, string? displayName = null)
    {
        if (!IsAdmin)
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
        if (IsAdmin) return true;
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
        if (DomainConstants.IsAdminRole(role))
            return TenantScopeCatalog.ScopeAdmin;

        return TenantScopeCatalog.NormalizeScopeTypeOrDefault(
            scopeType,
            string.Equals(ResolveTenantCode(null, officeCode), TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? TenantScopeCatalog.ScopeTenantAll
                : TenantScopeCatalog.ScopeOfficeOnly);
    }

    private static string ResolveBusinessOfficeCode(string? tenantCode)
        => TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode).FirstOrDefault()
           ?? DomainConstants.OfficeUsenet;

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
}

