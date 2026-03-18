namespace SalesMaster.Server.Api.Services;

public sealed class RevisionClock
{
    private long _current;
    private readonly object _lock = new();

    public void Initialize(long maxExisting)
    {
        lock (_lock)
        {
            _current = Math.Max(_current, maxExisting);
        }
    }

    public long NextRevision()
    {
        lock (_lock)
        {
            var candidate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (candidate <= _current)
            {
                candidate = _current + 1;
            }

            _current = candidate;
            return _current;
        }
    }
}
