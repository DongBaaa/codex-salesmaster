using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Services;

/// <summary>
/// Singleton: holds the authenticated JWT session for the current run.
/// IsOfflineMode = true when logged in from local cache (server unreachable).
/// </summary>
public sealed class SessionState
{
    public string? Token { get; private set; }
    public UserSessionDto? User { get; private set; }
    public bool IsOfflineMode { get; private set; }
    public bool IsLoggedIn => User is not null;

    public void SetSession(string token, UserSessionDto user)
    {
        Token = token;
        User = user;
        IsOfflineMode = false;
    }

    public void SetOfflineSession(UserSessionDto user)
    {
        Token = null;
        User = user;
        IsOfflineMode = true;
    }

    public void Clear()
    {
        Token = null;
        User = null;
        IsOfflineMode = false;
    }

    public bool HasPermission(string permissionName)
    {
        if (User is null) return false;
        if (User.Role == "Admin") return true;
        return User.Permissions.Contains(permissionName);
    }
}
