namespace 거래플랜.Server.Api.Services;

public interface ICurrentUserContext
{
    Guid? UserId { get; }
    string Username { get; }
    bool IsAdmin { get; }
    bool HasPermission(string permission);
}
