namespace GeoraePlan.Mobile.App.Services;

public sealed class SessionRecoveryResult
{
    public static SessionRecoveryResult SuccessResult(string message = "세션이 복구되었습니다.")
        => new() { Success = true, Message = message };

    public static SessionRecoveryResult FailureResult(string message)
        => new() { Success = false, Message = message };

    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}
