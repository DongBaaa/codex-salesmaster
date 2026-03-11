namespace SalesMaster.Desktop.App.Services;

public sealed class OfficeMutationResult
{
    public bool Success { get; init; }
    public bool PermissionDenied { get; init; }
    public bool NotFound { get; init; }
    public bool GrantedTemporaryAccess { get; init; }
    public Guid EntityId { get; init; }
    public string Message { get; init; } = string.Empty;

    public static OfficeMutationResult Ok(Guid entityId = default, string message = "", bool grantedTemporaryAccess = false) => new()
    {
        Success = true,
        EntityId = entityId,
        Message = message,
        GrantedTemporaryAccess = grantedTemporaryAccess
    };

    public static OfficeMutationResult Denied(string message) => new()
    {
        PermissionDenied = true,
        Message = message
    };

    public static OfficeMutationResult Missing(string message) => new()
    {
        NotFound = true,
        Message = message
    };
}
