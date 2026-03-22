namespace 거래플랜.Server.Api.Services;

public interface ICurrentUserContext
{
    Guid? UserId { get; }
    string Username { get; }
    string TenantCode { get; }
    string OfficeCode { get; }
    string ScopeType { get; }
    bool IsAdmin { get; }
    bool IsGodMode { get; }
    bool HasPermission(string permission);
}
