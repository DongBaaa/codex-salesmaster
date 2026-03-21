using System;

namespace GeoraePlan.Mobile.App.Models;

public sealed class SessionSnapshot
{
    public static SessionSnapshot Empty { get; } = new();

    public bool IsAuthenticated { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string TenantCode { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; init; }
    public bool IsAdmin => string.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase);
}
