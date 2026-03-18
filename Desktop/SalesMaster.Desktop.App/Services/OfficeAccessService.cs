namespace SalesMaster.Desktop.App.Services;

public sealed class OfficeAccessService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Dictionary<Guid, DateOnly>> _sessionCustomerAccess = new();

    public bool HasTemporaryCustomerAccess(SessionState session, Guid customerId)
    {
        if (session is null)
            return false;

        lock (_gate)
        {
            PurgeExpiredEntries_NoLock(session.SessionId);
            return _sessionCustomerAccess.TryGetValue(session.SessionId, out var grants) &&
                   grants.ContainsKey(customerId);
        }
    }

    public IReadOnlySet<Guid> GetTemporaryCustomerAccessIds(SessionState session)
    {
        if (session is null)
            return new HashSet<Guid>();

        lock (_gate)
        {
            PurgeExpiredEntries_NoLock(session.SessionId);
            return _sessionCustomerAccess.TryGetValue(session.SessionId, out var grants)
                ? grants.Keys.ToHashSet()
                : new HashSet<Guid>();
        }
    }

    public void GrantTemporaryCustomerAccess(SessionState session, Guid customerId)
    {
        if (session is null || customerId == Guid.Empty)
            return;

        var expiresOn = DateOnly.FromDateTime(DateTime.Now);
        lock (_gate)
        {
            PurgeExpiredEntries_NoLock(session.SessionId);
            if (!_sessionCustomerAccess.TryGetValue(session.SessionId, out var grants))
            {
                grants = new Dictionary<Guid, DateOnly>();
                _sessionCustomerAccess[session.SessionId] = grants;
            }

            grants[customerId] = expiresOn;
        }
    }

    public void RevokeTemporaryCustomerAccess(SessionState session, Guid customerId)
    {
        if (session is null || customerId == Guid.Empty)
            return;

        lock (_gate)
        {
            if (!_sessionCustomerAccess.TryGetValue(session.SessionId, out var grants))
                return;

            grants.Remove(customerId);
            if (grants.Count == 0)
                _sessionCustomerAccess.Remove(session.SessionId);
        }
    }

    private void PurgeExpiredEntries_NoLock(Guid sessionId)
    {
        if (!_sessionCustomerAccess.TryGetValue(sessionId, out var grants))
            return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var expired = grants
            .Where(pair => pair.Value != today)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var customerId in expired)
            grants.Remove(customerId);

        if (grants.Count == 0)
            _sessionCustomerAccess.Remove(sessionId);
    }
}
