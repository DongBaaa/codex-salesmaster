using System.Windows;
using System.Windows.Threading;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal sealed record DesktopCompatibilityLatchSnapshot(
    bool IsBlocked,
    DesktopCompatibilityEvidenceState EvidenceState,
    DesktopCompatibilityEvidence? Evidence,
    long Revision,
    string DiagnosticCode)
{
    public bool CanMutate => !IsBlocked;
}

public interface IDesktopCompatibilityRuntime
{
    bool CanMutate { get; }
    event Action<bool>? MutationAvailabilityChanged;
}

internal sealed class DesktopCompatibilityLatch
    : IDesktopCompatibilityRuntime
{
    private readonly object _gate = new();
    private DesktopCompatibilityLatchSnapshot _snapshot =
        new(
            false,
            DesktopCompatibilityEvidenceState.None,
            null,
            0,
            "none");

    public event Action<
        DesktopCompatibilityLatchSnapshot>? Changed;
    public event Action<bool>? MutationAvailabilityChanged;

    public DesktopCompatibilityLatchSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public bool CanMutate => Snapshot.CanMutate;

    public DesktopCompatibilityLatchSnapshot Activate(
        DesktopCompatibilityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        DesktopCompatibilityLatchSnapshot next;
        var changed = false;
        lock (_gate)
        {
            var merged =
                _snapshot.Evidence is null
                    ? evidence
                    : DesktopCompatibilityPolicy.Merge(
                        _snapshot.Evidence,
                        evidence);
            changed =
                !_snapshot.IsBlocked ||
                _snapshot.EvidenceState !=
                DesktopCompatibilityEvidenceState.Valid ||
                !Equals(
                    merged,
                    _snapshot.Evidence);
            next = new DesktopCompatibilityLatchSnapshot(
                true,
                DesktopCompatibilityEvidenceState.Valid,
                merged,
                changed
                    ? checked(_snapshot.Revision + 1)
                    : _snapshot.Revision,
                "required");
            _snapshot = next;
        }

        if (changed)
        {
            NotifySubscribers(
                Changed,
                next,
                nameof(Changed));
            NotifySubscribers(
                MutationAvailabilityChanged,
                next.CanMutate,
                nameof(MutationAvailabilityChanged));
        }
        return next;
    }

    private static void NotifySubscribers<T>(
        Action<T>? subscribers,
        T value,
        string eventName)
    {
        if (subscribers is null)
            return;

        foreach (Action<T> subscriber
                 in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(value);
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "UPDATE",
                    $"Desktop compatibility latch {eventName} subscriber failed.",
                    ex);
            }
        }
    }

    public DesktopCompatibilityLatchSnapshot ActivateUnreadable(
        string diagnosticCode)
    {
        DesktopCompatibilityLatchSnapshot next;
        var changed = false;
        lock (_gate)
        {
            if (_snapshot.IsBlocked &&
                _snapshot.EvidenceState ==
                DesktopCompatibilityEvidenceState.Valid &&
                _snapshot.Evidence is not null)
            {
                return _snapshot;
            }

            changed =
                !_snapshot.IsBlocked ||
                _snapshot.EvidenceState !=
                DesktopCompatibilityEvidenceState.Unreadable;
            next = new DesktopCompatibilityLatchSnapshot(
                true,
                DesktopCompatibilityEvidenceState.Unreadable,
                null,
                changed
                    ? checked(_snapshot.Revision + 1)
                    : _snapshot.Revision,
                string.IsNullOrWhiteSpace(diagnosticCode)
                    ? "unreadable"
                    : diagnosticCode);
            _snapshot = next;
        }

        if (changed)
        {
            Changed?.Invoke(next);
            MutationAvailabilityChanged?.Invoke(
                next.CanMutate);
        }
        return next;
    }

    public void ClearAfterDurableSuccess()
    {
        DesktopCompatibilityLatchSnapshot next;
        lock (_gate)
        {
            next = new DesktopCompatibilityLatchSnapshot(
                false,
                DesktopCompatibilityEvidenceState.None,
                null,
                checked(_snapshot.Revision + 1),
                "cleared");
            _snapshot = next;
        }

        Changed?.Invoke(next);
        MutationAvailabilityChanged?.Invoke(
            next.CanMutate);
    }
}

internal sealed class DesktopUpgradeRequiredSignal
{
    private readonly object _gate = new();
    private long _lastPublishedRevision;

    public event Action<
        DesktopCompatibilityLatchSnapshot>? UpgradeRequired;

    public async Task PublishAsync(
        DesktopCompatibilityLatchSnapshot snapshot)
    {
        if (!snapshot.IsBlocked)
            return;

        lock (_gate)
        {
            if (snapshot.Revision <=
                _lastPublishedRevision)
            {
                return;
            }

            _lastPublishedRevision =
                snapshot.Revision;
        }

        var dispatcher =
            Application.Current?.Dispatcher;
        if (dispatcher is null ||
            dispatcher.CheckAccess())
        {
            UpgradeRequired?.Invoke(snapshot);
            return;
        }

        await dispatcher.InvokeAsync(
            () => UpgradeRequired?.Invoke(snapshot),
            DispatcherPriority.Send);
    }
}

public interface IDesktopUpgradeRequiredObserver
{
    Task ObserveAsync(
        DesktopClientUpgradeRequiredException exception,
        CancellationToken ct = default);
}

internal sealed class DesktopUpgradeRequiredObserver
    : IDesktopUpgradeRequiredObserver
{
    private readonly DesktopCompatibilityLatch _latch;
    private readonly DesktopCompatibilityEvidenceStore _store;
    private readonly DesktopClientIdentityProvider _identity;
    private readonly DesktopUpgradeRequiredSignal _signal;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopUpgradeRequiredObserver(
        DesktopCompatibilityLatch latch,
        DesktopCompatibilityEvidenceStore store,
        DesktopClientIdentityProvider identity,
        DesktopUpgradeRequiredSignal signal)
        : this(
            latch,
            store,
            identity,
            signal,
            static () => DateTime.UtcNow)
    {
    }

    internal DesktopUpgradeRequiredObserver(
        DesktopCompatibilityLatch latch,
        DesktopCompatibilityEvidenceStore store,
        DesktopClientIdentityProvider identity,
        DesktopUpgradeRequiredSignal signal,
        Func<DateTime> utcNow)
    {
        _latch = latch;
        _store = store;
        _identity = identity;
        _signal = signal;
        _utcNow = utcNow;
    }

    public async Task ObserveAsync(
        DesktopClientUpgradeRequiredException exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        await _gate.WaitAsync(
                CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            var incoming =
                DesktopCompatibilityPolicy.From426(
                    exception,
                    _identity.GetRuntimeIdentity(),
                    _utcNow());
            var snapshot = _latch.Activate(incoming);

            try
            {
                await _store.PersistAsync(
                        snapshot.Evidence!,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception persistException)
            {
                AppLogger.Error(
                    "UPDATE",
                    "Desktop compatibility evidence persistence failed closed.",
                    persistException);
            }

            try
            {
                await _signal.PublishAsync(snapshot)
                    .ConfigureAwait(false);
            }
            catch (Exception signalException)
            {
                AppLogger.Error(
                    "UPDATE",
                    "Desktop compatibility UI signal failed after latching.",
                    signalException);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed record DesktopCompatibilityGateDecision(
    bool IsBlocked,
    string DiagnosticCode,
    DesktopCompatibilityEvidence? Evidence,
    AppUpdatePackageDto? VerifiedPackage)
{
    public bool CanStart => !IsBlocked;
}

internal sealed class DesktopCompatibilityGateService
{
    private const string StableChannel = "stable";
    private readonly DesktopCompatibilityEvidenceStore _store;
    private readonly DesktopCompatibilityLatch _latch;
    private readonly DesktopClientIdentityProvider _identity;
    private readonly ErpApiClient _api;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopCompatibilityGateService(
        DesktopCompatibilityEvidenceStore store,
        DesktopCompatibilityLatch latch,
        DesktopClientIdentityProvider identity,
        ErpApiClient api)
    {
        _store = store;
        _latch = latch;
        _identity = identity;
        _api = api;
    }

    public async Task<DesktopCompatibilityGateDecision>
        CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var loaded = await _store.LoadAsync(ct)
                .ConfigureAwait(false);
            if (loaded.State ==
                DesktopCompatibilityEvidenceState.Valid)
            {
                _latch.Activate(loaded.Evidence!);
            }
            else if (loaded.State ==
                     DesktopCompatibilityEvidenceState
                         .Unreadable)
            {
                var recovered =
                    _latch.ActivateUnreadable(
                    loaded.DiagnosticCode);
                if (recovered.Evidence is not null)
                {
                    try
                    {
                        await _store.PersistAsync(
                                recovered.Evidence,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(
                            "UPDATE",
                            "Desktop compatibility evidence recovery persist failed closed.",
                            ex);
                    }
                }
            }

            var runtime = _identity.GetRuntimeIdentity();
            var snapshot = _latch.Snapshot;
            if (snapshot.Evidence is not null &&
                DesktopCompatibilityPolicy
                    .RuntimeStrictlyAdvancedWithoutRegression(
                        snapshot.Evidence,
                        runtime) &&
                DesktopCompatibilityPolicy.RuntimeSatisfies(
                    snapshot.Evidence,
                    runtime))
            {
                if (await TryClearAsync(ct).ConfigureAwait(false))
                {
                    return new DesktopCompatibilityGateDecision(
                        false,
                        "runtime-advanced",
                        null,
                        null);
                }
            }

            AppUpdateManifestDto? manifest = null;
            Exception? manifestFailure = null;
            try
            {
                manifest = await _api
                    .GetUpdateManifestAsync(
                        StableChannel,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                manifestFailure = ex;
            }

            var verification =
                manifestFailure is null
                    ? DesktopStablePolicyVerifier.Verify(
                        manifest,
                        _api.GetBaseUri())
                    : new DesktopStablePolicyVerificationResult(
                        false,
                        null,
                        "network");

            snapshot = _latch.Snapshot;
            if (!snapshot.IsBlocked)
            {
                if (!verification.IsVerified)
                {
                    return new DesktopCompatibilityGateDecision(
                        false,
                        verification.DiagnosticCode,
                        null,
                        null);
                }

                var stable = verification.Policy!;
                if (!stable.RequiresUserAction ||
                    DesktopCompatibilityPolicy
                        .RuntimeSatisfies(stable, runtime))
                {
                    return new DesktopCompatibilityGateDecision(
                        false,
                        "compatible",
                        null,
                        stable.Package);
                }

                var evidence =
                    CreateEvidenceFromStablePolicy(
                        stable,
                        runtime);
                var activated = _latch.Activate(evidence);
                try
                {
                    await _store.PersistAsync(
                            activated.Evidence!,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The in-memory latch remains fail-closed.
                }

                return new DesktopCompatibilityGateDecision(
                    true,
                    "stable-required",
                    activated.Evidence,
                    stable.Package);
            }

            if (verification.IsVerified &&
                CanVerifiedPolicyClear(
                    snapshot,
                    verification.Policy!,
                    runtime) &&
                await TryClearAsync(ct).ConfigureAwait(false))
            {
                return new DesktopCompatibilityGateDecision(
                    false,
                    "newer-policy-compatible",
                    null,
                    null);
            }

            return new DesktopCompatibilityGateDecision(
                true,
                verification.IsVerified
                    ? "required"
                    : verification.DiagnosticCode,
                snapshot.Evidence,
                verification.Policy?.Package);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool CanVerifiedPolicyClear(
        DesktopCompatibilityLatchSnapshot snapshot,
        DesktopVerifiedStablePolicy stable,
        DesktopClientRuntimeIdentity runtime)
    {
        if (!DesktopCompatibilityPolicy
                .RuntimeSatisfies(stable, runtime))
        {
            return false;
        }

        if (snapshot.EvidenceState ==
            DesktopCompatibilityEvidenceState.Unreadable)
        {
            return true;
        }

        var evidence = snapshot.Evidence;
        return evidence is not null &&
               evidence.Kind ==
               DesktopCompatibilityEvidenceKind.Verified426 &&
               stable.PolicyVersion >
               evidence.PolicyVersion;
    }

    private async Task<bool> TryClearAsync(
        CancellationToken ct)
    {
        try
        {
            await _store.ClearAsync(ct)
                .ConfigureAwait(false);
            _latch.ClearAfterDurableSuccess();
            return true;
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "UPDATE",
                "Desktop compatibility evidence clear failed closed.",
                ex);
            return false;
        }
    }

    private static DesktopCompatibilityEvidence
        CreateEvidenceFromStablePolicy(
            DesktopVerifiedStablePolicy stable,
            DesktopClientRuntimeIdentity runtime)
        => new()
        {
            Kind =
                DesktopCompatibilityEvidenceKind.Verified426,
            PolicyVersion = stable.PolicyVersion,
            MinimumVersion = stable.MinimumVersion,
            MinimumBuild = stable.MinimumBuild,
            MinimumProtocolVersion =
                stable.MinimumProtocolVersion,
            LatestVersion = stable.LatestVersion,
            LatestBuild = stable.LatestBuild,
            ObservedVersion = runtime.Version,
            ObservedBuild = runtime.Build,
            ObservedProtocolVersion =
                runtime.ProtocolVersion,
            ObservedAtUtc = DateTime.UtcNow
        };
}
