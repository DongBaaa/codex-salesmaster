using GeoraePlan.Mobile.App.Services;

namespace GeoraePlan.Mobile.App.ViewModels;

internal sealed class MobileOwnerOperationGate
{
    private readonly SessionStore _sessionStore;
    private MobileSessionOwner? _visibleOwner;
    private long _operationToken;
    private bool _isBusy;
    private bool _deferredRefreshRequested;

    public MobileOwnerOperationGate(SessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    public bool IsBusy => _isBusy;

    public MobileSessionOwner EnsureCurrentOwner(Action resetForOwner)
    {
        var owner = _sessionStore.CaptureOwner();
        return EnsureCurrentOwner(owner, resetForOwner);
    }

    public MobileSessionOwner EnsureCurrentOwner(
        MobileSessionOwner owner,
        Action resetForOwner)
    {
        if (_visibleOwner is not null &&
            OwnersMatch(_visibleOwner, owner))
        {
            return owner;
        }

        _visibleOwner = owner;
        _operationToken++;
        _isBusy = false;
        _deferredRefreshRequested = false;
        resetForOwner();
        return owner;
    }

    public MobileOwnerUiOperation? TryBegin(
        Action resetForOwner,
        bool deferRefreshWhenBusy)
    {
        var owner = EnsureCurrentOwner(resetForOwner);
        return TryBegin(
            owner,
            resetForOwner,
            deferRefreshWhenBusy);
    }

    public MobileOwnerUiOperation? TryBegin(
        MobileSessionOwner owner,
        Action resetForOwner,
        bool deferRefreshWhenBusy)
    {
        if (!_sessionStore.IsOwnerCurrent(owner))
            return null;

        EnsureCurrentOwner(owner, resetForOwner);
        if (_isBusy)
        {
            if (deferRefreshWhenBusy)
                _deferredRefreshRequested = true;
            return null;
        }

        _isBusy = true;
        return new MobileOwnerUiOperation(
            ++_operationToken,
            owner);
    }

    public async Task<MobileOwnerUiOperation?> TryBeginAsync(
        MobileSessionOwner owner,
        Action resetForOwner,
        bool deferRefreshWhenBusy,
        Action startCurrentUi,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resetForOwner);
        ArgumentNullException.ThrowIfNull(startCurrentUi);
        IDisposable ownerLease;
        try
        {
            ownerLease =
                await _sessionStore.AcquireOwnerCommitLeaseAsync(
                    owner,
                    ct);
        }
        catch (StaleMobileSessionOwnerException)
        {
            return null;
        }

        using (ownerLease)
        {
            EnsureCurrentOwner(
                owner,
                resetForOwner);
            if (_isBusy)
            {
                if (deferRefreshWhenBusy)
                    _deferredRefreshRequested = true;
                return null;
            }

            _isBusy = true;
            var operation = new MobileOwnerUiOperation(
                ++_operationToken,
                owner);
            startCurrentUi();
            return operation;
        }
    }

    public async Task<T?> AwaitExternalResultAsync<T>(
        MobileOwnerUiOperation operation,
        Func<Task<T?>> externalOperation)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(externalOperation);
        if (!CanCommit(operation))
        {
            throw new StaleMobileSessionOwnerException(
                "The mobile owner operation changed before the external operation started.");
        }

        var result = await externalOperation();
        _sessionStore.ThrowIfOwnerChanged(
            operation.Owner);
        if (!CanCommit(operation))
        {
            throw new StaleMobileSessionOwnerException(
                "The mobile owner operation changed while the external operation was open.");
        }

        return result;
    }

    public bool CanCommit(MobileOwnerUiOperation operation)
        => _operationToken == operation.Token &&
           _visibleOwner is not null &&
           OwnersMatch(_visibleOwner, operation.Owner) &&
           _sessionStore.IsOwnerCurrent(operation.Owner);

    public async Task<bool> TryCommitAsync(
        MobileOwnerUiOperation operation,
        Action mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(mutation);
        IDisposable ownerLease;
        try
        {
            ownerLease =
                await _sessionStore.AcquireOwnerCommitLeaseAsync(
                    operation.Owner,
                    ct);
        }
        catch (StaleMobileSessionOwnerException)
        {
            return false;
        }

        using (ownerLease)
        {
            if (!CanCommitUnderOwnerLease(operation))
                return false;

            mutation();
            return true;
        }
    }

    public async Task<Task?> TryStartCallbackAsync(
        MobileOwnerUiOperation operation,
        Func<Task> callbackStart,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(callbackStart);
        IDisposable ownerLease;
        try
        {
            ownerLease =
                await _sessionStore.AcquireOwnerCommitLeaseAsync(
                    operation.Owner,
                    ct);
        }
        catch (StaleMobileSessionOwnerException)
        {
            return null;
        }

        using (ownerLease)
        {
            if (!CanCommitUnderOwnerLease(operation))
                return null;

            // Invoke only. Never await arbitrary callback/navigation work
            // while holding the non-reentrant session owner lease.
            return callbackStart();
        }
    }

    public MobileOwnerCallbackContext CreateCallbackContext(
        MobileOwnerUiOperation operation)
        => new(
            mutation => TryCommitAsync(
                operation,
                mutation));

    public async Task<bool> CompleteAsync(
        MobileOwnerUiOperation operation,
        Action<bool> commitCurrentUi,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(commitCurrentUi);
        IDisposable ownerLease;
        try
        {
            ownerLease =
                await _sessionStore.AcquireOwnerCommitLeaseAsync(
                    operation.Owner,
                    ct);
        }
        catch (StaleMobileSessionOwnerException)
        {
            return false;
        }

        using (ownerLease)
        {
            if (!CanCommitUnderOwnerLease(operation))
                return false;

            _isBusy = false;
            var shouldRunDeferredRefresh =
                _deferredRefreshRequested;
            _deferredRefreshRequested = false;
            commitCurrentUi(shouldRunDeferredRefresh);
            return shouldRunDeferredRefresh;
        }
    }

    public bool Complete(
        MobileOwnerUiOperation operation,
        Action resetForOwner)
    {
        EnsureCurrentOwner(resetForOwner);
        if (_operationToken != operation.Token ||
            _visibleOwner is null ||
            !OwnersMatch(_visibleOwner, operation.Owner))
        {
            return false;
        }

        _isBusy = false;
        if (!_deferredRefreshRequested ||
            !_sessionStore.IsOwnerCurrent(operation.Owner))
        {
            return false;
        }

        _deferredRefreshRequested = false;
        return true;
    }

    public bool IsCurrent(MobileSessionOwner owner)
        => _visibleOwner is not null &&
           OwnersMatch(_visibleOwner, owner) &&
           _sessionStore.IsOwnerCurrent(owner);

    private bool CanCommitUnderOwnerLease(
        MobileOwnerUiOperation operation)
        => _operationToken == operation.Token &&
           _visibleOwner is not null &&
           OwnersMatch(_visibleOwner, operation.Owner) &&
           _sessionStore.IsOwnerCurrent(operation.Owner);

    private static bool OwnersMatch(
        MobileSessionOwner left,
        MobileSessionOwner right)
        => left.IsAuthenticated == right.IsAuthenticated &&
           string.Equals(
               left.Username,
               right.Username,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               left.TenantCode,
               right.TenantCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               left.OfficeCode,
               right.OfficeCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               left.SessionGeneration,
               right.SessionGeneration,
               StringComparison.Ordinal);
}

internal sealed record MobileOwnerUiOperation(
    long Token,
    MobileSessionOwner Owner);

public sealed class MobileOwnerCallbackContext
{
    private readonly Func<Action, Task<bool>> _tryCommit;

    internal MobileOwnerCallbackContext(
        Func<Action, Task<bool>> tryCommit)
    {
        _tryCommit = tryCommit;
    }

    public Task<bool> TryCommitAsync(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return _tryCommit(mutation);
    }
}
