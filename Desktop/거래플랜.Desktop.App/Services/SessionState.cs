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
    public string OfficeCode { get; private set; } = DomainConstants.OfficeUsenet;
    public bool IsOfflineMode { get; private set; }
    public Guid SessionId { get; private set; } = Guid.NewGuid();
    public bool IsLoggedIn => User is not null;
    public bool IsAdmin => DomainConstants.IsAdminRole(User?.Role);

    public void SetSession(string token, UserSessionDto user)
    {
        SessionId = Guid.NewGuid();
        Token = token;
        User = user;
        IsOfflineMode = false;
        OfficeCode = ResolveOfficeCode(user.OfficeCode, user.Role);
    }

    public void SetOfflineSession(UserSessionDto user)
    {
        SessionId = Guid.NewGuid();
        Token = null;
        User = user;
        IsOfflineMode = true;
        OfficeCode = ResolveOfficeCode(user.OfficeCode, user.Role);
    }

    public void SetOfficeCode(string? officeCode)
    {
        if (string.IsNullOrWhiteSpace(officeCode))
            return;

        OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, OfficeCode);
    }

    public void Clear()
    {
        SessionId = Guid.NewGuid();
        Token = null;
        User = null;
        IsOfflineMode = false;
        OfficeCode = DomainConstants.OfficeUsenet;
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
}

