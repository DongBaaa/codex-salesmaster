namespace 거래플랜.Desktop.App.Services;

public sealed class LocalMutationResult
{
    public bool Success { get; init; }
    public bool PermissionDenied { get; init; }
    public bool NotFound { get; init; }
    public Guid EntityId { get; init; }
    public Guid RelatedEntityId { get; init; }
    public string Message { get; init; } = string.Empty;

    public static LocalMutationResult Ok(Guid entityId = default, string message = "", Guid relatedEntityId = default) => new()
    {
        Success = true,
        EntityId = entityId,
        RelatedEntityId = relatedEntityId,
        Message = message
    };

    public static LocalMutationResult Denied(string message) => new()
    {
        PermissionDenied = true,
        Message = message
    };

    public static LocalMutationResult Missing(string message) => new()
    {
        NotFound = true,
        Message = message
    };
}
