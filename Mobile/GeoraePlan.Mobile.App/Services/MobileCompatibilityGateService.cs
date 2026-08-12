namespace GeoraePlan.Mobile.App.Services;

internal sealed class MobileCompatibilityGateOutcome
{
    public bool IsBlocked { get; init; }
    public bool NetworkUnavailable { get; init; }
    public string Source { get; init; } = string.Empty;
    public string StatusMessage { get; init; } = string.Empty;
    public MobileAppUpdateCheckResult Update { get; init; } = new();
}

public sealed class MobileCompatibilityGateService
{
    private static readonly TimeSpan DefaultFreshnessWindow =
        TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<MobileAppUpdateCheckResult>>
        _checkForUpdates;
    private readonly MobileClientRuntimeIdentity _identity;
    private readonly IMobileUpdateGatePolicyStore _policyStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _freshnessWindow;
    private readonly object _flightGate = new();
    private readonly SemaphoreSlim _policyLock = new(1, 1);
    private Task<MobileCompatibilityGateOutcome>? _inFlightCheck;
    private LatestOutcomeSnapshot? _latestSnapshot;

    internal MobileCompatibilityGateService(
        MobileAppUpdateService updateService,
        MobileClientIdentityProvider identityProvider)
        : this(
            CreateUpdateCheck(updateService),
            (identityProvider ??
             throw new ArgumentNullException(nameof(identityProvider)))
                .GetRuntimeIdentity(),
            new MobileUpdateGatePolicyStore())
    {
    }

    internal MobileCompatibilityGateService(
        Func<CancellationToken, Task<MobileAppUpdateCheckResult>> checkForUpdates,
        MobileClientRuntimeIdentity identity,
        IMobileUpdateGatePolicyStore policyStore)
        : this(
            checkForUpdates,
            identity,
            policyStore,
            static () => DateTimeOffset.UtcNow,
            DefaultFreshnessWindow)
    {
    }

    internal MobileCompatibilityGateService(
        Func<CancellationToken, Task<MobileAppUpdateCheckResult>> checkForUpdates,
        MobileClientRuntimeIdentity identity,
        IMobileUpdateGatePolicyStore policyStore,
        Func<DateTimeOffset> utcNow,
        TimeSpan freshnessWindow)
    {
        _checkForUpdates = checkForUpdates ??
            throw new ArgumentNullException(nameof(checkForUpdates));
        _identity = identity ??
            throw new ArgumentNullException(nameof(identity));
        _policyStore = policyStore ??
            throw new ArgumentNullException(nameof(policyStore));
        _utcNow = utcNow ??
            throw new ArgumentNullException(nameof(utcNow));
        if (freshnessWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(freshnessWindow));
        _freshnessWindow = freshnessWindow;
    }

    public bool IsBlocking
        => Volatile.Read(ref _latestSnapshot)?.Outcome.IsBlocked == true;

    internal MobileCompatibilityGateOutcome? LatestOutcome
        => Volatile.Read(ref _latestSnapshot)?.Outcome;

    internal Task<MobileCompatibilityGateOutcome> CheckAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        Task<MobileCompatibilityGateOutcome> shared;
        lock (_flightGate)
        {
            if (!forceRefresh &&
                _inFlightCheck is null &&
                TryGetFreshOutcome(out var fresh))
            {
                return Task.FromResult(fresh!);
            }

            shared = _inFlightCheck ??= RunSharedCheckAsync();
        }

        return AwaitSharedAsync(shared, ct);
    }

    private bool TryGetFreshOutcome(
        out MobileCompatibilityGateOutcome? outcome)
    {
        var snapshot = Volatile.Read(ref _latestSnapshot);
        outcome = snapshot?.Outcome;
        if (snapshot is null)
            return false;

        var latestTicks = snapshot.ObservedAtUtcTicks;
        if (latestTicks <= 0)
            return false;
        var age = _utcNow() -
                  new DateTimeOffset(
                      latestTicks,
                      TimeSpan.Zero);
        return age >= TimeSpan.Zero &&
               age <= _freshnessWindow;
    }

    internal async Task<MobileCompatibilityGateOutcome> ActivateAsync(
        MobileClientUpgradeRequiredException exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        await _policyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ActivateWhileLockedAsync(exception, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _policyLock.Release();
        }
    }

    private async Task<MobileCompatibilityGateOutcome>
        ActivateWhileLockedAsync(
            MobileClientUpgradeRequiredException exception,
            CancellationToken ct)
    {
        var update = MobileUpdateGatePolicy.EvaluateUpgradeRequired(
            exception.Response,
            _identity);
        var incoming =
            MobileUpdateGatePolicy.CreateCachedRequirement(update);
        var latest = LatestOutcome;
        if (ShouldKeepLatestOutcome(latest, update, incoming))
            return latest!;

        if (incoming is not null)
        {
            var cachedLoad =
                await LoadCachedPolicySafeAsync(ct).ConfigureAwait(false);
            var cached = cachedLoad.Requirement;
            var requirementToPersist =
                cached is null
                    ? incoming
                    : MobileUpdateGatePolicy
                        .ResolveRequiredEvidenceForPersistence(
                            incoming,
                            cached);
            if (!ReferenceEquals(requirementToPersist, cached))
            {
                await SaveCachedPolicySafeAsync(requirementToPersist, ct)
                    .ConfigureAwait(false);
            }

            if (!ReferenceEquals(requirementToPersist, incoming))
            {
                update =
                    MobileUpdateGatePolicy.FromCached(
                        requirementToPersist,
                        _identity);
            }
        }

        var outcome = new MobileCompatibilityGateOutcome
        {
            IsBlocked = true,
            Source = "server-426",
            StatusMessage = update.Message,
            Update = update
        };
        return SetLatest(outcome);
    }

    private async Task<MobileCompatibilityGateOutcome> RunSharedCheckAsync()
    {
        // Ensure CheckAsync assigns the shared task before the cleanup path can
        // clear it, even when every dependency completes synchronously.
        await Task.Yield();

        try
        {
            return await CheckCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_flightGate)
            {
                _inFlightCheck = null;
            }
        }
    }

    private async Task<MobileCompatibilityGateOutcome> CheckCoreAsync()
    {
        await _policyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cachedLoad =
                await LoadCachedPolicySafeAsync().ConfigureAwait(false);
            var cached = cachedLoad.Requirement;
            if (cached is not null &&
                !MobileUpdateGatePolicy.IsRequiredFor(cached, _identity))
            {
                await ClearCachedPolicySafeAsync().ConfigureAwait(false);
                cached = null;
                cachedLoad = MobileUpdateGatePolicyLoadResult.Absent();
            }

            try
            {
                var update = await _checkForUpdates(
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (update.ManifestVerified)
                {
                    var latest = LatestOutcome;
                    var inMemoryRequirement =
                        latest?.IsBlocked == true
                            ? MobileUpdateGatePolicy.CreateCachedRequirement(
                                latest.Update)
                            : null;
                    if (inMemoryRequirement is not null &&
                        MobileUpdateGatePolicy.IsRequiredFor(
                            inMemoryRequirement,
                            _identity))
                    {
                        var incomingRequirement =
                            update.RequiresImmediateUpdate
                                ? MobileUpdateGatePolicy.CreateCachedRequirement(
                                    update)
                                : null;
                        if (update.RequiresImmediateUpdate &&
                            ShouldKeepLatestOutcome(
                                latest,
                                update,
                                incomingRequirement))
                        {
                            return SetLatest(latest!);
                        }

                        if (!update.RequiresImmediateUpdate &&
                            !MobileUpdateGatePolicy.CanVerifiedCompatibleResultClear(
                                inMemoryRequirement,
                                update))
                        {
                            var requiredUpdate =
                                MobileUpdateGatePolicy.FromCached(
                                    inMemoryRequirement,
                                    _identity);
                            return SetLatest(
                                new MobileCompatibilityGateOutcome
                                {
                                    IsBlocked = true,
                                    Source =
                                        "in-memory-required-policy",
                                    StatusMessage =
                                        "A newer required-update decision was already received during this run.",
                                    Update = requiredUpdate
                                });
                        }
                    }

                    if (update.RequiresImmediateUpdate)
                    {
                        var incoming =
                            MobileUpdateGatePolicy.CreateCachedRequirement(update);
                        if (incoming is not null)
                        {
                            var requirementToPersist =
                                cached is null
                                    ? incoming
                                    : MobileUpdateGatePolicy
                                        .ResolveRequiredEvidenceForPersistence(
                                            incoming,
                                            cached);
                            if (!ReferenceEquals(
                                    requirementToPersist,
                                    cached))
                            {
                                await SaveCachedPolicySafeAsync(
                                        requirementToPersist)
                                    .ConfigureAwait(false);
                            }

                            cached = requirementToPersist;
                        }

                        if (cached is not null &&
                            MobileUpdateGatePolicy.IsRequiredFor(cached, _identity) &&
                            incoming is not null &&
                            !ReferenceEquals(cached, incoming))
                        {
                            update = MobileUpdateGatePolicy.FromCached(
                                cached,
                                _identity);
                        }

                        return SetLatest(new MobileCompatibilityGateOutcome
                        {
                            IsBlocked = true,
                            Source = "verified-manifest",
                            StatusMessage = update.Message,
                            Update = update
                        });
                    }

                    if (cachedLoad.Status ==
                        MobileUpdateGatePolicyLoadStatus.Unreadable)
                    {
                        if (!await ClearCachedPolicySafeAsync()
                                .ConfigureAwait(false))
                        {
                            return CreateUnreadablePolicyBlock(
                                update,
                                "A verified compatible response was received, but unreadable required-update evidence could not be cleared.");
                        }

                        cachedLoad =
                            MobileUpdateGatePolicyLoadResult.Absent();
                    }

                    if (cached is not null &&
                        MobileUpdateGatePolicy.IsRequiredFor(cached, _identity))
                    {
                        if (MobileUpdateGatePolicy.CanVerifiedCompatibleResultClear(
                                cached,
                                update))
                        {
                            if (!await ClearCachedPolicySafeAsync()
                                    .ConfigureAwait(false))
                            {
                                var cachedUpdate =
                                    MobileUpdateGatePolicy.FromCached(
                                        cached,
                                        _identity);
                                return SetLatest(
                                    new MobileCompatibilityGateOutcome
                                    {
                                        IsBlocked = true,
                                        Source =
                                            "cached-policy-clear-failed",
                                        StatusMessage =
                                            "The verified compatibility result could not be persisted. This run remains blocked.",
                                        Update = cachedUpdate
                                    });
                            }

                            cached = null;
                        }
                        else
                        {
                            var cachedUpdate =
                                MobileUpdateGatePolicy.FromCached(cached, _identity);
                            return SetLatest(new MobileCompatibilityGateOutcome
                            {
                                IsBlocked = true,
                                Source = "cached-required-policy",
                                StatusMessage =
                                    "서버에서 더 새로운 해제 정책을 확인하지 못해 이전 필수 업데이트 정책을 유지합니다.",
                                Update = cachedUpdate
                            });
                        }
                    }

                    return SetLatest(new MobileCompatibilityGateOutcome
                    {
                        IsBlocked = false,
                        Source = "verified-manifest",
                        StatusMessage = update.Message,
                        Update = update
                    });
                }

                return ResolveUnavailable(
                    cachedLoad,
                    cached,
                    update,
                    update.VerificationFailure);
            }
            catch (MobileClientUpgradeRequiredException upgradeRequired)
            {
                // The global signal stops realtime immediately, but the
                // startup gate must also return blocked synchronously. This
                // closes the race where the signal handler is waiting on the
                // same policy lock while startup proceeds to the shell.
                return await ActivateWhileLockedAsync(
                        upgradeRequired,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MobileAppLogger.Warn(
                    "UPDATE",
                    $"호환성 매니페스트 확인 실패: {ex.Message}");
                return ResolveUnavailable(
                    cachedLoad,
                    cached,
                    CreateUnavailableUpdate(ex.Message),
                    ex.Message);
            }
        }
        finally
        {
            _policyLock.Release();
        }
    }

    private MobileCompatibilityGateOutcome ResolveUnavailable(
        MobileUpdateGatePolicyLoadResult cachedLoad,
        MobileCachedUpdateRequirement? cached,
        MobileAppUpdateCheckResult unavailable,
        string detail)
    {
        if (cached is not null &&
            MobileUpdateGatePolicy.IsRequiredFor(cached, _identity))
        {
            var cachedUpdate = MobileUpdateGatePolicy.FromCached(cached, _identity);
            return SetLatest(new MobileCompatibilityGateOutcome
            {
                IsBlocked = true,
                NetworkUnavailable = true,
                Source = "cached-required-policy",
                StatusMessage =
                    "새 정책을 확인하지 못했지만 이전에 검증된 필수 업데이트 정책을 유지합니다.",
                Update = cachedUpdate
            });
        }

        if (cachedLoad.Status ==
            MobileUpdateGatePolicyLoadStatus.Unreadable)
        {
            return CreateUnreadablePolicyBlock(
                unavailable,
                "Required-update evidence is present but unreadable. Reconnect and verify compatibility before continuing.",
                networkUnavailable: true);
        }

        var latest = LatestOutcome;
        if (latest is not null && latest.IsBlocked)
        {
            return SetLatest(new MobileCompatibilityGateOutcome
            {
                IsBlocked = true,
                NetworkUnavailable = true,
                Source = "in-memory-required-policy",
                StatusMessage =
                    "The current run has already received a required-update decision. Reconnect and verify compatibility before continuing.",
                Update = latest.Update
            });
        }

        return SetLatest(new MobileCompatibilityGateOutcome
        {
            IsBlocked = false,
            NetworkUnavailable = true,
            Source = "unavailable-no-cached-block",
            StatusMessage = string.IsNullOrWhiteSpace(detail)
                ? "업데이트 정책을 확인하지 못했지만 검증된 차단 정책이 없어 앱을 계속 실행합니다."
                : $"업데이트 정책을 확인하지 못했지만 검증된 차단 정책이 없어 앱을 계속 실행합니다. {detail}",
            Update = unavailable
        });
    }

    private MobileCompatibilityGateOutcome CreateUnreadablePolicyBlock(
        MobileAppUpdateCheckResult update,
        string message,
        bool networkUnavailable = false)
        => SetLatest(new MobileCompatibilityGateOutcome
        {
            IsBlocked = true,
            NetworkUnavailable = networkUnavailable,
            Source = "unreadable-required-policy",
            StatusMessage = message,
            Update = update
        });

    private MobileAppUpdateCheckResult CreateUnavailableUpdate(string detail)
        => new()
        {
            CurrentVersion = _identity.Version,
            CurrentBuild = _identity.Build,
            CurrentProtocolVersion = _identity.ProtocolVersion,
            LatestVersion = _identity.Version,
            ManifestVerified = false,
            VerificationFailure = detail,
            Message = detail
        };

    private static bool ShouldKeepLatestOutcome(
        MobileCompatibilityGateOutcome? latest,
        MobileAppUpdateCheckResult incomingUpdate,
        MobileCachedUpdateRequirement? incomingRequirement)
    {
        if (latest is null)
            return false;

        // An opaque 426 cannot be ordered against a manifest policy revision.
        // Treat the server's status code as authoritative for this run.
        if (incomingRequirement?.OpaqueServerEnforced == true)
            return false;

        if (!latest.IsBlocked &&
            latest.Update.ManifestVerified &&
            latest.Update.PolicyVersion > incomingUpdate.PolicyVersion)
        {
            return true;
        }

        if (!latest.IsBlocked)
            return false;

        var latestRequirement =
            MobileUpdateGatePolicy.CreateCachedRequirement(latest.Update);
        if (latestRequirement is not null &&
            incomingRequirement is not null &&
            MobileUpdateGatePolicy.IsIncomingRequirementAtLeastAsNew(
                latestRequirement,
                incomingRequirement))
        {
            return true;
        }

        return latestRequirement is null &&
               incomingRequirement is null &&
               string.Equals(
                   latest.Update.Message,
                   incomingUpdate.Message,
                   StringComparison.Ordinal);
    }

    private MobileCompatibilityGateOutcome SetLatest(
        MobileCompatibilityGateOutcome outcome)
    {
        Volatile.Write(
            ref _latestSnapshot,
            new LatestOutcomeSnapshot(
                outcome,
                _utcNow().UtcTicks));
        return outcome;
    }

    private sealed record LatestOutcomeSnapshot(
        MobileCompatibilityGateOutcome Outcome,
        long ObservedAtUtcTicks);

    private static async Task<MobileCompatibilityGateOutcome> AwaitSharedAsync(
        Task<MobileCompatibilityGateOutcome> shared,
        CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
            return await shared.ConfigureAwait(false);

        return await shared.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<MobileUpdateGatePolicyLoadResult>
        LoadCachedPolicySafeAsync(CancellationToken ct = default)
    {
        try
        {
            return await _policyStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn(
                "UPDATE",
                $"저장된 강제 업데이트 정책을 읽지 못했습니다: {ex.Message}");
            return MobileUpdateGatePolicyLoadResult.Unreadable(
                "policy-store-read-failed");
        }
    }

    private async Task<bool> SaveCachedPolicySafeAsync(
        MobileCachedUpdateRequirement requirement,
        CancellationToken ct = default)
    {
        try
        {
            await _policyStore.SaveAsync(requirement, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn(
                "UPDATE",
                $"검증된 강제 업데이트 정책을 저장하지 못했습니다: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ClearCachedPolicySafeAsync(
        CancellationToken ct = default)
    {
        try
        {
            await _policyStore.ClearAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn(
                "UPDATE",
                $"해제된 강제 업데이트 정책을 정리하지 못했습니다: {ex.Message}");
            return false;
        }
    }

    private static Func<CancellationToken, Task<MobileAppUpdateCheckResult>>
        CreateUpdateCheck(MobileAppUpdateService updateService)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        return updateService.CheckCompatibilityRecoveryAsync;
    }
}
