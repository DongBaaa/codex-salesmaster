namespace 거래플랜.Server.Api.Services;

public sealed class DatabaseInitializationState
{
    private readonly object _gate = new();
    private DateTime? _startedAtUtc;
    private DateTime? _completedAtUtc;
    private DateTime? _failedAtUtc;
    private string _errorMessage = string.Empty;

    public void MarkStarted()
    {
        lock (_gate)
        {
            _startedAtUtc = DateTime.UtcNow;
            _completedAtUtc = null;
            _failedAtUtc = null;
            _errorMessage = string.Empty;
        }
    }

    public void MarkCompleted()
    {
        lock (_gate)
        {
            _completedAtUtc = DateTime.UtcNow;
            _failedAtUtc = null;
            _errorMessage = string.Empty;
        }
    }

    public void MarkFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate)
        {
            _failedAtUtc = DateTime.UtcNow;
            _errorMessage = exception.Message;
        }
    }

    public DatabaseInitializationSnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            return new DatabaseInitializationSnapshot(
                Started: _startedAtUtc.HasValue,
                Completed: _completedAtUtc.HasValue,
                Failed: _failedAtUtc.HasValue,
                StartedAtUtc: _startedAtUtc,
                CompletedAtUtc: _completedAtUtc,
                FailedAtUtc: _failedAtUtc,
                ErrorMessage: _errorMessage);
        }
    }
}

public sealed record DatabaseInitializationSnapshot(
    bool Started,
    bool Completed,
    bool Failed,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? FailedAtUtc,
    string ErrorMessage);
