using System.Security.Claims;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

public sealed class HttpCurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public string Username =>
        _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? "anonymous";

    public string TenantCode =>
        TenantScopeCatalog.NormalizeTenantCodeOrDefault(_httpContextAccessor.HttpContext?.User.FindFirstValue("tenant"));

    public string OfficeCode =>
        OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_httpContextAccessor.HttpContext?.User.FindFirstValue("office"));

    public string ScopeType =>
        TenantScopeCatalog.NormalizeScopeTypeOrDefault(_httpContextAccessor.HttpContext?.User.FindFirstValue("scope"));

    public bool IsAdmin =>
        _httpContextAccessor.HttpContext?.User.IsInRole("Admin") ?? false;

    public bool HasPermission(string permission)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null) return false;
        return user.IsInRole("Admin") ||
               user.Claims.Any(c => c.Type == "perm" && c.Value == permission);
    }
}
