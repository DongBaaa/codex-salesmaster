using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Background sync service: push local dirty rows, then pull latest rows.
/// </summary>
public sealed record SyncScopeExecutionResult(
    string ScopeKey,
    string ScopeDisplayName,
    int PendingCountBefore,
    int PendingCountAfter,
    bool Attempted,
    bool Succeeded,
    bool UsedCurrentSession,
    bool UsedStoredCredential,
    string Message);

public sealed class SyncService : IDisposable
{
    internal const string OfficeSessionHttpClientName =
        "GeoraePlan.OfficeSession";

    private const int MaxRetryCount = 3;
    private const int PullQueryContainsBatchSize = 500;
    private const string DisableServerSyncEnvironmentKey = "GEORAEPLAN_DISABLE_SERVER_SYNC";
    private const string DeviceIdSettingKey = "Sync.DeviceId";
    private const string LastConflictSummarySettingKey = "Sync.LastConflictSummary";
    private const string ItemCatalogExtensionVersionSettingKey =
        "Sync.ItemCatalogExtensionVersion";
    private const string
        InventoryTransferStockAtomicityRollbackNoticeCode =
            "inventory-transfer-stock-atomicity-rollback";
    private const string
        InventoryTransferStockAtomicityRollbackOutboxErrorPrefix =
            "[inventory-transfer-stock-atomicity-rollback]";
    private static readonly TimeSpan AdministrativeBusinessCacheRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AdministrativeBusinessCachePullTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DebouncedSyncDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TransientFailureRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> EquivalentConflictIgnoredPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreatedAtUtc",
        "UpdatedAtUtc",
        "Revision",
        "ExpectedRevision",
        "MutationId",
        "MutationCreatedAtUtc",
        "FileContent",
        "PreparedAtUtc",
        "SentAtUtc",
        "AcknowledgedAtUtc"
    };
    private static readonly HashSet<string> PreparedMutationPayloadIgnoredPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UpdatedAtUtc",
        "Revision",
        "ExpectedRevision",
        "MutationId",
        "MutationCreatedAtUtc"
    };
    private static readonly HashSet<string> PreparedInvoiceMutationPayloadIgnoredPropertyNames = new(
        PreparedMutationPayloadIgnoredPropertyNames,
        StringComparer.OrdinalIgnoreCase)
    {
        // Push 준비 과정에서 로컬 고객명을 조회해 DTO에 보강하는 파생 표시값이다.
        "CustomerName",
        // 수금은 request.Payments에서 별도 mutation으로 동기화하며 Invoice upsert payload가 아니다.
        "Payments"
    };
    private static readonly HashSet<string> RentalBillingTemplateOnlyConflictIgnoredPropertyNames = new(
        EquivalentConflictIgnoredPropertyNames,
        StringComparer.OrdinalIgnoreCase)
    {
        "BillingTemplateJson"
    };
    private static readonly HashSet<string> RentalAssetRevisionRetryIgnoredPropertyNames = new(
        EquivalentConflictIgnoredPropertyNames,
        StringComparer.OrdinalIgnoreCase)
    {
        "BillingProfileId",
        "CustomerId",
        "CustomerName",
        "CurrentCustomerName",
        "LastAssignmentClearedAtUtc",
        "ManagementId",
        "ResponsibleOfficeCode",
        "ItemId",
        "InstallLocation",
        "InstallSiteName",
        "Notes",
        "SalePrice"
    };
    private static readonly HashSet<string> ItemCanonicalRepairIgnoredPropertyNames = new(
        EquivalentConflictIgnoredPropertyNames,
        StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "OfficeCode",
        "TenantCode"
    };
    private static readonly SemaphoreSlim GlobalSyncOperationLock = new(1, 1);

    private readonly LocalDbContext _db;
    private readonly LocalStateService _local;
    private readonly RentalStateService _rental;
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly SyncRequestDispatcher _dispatcher;
    private readonly SyncDiagnosticsService _diagnostics;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IDesktopCompatibilityRuntime? _compatibilityRuntime;
    private readonly IDesktopUpgradeRequiredObserver? _upgradeObserver;
    private readonly SemaphoreSlim _administrativeBusinessCacheRefreshLock = new(1, 1);
    private readonly object _immediateSyncGate = new();
    private readonly HashSet<Task> _observedBackgroundTasks = [];
    private Timer? _timer;
    private CancellationTokenSource? _immediateSyncCts;
    private CancellationTokenSource? _transientFailureRetryCts;
    private CancellationTokenSource _compatibilityBlockCts = new();
    private CancellationTokenSource _runtimeStopCts = new();
    private Task<bool>? _currentSyncTask;
    private Task? _stopAndDrainTask;
    private bool _resyncRequested;
    private bool _flushRequested;
    private bool _stopping;
    private bool _disposeRequested;
    private bool _disposed;
    private bool _dispatcherSubscribed;
    private static int _globalSyncOperationActiveCount;
    private DateTime _lastSyncStartedUtc = DateTime.MinValue;
    private DateTime _lastSyncCompletedUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastAdministrativeBusinessCacheRefreshUtcByDatabase =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SyncEntityKey, TrackedMutationPreservation>
        _trackedMutationsPreservedDuringSync = [];
    private readonly List<TrackedEntityPreservation>
        _trackedNonMutationChangesPreservedDuringSync = [];
    private SyncService? _isolatedOperationOwner;
    private Guid _lastAdministrativeBusinessCacheSessionId = Guid.Empty;
    private ItemWarehouseStockReplayPullGuard?
        _itemWarehouseStockReplayPullGuard;
    private bool
        _itemWarehouseStockReplayGuardValidatedBeforeMirrorReset;
    private bool
        _itemWarehouseStockReplayGuardValidatedForPullTransaction;

    private sealed record SyncOperationOwnerBoundary(
        long ScopeEpoch,
        Guid SessionId,
        Guid UserId,
        string AuthenticatedTenantCode,
        string TenantCode,
        string OfficeCode,
        string BusinessOfficeCode,
        string ScopeType,
        string BusinessDatabaseName);

    private sealed record ItemWarehouseStockRevisionConflictResolution(
        IReadOnlyList<ConflictLogDto> ResolvedConflicts,
        IReadOnlyList<ConflictLogDto> RetryRequiredConflicts);

    private sealed record ItemWarehouseStockReplayPullGuard(
        IReadOnlySet<Guid> AffectedItemIds,
        IReadOnlyDictionary<string, ItemWarehouseStockDto>
            ExpectedStocksByKey);

    private enum ItemWarehouseStockRevisionConflictOutcome
    {
        Unresolved,
        ResolvedFromServer,
        RetryRequired
    }

    private sealed class SyncPullBlockedException(
        string message,
        Exception? innerException = null)
        : Exception(message, innerException);

    public event Action<string>? SyncStatusChanged;

    internal Func<CancellationToken, Task>? BeforePreparedOutboxSaveAsyncForTesting { get; set; }
    internal Func<CancellationToken, Task>? BeforeSharedMirrorResetAsyncForTesting { get; set; }
    internal Func<CancellationToken, Task>? AfterAttachmentCommitAsyncForTesting { get; set; }
    internal Func<CancellationToken, Task>?
        AfterPostCommitOwnerCheckAsyncForTesting { get; set; }
    internal Action? CurrentOwnerRefreshScheduledForTesting { get; set; }
    internal Func<CancellationToken, Task>? AfterPulledPurgeRecordsAsyncForTesting { get; set; }
    internal Func<CancellationToken, Task>?
        BeforeAcceptedRevisionCleanAsyncForTesting { get; set; }
    internal Func<CancellationToken, Task>?
        AfterInventoryTransferPurgePushAppliedAsyncForTesting { get; set; }
    internal Action<int>?
        AcceptedRevisionCleanAffectedRowsForTesting { get; set; }
    internal Func<CancellationToken, Task>?
        BeforeItemWarehouseStockTombstoneConditionalDeleteAsyncForTesting
        { get; set; }
    internal Func<CancellationToken, Task>?
        AfterPartialWarehouseStockAcceptedSideEffectsAsyncForTesting
        { get; set; }
    internal Func<CancellationToken, Task>?
        BeforePulledItemCatalogMismatchRequeueAsyncForTesting { get; set; }
    internal Action?
        EligibleOutboxReconciliationCandidateLoadStartedForTesting { get; set; }
    internal Func<CancellationToken, Task>?
        AfterInitialOutboxReconciliationCandidateSnapshotLoadedAsyncForTesting
        { get; set; }
    internal Func<CancellationToken, Task>?
        BeforeStrictPullCommitGuardAsyncForTesting { get; set; }
    internal Func<Guid, CancellationToken, Task>?
        BeforeOutboxSupersedeUpdateAsyncForTesting { get; set; }

    public bool HasRecentSuccessfulSync(TimeSpan window)
        => !_disposed
           && _lastSyncCompletedUtc != DateTime.MinValue
           && DateTime.UtcNow - _lastSyncCompletedUtc < window;

    /// <summary>
    /// 가장 최근 동기화에서 현재 로컬 화면에 다시 반영할 서버 변경 건수입니다.
    /// 서버의 전역 revision만 증가하고 현재 업체 DB에 내려온 변경이 없는 경우에는 0입니다.
    /// </summary>
    public int LastPullChangeCount { get; private set; }

    public bool HasActiveOrQueuedSync
    {
        get
        {
            lock (_immediateSyncGate)
            {
                return (_currentSyncTask is not null && !_currentSyncTask.IsCompleted)
                       || _resyncRequested
                       || (_immediateSyncCts is not null && !_immediateSyncCts.IsCancellationRequested)
                       || Volatile.Read(ref _globalSyncOperationActiveCount) > 0;
            }
        }
    }

    public SyncService(
        LocalDbContext db,
        LocalStateService local,
        RentalStateService rental,
        ErpApiClient api,
        SessionState session,
        SyncRequestDispatcher dispatcher,
        SyncDiagnosticsService diagnostics,
        IServiceScopeFactory? scopeFactory = null,
        IHttpClientFactory? httpClientFactory = null,
        IDesktopCompatibilityRuntime? compatibilityRuntime = null,
        IDesktopUpgradeRequiredObserver? upgradeObserver = null)
    {
        _db = db;
        _local = local;
        _rental = rental;
        _api = api;
        _session = session;
        _dispatcher = dispatcher;
        _diagnostics = diagnostics;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _compatibilityRuntime = compatibilityRuntime;
        _upgradeObserver = upgradeObserver;
        if (_compatibilityRuntime is not null)
        {
            _compatibilityRuntime
                    .MutationAvailabilityChanged +=
                HandleMutationAvailabilityChanged;
            if (!_compatibilityRuntime.CanMutate)
                CancelForCompatibilityBlock();
        }
    }

    internal HttpClient CreateOfficeSessionHttpClient()
    {
        var httpClient = _httpClientFactory is null
            ? new HttpClient(
                new DesktopUpgradeRequiredHandler(
                    _upgradeObserver)
                {
                    InnerHandler = new HttpClientHandler()
                },
                disposeHandler: true)
            : _httpClientFactory.CreateClient(
                OfficeSessionHttpClientName);
        httpClient.BaseAddress = _api.GetBaseUri();
        httpClient.Timeout = TimeSpan.FromSeconds(100);
        return httpClient;
    }

    public void Start(TimeSpan interval, bool runImmediately = false)
    {
        lock (_immediateSyncGate)
        {
            if (_disposed ||
                _stopping ||
                !CanRunServerMutation() ||
                IsServerSyncDisabled())
                return;

            SubscribeToDispatcher();
            if (_timer is not null)
            {
                ObserveBackgroundTask(
                    _timer.DisposeAsync().AsTask(),
                    "동기화 타이머 교체 종료");
            }
            var normalizedInterval = interval <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : interval;
            var due = runImmediately ? TimeSpan.Zero : normalizedInterval;
            _timer = new Timer(_ => ObserveBackgroundTask(TrySyncAsync(), "타이머 자동 동기화"), null,
                due, normalizedInterval);
        }
    }

    public void Start(int intervalMinutes = 5, bool runImmediately = false)
    {
        Start(TimeSpan.FromMinutes(intervalMinutes), runImmediately);
    }

    private void SubscribeToDispatcher()
    {
        if (_dispatcherSubscribed)
            return;

        _dispatcher.SyncRequested += HandleSyncRequested;
        _dispatcherSubscribed = true;
    }

    public Task<bool> TrySyncAsync(CancellationToken ct = default)
        => _disposed || _stopping
            ? Task.FromResult(false)
            : !CanRunServerMutation()
                ? Task.FromResult(false)
            : IsServerSyncDisabled()
                ? Task.FromResult(true)
                : StartSyncAsync(waitForRunningSync: false, ct);

    public async Task<bool> TryAuthoritativePullOnlyAsync(
        CancellationToken ct = default)
    {
        if (_disposed ||
            _stopping ||
            !_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            !CanRunServerMutation() ||
            IsServerSyncDisabled())
        {
            return false;
        }

        if (HasPendingTrackedUserChanges())
            return false;

        using var compatibilityCts =
            CreateCompatibilityLinkedTokenSource(ct);
        if (compatibilityCts is null)
            return false;
        var operationToken = compatibilityCts.Token;

        return await ExecuteWithGlobalSyncOperationLockAsync(
            () => ExecuteUsingIsolatedRuntimeScopeAsync(
                child => child.TryAuthoritativePullOnlyCoreAsync(operationToken),
                () => TryAuthoritativePullOnlyCoreAsync(operationToken)),
            operationToken);
    }

    private async Task<bool> TryAuthoritativePullOnlyCoreAsync(
        CancellationToken ct)
    {
        if (_trackedMutationsPreservedDuringSync.Count > 0 ||
            _trackedNonMutationChangesPreservedDuringSync.Count > 0)
        {
            RestoreTrackedMutationsPreservedDuringSync();
        }

        try
        {
            PreservePendingTrackedChangesForSync();
            LastPullChangeCount = 0;
            SetStatus("서버 확정 결과를 다시 불러오는 중...");
            var fullyApplied = false;
            await ExecuteWithRetryAsync(
                async token =>
                {
                    fullyApplied =
                        await PullNewAuthoritativeOnlyAsync(token);
                },
                "서버 확정 결과 다운로드",
                ct);
            if (!fullyApplied)
                return false;
            SetStatus("서버 확정 결과를 다시 불러왔습니다.");
            return true;
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            SetStatus("필수 PC 업데이트가 확인되어 서버 확정 결과 확인을 중단했습니다.");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", "서버 확정 결과 pull-only 복구 실패", ex);
            SetStatus("서버 확정 결과를 다시 불러오지 못했습니다. 같은 작업을 반복하지 마세요.");
            return false;
        }
        finally
        {
            RestoreTrackedMutationsPreservedDuringSync();
        }
    }

    public async Task<bool> FlushPendingChangesAsync(CancellationToken ct = default)
    {
        if (_disposed ||
            _stopping ||
            !_session.IsLoggedIn ||
            !CanRunServerMutation())
            return false;
        if (IsServerSyncDisabled())
            return true;

        CancelPendingImmediateSync();

        var attempts = 0;
        while (attempts < 3)
        {
            attempts++;
            var synced = await StartSyncAsync(waitForRunningSync: true, ct).WaitAsync(ct);
            var hasPendingChanges = await _local.HasPendingSyncChangesAsync(_session, ct);
            if (!hasPendingChanges)
                return synced;
            if (!synced)
                return false;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return !await _local.HasPendingSyncChangesAsync(_session, ct);
    }

    public async Task<SyncScopeExecutionResult> TrySyncScopeAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        if (_disposed ||
            _stopping ||
            !_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            !CanRunServerMutation() ||
            IsServerSyncDisabled())
            return await TrySyncScopeCoreAsync(scopeKey, ct);

        using var compatibilityCts =
            CreateCompatibilityLinkedTokenSource(ct);
        if (compatibilityCts is null)
            return await TrySyncScopeCoreAsync(scopeKey, ct);
        var operationToken = compatibilityCts.Token;
        return await ExecuteWithGlobalSyncOperationLockAsync(
            () => ExecuteUsingIsolatedRuntimeScopeAsync(
                child => child.TrySyncScopeCoreAsync(
                    scopeKey,
                    operationToken),
                () => TrySyncScopeWithTrackedChangesPreservedAsync(
                    scopeKey,
                    operationToken)),
            operationToken);
    }

    private async Task<SyncScopeExecutionResult> TrySyncScopeWithTrackedChangesPreservedAsync(
        string scopeKey,
        CancellationToken ct)
    {
        try
        {
            if (_trackedMutationsPreservedDuringSync.Count > 0 ||
                _trackedNonMutationChangesPreservedDuringSync.Count > 0)
            {
                RestoreTrackedMutationsPreservedDuringSync();
            }

            PreservePendingTrackedChangesForSync();
            return await TrySyncScopeCoreAsync(scopeKey, ct);
        }
        finally
        {
            RestoreTrackedMutationsPreservedDuringSync();
        }
    }

    private async Task<SyncScopeExecutionResult> TrySyncScopeCoreAsync(string scopeKey, CancellationToken ct)
    {
        if (_disposed || _stopping)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, false, false, false, "동기화 서비스를 사용할 수 없습니다.");

        if (!_session.IsLoggedIn)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, false, false, false, "로그인 후 다시 시도하세요.");

        if (_session.IsOfflineMode)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, false, false, false, "오프라인 모드에서는 선택 범위 동기화를 실행할 수 없습니다.");

        if (!CanRunServerMutation())
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, false, false, false, "필수 PC 업데이트로 선택 범위 동기화가 차단되었습니다.");

        if (IsServerSyncDisabled())
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, true, false, false, "서버 동기화가 비활성화되어 있어 선택 범위 동기화를 건너뜁니다.");

        var blockingReason = await _local.GetPendingSyncBlockingReasonAsync(_session, scopeKey, ct);
        if (blockingReason is null)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, true, false, false, "선택한 범위에는 남은 변경이 없습니다.");

        PreservePendingTrackedChangesForSync();
        var usedCurrentSession = blockingReason.IsCurrentScope;
        var usedStoredCredential = false;

        try
        {
            if (string.Equals(scopeKey, "SHARED", StringComparison.OrdinalIgnoreCase))
            {
                if (!blockingReason.IsCurrentScope)
                    return new SyncScopeExecutionResult(scopeKey, blockingReason.ScopeDisplayName, blockingReason.PendingCount, blockingReason.PendingCount, false, false, false, false, blockingReason.Message);

                SetStatus("공용 마스터 범위를 동기화하는 중...");
                await ExecuteWithRetryAsync(token => PushDirtyAsync(_api, _session, includeSharedDirty: true, token), "공용 마스터 업로드", ct);
                await ClearStaleDirtyAsync(_api, _session, includeSharedDirty: true, ct);
            }
            else if (blockingReason.IsCurrentScope)
            {
                SetStatus($"{blockingReason.ScopeDisplayName} 범위를 동기화하는 중...");
                await ExecuteWithRetryAsync(token => PushDirtyAsync(_api, _session, includeSharedDirty: false, token), $"{blockingReason.ScopeDisplayName} 업로드", ct);
                await ClearStaleDirtyAsync(_api, _session, includeSharedDirty: false, ct);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(blockingReason.RequiredOfficeCode))
                    return new SyncScopeExecutionResult(scopeKey, blockingReason.ScopeDisplayName, blockingReason.PendingCount, blockingReason.PendingCount, false, false, false, false, blockingReason.Message);

                var credential = await _local.GetStoredSyncCredentialAsync(blockingReason.RequiredOfficeCode, ct);
                if (credential is null)
                {
                    PreservePendingTrackedChangesForSync();
                    await TryRecordPendingScopeDiagnosticAsync(scopeKey, blockingReason.PendingCount, "missing_sync_credential");
                    return new SyncScopeExecutionResult(scopeKey, blockingReason.ScopeDisplayName, blockingReason.PendingCount, blockingReason.PendingCount, false, false, false, false, blockingReason.Message);
                }

                var login = await AwaitWithTrackedChangesPreservedAsync(
                    () => _api.LoginAsync(credential.Username, credential.Password, ct));
                if (login is null || string.IsNullOrWhiteSpace(login.Token))
                {
                    await InvalidateStoredOfficeCredentialAsync(credential, ct);
                    var refreshedReason = await _local.GetPendingSyncBlockingReasonAsync(_session, scopeKey, ct);
                    var failureMessage = refreshedReason?.Message ?? blockingReason.Message;
                    return new SyncScopeExecutionResult(scopeKey, blockingReason.ScopeDisplayName, blockingReason.PendingCount, blockingReason.PendingCount, false, false, false, false, failureMessage);
                }

                var officeSession = new SessionState();
                officeSession.SetSession(login.Token, login.User, login.ExpiresAtUtc);
                using var officeHttpClient =
                    CreateOfficeSessionHttpClient();
                var officeApi = new ErpApiClient(officeHttpClient, officeSession);
                usedStoredCredential = true;
                SetStatus($"{blockingReason.ScopeDisplayName} 범위를 저장된 계정으로 동기화하는 중...");
                await ExecuteWithRetryAsync(token => PushDirtyAsync(officeApi, officeSession, includeSharedDirty: false, token), $"{blockingReason.ScopeDisplayName} 추가 업로드", ct);
                await ClearStaleDirtyAsync(officeApi, officeSession, includeSharedDirty: false, ct);
            }

            var remainingReason = await _local.GetPendingSyncBlockingReasonAsync(_session, scopeKey, ct);
            PreservePendingTrackedChangesForSync();
            var pendingCountAfter = remainingReason?.PendingCount ?? 0;
            if (pendingCountAfter > 0)
            {
                await TryRecordPendingScopeDiagnosticAsync(scopeKey, pendingCountAfter, "remaining_dirty");
                return new SyncScopeExecutionResult(
                    scopeKey,
                    blockingReason.ScopeDisplayName,
                    blockingReason.PendingCount,
                    pendingCountAfter,
                    true,
                    false,
                    usedCurrentSession,
                    usedStoredCredential,
                    remainingReason?.Message ?? $"{blockingReason.ScopeDisplayName} 범위에 서버 반영 대기 변경이 남아 있습니다.");
            }

            return new SyncScopeExecutionResult(
                scopeKey,
                blockingReason.ScopeDisplayName,
                blockingReason.PendingCount,
                0,
                true,
                true,
                usedCurrentSession,
                usedStoredCredential,
                $"{blockingReason.ScopeDisplayName} 범위 동기화를 완료했습니다.");
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            PreservePendingTrackedChangesForSync();
            SetStatus(
                "필수 PC 업데이트가 확인되어 동기화를 중단했습니다. 저장된 변경 내용은 그대로 보존됩니다.");
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PreservePendingTrackedChangesForSync();
            var detail = ex.InnerException?.Message ?? ex.Message;
            await TryRecordDiagnosticAsync(
                phase: "scope-sync",
                rawMessage: $"{blockingReason.ScopeDisplayName} 범위 동기화 확인 필요: {detail}",
                exception: ex,
                severity: "Warning");
            return new SyncScopeExecutionResult(
                scopeKey,
                blockingReason.ScopeDisplayName,
                blockingReason.PendingCount,
                blockingReason.PendingCount,
                true,
                false,
                usedCurrentSession,
                usedStoredCredential,
                $"{blockingReason.ScopeDisplayName} 범위 동기화에 실패했습니다. {detail}");
        }
    }

    public async Task<bool> RefreshSharedMirrorFromServerAsync(
        CancellationToken ct = default)
    {
        if (_disposed || !CanRunServerMutation())
            return false;

        using var compatibilityCts =
            CreateCompatibilityLinkedTokenSource(ct);
        if (compatibilityCts is null)
            return false;
        var operationToken = compatibilityCts.Token;
        return await ExecuteWithGlobalSyncOperationLockAsync(
            () => ExecuteUsingIsolatedRuntimeScopeAsync(
                child => child.RefreshSharedMirrorFromServerCoreAsync(
                    operationToken),
                () => RefreshSharedMirrorFromServerCoreAsync(
                    operationToken)),
            operationToken);
    }

    private async Task<bool> RefreshSharedMirrorFromServerCoreAsync(CancellationToken ct)
    {
        if (_disposed ||
            _stopping ||
            !_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            !CanRunServerMutation())
            return false;
        if (IsServerSyncDisabled())
            return true;
        if (_trackedMutationsPreservedDuringSync.Count > 0 ||
            _trackedNonMutationChangesPreservedDuringSync.Count > 0)
        {
            RestoreTrackedMutationsPreservedDuringSync();
        }

        if (HasPendingTrackedUserChanges())
        {
            SetStatus("저장되지 않은 편집이 있어 서버 캐시 새로고침을 중단했습니다. 먼저 저장하거나 취소한 뒤 다시 시도하세요.");
            return false;
        }

        if (await _local.CountDirtyAsync(_session, ct) > 0)
        {
            var pendingMessage = await _local.GetPendingSyncWaitingMessageAsync(_session, "로컬 미동기화 변경이 남아 있어 중앙 서버 기준 캐시를 다시 불러올 수 없습니다.");
            SetStatus(pendingMessage ?? "로컬 미동기화 변경이 남아 있어 중앙 서버 기준 캐시를 다시 불러올 수 없습니다.");
            return false;
        }

        SetStatus("중앙 서버 기준 캐시를 다시 불러오는 중...");

        try
        {
            return await TryRefreshSharedMirrorCoreAsync(ct);
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            SetStatus(
                "필수 PC 업데이트가 확인되어 서버 캐시 새로고침을 중단했습니다.");
            return false;
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", "중앙 서버 기준 캐시 재구성 실패", ex);
            await TryRecordDiagnosticAsync(
                phase: "shared-refresh",
                rawMessage: ex.InnerException?.Message ?? ex.Message,
                exception: ex,
                severity: "Warning");
            await TrySetSettingSafeAsync(
                "Sync.LastError",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.InnerException?.Message ?? ex.Message}",
                CancellationToken.None);
            SetStatus("중앙 서버 캐시 재구성에 실패했지만 앱은 계속 사용할 수 있습니다. 동기화를 다시 시도하세요.");
            return false;
        }
        finally
        {
            RestoreTrackedMutationsPreservedDuringSync();
        }
    }

    public async Task<bool> RefreshCurrentBusinessScopeFromServerAsync(
        CancellationToken ct = default)
    {
        if (_disposed || !CanRunServerMutation())
            return false;

        using var compatibilityCts =
            CreateCompatibilityLinkedTokenSource(ct);
        if (compatibilityCts is null)
            return false;
        var operationToken = compatibilityCts.Token;
        return await ExecuteWithGlobalSyncOperationLockAsync(
            () => RefreshCurrentBusinessScopeFromServerInsideGlobalOperationCoreAsync(
                operationToken),
            operationToken);
    }

    internal async Task<bool> RefreshCurrentBusinessScopeFromServerInsideGlobalOperationAsync(
        CancellationToken ct = default)
    {
        if (_disposed || !CanRunServerMutation())
            return false;

        using var compatibilityCts = CreateCompatibilityLinkedTokenSource(ct);
        if (compatibilityCts is null)
            return false;
        return await RefreshCurrentBusinessScopeFromServerInsideGlobalOperationCoreAsync(
            compatibilityCts.Token);
    }

    private Task<bool> RefreshCurrentBusinessScopeFromServerInsideGlobalOperationCoreAsync(
        CancellationToken operationToken)
        => ExecuteUsingIsolatedRuntimeScopeAsync(
            child => child.RefreshCurrentBusinessScopeFromServerCoreAsync(operationToken),
            () => RefreshCurrentBusinessScopeFromServerCoreAsync(operationToken));

    public async Task<bool> ReplaceCurrentBusinessScopeCacheFromServerAsync(
        CancellationToken ct = default)
    {
        if (_disposed || !CanRunServerMutation())
            return false;

        using var compatibilityCts =
            CreateCompatibilityLinkedTokenSource(ct);
        if (compatibilityCts is null)
            return false;
        var operationToken = compatibilityCts.Token;
        return await ExecuteWithGlobalSyncOperationLockAsync(
            () => ExecuteUsingIsolatedRuntimeScopeAsync(
                child => child.RefreshCurrentBusinessScopeFromServerCoreAsync(
                    operationToken,
                    replaceLocalBusinessCache: true),
                () => RefreshCurrentBusinessScopeFromServerCoreAsync(
                    operationToken,
                    replaceLocalBusinessCache: true)),
            operationToken);
    }

    private async Task<bool> RefreshCurrentBusinessScopeFromServerCoreAsync(
        CancellationToken ct,
        bool replaceLocalBusinessCache = false)
    {
        if (_disposed ||
            !_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            !CanRunServerMutation())
            return false;
        if (IsServerSyncDisabled())
            return !replaceLocalBusinessCache;
        if (_trackedMutationsPreservedDuringSync.Count > 0 ||
            _trackedNonMutationChangesPreservedDuringSync.Count > 0)
        {
            RestoreTrackedMutationsPreservedDuringSync();
        }

        if (HasPendingTrackedUserChanges())
        {
            SetStatus("저장되지 않은 편집이 있어 현재 업체 캐시 새로고침을 중단했습니다. 먼저 저장하거나 취소한 뒤 다시 시도하세요.");
            return false;
        }

        var currentScopeDirtyCount = await _local.CountDirtyAsync(_session, ct);
        if (currentScopeDirtyCount > 0)
        {
            SetStatus($"현재 업체 DB에 미동기화 변경 {currentScopeDirtyCount:N0}건이 남아 있어 범위 재구성을 건너뜁니다.");
            return false;
        }

        SetStatus("현재 업체 DB 기준 캐시를 다시 불러오는 중...");

        try
        {
            return replaceLocalBusinessCache
                ? await TryRefreshCurrentBusinessScopeCoreInternalAsync(
                    ct,
                    preserveTrackedChanges: false,
                    replaceLocalBusinessCache: true)
                : await TryRefreshCurrentBusinessScopeCoreAsync(ct);
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            SetStatus(
                "필수 PC 업데이트가 확인되어 업체 DB 캐시 새로고침을 중단했습니다.");
            return false;
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", "현재 업체 DB 기준 캐시 재구성 실패", ex);
            await TryRecordDiagnosticAsync(
                phase: "scoped-refresh",
                rawMessage: ex.InnerException?.Message ?? ex.Message,
                exception: ex,
                severity: "Warning");
            await TrySetSettingSafeAsync(
                "Sync.LastError",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.InnerException?.Message ?? ex.Message}",
                CancellationToken.None);
            SetStatus("현재 업체 DB 캐시 재구성에 실패했지만 앱은 계속 사용할 수 있습니다. 동기화를 다시 시도하세요.");
            return false;
        }
        finally
        {
            RestoreTrackedMutationsPreservedDuringSync();
        }
    }

    public async Task<bool> EnsureAdministrativeBusinessCachesAsync(
        CancellationToken ct = default)
    {
        if (_disposed ||
            _isolatedOperationOwner is not null ||
            !CanRunServerMutation())
            return false;

        using var compatibilityCts =
            CreateCompatibilityLinkedTokenSource(ct);
        if (compatibilityCts is null)
            return false;
        var operationToken = compatibilityCts.Token;
        return await ExecuteWithGlobalSyncOperationLockAsync(
            () => ExecuteUsingIsolatedRuntimeScopeAsync(
                child => child.EnsureAdministrativeBusinessCachesCoreAsync(
                    operationToken),
                () => EnsureAdministrativeBusinessCachesCoreAsync(
                    operationToken)),
            operationToken);
    }

    private async Task<bool> EnsureAdministrativeBusinessCachesCoreAsync(CancellationToken ct)
    {
        if (_disposed ||
            !_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            !_session.HasAdministrativePrivileges ||
            !CanRunServerMutation())
            return false;
        if (IsServerSyncDisabled())
            return false;
        if (HasPendingTrackedUserChanges())
        {
            SetStatus("저장되지 않은 편집이 있어 관리자 업체 캐시 병합을 중단했습니다.");
            return false;
        }

        var operationOwner =
            CaptureSyncOperationOwnerBoundary();
        var runningSyncTask = GetCurrentRunningSyncTask();
        if (runningSyncTask is not null)
        {
            AppLogger.Info("SYNC", "관리자 전체 업체 캐시 병합은 실행 중인 일반 동기화를 막지 않도록 이번 회차에서 건너뜁니다.");
            return false;
        }

        await _administrativeBusinessCacheRefreshLock.WaitAsync(ct);
        try
        {
            if (!IsSyncOperationOwnerCurrent(operationOwner))
            {
                SetStatus(
                    "관리자 업체 캐시 시작 전에 로그인·권한·업체 범위가 변경되어 병합을 중단했습니다.");
                return false;
            }

            if (_lastAdministrativeBusinessCacheSessionId != _session.SessionId)
            {
                _lastAdministrativeBusinessCacheRefreshUtcByDatabase.Clear();
                _lastAdministrativeBusinessCacheSessionId = _session.SessionId;
            }

            var now = DateTime.UtcNow;
            var mergedBusinessDatabaseCount = 0;
            foreach (var businessDatabaseName in TenantScopeCatalog.AllTenants
                         .Select(TenantScopeCatalog.GetDatabaseName)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsSyncOperationOwnerCurrent(operationOwner))
                {
                    SetStatus(
                        "관리자 업체 캐시 병합 중 로그인·권한·업체 범위가 변경되어 이전 세션 응답을 폐기했습니다.");
                    return false;
                }

                if (_lastAdministrativeBusinessCacheRefreshUtcByDatabase.TryGetValue(
                        businessDatabaseName,
                        out var lastRefreshUtc) &&
                    now - lastRefreshUtc < AdministrativeBusinessCacheRefreshInterval)
                {
                    continue;
                }

                var businessCacheStartedAtUtc = DateTime.UtcNow;

                try
                {
                    using var pullTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pullTimeoutCts.CancelAfter(AdministrativeBusinessCachePullTimeout);

                    var revisionSettingKey = SyncSettingKeys.BuildAdministrativeBusinessCacheRevisionKey(businessDatabaseName);
                    var sinceRevision = await GetAdministrativeBusinessCacheRevisionAsync(revisionSettingKey, ct);
                    if (HasPendingTrackedUserChanges())
                    {
                        SetStatus("저장되지 않은 편집이 있어 관리자 업체 캐시 병합을 중단했습니다.");
                        return false;
                    }

                    var trackedStateBeforePull = CaptureTrackedStateBeforePush();
                    var trackedChangesArrivedDuringPull = false;
                    SyncPullResponse? pull;
                    try
                    {
                        pull = await _api.PullAsync(
                            sinceRevision,
                            businessDatabaseName,
                            rentalAdministrationOnly: true,
                            pullTimeoutCts.Token);
                    }
                    finally
                    {
                        trackedChangesArrivedDuringPull =
                            HasTrackedUserChangesSinceBoundary(trackedStateBeforePull);
                    }

                    if (trackedChangesArrivedDuringPull)
                    {
                        SetStatus("서버 응답 대기 중 저장되지 않은 편집이 발생해 관리자 업체 캐시 병합을 중단했습니다.");
                        return false;
                    }
                    if (pull is null)
                        continue;
                    if (!IsSyncOperationOwnerCurrent(operationOwner))
                    {
                        SetStatus(
                            "관리자 업체 캐시 응답 대기 중 로그인·권한·업체 범위가 변경되어 이전 세션 응답을 폐기했습니다.");
                        return false;
                    }
                    if (await _local.CountDirtyAsync(ct) > 0)
                    {
                        SetStatus("서버 응답 대기 중 저장된 미동기화 변경이 발생해 관리자 업체 캐시 병합을 중단했습니다.");
                        return false;
                    }

                    if (pull.CurrentServerRevision < sinceRevision)
                    {
                        AppLogger.Warn(
                            "SYNC",
                            $"관리자 업체 캐시 revision이 서버보다 앞서 전체 재조회합니다: db={businessDatabaseName}, local={sinceRevision:N0}, server={pull.CurrentServerRevision:N0}");
                        sinceRevision = 0;
                        trackedStateBeforePull = CaptureTrackedStateBeforePush();
                        trackedChangesArrivedDuringPull = false;
                        try
                        {
                            pull = await _api.PullAsync(
                                sinceRevision,
                                businessDatabaseName,
                                rentalAdministrationOnly: true,
                                pullTimeoutCts.Token);
                        }
                        finally
                        {
                            trackedChangesArrivedDuringPull =
                                HasTrackedUserChangesSinceBoundary(trackedStateBeforePull);
                        }

                        if (trackedChangesArrivedDuringPull)
                        {
                            SetStatus("서버 응답 대기 중 저장되지 않은 편집이 발생해 관리자 업체 캐시 병합을 중단했습니다.");
                            return false;
                        }
                        if (pull is null)
                            continue;
                        if (!IsSyncOperationOwnerCurrent(operationOwner))
                        {
                            SetStatus(
                                "관리자 업체 캐시 재조회 중 로그인·권한·업체 범위가 변경되어 이전 세션 응답을 폐기했습니다.");
                            return false;
                        }
                        if (await _local.CountDirtyAsync(ct) > 0)
                        {
                            SetStatus("서버 응답 대기 중 저장된 미동기화 변경이 발생해 관리자 업체 캐시 병합을 중단했습니다.");
                            return false;
                        }
                    }

                    if (HasPendingTrackedUserChanges())
                    {
                        SetStatus("저장되지 않은 편집이 있어 관리자 업체 캐시 병합을 중단했습니다.");
                        return false;
                    }

                    using (_local.SuppressSyncDispatch())
                    {
                        var applied = await TryApplyPullAtomicallyAsync(
                            pull,
                            sinceRevision,
                            ct,
                            updateSyncRevision: false,
                            expectedOwner: operationOwner,
                            applyCompleteItemWarehouseStockSnapshot: false);
                        if (!applied)
                        {
                            SetStatus(
                                "관리자 업체 캐시 반영 중 로그인·권한·업체 범위가 변경되어 DB와 첨부파일 변경을 롤백했습니다.");
                            return false;
                        }
                    }

                    if (HasPendingTrackedUserChanges())
                    {
                        SetStatus("캐시 병합 중 저장되지 않은 편집이 발생해 후속 캐시 작업을 중단했습니다.");
                        return false;
                    }

                    if (!IsSyncOperationOwnerCurrent(operationOwner))
                    {
                        return true;
                    }

                    await TrySetSettingSafeAsync(
                        revisionSettingKey,
                        Math.Max(0L, pull.CurrentServerRevision).ToString(CultureInfo.InvariantCulture),
                        CancellationToken.None);

                    OperationTiming.LogIfSlow(
                        "SYNC",
                        "관리자 업체 렌탈 캐시 병합",
                        DateTime.UtcNow - businessCacheStartedAtUtc,
                        detail: $"db={businessDatabaseName}, since={sinceRevision:N0}, current={pull.CurrentServerRevision:N0}",
                        infoThreshold: TimeSpan.FromMilliseconds(400),
                        warningThreshold: TimeSpan.FromSeconds(2));

                    if (HasPendingTrackedUserChanges())
                    {
                        SetStatus("캐시 병합 중 저장되지 않은 편집이 발생해 후속 캐시 작업을 중단했습니다.");
                        return false;
                    }

                    _db.ChangeTracker.Clear();
                    _lastAdministrativeBusinessCacheRefreshUtcByDatabase[businessDatabaseName] = DateTime.UtcNow;
                    mergedBusinessDatabaseCount++;
                }
                catch (DesktopClientUpgradeRequiredException)
                {
                    SetStatus(
                        "필수 PC 업데이트가 확인되어 관리자 업체 캐시 갱신을 중단했습니다.");
                    return false;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (HasPendingTrackedUserChanges())
                    {
                        SetStatus("캐시 병합 중 저장되지 않은 편집이 발생해 후속 캐시 작업을 중단했습니다.");
                        return false;
                    }

                    _db.ChangeTracker.Clear();
                    AppLogger.Warn(
                        "SYNC",
                        $"관리자 전체 업체 캐시 병합 실패: db={businessDatabaseName}, detail={ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (mergedBusinessDatabaseCount == 0)
                return false;

            return true;
        }
        finally
        {
            _administrativeBusinessCacheRefreshLock.Release();
        }
    }

    private async Task<long> GetAdministrativeBusinessCacheRevisionAsync(
        string revisionSettingKey,
        CancellationToken ct)
    {
        var raw = await _local.GetSettingAsync(revisionSettingKey, ct);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) && revision > 0
            ? revision
            : 0L;
    }

    private Task<bool>? GetCurrentRunningSyncTask()
    {
        if (_isolatedOperationOwner is not null)
            return _isolatedOperationOwner.GetCurrentRunningSyncTask();

        lock (_immediateSyncGate)
        {
            return _currentSyncTask is not null && !_currentSyncTask.IsCompleted
                ? _currentSyncTask
                : null;
        }
    }

    private Task<bool> StartSyncAsync(bool waitForRunningSync, CancellationToken ct)
    {
        if (_disposed ||
            _stopping ||
            !_session.IsLoggedIn ||
            !CanRunServerMutation())
            return Task.FromResult(false);
        if (IsServerSyncDisabled())
            return Task.FromResult(true);

        lock (_immediateSyncGate)
        {
            if (_disposed || _stopping)
                return Task.FromResult(false);

            if (_currentSyncTask is not null && !_currentSyncTask.IsCompleted)
                return waitForRunningSync ? _currentSyncTask : Task.FromResult(false);

            var linkedCts =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        ct,
                        _compatibilityBlockCts.Token,
                        _runtimeStopCts.Token);
            var syncTask =
                RunSyncWithLinkedCancellationAsync(linkedCts);
            _currentSyncTask = syncTask;
            ObserveBackgroundTask(FinalizeSyncAsync(syncTask), "동기화 후처리");
            return syncTask;
        }
    }

    private async Task<bool> RunSyncWithLinkedCancellationAsync(
        CancellationTokenSource linkedCts)
    {
        using (linkedCts)
            return await RunSyncCoreAsync(linkedCts.Token);
    }

    private async Task FinalizeSyncAsync(Task<bool> syncTask)
    {
        var succeeded = false;
        try
        {
            succeeded = await syncTask;
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", "동기화 후처리 대기 실패", ex);
        }

        CancellationTokenSource? rerunCts = null;
        var rerunImmediately = false;
        lock (_immediateSyncGate)
        {
            if (ReferenceEquals(_currentSyncTask, syncTask))
                _currentSyncTask = null;

            if (!_stopping &&
                _resyncRequested &&
                _session.IsLoggedIn &&
                !_session.IsOfflineMode &&
                CanRunServerMutation())
            {
                _resyncRequested = false;
                _immediateSyncCts?.Cancel();
                _immediateSyncCts?.Dispose();
                _immediateSyncCts = null;

                if (_flushRequested)
                {
                    _flushRequested = false;
                    rerunImmediately = true;
                }
                else
                {
                    _immediateSyncCts = new CancellationTokenSource();
                    rerunCts = _immediateSyncCts;
                }
            }
        }

        if (rerunCts is not null)
        {
            ObserveBackgroundTask(
                StartBackgroundTaskWithoutExecutionContext(
                    () => RunDeferredImmediateSyncAsync(rerunCts.Token)),
                "예약된 즉시 동기화");
            return;
        }

        if (rerunImmediately)
        {
            ObserveBackgroundTask(
                StartBackgroundTaskWithoutExecutionContext(
                    () => StartSyncAsync(waitForRunningSync: true, CancellationToken.None)),
                "즉시 재동기화");
            return;
        }

        _dispatcher.CompleteSync(succeeded);
        if (succeeded)
            ScheduleAdministrativeBusinessCacheWarmup();
    }

    private void ScheduleAdministrativeBusinessCacheWarmup()
    {
        CancellationToken runtimeStopToken;
        lock (_immediateSyncGate)
        {
            if (_disposed ||
                _stopping ||
                _timer is null ||
                !_session.IsLoggedIn ||
                _session.IsOfflineMode ||
                !_session.HasAdministrativePrivileges)
            {
                return;
            }

            runtimeStopToken = _runtimeStopCts.Token;
        }

        ObserveBackgroundTask(
            StartBackgroundTaskWithoutExecutionContext(
                () => EnsureAdministrativeBusinessCachesAsync(
                    runtimeStopToken)),
            "관리자 업체 캐시 사전 준비");
    }

    internal static async Task<T> ExecuteWithGlobalSyncOperationLockAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var entered = false;
        try
        {
            await GlobalSyncOperationLock.WaitAsync(ct);
            entered = true;
            Interlocked.Increment(ref _globalSyncOperationActiveCount);
            return await operation();
        }
        finally
        {
            if (entered)
            {
                Interlocked.Decrement(ref _globalSyncOperationActiveCount);
                GlobalSyncOperationLock.Release();
            }
        }
    }

    private async Task<T> ExecuteUsingIsolatedRuntimeScopeAsync<T>(
        Func<SyncService, Task<T>> isolatedOperation,
        Func<Task<T>> fallbackOperation)
    {
        if (_disposed ||
            _scopeFactory is null ||
            _isolatedOperationOwner is not null)
            return await fallbackOperation();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var child = scope.ServiceProvider.GetRequiredService<SyncService>();
        if (ReferenceEquals(child, this))
            return await fallbackOperation();

        child._isolatedOperationOwner = this;
        child.LastPullChangeCount = LastPullChangeCount;
        child._lastSyncStartedUtc = _lastSyncStartedUtc;
        child._lastSyncCompletedUtc = _lastSyncCompletedUtc;
        child._lastAdministrativeBusinessCacheSessionId =
            _lastAdministrativeBusinessCacheSessionId;
        child._lastAdministrativeBusinessCacheRefreshUtcByDatabase.Clear();
        foreach (var (databaseName, refreshedAtUtc) in
                 _lastAdministrativeBusinessCacheRefreshUtcByDatabase)
        {
            child._lastAdministrativeBusinessCacheRefreshUtcByDatabase[databaseName] =
                refreshedAtUtc;
        }

        child.BeforePreparedOutboxSaveAsyncForTesting =
            BeforePreparedOutboxSaveAsyncForTesting;
        child.BeforeSharedMirrorResetAsyncForTesting =
            BeforeSharedMirrorResetAsyncForTesting;
        child.AfterAttachmentCommitAsyncForTesting =
            AfterAttachmentCommitAsyncForTesting;
        child.AfterPostCommitOwnerCheckAsyncForTesting =
            AfterPostCommitOwnerCheckAsyncForTesting;
        child.CurrentOwnerRefreshScheduledForTesting =
            CurrentOwnerRefreshScheduledForTesting;
        child.AfterPulledPurgeRecordsAsyncForTesting =
            AfterPulledPurgeRecordsAsyncForTesting;
        child.BeforeAcceptedRevisionCleanAsyncForTesting =
            BeforeAcceptedRevisionCleanAsyncForTesting;
        child.AfterInventoryTransferPurgePushAppliedAsyncForTesting =
            AfterInventoryTransferPurgePushAppliedAsyncForTesting;
        child.AcceptedRevisionCleanAffectedRowsForTesting =
            AcceptedRevisionCleanAffectedRowsForTesting;
        child.BeforeItemWarehouseStockTombstoneConditionalDeleteAsyncForTesting =
            BeforeItemWarehouseStockTombstoneConditionalDeleteAsyncForTesting;
        child.AfterPartialWarehouseStockAcceptedSideEffectsAsyncForTesting =
            AfterPartialWarehouseStockAcceptedSideEffectsAsyncForTesting;
        child.BeforePulledItemCatalogMismatchRequeueAsyncForTesting =
            BeforePulledItemCatalogMismatchRequeueAsyncForTesting;
        child.EligibleOutboxReconciliationCandidateLoadStartedForTesting =
            EligibleOutboxReconciliationCandidateLoadStartedForTesting;
        child.AfterInitialOutboxReconciliationCandidateSnapshotLoadedAsyncForTesting =
            AfterInitialOutboxReconciliationCandidateSnapshotLoadedAsyncForTesting;
        child.BeforeStrictPullCommitGuardAsyncForTesting =
            BeforeStrictPullCommitGuardAsyncForTesting;
        child.BeforeOutboxSupersedeUpdateAsyncForTesting =
            BeforeOutboxSupersedeUpdateAsyncForTesting;

        void ForwardStatus(string message) => SetStatus(message);
        void ForwardRentalState(
            object? sender,
            RentalStateChangedEventArgs args)
        {
            using var callbackBoundary =
                LocalDbContext.SuppressRuntimeMutationOwnerForCallback();
            _rental.PublishSynchronizedStateChanges(
                args.AssetIds,
                args.BillingProfileIds);
        }

        child.SyncStatusChanged += ForwardStatus;
        child._rental.StateChanged += ForwardRentalState;
        try
        {
            return await isolatedOperation(child);
        }
        finally
        {
            try
            {
                await child.StopAndDrainAsync().ConfigureAwait(false);
            }
            finally
            {
                child.SyncStatusChanged -= ForwardStatus;
                child._rental.StateChanged -= ForwardRentalState;
                LastPullChangeCount = child.LastPullChangeCount;
                _lastSyncStartedUtc = child._lastSyncStartedUtc;
                _lastSyncCompletedUtc = child._lastSyncCompletedUtc;
                _lastAdministrativeBusinessCacheSessionId =
                    child._lastAdministrativeBusinessCacheSessionId;
                _lastAdministrativeBusinessCacheRefreshUtcByDatabase.Clear();
                foreach (var (databaseName, refreshedAtUtc) in
                         child._lastAdministrativeBusinessCacheRefreshUtcByDatabase)
                {
                    _lastAdministrativeBusinessCacheRefreshUtcByDatabase[databaseName] =
                        refreshedAtUtc;
                }

                child._isolatedOperationOwner = null;
            }
        }
    }

    private async Task<T> AwaitWithTrackedChangesPreservedAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        finally
        {
            PreservePendingTrackedChangesForSync();
        }
    }

    private async Task<bool> RunSyncCoreAsync(CancellationToken ct)
    {
        try
        {
            return await ExecuteWithGlobalSyncOperationLockAsync(
                () => ExecuteUsingIsolatedRuntimeScopeAsync(
                    child => child.RunSyncCoreLockedAsync(ct),
                    () => RunSyncCoreLockedAsync(ct)),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> RunSyncCoreLockedAsync(CancellationToken ct)
    {
        _itemWarehouseStockReplayPullGuard = null;
        _itemWarehouseStockReplayGuardValidatedBeforeMirrorReset =
            false;
        _itemWarehouseStockReplayGuardValidatedForPullTransaction =
            false;
        if (_trackedMutationsPreservedDuringSync.Count > 0 ||
            _trackedNonMutationChangesPreservedDuringSync.Count > 0)
            RestoreTrackedMutationsPreservedDuringSync();

        try
        {
            PreservePendingTrackedChangesForSync();

            LastPullChangeCount = 0;
            _lastSyncStartedUtc = DateTime.UtcNow;
            SetStatus("동기화 중...");
            AppLogger.Info("SYNC", "동기화 시작");
            await TrySetSettingSafeAsync(LastConflictSummarySettingKey, string.Empty, CancellationToken.None);

            var normalizedSharedOptionIdCount = await _local.NormalizeSharedOptionIdCasingAsync(ct);
            if (normalizedSharedOptionIdCount > 0)
                AppLogger.Info("SYNC", $"동기화 전 공유 선택옵션 ID 대소문자 정리 {normalizedSharedOptionIdCount:N0}건을 적용했습니다.");

            await EnsureUnitCatalogSyncSafetyAsync(ct);
            await ExecuteWithRetryAsync(token => PushDirtyAsync(_api, _session, includeSharedDirty: true, token), "업로드", ct);
            await PushDirtyWithStoredOfficeSessionsAsync(ct);
            await ClearStaleDirtyWithStoredOfficeSessionsAsync(ct);
            await RetryDeferredPurgeRecordsAsync(ct);
            await ExecuteWithRetryAsync(PullNewAsync, "다운로드", ct);

            var remainingDirtyCount = await _local.CountDirtyAsync(_session, ct);

            await TrySetSettingSafeAsync("Sync.LastSuccessAt", DateTime.Now.ToString("O"), CancellationToken.None);
            await TrySetSettingSafeAsync("Sync.LastError", string.Empty, CancellationToken.None);
            await _diagnostics.ResolveOpenIssuesAsync(ct: CancellationToken.None);
            _lastSyncCompletedUtc = DateTime.UtcNow;
            if (remainingDirtyCount > 0)
                await ReportRemainingDirtyOfficesAsync("동기화는 완료했지만 아직 미동기화 변경이 남아 있습니다.", null, ct);
            else
                SetStatus($"동기화 완료 {DateTime.Now:HH:mm:ss}");
            AppLogger.Info("SYNC", "동기화 완료");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppLogger.Info("SYNC", "동기화 요청이 더 최신 변경/종료 요청으로 취소되어 조용히 재예약합니다.");
            return false;
        }
        catch (DesktopClientUpgradeRequiredException ex)
        {
            SetStatus(
                "필수 PC 업데이트가 확인되어 동기화를 즉시 중단했습니다. 미동기화 변경 내용은 보존됩니다.");
            AppLogger.Warn(
                "UPDATE",
                $"PC 호환성 게이트가 동기화를 중단했습니다. path={ex.RequestPath}");
            return false;
        }
        catch (Exception ex) when (IsDisposedContextException(ex))
        {
            AppLogger.Warn("SYNC", $"동기화 종료 중 안전 무시: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested &&
                ex is not SyncPullBlockedException)
                await TryClearStaleDirtyAfterFailureAsync(ct);

            _db.ChangeTracker.Clear();
            var detail = ex.InnerException?.Message ?? ex.Message;
            if (detail.Length > 220)
                detail = detail[..220] + "...";

            if (IsTransient(ex, ct))
            {
                var retryMessage = "서버 응답 지연으로 동기화를 잠시 후 자동 재시도합니다. 업무는 계속 가능하며, 상세 원인은 동기화 진단에서 확인하세요.";
                await TrySetSettingSafeAsync(
                    "Sync.LastError",
                    string.Empty,
                    CancellationToken.None);

                await TryRecordDiagnosticAsync(
                    phase: "sync-transient",
                    rawMessage: detail,
                    exception: ex,
                    severity: "Warning",
                    recoveryAttempted: true);

                SetStatus(retryMessage);
                AppLogger.Warn("SYNC", retryMessage);
                ScheduleTransientFailureRetry();
                return false;
            }

            await TrySetSettingSafeAsync(
                "Sync.LastError",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {detail}",
                CancellationToken.None);

            await TryRecordDiagnosticAsync(
                phase: "sync",
                rawMessage: detail,
                exception: ex,
                severity: "Error");

            SetStatus(BuildUserFacingSyncAttentionStatus(detail));
            AppLogger.Error("SYNC", "동기화 확인 필요", ex);
            return false;
        }
        finally
        {
            _itemWarehouseStockReplayPullGuard = null;
            _itemWarehouseStockReplayGuardValidatedBeforeMirrorReset =
                false;
            _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                false;
            RestoreTrackedMutationsPreservedDuringSync();
        }
    }

    private async Task TryClearStaleDirtyAfterFailureAsync(CancellationToken ct)
    {
        try
        {
            await ClearStaleDirtyWithStoredOfficeSessionsAsync(ct);
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            throw;
        }
        catch (Exception cleanupEx) when (!ct.IsCancellationRequested)
        {
            AppLogger.Warn("SYNC", $"실패 후 stale dirty 정리 실패: {cleanupEx.Message}");
            await TryRecordDiagnosticAsync(
                phase: "stale-dirty-after-failure",
                rawMessage: cleanupEx.InnerException?.Message ?? cleanupEx.Message,
                exception: cleanupEx,
                severity: "Warning");
        }
    }

    private static bool IsDisposedContextException(Exception ex)
    {
        if (ex is ObjectDisposedException)
            return true;

        var details = ex.ToString();
        return details.Contains("disposed context", StringComparison.OrdinalIgnoreCase)
               || details.Contains("Object name: 'LocalDbContext'", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(inner => IsTransient(inner, ct));

        if (IsTransientSingle(ex, ct))
            return true;

        return ex.InnerException is not null && IsTransient(ex.InnerException, ct);
    }

    private static bool IsTransientSingle(Exception ex, CancellationToken ct)
    {
        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
            return true;

        if (ex is TimeoutException)
            return true;

        if (ex is HttpRequestException httpEx)
            return httpEx.StatusCode is null
                   || httpEx.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                   || (int?)httpEx.StatusCode == 429
                   || httpEx.StatusCode == System.Net.HttpStatusCode.InternalServerError
                   || httpEx.StatusCode == System.Net.HttpStatusCode.BadGateway
                   || httpEx.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                   || httpEx.StatusCode == System.Net.HttpStatusCode.GatewayTimeout;

        return false;
    }

    private void ScheduleTransientFailureRetry()
    {
        if (_isolatedOperationOwner is not null)
        {
            _isolatedOperationOwner.ScheduleTransientFailureRetry();
            return;
        }

        if (_disposed ||
            !_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            !CanRunServerMutation())
            return;

        lock (_immediateSyncGate)
        {
            if (_disposed || _stopping)
                return;

            if (_transientFailureRetryCts is not null && !_transientFailureRetryCts.IsCancellationRequested)
                return;

            _transientFailureRetryCts = new CancellationTokenSource();
            var retryCts = _transientFailureRetryCts;
            ObserveBackgroundTask(
                StartBackgroundTaskWithoutExecutionContext(
                    () => RunTransientFailureRetryAsync(retryCts)),
                "서버 응답 지연 후 동기화 자동 재시도");
        }
    }

    private async Task RunTransientFailureRetryAsync(CancellationTokenSource retryCts)
    {
        try
        {
            await Task.Delay(TransientFailureRetryDelay, retryCts.Token);

            if (_disposed ||
                _stopping ||
                !_session.IsLoggedIn ||
                _session.IsOfflineMode ||
                !CanRunServerMutation())
                return;

            await StartSyncAsync(waitForRunningSync: true, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // A newer sync request or shutdown superseded this retry.
        }
        finally
        {
            lock (_immediateSyncGate)
            {
                if (ReferenceEquals(_transientFailureRetryCts, retryCts))
                    _transientFailureRetryCts = null;
            }

            retryCts.Dispose();
        }
    }

    private void HandleSyncRequested(SyncRequestMode mode)
    {
        if (_disposed ||
            _stopping ||
            !_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            !CanRunServerMutation())
            return;

        lock (_immediateSyncGate)
        {
            if (_disposed || _stopping)
                return;

            if (_currentSyncTask is not null && !_currentSyncTask.IsCompleted)
            {
                _resyncRequested = true;
                if (mode == SyncRequestMode.Flush)
                    _flushRequested = true;
                return;
            }

            _immediateSyncCts?.Cancel();
            _immediateSyncCts?.Dispose();
            _immediateSyncCts = null;

            if (mode == SyncRequestMode.Flush)
            {
                _flushRequested = false;
                ObserveBackgroundTask(
                    StartBackgroundTaskWithoutExecutionContext(
                        () => StartSyncAsync(
                            waitForRunningSync: true,
                            CancellationToken.None)),
                    "수동 즉시 동기화");
            }
            else
            {
                _immediateSyncCts = new CancellationTokenSource();
                ObserveBackgroundTask(
                    StartBackgroundTaskWithoutExecutionContext(
                        () => RunDeferredImmediateSyncAsync(
                            _immediateSyncCts.Token)),
                    "지연 즉시 동기화");
            }
        }
    }

    private async Task RunDeferredImmediateSyncAsync(CancellationToken ct)
    {
        if (_disposed || _stopping)
            return;

        try
        {
            await Task.Delay(DebouncedSyncDelay, ct);

            if (_disposed ||
                _stopping ||
                !_session.IsLoggedIn ||
                _session.IsOfflineMode ||
                !CanRunServerMutation())
                return;

            await StartSyncAsync(waitForRunningSync: true, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // newer local change arrived; debounce in progress
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", "즉시 동기화 확인 필요", ex);
            await TryRecordDiagnosticAsync(
                phase: "debounced-sync",
                rawMessage: ex.InnerException?.Message ?? ex.Message,
                exception: ex,
                severity: "Warning");
        }
    }

    private void ObserveBackgroundTask(Task task, string operationName)
    {
        var observedTask = ObserveBackgroundTaskCoreAsync(task, operationName);
        lock (_immediateSyncGate)
        {
            _observedBackgroundTasks.Add(observedTask);
        }

        _ = observedTask.ContinueWith(
            static (completedTask, state) =>
            {
                var owner = (SyncService)state!;
                lock (owner._immediateSyncGate)
                {
                    owner._observedBackgroundTasks.Remove(completedTask);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Task StartBackgroundTaskWithoutExecutionContext(
        Func<Task> operation)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(operation);
    }

    private async Task ObserveBackgroundTaskCoreAsync(
        Task task,
        string operationName)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, compatibility changes, and newer requests may supersede background work.
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", $"{operationName} 실패", ex);
            await ObserveBackgroundTaskFailureAsync(operationName, ex)
                .ConfigureAwait(false);
        }
    }

    private async Task ObserveBackgroundTaskFailureAsync(string operationName, Exception ex)
    {
        if (IsDisposedContextException(ex))
            return;

        await TryRecordDiagnosticAsync(
            phase: "sync-background",
            rawMessage: $"{operationName}: {ex.InnerException?.Message ?? ex.Message}",
            exception: ex,
            severity: "Warning");
    }

    private async Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken ct)
    {
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            try
            {
                await operation(ct);
                if (attempt > 1)
                {
                    var recoveredMessage = $"동기화 {operationName} 복구 ({attempt}/{MaxRetryCount})";
                    SetStatus(recoveredMessage);
                    AppLogger.Info("SYNC", recoveredMessage);
                }
                return;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRetryCount)
            {
                var retryMessage = $"동기화 {operationName} 실패 ({attempt}/{MaxRetryCount}), {delay.TotalSeconds:0}초 후 재시도";
                SetStatus(retryMessage);
                AppLogger.Warn("SYNC", $"{retryMessage}: {ex.Message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
        }

        await operation(ct);
    }

    private void SetStatus(string message)
    {
        using var callbackBoundary =
            LocalDbContext.SuppressRuntimeMutationOwnerForCallback();
        SyncStatusChanged?.Invoke(message);
    }

    private static string BuildUserFacingSyncAttentionStatus(string? detail)
    {
        var normalized = (detail ?? string.Empty).Trim();
        const string suffix = "업무는 계속 가능하며, 상세 내용과 복구 방법은 동기화 진단에서 확인하세요.";

        if (normalized.Contains("Referenced customer was not found", StringComparison.OrdinalIgnoreCase))
            return $"동기화 확인 필요. 연결된 거래처를 찾을 수 없는 변경이 있습니다. {suffix}";

        if (normalized.Contains("outside the readable office scope", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("현재 계정 권한", StringComparison.Ordinal))
        {
            return $"동기화 확인 필요. 현재 계정 권한 또는 담당지점 범위로 반영할 수 없는 변경이 있습니다. {suffix}";
        }

        if (normalized.StartsWith("동기화 충돌 ", StringComparison.Ordinal))
        {
            var separatorIndex = normalized.IndexOf(':');
            var summary = separatorIndex > 0 ? normalized[..separatorIndex].Trim() : normalized;
            if (summary.Length <= 80)
                return $"동기화 확인 필요. {summary}이 남아 있습니다. {suffix}";
        }

        if (normalized.Contains("revision", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("동시 수정", StringComparison.Ordinal))
        {
            return $"동기화 확인 필요. 다른 PC에서 먼저 저장한 변경과 겹친 항목이 있습니다. {suffix}";
        }

        return $"동기화 확인 필요. 일부 변경을 서버에 반영하지 못했습니다. {suffix}";
    }

    private async Task<IReadOnlyList<DirtyOfficeSummary>> GetPendingDirtyOfficeSummariesOutsideCurrentSessionAsync(CancellationToken ct)
    {
        var currentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, _session.OfficeCode);
        return (await _local.GetDirtyOfficeSummariesAsync(ct))
            .Select(summary => new
            {
                Summary = summary,
                OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(summary.OfficeCode, summary.OfficeCode)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.OfficeCode))
            .Where(entry => !string.Equals(entry.OfficeCode, currentOfficeCode, StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tenantCode = group
                    .OrderByDescending(entry => entry.Summary.Count)
                    .Select(entry => entry.Summary.TenantCode)
                    .FirstOrDefault() ?? string.Empty;
                return new DirtyOfficeSummary(group.Key, tenantCode, group.Sum(entry => entry.Summary.Count));
            })
            .OrderByDescending(summary => summary.Count)
            .ThenBy(summary => summary.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<DirtyOfficeSummary>>
        GetPendingReconciliationOfficeSummariesOutsideCurrentSessionAsync(
            CancellationToken ct)
        => await BuildPendingReconciliationOfficeSummariesOutsideCurrentSessionAsync(
            await LoadEligibleOutboxReconciliationCandidatesAsync(ct),
            ct);

    private async Task<IReadOnlyList<DirtyOfficeSummary>>
        BuildPendingReconciliationOfficeSummariesOutsideCurrentSessionAsync(
            IReadOnlyList<LocalSyncOutboxEntry> outboxOwners,
            CancellationToken ct)
    {
        var currentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            _session.OfficeCode,
            _session.OfficeCode);
        var summaries = (await GetPendingDirtyOfficeSummariesOutsideCurrentSessionAsync(ct))
            .ToDictionary(
                summary => summary.OfficeCode,
                summary => summary,
                StringComparer.OrdinalIgnoreCase);

        foreach (var owner in outboxOwners)
        {
            if (!TryNormalizeOutboxReconciliationEntityScope(
                    owner.TenantCode,
                    owner.OfficeCode,
                    owner.ResponsibleOfficeCode,
                    out var ownerScope))
            {
                continue;
            }

            var ownerOfficeCodes = new[]
                {
                    ownerScope.OfficeCode,
                    ownerScope.ResponsibleOfficeCode
                }
                .Select(value =>
                    OfficeCodeCatalog.TryNormalizeOfficeCode(
                        value,
                        out var officeCode)
                        ? officeCode
                        : string.Empty)
                .Where(officeCode =>
                    !string.IsNullOrWhiteSpace(officeCode) &&
                    TenantScopeCatalog.TenantContainsOffice(
                        ownerScope.TenantCode,
                        officeCode) &&
                    !string.Equals(
                        officeCode,
                        currentOfficeCode,
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var officeCode in ownerOfficeCodes)
            {
                if (summaries.TryGetValue(officeCode, out var existing))
                {
                    summaries[officeCode] = existing with
                    {
                        Count = existing.Count + 1
                    };
                }
                else
                {
                    summaries[officeCode] = new DirtyOfficeSummary(
                        officeCode,
                        ownerScope.TenantCode,
                        1);
                }
            }
        }

        return summaries.Values
            .OrderByDescending(summary => summary.Count)
            .ThenBy(summary => summary.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<bool> HasPendingOutboxForSessionAsync(
        SessionState session,
        CancellationToken ct)
        => HasPendingOutboxForSession(
            session,
            await LoadEligibleOutboxReconciliationCandidatesAsync(ct));

    private async Task<bool> HasPendingReconciliationForOperationOwnerAsync(
        SyncOperationOwnerBoundary operationOwner,
        CancellationToken ct)
    {
        if (!IsSyncOperationOwnerCurrent(operationOwner))
            return false;

        return HasPendingOutboxForSession(
            _session,
            await LoadEligibleOutboxReconciliationCandidatesAsync(ct));
    }

    private static bool HasPendingOutboxForSession(
        SessionState session,
        IReadOnlyList<LocalSyncOutboxEntry> pendingOwners)
    {
        if (session.User is null || session.User.UserId == Guid.Empty)
            return false;

        var writableOfficeCodes = TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            session.AuthenticatedTenantCode,
            session.ScopeType,
            session.HasGlobalDataScope);

        return pendingOwners.Any(owner =>
            owner.UserId == session.User.UserId &&
            TryNormalizeOutboxReconciliationEntityScope(
                owner.TenantCode,
                owner.OfficeCode,
                owner.ResponsibleOfficeCode,
                out var ownerScope) &&
            (session.HasGlobalDataScope ||
             string.Equals(
                 ownerScope.TenantCode,
                 TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                     session.AuthenticatedTenantCode,
                     session.OfficeCode),
                 StringComparison.OrdinalIgnoreCase)) &&
            new[] { ownerScope.OfficeCode, ownerScope.ResponsibleOfficeCode }
                .Any(officeCode =>
                    OfficeCodeCatalog.TryNormalizeOfficeCode(
                        officeCode,
                        out var normalizedOfficeCode) &&
                    TenantScopeCatalog.TenantContainsOffice(
                        ownerScope.TenantCode,
                        normalizedOfficeCode) &&
                    writableOfficeCodes.Contains(normalizedOfficeCode)));
    }

    private async Task<IReadOnlyList<LocalSyncOutboxEntry>>
        LoadEligibleOutboxReconciliationCandidatesAsync(CancellationToken ct)
    {
        EligibleOutboxReconciliationCandidateLoadStartedForTesting?.Invoke();
        var currentDeviceId = (await _local.GetSettingAsync(
                DeviceIdSettingKey,
                ct) ?? string.Empty)
            .Trim();
        if (string.IsNullOrWhiteSpace(currentDeviceId))
            return [];

        var currentBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(
            _session.SelectedBusinessDatabaseName);
        var rows = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.Status != "Acknowledged" &&
                entry.EntityId != Guid.Empty)
            .ToListAsync(ct);
        var ownerCandidates = rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.EntityName) &&
                !string.IsNullOrWhiteSpace(row.OfficeCode) &&
                !string.IsNullOrWhiteSpace(row.ResponsibleOfficeCode) &&
                !string.IsNullOrWhiteSpace(row.BusinessDatabaseName) &&
                !string.IsNullOrWhiteSpace(row.DeviceId) &&
                !string.IsNullOrWhiteSpace(row.MutationId) &&
                row.SessionId != Guid.Empty &&
                row.UserId != Guid.Empty &&
                string.Equals(
                    row.DeviceId.Trim(),
                    currentDeviceId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    TenantScopeCatalog.GetDatabaseName(
                        row.BusinessDatabaseName),
                    currentBusinessDatabaseName,
                    StringComparison.OrdinalIgnoreCase) &&
                TryNormalizeOutboxReconciliationEntityScope(
                    row.TenantCode,
                    row.OfficeCode,
                    row.ResponsibleOfficeCode,
                    out _))
            .ToList();
        if (ownerCandidates.Count == 0)
            return [];

        var cleanEntityRevisions = new Dictionary<(string EntityName, Guid EntityId), long>();

        async Task AddCleanEntityKeysAsync<TLocal>()
            where TLocal : class, ILocalSyncEntity
        {
            var entityName = typeof(TLocal).Name;
            var ids = ownerCandidates
                .Where(row => string.Equals(
                    row.EntityName,
                    entityName,
                    StringComparison.Ordinal))
                .Select(row => row.EntityId)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
                return;

            var cleanEntities = await _db.Set<TLocal>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity => ids.Contains(entity.Id) && !entity.IsDirty)
                .Select(entity => new { entity.Id, entity.Revision })
                .ToListAsync(ct);
            foreach (var entity in cleanEntities)
                cleanEntityRevisions[(entityName, entity.Id)] = entity.Revision;
        }

        await AddCleanEntityKeysAsync<LocalCompanyProfile>();
        await AddCleanEntityKeysAsync<LocalUnit>();
        await AddCleanEntityKeysAsync<LocalCustomerCategory>();
        await AddCleanEntityKeysAsync<LocalPriceGradeOption>();
        await AddCleanEntityKeysAsync<LocalTradeTypeOption>();
        await AddCleanEntityKeysAsync<LocalItemCategoryOption>();
        await AddCleanEntityKeysAsync<LocalCustomerMaster>();
        await AddCleanEntityKeysAsync<LocalCustomer>();
        await AddCleanEntityKeysAsync<LocalCustomerContract>();
        await AddCleanEntityKeysAsync<LocalItem>();
        await AddCleanEntityKeysAsync<LocalItemPriceGrade>();
        await AddCleanEntityKeysAsync<LocalTransaction>();
        await AddCleanEntityKeysAsync<LocalTransactionAttachment>();
        await AddCleanEntityKeysAsync<LocalInventoryTransfer>();
        await AddCleanEntityKeysAsync<LocalRentalManagementCompany>();
        await AddCleanEntityKeysAsync<LocalRentalBillingProfile>();
        await AddCleanEntityKeysAsync<LocalRentalAsset>();
        await AddCleanEntityKeysAsync<LocalRentalAssetAssignmentHistory>();
        await AddCleanEntityKeysAsync<LocalRentalBillingLog>();
        await AddCleanEntityKeysAsync<LocalInvoice>();
        await AddCleanEntityKeysAsync<LocalPayment>();

        return ownerCandidates
            .Where(row =>
                cleanEntityRevisions.TryGetValue(
                    (row.EntityName, row.EntityId),
                    out var localRevision) &&
                localRevision > row.ExpectedRevision)
            .ToList();
    }

    private async Task<IReadOnlyList<StoredSyncCredential>> GetStoredCredentialsForPendingDirtyOfficesAsync(
        IReadOnlyList<DirtyOfficeSummary> pendingOfficeSummaries,
        CancellationToken ct)
    {
        if (pendingOfficeSummaries.Count == 0)
            return [];

        var pendingOffices = pendingOfficeSummaries
            .Select(summary => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(summary.OfficeCode, summary.OfficeCode))
            .Where(officeCode => !string.IsNullOrWhiteSpace(officeCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pendingOffices.Count == 0)
            return [];

        return (await _local.GetStoredSyncCredentialsAsync(ct))
            .Where(credential => pendingOffices.Contains(
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(credential.OfficeCode, credential.OfficeCode)))
            .OrderByDescending(credential => credential.SavedAtUtc)
            .ThenBy(credential => credential.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task InvalidateStoredOfficeCredentialAsync(StoredSyncCredential credential, CancellationToken ct)
    {
        await _local.ClearOfficeSyncCredentialAsync(credential.OfficeCode, ct);
        AppLogger.Info("SYNC", $"저장된 지점별 로그인 정보가 더 이상 유효하지 않아 제거했습니다: office={credential.OfficeCode}, username={credential.Username}");
        await TryRecordDiagnosticAsync(
            phase: "office-sync-login",
            rawMessage: $"저장된 지점별 로그인 정보 제거: {credential.OfficeCode} / {credential.Username}",
            severity: "Info");
    }

    private async Task PushDirtyWithStoredOfficeSessionsAsync(CancellationToken ct)
    {
        var remainingDirtyCount = await _local.CountDirtyAsync(ct);
        if (remainingDirtyCount == 0)
            return;

        var pendingOfficeSummaries = await GetPendingDirtyOfficeSummariesOutsideCurrentSessionAsync(ct);
        if (pendingOfficeSummaries.Count == 0)
            return;

        var storedCredentials = await GetStoredCredentialsForPendingDirtyOfficesAsync(pendingOfficeSummaries, ct);
        if (storedCredentials.Count == 0)
        {
            await ReportRemainingDirtyOfficesAsync("저장된 지점별 로그인 정보가 없어 일부 변경을 보류했습니다.", "missing_sync_credential", ct);
            return;
        }

        var attemptedOffices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, _session.OfficeCode)
        };

        foreach (var credential in storedCredentials)
        {
            ct.ThrowIfCancellationRequested();

            var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(credential.OfficeCode, credential.OfficeCode);
            if (!attemptedOffices.Add(normalizedOfficeCode))
                continue;

            try
            {
                var login = await AwaitWithTrackedChangesPreservedAsync(
                    () => _api.LoginAsync(credential.Username, credential.Password, ct));
                if (login is null || string.IsNullOrWhiteSpace(login.Token))
                {
                    await InvalidateStoredOfficeCredentialAsync(credential, ct);
                    continue;
                }

                var officeSession = new SessionState();
                officeSession.SetSession(login.Token, login.User, login.ExpiresAtUtc);

                var officeDirtyCount = await _local.CountDirtyAsync(officeSession, ct);
                if (officeDirtyCount == 0)
                    continue;

                using var officeHttpClient =
                    CreateOfficeSessionHttpClient();
                var officeApi = new ErpApiClient(officeHttpClient, officeSession);
                SetStatus($"{normalizedOfficeCode} 지점 변경분을 추가 동기화하는 중...");
                await ExecuteWithRetryAsync(
                    token => PushDirtyAsync(officeApi, officeSession, includeSharedDirty: false, token),
                    $"{normalizedOfficeCode} 지점 업로드",
                    ct);
            }
            catch (DesktopClientUpgradeRequiredException)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLogger.Warn("SYNC", $"지점별 추가 동기화 확인 필요: office={normalizedOfficeCode}, detail={ex.Message}");
                await TryRecordDiagnosticAsync(
                    phase: "office-sync",
                    rawMessage: $"지점별 추가 동기화 확인 필요({normalizedOfficeCode}): {ex.InnerException?.Message ?? ex.Message}",
                    exception: ex,
                    severity: "Warning");
            }
        }

        await ReportRemainingDirtyOfficesAsync(null, "remaining_dirty", ct);
    }

    private async Task ClearStaleDirtyWithStoredOfficeSessionsAsync(CancellationToken ct)
    {
        IReadOnlyList<LocalSyncOutboxEntry>? candidateSnapshot = null;

        async Task<IReadOnlyList<LocalSyncOutboxEntry>> GetCandidatesAsync()
        {
            candidateSnapshot ??=
                await LoadEligibleOutboxReconciliationCandidatesAsync(ct);
            return candidateSnapshot;
        }

        void InvalidateCandidates() => candidateSnapshot = null;

        var candidates = await GetCandidatesAsync();
        if (AfterInitialOutboxReconciliationCandidateSnapshotLoadedAsyncForTesting
            is not null)
        {
            await AfterInitialOutboxReconciliationCandidateSnapshotLoadedAsyncForTesting(
                ct);
        }

        var currentSessionHasPendingOutbox =
            HasPendingOutboxForSession(_session, candidates);
        var currentSessionHasPendingWork =
            await _local.CountDirtyAsync(_session, ct) > 0 ||
            currentSessionHasPendingOutbox;
        var pendingOfficeSummaries =
            await BuildPendingReconciliationOfficeSummariesOutsideCurrentSessionAsync(
                candidates,
                ct);
        if (!currentSessionHasPendingWork && pendingOfficeSummaries.Count == 0)
            return;

        if (currentSessionHasPendingWork)
        {
            try
            {
                await ClearStaleDirtyCoreAsync(
                    _api,
                    _session,
                    includeSharedDirty: true,
                    currentSessionHasPendingOutbox,
                    ct);
            }
            finally
            {
                InvalidateCandidates();
            }

            candidates = await GetCandidatesAsync();
            pendingOfficeSummaries =
                await BuildPendingReconciliationOfficeSummariesOutsideCurrentSessionAsync(
                    candidates,
                    ct);
        }

        if (pendingOfficeSummaries.Count == 0)
            return;

        var storedCredentials = await GetStoredCredentialsForPendingDirtyOfficesAsync(pendingOfficeSummaries, ct);
        if (storedCredentials.Count == 0)
            return;

        var attemptedOffices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, _session.OfficeCode)
        };

        foreach (var credential in storedCredentials)
        {
            ct.ThrowIfCancellationRequested();

            var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(credential.OfficeCode, credential.OfficeCode);
            if (!attemptedOffices.Add(normalizedOfficeCode))
                continue;

            try
            {
                var login = await AwaitWithTrackedChangesPreservedAsync(
                    () => _api.LoginAsync(credential.Username, credential.Password, ct));
                if (login is null || string.IsNullOrWhiteSpace(login.Token))
                {
                    await InvalidateStoredOfficeCredentialAsync(credential, ct);
                    continue;
                }

                var officeSession = new SessionState();
                officeSession.SetSession(login.Token, login.User, login.ExpiresAtUtc);
                candidates = await GetCandidatesAsync();
                var officeSessionHasPendingOutbox =
                    HasPendingOutboxForSession(officeSession, candidates);
                if (await _local.CountDirtyAsync(officeSession, ct) == 0 &&
                    !officeSessionHasPendingOutbox)
                    continue;

                using var officeHttpClient =
                    CreateOfficeSessionHttpClient();
                var officeApi = new ErpApiClient(officeHttpClient, officeSession);
                try
                {
                    await ClearStaleDirtyCoreAsync(
                        officeApi,
                        officeSession,
                        includeSharedDirty: false,
                        officeSessionHasPendingOutbox,
                        ct);
                }
                finally
                {
                    InvalidateCandidates();
                }
            }
            catch (DesktopClientUpgradeRequiredException)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLogger.Warn("SYNC", $"stale dirty 정리 실패: office={normalizedOfficeCode}, detail={ex.Message}");
                await TryRecordDiagnosticAsync(
                    phase: "stale-dirty",
                    rawMessage: $"stale dirty 정리 실패({normalizedOfficeCode}): {ex.InnerException?.Message ?? ex.Message}",
                    exception: ex,
                    severity: "Warning");
            }
        }
    }

    private async Task ClearStaleDirtyAsync(
        ErpApiClient apiClient,
        SessionState session,
        bool includeSharedDirty,
        CancellationToken ct)
        => await ClearStaleDirtyCoreAsync(
            apiClient,
            session,
            includeSharedDirty,
            HasPendingOutboxForSession(
                session,
                await LoadEligibleOutboxReconciliationCandidatesAsync(ct)),
            ct);

    private async Task ClearStaleDirtyCoreAsync(
        ErpApiClient apiClient,
        SessionState session,
        bool includeSharedDirty,
        bool hasPendingOutbox,
        CancellationToken ct)
    {
        var sessionDirtyCount = await _local.CountDirtyAsync(session, ct);
        if (sessionDirtyCount == 0 &&
            !hasPendingOutbox &&
            (!includeSharedDirty || !session.HasAdministrativePrivileges))
            return;

        var pull = await AwaitWithTrackedChangesPreservedAsync(
            () => apiClient.PullAsync(0, ct));
        if (pull is null)
            return;

        // A stock replay guard may have been established by the upload that
        // immediately precedes this stale-dirty cleanup. Revalidate after the
        // network wait and before clearing any local pending state so a
        // separate-context stock edit cannot be followed by another pull or
        // by cleanup mutations based on the now-stale response.
        await EnsureItemWarehouseStockReplayPullGuardUnchangedAsync(ct);

        using (_local.SuppressSyncDispatch())
        {
            var clearedCount = 0;

            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyCustomerMastersForSyncAsync(session, ct), pull.CustomerMasters, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyCustomersForSyncAsync(session, ct), pull.Customers, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyCustomerContractsForSyncAsync(session, ct), pull.CustomerContracts, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyItemsForSyncAsync(session, ct), pull.Items, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyTransactionsForSyncAsync(session, ct), pull.Transactions, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyTransactionAttachmentsForSyncAsync(session, ct), pull.TransactionAttachments, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyInventoryTransfersForSyncAsync(session, ct), pull.InventoryTransfers, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyRentalBillingProfilesForSyncAsync(session, ct), pull.RentalBillingProfiles, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyRentalAssetsForSyncAsync(session, ct), pull.RentalAssets, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyRentalAssetAssignmentHistoriesForSyncAsync(session, ct), pull.RentalAssetAssignmentHistories, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyRentalBillingLogsForSyncAsync(session, ct), pull.RentalBillingLogs, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyInvoicesForSyncAsync(session, ct), pull.Invoices, ct);
            clearedCount += await ClearStaleDirtyEntitiesAsync(await _local.GetDirtyPaymentsForSyncAsync(session, ct), pull.Payments, ct);

            if (includeSharedDirty && session.HasAdministrativePrivileges)
            {
                clearedCount += await ClearStaleDirtyEntitiesAsync(
                    await _db.CompanyProfiles.IgnoreQueryFilters().Where(entity => entity.IsDirty).ToListAsync(ct),
                    pull.CompanyProfiles,
                    ct);
                clearedCount += await ClearStaleDirtyEntitiesAsync(
                    await _db.Units.IgnoreQueryFilters().Where(entity => entity.IsDirty).ToListAsync(ct),
                    pull.Units,
                    ct);
                clearedCount += await ClearStaleDirtyEntitiesAsync(
                    await _db.CustomerCategories.IgnoreQueryFilters().Where(entity => entity.IsDirty).ToListAsync(ct),
                    pull.CustomerCategories,
                    ct);
                clearedCount += await ClearStaleDirtyEntitiesAsync(
                    await _db.PriceGradeOptions.IgnoreQueryFilters().Where(entity => entity.IsDirty).ToListAsync(ct),
                    pull.PriceGradeOptions,
                    ct);
                clearedCount += await ClearStaleDirtyEntitiesAsync(
                    await _db.TradeTypeOptions.IgnoreQueryFilters().Where(entity => entity.IsDirty).ToListAsync(ct),
                    pull.TradeTypeOptions,
                    ct);
                clearedCount += await ClearStaleDirtyEntitiesAsync(
                    await _db.ItemCategoryOptions.IgnoreQueryFilters().Where(entity => entity.IsDirty).ToListAsync(ct),
                    pull.ItemCategoryOptions,
                    ct);
                clearedCount += await ClearStaleDirtyEntitiesAsync(
                    await _db.RentalManagementCompanies.IgnoreQueryFilters().Where(entity => entity.IsDirty).ToListAsync(ct),
                    pull.RentalManagementCompanies,
                    ct);
            }

            if (await _db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Status != "Acknowledged", ct))
            {
                var reconciliationDeviceId = await GetOrCreateDeviceIdAsync(ct);
                var reconciliationBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                    session.SelectedBusinessDatabaseName);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomerMaster, CustomerMasterDto>(pull.CustomerMasters, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(pull.Customers, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomerContract, CustomerContractDto>(pull.CustomerContracts, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalItem, ItemDto>(pull.Items, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalItemPriceGrade, ItemPriceGradeDto>(pull.ItemPriceGrades, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalTransaction, TransactionDto>(pull.Transactions, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalTransactionAttachment, TransactionAttachmentDto>(pull.TransactionAttachments, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalInventoryTransfer, InventoryTransferDto>(pull.InventoryTransfers, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalRentalBillingProfile, RentalBillingProfileDto>(pull.RentalBillingProfiles, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalRentalAsset, RentalAssetDto>(pull.RentalAssets, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalRentalAssetAssignmentHistory, RentalAssetAssignmentHistoryDto>(pull.RentalAssetAssignmentHistories, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalRentalBillingLog, RentalBillingLogDto>(pull.RentalBillingLogs, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalInvoice, InvoiceDto>(pull.Invoices, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalPayment, PaymentDto>(pull.Payments, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);

                if (includeSharedDirty && session.HasAdministrativePrivileges)
                {
                    clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCompanyProfile, CompanyProfileDto>(pull.CompanyProfiles, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                    clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalUnit, UnitDto>(pull.Units, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                    clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomerCategory, CustomerCategoryDto>(pull.CustomerCategories, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                    clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalPriceGradeOption, PriceGradeOptionDto>(pull.PriceGradeOptions, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                    clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalTradeTypeOption, TradeTypeOptionDto>(pull.TradeTypeOptions, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                    clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalItemCategoryOption, ItemCategoryOptionDto>(pull.ItemCategoryOptions, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                    clearedCount += await MarkOutboxAcknowledgedForCleanEntitiesAsync<LocalRentalManagementCompany, RentalManagementCompanyDto>(pull.RentalManagementCompanies, session, reconciliationDeviceId, reconciliationBusinessDatabaseName, ct);
                }
            }

            if (clearedCount > 0)
            {
                AppLogger.Info("SYNC", $"stale dirty 자동정리: office={session.OfficeCode}, cleaned={clearedCount}");
                PreservePendingTrackedChangesForSync();
                await TryRecordDiagnosticAsync(
                    phase: "stale-dirty-repair",
                    rawMessage: $"stale dirty 자동정리 완료: office={OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, session.OfficeCode)}, cleaned={clearedCount}",
                    severity: "Warning",
                    recoveryAttempted: true,
                    recoverySucceeded: true);
            }
        }
    }

    private async Task<int> ClearStaleDirtyEntitiesAsync<TLocal, TDto>(
        IReadOnlyCollection<TLocal> dirtyEntities,
        IReadOnlyCollection<TDto> serverEntities,
        CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        if (dirtyEntities.Count == 0 || serverEntities.Count == 0)
            return 0;

        var dirtyIds = dirtyEntities.Select(entity => entity.Id).Distinct().ToList();
        if (dirtyIds.Count == 0)
            return 0;

        var serverMap = serverEntities
            .Where(entity => dirtyIds.Contains(entity.Id))
            .ToDictionary(entity => entity.Id, entity => entity);
        if (serverMap.Count == 0)
            return 0;

        var changed = 0;
        var cleanedIds = new HashSet<Guid>();
        foreach (var entityId in serverMap.Keys)
        {
            var entity = await LoadCurrentSyncEntitySnapshotAsync<TLocal>(entityId, ct);
            if (entity is null)
                continue;

            if (!entity.IsDirty || !serverMap.TryGetValue(entity.Id, out var serverEntity))
                continue;

            var localEntityName = NormalizeSyncEntityName(typeof(TLocal).Name);
            var persistedSnapshot = new PreparedMutationSnapshot(
                entity.Revision,
                NormalizeMutationUtc(entity.UpdatedAtUtc),
                entity.IsDeleted,
                ComputePreparedMutationPayloadHash(
                    localEntityName,
                    MapLocalEntityToPreparedMutationDto(entity)),
                InvoiceNumber: entity is LocalInvoice invoice
                    ? invoice.InvoiceNumber
                    : null,
                TaxInvoiceNumber: entity is LocalInvoice taxInvoice
                    ? taxInvoice.TaxInvoiceNumber
                    : null);
            if (HasPendingTrackedMutationAfterPush<TLocal>(
                    entity.Id,
                    localEntityName,
                    persistedSnapshot))
            {
                var key = new SyncEntityKey(localEntityName, entity.Id);
                if (serverEntity.Revision > 0 &&
                    _trackedMutationsPreservedDuringSync.TryGetValue(key, out var preservation))
                {
                    await _db.Set<TLocal>()
                        .IgnoreQueryFilters()
                        .Where(current =>
                            current.Id == entity.Id &&
                            current.IsDirty &&
                            current.Revision < serverEntity.Revision)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(
                                current => current.Revision,
                                serverEntity.Revision),
                            ct);
                    preservation.RebaseAcceptedRevision(serverEntity.Revision);
                }

                continue;
            }

            // Revision만 같다는 이유로 dirty를 지우면 아직 전송되지 않은 로컬 재편집을 잃을 수 있다.
            // 상태와 실제 payload가 모두 서버 스냅샷과 일치할 때만 정리한다.
            if (!IsStaleDirtyMatch(entity, serverEntity) ||
                !IsStaleDirtyPayloadMatch(entity, serverEntity))
            {
                continue;
            }

            var expectedRevision = entity.Revision;
            var expectedUpdatedAtUtc = NormalizeMutationUtc(entity.UpdatedAtUtc);
            var expectedIsDeleted = entity.IsDeleted;
            var serverUpdatedAtUtc = NormalizeMutationUtc(serverEntity.UpdatedAtUtc);
            var affected = await _db.Set<TLocal>()
                .IgnoreQueryFilters()
                .Where(current =>
                    current.Id == entity.Id &&
                    current.IsDirty &&
                    current.Revision == expectedRevision &&
                    current.UpdatedAtUtc == expectedUpdatedAtUtc &&
                    current.IsDeleted == expectedIsDeleted)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(current => current.Revision, serverEntity.Revision)
                        .SetProperty(current => current.UpdatedAtUtc, serverUpdatedAtUtc)
                        .SetProperty(current => current.IsDeleted, serverEntity.IsDeleted)
                        .SetProperty(current => current.IsDirty, false),
                    ct);
            if (affected <= 0)
                continue;

            cleanedIds.Add(entity.Id);
            changed += affected;
        }

        DetachTrackedEntities<TLocal>(cleanedIds);

        return changed;
    }

    private async Task<int> MarkOutboxAcknowledgedForCleanEntitiesAsync<TLocal, TDto>(
        IReadOnlyCollection<TDto> serverEntities,
        SessionState session,
        string deviceId,
        string businessDatabaseName,
        CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        if (serverEntities.Count == 0)
            return 0;

        var serverMap = serverEntities
            .Where(entity => entity.Id != Guid.Empty)
            .GroupBy(entity => entity.Id)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(entity => entity.Revision)
                    .ThenByDescending(entity => entity.UpdatedAtUtc)
                    .First());
        if (serverMap.Count == 0)
            return 0;

        var entityName = typeof(TLocal).Name;
        var rows = await _db.SyncOutboxEntries
            .Where(entry => entry.EntityName == entityName && entry.Status != "Acknowledged")
            .ToListAsync(ct);
        if (rows.Count == 0)
            return 0;

        var scopeRequest = BuildPreparedMutationScopeRequest(serverEntities);
        var scopeLookup = await BuildPreparedMutationScopeLookupAsync(
            _db,
            scopeRequest,
            session,
            ct);

        var entityIds = rows
            .Select(entry => entry.EntityId)
            .Where(id => id != Guid.Empty && serverMap.ContainsKey(id))
            .Distinct()
            .ToList();
        if (entityIds.Count == 0)
            return 0;

        var cleanEntities = await _db.Set<TLocal>()
            .IgnoreQueryFilters()
            .Where(entity => entityIds.Contains(entity.Id) && !entity.IsDirty)
            .ToListAsync(ct);
        if (cleanEntities.Count == 0)
            return 0;

        var reconciledEntityIds = new HashSet<Guid>();
        foreach (var entity in cleanEntities)
        {
            if (!serverMap.TryGetValue(entity.Id, out var serverEntity))
                continue;

            if (entity.IsDeleted == serverEntity.IsDeleted &&
                IsStaleDirtyPayloadMatch(entity, serverEntity))
            {
                reconciledEntityIds.Add(entity.Id);
            }
        }

        if (reconciledEntityIds.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var changed = 0;
        foreach (var row in rows)
        {
            if (!reconciledEntityIds.Contains(row.EntityId))
                continue;

            var serverEntity = serverMap[row.EntityId];
            if (serverEntity.Revision <= row.ExpectedRevision ||
                !TryBuildOutboxReconciliationOwnerScope(
                    row,
                    out var rowScope) ||
                !TryBuildActivePullOutboxReconciliationOwnerScope(
                    serverEntity,
                    session,
                    deviceId,
                    businessDatabaseName,
                    scopeLookup,
                    out var activePullScope) ||
                rowScope != activePullScope)
            {
                continue;
            }

            row.Status = "Acknowledged";
            row.AcknowledgedAtUtc = now;
            row.AcceptedRevision = serverEntity.Revision;
            row.AcceptedUpdatedAtUtc = NormalizeMutationUtc(serverEntity.UpdatedAtUtc);
            row.ErrorMessage = string.Empty;
            changed++;
        }

        if (changed > 0)
            await _db.SaveChangesAsync(ct);

        return changed;
    }

    private static SyncPushRequest BuildPreparedMutationScopeRequest<TDto>(
        IReadOnlyCollection<TDto> serverEntities)
        where TDto : SyncEntityDto
    {
        var request = new SyncPushRequest();
        switch (serverEntities)
        {
            case IReadOnlyCollection<CustomerContractDto> customerContracts:
                request.CustomerContracts.AddRange(customerContracts);
                break;
            case IReadOnlyCollection<ItemPriceGradeDto> itemPriceGrades:
                request.ItemPriceGrades.AddRange(itemPriceGrades);
                break;
            case IReadOnlyCollection<TransactionAttachmentDto> transactionAttachments:
                request.TransactionAttachments.AddRange(transactionAttachments);
                break;
            case IReadOnlyCollection<PaymentDto> payments:
                request.Payments.AddRange(payments);
                break;
        }

        return request;
    }

    private bool TryBuildActivePullOutboxReconciliationOwnerScope(
        SyncEntityDto serverEntity,
        SessionState session,
        string deviceId,
        string businessDatabaseName,
        PreparedMutationScopeLookup scopeLookup,
        out OutboxReconciliationOwnerScope scope)
    {
        scope = default;
        if (!TryResolveOutboxReconciliationScope(
                serverEntity,
                session,
                scopeLookup,
                out var preparedScope))
        {
            return false;
        }
        var normalizedBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(
            businessDatabaseName);
        var tenantCode = serverEntity is PriceGradeOptionDto
            ? TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                normalizedBusinessDatabaseName,
                preparedScope.TenantCode)
            : preparedScope.TenantCode;
        var activePullOwner = new LocalSyncOutboxEntry
        {
            TenantCode = tenantCode,
            OfficeCode = preparedScope.OfficeCode,
            ResponsibleOfficeCode = preparedScope.ResponsibleOfficeCode,
            BusinessDatabaseName = normalizedBusinessDatabaseName,
            DeviceId = deviceId,
            MutationId = "active-pull-owner",
            SessionId = session.SessionId,
            UserId = session.User?.UserId ?? Guid.Empty
        };
        return TryBuildOutboxReconciliationOwnerScope(
            activePullOwner,
            out scope);
    }

    private static bool IsStaleDirtyMatch(ILocalSyncEntity localEntity, SyncEntityDto serverEntity)
    {
        if (localEntity.Id != serverEntity.Id)
            return false;
        if (localEntity.IsDeleted != serverEntity.IsDeleted)
            return false;
        if (localEntity.Revision == serverEntity.Revision)
            return true;

        return localEntity.Revision <= serverEntity.Revision &&
               AreEquivalentUtc(localEntity.UpdatedAtUtc, serverEntity.UpdatedAtUtc);
    }

    private static bool IsStaleDirtyPayloadMatch<TLocal, TDto>(TLocal localEntity, TDto serverEntity)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        return TryMapLocalEntityToDto(localEntity) is TDto localDto &&
               AreEquivalentConflictPayloads(localDto, serverEntity);
    }

    private static SyncEntityDto? TryMapLocalEntityToDto(ILocalSyncEntity entity)
        => entity switch
        {
            LocalCompanyProfile value => LocalMappings.ToDto(value),
            LocalUnit value => LocalMappings.ToDto(value),
            LocalCustomerCategory value => LocalMappings.ToDto(value),
            LocalPriceGradeOption value => LocalMappings.ToDto(value),
            LocalTradeTypeOption value => LocalMappings.ToDto(value),
            LocalItemCategoryOption value => LocalMappings.ToDto(value),
            LocalCustomerMaster value => LocalMappings.ToDto(value),
            LocalCustomer value => LocalMappings.ToDto(value),
            LocalCustomerContract value => LocalMappings.ToDto(value),
            LocalItem value => LocalMappings.ToDto(value),
            LocalItemPriceGrade value => LocalMappings.ToDto(value),
            LocalTransaction value => LocalMappings.ToDto(value),
            LocalTransactionAttachment value => LocalMappings.ToDto(value),
            LocalInventoryTransfer value => LocalMappings.ToDto(value),
            LocalRentalManagementCompany value => LocalMappings.ToDto(value),
            LocalRentalBillingProfile value => LocalMappings.ToDto(value),
            LocalRentalAsset value => LocalMappings.ToDto(value),
            LocalRentalAssetAssignmentHistory value => LocalMappings.ToDto(value),
            LocalRentalBillingLog value => LocalMappings.ToDto(value),
            LocalInvoice value => LocalMappings.ToDto(value),
            LocalPayment value => LocalMappings.ToDto(value),
            _ => null
        };

    private static bool AreEquivalentUtc(DateTime left, DateTime right)
        => Math.Abs((left.ToUniversalTime() - right.ToUniversalTime()).TotalSeconds) < 1;

    private async Task ReportRemainingDirtyOfficesAsync(string? prefix, string? diagnosticReason, CancellationToken ct)
    {
        var remainingDirtyCount = await _local.CountDirtyAsync(ct);
        if (remainingDirtyCount == 0)
            return;

        if (!string.IsNullOrWhiteSpace(diagnosticReason))
            await TryRecordPendingScopeDiagnosticsAsync(diagnosticReason, ct);

        var officeSummaries = await _local.GetDirtyOfficeSummariesAsync(ct);
        if (officeSummaries.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(prefix)
                ? $"일부 변경 {remainingDirtyCount}건이 아직 남아 있습니다."
                : $"{prefix} 남은 변경 {remainingDirtyCount}건을 확인하세요.";
            SetStatus(message);
            AppLogger.Warn("SYNC", message);
            return;
        }

        var detail = string.Join(", ",
            officeSummaries
                .Take(5)
                .Select(summary => $"{summary.OfficeCode} {summary.Count}건"));
        var status = string.IsNullOrWhiteSpace(prefix)
            ? $"일부 지점 변경이 남아 있습니다: {detail}"
            : $"{prefix} ({detail})";

        SetStatus(status);
        AppLogger.Warn("SYNC", $"미동기화 지점별 변경 감지: total={remainingDirtyCount}, detail={detail}");
    }

    private async Task TryRecordPendingScopeDiagnosticsAsync(string diagnosticReason, CancellationToken ct)
    {
        var pendingSummary = await _local.GetPendingSyncSummaryAsync(ct);
        if (pendingSummary.TotalCount == 0)
            return;

        foreach (var scopeGroup in pendingSummary.Buckets
                     .GroupBy(bucket => bucket.ScopeKey, StringComparer.OrdinalIgnoreCase))
        {
            await TryRecordPendingScopeDiagnosticAsync(
                scopeGroup.Key,
                scopeGroup.Sum(bucket => bucket.Count),
                diagnosticReason);
        }
    }

    private async Task TryRecordPendingScopeDiagnosticAsync(string scopeKey, int count, string diagnosticReason)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || count <= 0)
            return;

        var officeCode = ResolveScopeRequiredOfficeCode(scopeKey);
        var tenantCode = ResolveScopeTenantCode(scopeKey);
        var rawMessage = string.Equals(diagnosticReason, "missing_sync_credential", StringComparison.OrdinalIgnoreCase)
            ? $"저장된 지점 동기화 계정 없음으로 dirty 보류: scope={scopeKey}, office={officeCode}, tenant={tenantCode}, count={count}"
            : $"동기화 후 dirty 잔존: scope={scopeKey}, office={officeCode}, tenant={tenantCode}, count={count}";

        await TryRecordDiagnosticAsync(
            phase: "pending-scope",
            rawMessage: rawMessage,
            severity: "Warning");
    }

    private static string ResolveScopeRequiredOfficeCode(string scopeKey)
    {
        if (scopeKey.StartsWith("OFFICE:", StringComparison.OrdinalIgnoreCase))
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(scopeKey[7..], string.Empty);

        if (scopeKey.StartsWith("TENANT:", StringComparison.OrdinalIgnoreCase))
        {
            var tenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(scopeKey[7..], string.Empty);
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode).FirstOrDefault(),
                string.Empty);
        }

        return string.Empty;
    }

    private static string ResolveScopeTenantCode(string scopeKey)
    {
        if (scopeKey.StartsWith("TENANT:", StringComparison.OrdinalIgnoreCase))
            return TenantScopeCatalog.NormalizeTenantCodeOrDefault(scopeKey[7..], string.Empty);

        var requiredOfficeCode = ResolveScopeRequiredOfficeCode(scopeKey);
        return string.IsNullOrWhiteSpace(requiredOfficeCode)
            ? string.Empty
            : TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(string.Empty, requiredOfficeCode);
    }

    private sealed record RentalTenantSyncPayload(
        string BusinessDatabaseName,
        List<CustomerDto> Customers,
        List<PriceGradeOptionDto> PriceGradeOptions,
        List<ItemDto> Items,
        List<ItemPriceGradeDto> ItemPriceGrades,
        List<ItemWarehouseStockDto> ItemWarehouseStocks,
        List<RentalManagementCompanyDto> ManagementCompanies,
        List<RentalBillingProfileDto> BillingProfiles,
        List<RentalAssetDto> Assets,
        List<RentalAssetAssignmentHistoryDto> AssignmentHistories,
        List<RentalBillingLogDto> BillingLogs);

    private async Task MarkDirtyItemCatalogExtensionsPendingAsync(
        IReadOnlyList<LocalItem> dirtyItems,
        bool hasObservedCatalogExtensionCapability,
        CancellationToken ct)
    {
        if (dirtyItems.Count == 0)
            return;

        foreach (var item in dirtyItems)
        {
            if (item.IsDeleted)
                item.CatalogExtensionSyncPending = false;
            else if (hasObservedCatalogExtensionCapability)
                item.CatalogExtensionSyncPending = true;
        }

        if (hasObservedCatalogExtensionCapability)
        {
            var activeItemIds = dirtyItems
                .Where(item => !item.IsDeleted && item.Id != Guid.Empty)
                .Select(item => item.Id)
                .Distinct()
                .ToList();
            if (activeItemIds.Count > 0)
            {
                await _db.Items
                    .IgnoreQueryFilters()
                    .Where(item =>
                        item.IsDirty &&
                        !item.IsDeleted &&
                        activeItemIds.Contains(item.Id))
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            item => item.CatalogExtensionSyncPending,
                            true),
                        ct);
            }
        }

        var deletedItemIds = dirtyItems
            .Where(item => item.IsDeleted && item.Id != Guid.Empty)
            .Select(item => item.Id)
            .Distinct()
            .ToList();
        if (deletedItemIds.Count > 0)
        {
            await _db.Items
                .IgnoreQueryFilters()
                .Where(item =>
                    item.IsDirty &&
                    item.IsDeleted &&
                    deletedItemIds.Contains(item.Id))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.CatalogExtensionSyncPending,
                        false),
                    ct);
        }
    }

    private static ItemDto MapItemForOutboundSync(LocalItem item)
    {
        var dto = LocalMappings.ToDto(item);
        if (item.CatalogExtensionSyncPending)
            return dto;

        dto.BoxQuantity = null;
        dto.StorageLocation = null;
        dto.LastPurchaseDate = null;
        dto.LastPurchaseDateSpecified = null;
        dto.LastSaleDate = null;
        dto.LastSaleDateSpecified = null;
        return dto;
    }

    private async Task PushDirtyAsync(
        ErpApiClient apiClient,
        SessionState session,
        bool includeSharedDirty,
        CancellationToken ct)
    {
        var customerMasterRepair = await _local.RepairDirtyCustomerMastersForSyncAsync(session, ct);
        if (customerMasterRepair.SkippedOutOfScopeCount > 0 ||
            customerMasterRepair.MarkedCleanOutOfScopeCount > 0 ||
            customerMasterRepair.ClearedMissingCategoryCount > 0 ||
            customerMasterRepair.NormalizedScopeCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 거래처 기준정보 보정: scanned={customerMasterRepair.ScannedCount}, " +
                $"normalizedScope={customerMasterRepair.NormalizedScopeCount}, " +
                $"clearedMissingCategory={customerMasterRepair.ClearedMissingCategoryCount}, " +
                $"clearedOutOfScopeDirty={customerMasterRepair.MarkedCleanOutOfScopeCount}, " +
                $"skippedOutOfScopeDirty={customerMasterRepair.SkippedOutOfScopeCount}");
        }

        var customerRepair = await _local.RepairDirtyCustomersForSyncAsync(session, ct);
        if (customerRepair.SkippedOutOfScopeCount > 0 ||
            customerRepair.MarkedCleanOutOfScopeCount > 0 ||
            customerRepair.ClearedMissingCategoryCount > 0 ||
            customerRepair.ClearedMissingCustomerMasterCount > 0 ||
            customerRepair.NormalizedScopeCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 거래처 보정: scanned={customerRepair.ScannedCount}, " +
                $"normalizedScope={customerRepair.NormalizedScopeCount}, " +
                $"clearedMissingCategory={customerRepair.ClearedMissingCategoryCount}, " +
                $"clearedMissingCustomerMaster={customerRepair.ClearedMissingCustomerMasterCount}, " +
                $"clearedOutOfScopeDirty={customerRepair.MarkedCleanOutOfScopeCount}, " +
                $"skippedOutOfScopeDirty={customerRepair.SkippedOutOfScopeCount}");
        }

        var scopedDirtyRentalAssetIds = (await _local.GetDirtyRentalAssetsForOutboundSyncAsync(session, ct))
            .Where(asset => !asset.IsDeleted)
            .Select(asset => asset.Id)
            .Distinct()
            .ToList();

        if (scopedDirtyRentalAssetIds.Count > 0)
        {
            var rentalRepair = await _rental.RepairRentalCatalogLinksAsync(scopedDirtyRentalAssetIds, session, ct);
            if (rentalRepair.UpdatedAssetCount > 0 ||
                rentalRepair.AddedItemNames.Count > 0 ||
                rentalRepair.AmbiguousItemNames.Count > 0)
            {
                AppLogger.Warn(
                    "SYNC",
                    $"동기화 전 렌탈 자산 품목 보정: scanned={rentalRepair.ScannedAssetCount}, " +
                    $"updatedAssets={rentalRepair.UpdatedAssetCount}, " +
                    $"addedItems={rentalRepair.AddedItemNames.Count}, " +
                    $"ambiguousItems={rentalRepair.AmbiguousItemNames.Count}");
            }
        }

        var transactionRepair = await _local.RepairDirtyTransactionsForSyncAsync(session, ct);
        if (transactionRepair.ClearedMissingInvoiceLinkCount > 0 ||
            transactionRepair.ClearedMissingRentalLinkCount > 0 ||
            transactionRepair.ResolvedMissingCustomerCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 거래내역 참조 보정: scanned={transactionRepair.ScannedCount}, " +
                $"clearedInvoiceLinks={transactionRepair.ClearedMissingInvoiceLinkCount}, " +
                $"clearedRentalLinks={transactionRepair.ClearedMissingRentalLinkCount}, " +
                $"resolvedCustomers={transactionRepair.ResolvedMissingCustomerCount}");
        }

        var duplicateLatestInvoiceRepairCount = await _local.RepairDuplicateLatestInvoiceVersionGroupsForSyncAsync(session, ct);
        if (duplicateLatestInvoiceRepairCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전표 최신 버전 중복 보정: changed={duplicateLatestInvoiceRepairCount}");
        }

        var invoiceRepair = await _local.RepairDirtyInvoicesForSyncAsync(session, ct);
        if (invoiceRepair.ResolvedMissingCustomerCount > 0 ||
            invoiceRepair.SkippedOutOfScopeCount > 0 ||
            invoiceRepair.MarkedCleanOutOfScopeCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 전표 참조 보정: scanned={invoiceRepair.ScannedCount}, " +
                $"resolvedCustomers={invoiceRepair.ResolvedMissingCustomerCount}, " +
                $"clearedOutOfScopeDirty={invoiceRepair.MarkedCleanOutOfScopeCount}, " +
                $"skippedOutOfScopeDirty={invoiceRepair.SkippedOutOfScopeCount}");
        }

        var transactionAttachmentRepair = await _local.RepairDirtyTransactionAttachmentsForSyncAsync(session, ct);
        if (transactionAttachmentRepair.MarkedDeletedMissingTransactionCount > 0 ||
            transactionAttachmentRepair.MarkedCleanStaleDeletedCount > 0 ||
            transactionAttachmentRepair.SkippedOutOfScopeCount > 0 ||
            transactionAttachmentRepair.MarkedCleanOutOfScopeCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 증빙 참조 보정: scanned={transactionAttachmentRepair.ScannedCount}, " +
                $"markedDeletedMissingTransaction={transactionAttachmentRepair.MarkedDeletedMissingTransactionCount}, " +
                $"cleanedStaleDeleted={transactionAttachmentRepair.MarkedCleanStaleDeletedCount}, " +
                $"clearedOutOfScopeDirty={transactionAttachmentRepair.MarkedCleanOutOfScopeCount}, " +
                $"skippedOutOfScopeDirty={transactionAttachmentRepair.SkippedOutOfScopeCount}");
        }

        var paymentRepair = await _local.RepairDirtyPaymentsForSyncAsync(session, ct);
        if (paymentRepair.MarkedDeletedMissingInvoiceCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 결제 참조 보정: scanned={paymentRepair.ScannedCount}, " +
                $"deletedMissingInvoicePayments={paymentRepair.MarkedDeletedMissingInvoiceCount}");
        }

        var negativeStockRepairCount = await _local.RepairNegativeItemWarehouseStocksAsync(ct);
        if (negativeStockRepairCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 음수 재고 스냅샷 {negativeStockRepairCount}건을 0 이상으로 복구했습니다.");
        }

        var nonInventoryStockCleanupCount = await PruneNonInventoryItemWarehouseStocksAsync(ct);
        if (nonInventoryStockCleanupCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 재고 추적 대상이 아닌 품목의 창고재고 스냅샷 {nonInventoryStockCleanupCount}건을 제외했습니다.");
        }

        var canSyncCompanyProfiles = includeSharedDirty && session.HasPermission(AppPermissionNames.CompanyProfileEdit);
        var canSyncSettings = includeSharedDirty && session.HasPermission(AppPermissionNames.SettingsEdit);
        var canSyncCustomers =
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.CustomerEdit);
        var canSyncItems =
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.ItemEdit);
        var canSyncItemPriceGrades = canSyncItems;
        var canSyncItemWarehouseStocks = canSyncItems;
        var canSyncRentalProfiles =
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.RentalProfileEdit) ||
            session.HasPermission(AppPermissionNames.RentalEditAll);
        var canSyncRentalAssets =
            session.HasAdministrativePrivileges ||
            session.HasPermission(AppPermissionNames.RentalAssetEdit) ||
            session.HasPermission(AppPermissionNames.RentalEditAll);
        var canSyncRentalSettings = includeSharedDirty && session.HasPermission(AppPermissionNames.RentalSettingsEdit);

        var dirtyCompanyProfiles = canSyncCompanyProfiles
            ? await _db.CompanyProfiles.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyUnits = canSyncSettings
            ? await _db.Units.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyCustomerCategories = canSyncSettings
            ? await _db.CustomerCategories.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyPriceGradeOptions = canSyncSettings
            ? await _db.PriceGradeOptions.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyTradeTypeOptions = canSyncSettings
            ? await _db.TradeTypeOptions.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyItemCategoryOptions = canSyncSettings
            ? await _db.ItemCategoryOptions.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyCustomerMasters = await _local.GetDirtyCustomerMastersForSyncAsync(session, ct);
        var dirtyCustomers = await _local.GetDirtyCustomersForSyncAsync(session, ct);
        var dirtyCustomerContracts = await _local.GetDirtyCustomerContractsForSyncAsync(session, ct);
        var dirtyItems = await _local.GetDirtyItemsForSyncAsync(session, ct);
        var rawItemCatalogExtensionVersion = await _local.GetSettingAsync(
            ItemCatalogExtensionVersionSettingKey,
            ct);
        var hasObservedItemCatalogExtensionCapability =
            int.TryParse(
                rawItemCatalogExtensionVersion,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var observedItemCatalogExtensionVersion) &&
            observedItemCatalogExtensionVersion > 0;
        await MarkDirtyItemCatalogExtensionsPendingAsync(
            dirtyItems,
            hasObservedItemCatalogExtensionCapability,
            ct);
        var inventorySnapshotItemIds =
            dirtyItems
                .Where(item =>
                    !item.IsDeleted &&
                    SupportsInventoryTracking(item))
                .Select(item => item.Id)
                .Where(itemId => itemId != Guid.Empty)
                .ToHashSet();
        var dirtyItemPriceGrades = canSyncItemPriceGrades
            ? await _local.GetDirtyItemPriceGradesForSyncAsync(session, ct)
            : [];
        var dirtyItemWarehouseStocks = canSyncItemWarehouseStocks
            ? await LoadInventoryTrackedItemWarehouseStocksForPushForSessionAsync(
                session,
                inventorySnapshotItemIds,
                ct)
            : [];
        var dirtyTransactions = await _local.GetDirtyTransactionsForSyncAsync(session, ct);
        var dirtyTransactionAttachments = await _local.GetDirtyTransactionAttachmentsForSyncAsync(session, ct);
        var dirtyInventoryTransfers = await _local.GetDirtyInventoryTransfersForSyncAsync(session, ct);
        var dirtyRentalManagementCompanies = canSyncRentalSettings
            ? await _db.RentalManagementCompanies.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyRentalBillingProfiles = await _local.GetDirtyRentalBillingProfilesForOutboundSyncAsync(session, ct);
        var dirtyRentalAssets = await _local.GetDirtyRentalAssetsForOutboundSyncAsync(session, ct);
        var dirtyRentalAssetAssignmentHistories = await _local.GetDirtyRentalAssetAssignmentHistoriesForOutboundSyncAsync(session, ct);
        var dirtyRentalBillingLogs = await _local.GetDirtyRentalBillingLogsForOutboundSyncAsync(session, ct);
        var dirtyInvoices = await _local.GetDirtyInvoicesForSyncAsync(session, ct);
        var dirtyPayments = await _local.GetDirtyPaymentsForSyncAsync(session, ct);

        var companyProfiles = dirtyCompanyProfiles.Select(LocalMappings.ToDto).ToList();
        var units = dirtyUnits.Select(LocalMappings.ToDto).ToList();
        var customerCategories = dirtyCustomerCategories.Select(LocalMappings.ToDto).ToList();
        var priceGradeOptions = dirtyPriceGradeOptions.Select(LocalMappings.ToDto).ToList();
        var tradeTypeOptions = dirtyTradeTypeOptions.Select(LocalMappings.ToDto).ToList();
        var itemCategoryOptions = dirtyItemCategoryOptions.Select(LocalMappings.ToDto).ToList();
        var customerMasters = dirtyCustomerMasters.Select(LocalMappings.ToDto).ToList();
        var customers = dirtyCustomers.Select(LocalMappings.ToDto).ToList();
        var customerContracts = dirtyCustomerContracts.Select(LocalMappings.ToDto).ToList();
        var items = dirtyItems.Select(MapItemForOutboundSync).ToList();
        var itemPriceGrades = dirtyItemPriceGrades.Select(LocalMappings.ToDto).ToList();
        var itemWarehouseStocks = dirtyItemWarehouseStocks.Select(LocalMappings.ToDto).ToList();
        // A destructive completeness marker is safe only after the client has
        // durable proof that its scoped warehouse cache is complete. Local
        // item revisions do not provide that proof, so Desktop sends explicit
        // zero-quantity rows and leaves omission semantics opt-in for clients
        // that own a verified completeness token.
        var itemWarehouseStockSnapshotMarkers =
            new List<ItemWarehouseStockSnapshotMarkerDto>();
        var priceGradeOptionsById = await BuildPriceGradeOptionLookupAsync(
            priceGradeOptions,
            itemPriceGrades,
            ct);
        var transactions = dirtyTransactions.Select(LocalMappings.ToDto).ToList();
        var transactionAttachments = dirtyTransactionAttachments
            .Select(entity => LocalMappings.ToDto(entity, ReadTransactionAttachmentContent(entity)))
            .ToList();
        var inventoryTransfers = dirtyInventoryTransfers
            .Select(LocalMappings.ToDto)
            .Select(transfer =>
            {
                transfer.Lines = (transfer.Lines ?? [])
                    .OrderBy(line => line.Id)
                    .ToList();
                return transfer;
            })
            .ToList();
        var referencedRentalAssets = canSyncRentalAssets
            ? await LoadReferencedRentalAssetsForPushAsync(
                dirtyRentalAssetAssignmentHistories,
                dirtyRentalAssets,
                ct)
            : [];
        var rentalAssetEntities = dirtyRentalAssets
            .Concat(referencedRentalAssets)
            .GroupBy(asset => asset.Id)
            .Select(group => group.First())
            .ToList();
        // 참조용 청구 프로필을 실으면 서버는 Rental.ProfileEdit/Rental.EditAll 권한을 요구한다.
        // 자산 전용 계정에는 해당 payload를 보내지 않고 서버에 이미 존재하는 참조만 사용한다.
        var referencedRentalBillingProfiles = canSyncRentalProfiles
            ? await LoadReferencedRentalBillingProfilesForPushAsync(
                rentalAssetEntities,
                dirtyRentalAssetAssignmentHistories,
                dirtyRentalBillingLogs,
                dirtyRentalBillingProfiles,
                ct)
            : [];
        // 렌탈 관리업체는 Rental.SettingsEdit 전용 기준정보다. 청구 프로필/자산 편집 권한만 있는
        // 담당지점 사용자의 정상 변경에 참조용 관리업체를 함께 싣으면 서버 권한 검사에서 전체
        // push가 403으로 거절된다. 기준정보 편집 권한이 있을 때만 참조 보강 payload를 만든다.
        var referencedRentalManagementCompanies = canSyncRentalSettings
            ? await LoadReferencedRentalManagementCompaniesForPushAsync(
                rentalAssetEntities,
                dirtyRentalBillingProfiles.Concat(referencedRentalBillingProfiles).ToList(),
                dirtyRentalManagementCompanies,
                ct)
            : [];
        var rentalBillingProfileEntities = dirtyRentalBillingProfiles
            .Concat(referencedRentalBillingProfiles)
            .GroupBy(profile => profile.Id)
            .Select(group => group.First())
            .ToList();
        var referencedRentalCustomers = canSyncCustomers
            ? await LoadReferencedRentalCustomersForPushAsync(
                rentalAssetEntities,
                rentalBillingProfileEntities,
                dirtyRentalAssetAssignmentHistories,
                dirtyCustomers,
                ct)
            : [];
        var referencedRentalItems = canSyncItems
            ? await LoadReferencedRentalItemsForPushAsync(
                rentalAssetEntities,
                dirtyItems,
                ct)
            : [];
        if (referencedRentalCustomers.Count > 0)
        {
            customers = customers
                .Concat(referencedRentalCustomers.Select(LocalMappings.ToDto))
                .GroupBy(customer => customer.Id)
                .Select(group => group.First())
                .ToList();
        }
        if (referencedRentalItems.Count > 0)
        {
            items = items
                .Concat(referencedRentalItems.Select(MapItemForOutboundSync))
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .ToList();
        }
        if (referencedRentalManagementCompanies.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 렌탈 관리업체 보강: 렌탈 자산/청구 프로필이 참조하는 관리업체 {referencedRentalManagementCompanies.Count}건을 함께 업로드합니다.");
        }
        if (referencedRentalBillingProfiles.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 렌탈 청구 프로필 보강: 자산이 참조하는 청구 프로필 {referencedRentalBillingProfiles.Count}건을 함께 업로드합니다.");
        }

        var rentalManagementCompanies =
            BuildRentalManagementCompanyPushPayload(
                dirtyRentalManagementCompanies,
                referencedRentalManagementCompanies);
        var rentalBillingProfiles = rentalBillingProfileEntities
            .Select(LocalMappings.ToDto)
            .ToList();
        var dirtyCustomerIds = dirtyCustomers
            .Select(customer => customer.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var dirtyItemIds = dirtyItems
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var dirtyPriceGradeOptionIds = dirtyPriceGradeOptions
            .Select(option => option.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var dirtyRentalManagementCompanyIds = dirtyRentalManagementCompanies
            .Select(company => company.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var dirtyRentalBillingProfileIds = dirtyRentalBillingProfiles
            .Select(profile => profile.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var dependencyOnlyCandidateKeys = new HashSet<SyncEntityKey>();
        foreach (var option in priceGradeOptionsById.Values)
        {
            if (option.Id != Guid.Empty &&
                !dirtyPriceGradeOptionIds.Contains(option.Id))
            {
                dependencyOnlyCandidateKeys.Add(new SyncEntityKey(
                    NormalizeSyncEntityName(nameof(LocalPriceGradeOption)),
                    option.Id));
            }
        }
        foreach (var customer in referencedRentalCustomers)
        {
            if (customer.Id != Guid.Empty &&
                !dirtyCustomerIds.Contains(customer.Id))
            {
                dependencyOnlyCandidateKeys.Add(new SyncEntityKey(
                    NormalizeSyncEntityName(nameof(LocalCustomer)),
                    customer.Id));
            }
        }
        foreach (var item in referencedRentalItems)
        {
            if (item.Id != Guid.Empty &&
                !dirtyItemIds.Contains(item.Id))
            {
                dependencyOnlyCandidateKeys.Add(new SyncEntityKey(
                    NormalizeSyncEntityName(nameof(LocalItem)),
                    item.Id));
            }
        }
        foreach (var company in referencedRentalManagementCompanies)
        {
            if (company.Id != Guid.Empty &&
                !dirtyRentalManagementCompanyIds.Contains(company.Id))
            {
                dependencyOnlyCandidateKeys.Add(new SyncEntityKey(
                    NormalizeSyncEntityName(nameof(LocalRentalManagementCompany)),
                    company.Id));
            }
        }
        foreach (var profile in referencedRentalBillingProfiles)
        {
            if (profile.Id != Guid.Empty &&
                !dirtyRentalBillingProfileIds.Contains(profile.Id))
            {
                dependencyOnlyCandidateKeys.Add(new SyncEntityKey(
                    NormalizeSyncEntityName(nameof(LocalRentalBillingProfile)),
                    profile.Id));
            }
        }
        foreach (var asset in referencedRentalAssets)
        {
            if (asset.Id != Guid.Empty)
            {
                dependencyOnlyCandidateKeys.Add(new SyncEntityKey(
                    NormalizeSyncEntityName(nameof(LocalRentalAsset)),
                    asset.Id));
            }
        }
        var rentalAssets = rentalAssetEntities.Select(LocalMappings.ToDto).ToList();
        var rentalAssetAssignmentHistories = dirtyRentalAssetAssignmentHistories.Select(LocalMappings.ToDto).ToList();
        var rentalBillingLogs = dirtyRentalBillingLogs.Select(LocalMappings.ToDto).ToList();
        var invoices = dirtyInvoices.Select(LocalMappings.ToDto).ToList();
        foreach (var invoice in invoices)
        {
            // Payment는 request.Payments에서 독립 mutation으로 전송한다. Invoice mutation에
            // 중복 포함하면 수금 변경만으로 동일 Invoice mutation payload가 달라진다.
            invoice.Payments = [];
        }
        if (invoices.Count > 0)
        {
            var invoiceCustomerIds = invoices
                .Where(invoice => invoice.CustomerId != Guid.Empty)
                .Select(invoice => invoice.CustomerId)
                .Distinct()
                .ToList();
            if (invoiceCustomerIds.Count > 0)
            {
                var invoiceCustomers = await _db.Customers.IgnoreQueryFilters()
                    .Where(customer => invoiceCustomerIds.Contains(customer.Id))
                    .Select(customer => new { customer.Id, customer.NameOriginal })
                    .ToDictionaryAsync(customer => customer.Id, customer => customer.NameOriginal, ct);

                foreach (var invoice in invoices)
                {
                    if (invoice.CustomerId != Guid.Empty &&
                        invoiceCustomers.TryGetValue(invoice.CustomerId, out var customerName))
                    {
                        invoice.CustomerName = customerName ?? string.Empty;
                    }
                }
            }
        }
        var payments = dirtyPayments.Select(LocalMappings.ToDto).ToList();

        var req = new SyncPushRequest
        {
            CompanyProfiles = companyProfiles,
            Units = units,
            CustomerCategories = customerCategories,
            PriceGradeOptions = priceGradeOptions,
            TradeTypeOptions = tradeTypeOptions,
            ItemCategoryOptions = itemCategoryOptions,
            CustomerMasters = customerMasters,
            Customers = customers,
            CustomerContracts = customerContracts,
            Items = items,
            ItemPriceGrades = itemPriceGrades,
            ItemWarehouseStocks = itemWarehouseStocks,
            ItemWarehouseStockSnapshotMarkers =
                itemWarehouseStockSnapshotMarkers,
            Transactions = transactions,
            TransactionAttachments = transactionAttachments,
            InventoryTransfers = inventoryTransfers,
            RentalManagementCompanies = rentalManagementCompanies,
            RentalBillingProfiles = rentalBillingProfiles,
            RentalAssets = rentalAssets,
            RentalAssetAssignmentHistories = rentalAssetAssignmentHistories,
            RentalBillingLogs = rentalBillingLogs,
            Invoices = invoices,
            Payments = payments
        };

        req.DeviceId = await GetOrCreateDeviceIdAsync(ct);
        var additionalRentalRequests = new List<RentalTenantSyncPayload>();
        IReadOnlySet<SyncEntityKey>? primaryDependencyOnlyKeys = null;
        if (session.HasAdministrativePrivileges)
        {
            var currentBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(session.SelectedBusinessDatabaseName);
            var itemBusinessDatabaseNames = await BuildItemBusinessDatabaseNameLookupAsync(
                items,
                itemPriceGrades,
                itemWarehouseStocks,
                ct);
            var rentalTenantPayloads = BuildAdministrativeRentalTenantPayloads(
                customers,
                items,
                itemPriceGrades,
                itemWarehouseStocks,
                itemBusinessDatabaseNames,
                priceGradeOptionsById,
                referencedRentalCustomers
                    .Select(LocalMappings.ToDto)
                    .ToDictionary(customer => customer.Id),
                rentalManagementCompanies,
                rentalBillingProfiles,
                rentalAssets,
                rentalAssetAssignmentHistories,
                rentalBillingLogs);
            if (rentalTenantPayloads.TryGetValue(currentBusinessDatabaseName, out var currentPayload))
            {
                req.PriceGradeOptions = req.PriceGradeOptions
                    .Concat(currentPayload.PriceGradeOptions)
                    .GroupBy(option => option.Id)
                    .Select(group => group.First())
                    .ToList();
                req.Customers = currentPayload.Customers;
                req.Items = currentPayload.Items;
                req.ItemPriceGrades = currentPayload.ItemPriceGrades;
                req.ItemWarehouseStocks = currentPayload.ItemWarehouseStocks;
                req.RentalManagementCompanies = currentPayload.ManagementCompanies;
                req.RentalBillingProfiles = currentPayload.BillingProfiles;
                req.RentalAssets = currentPayload.Assets;
                req.RentalAssetAssignmentHistories = currentPayload.AssignmentHistories;
                req.RentalBillingLogs = currentPayload.BillingLogs;
                rentalTenantPayloads.Remove(currentBusinessDatabaseName);
            }
            else
            {
                req.Customers = [];
                req.Items = [];
                req.ItemPriceGrades = [];
                req.ItemWarehouseStocks = [];
                req.RentalManagementCompanies = [];
                req.RentalBillingProfiles = [];
                req.RentalAssets = [];
                req.RentalAssetAssignmentHistories = [];
                req.RentalBillingLogs = [];
            }

            var currentItemIds = req.Items
                .Select(item => item.Id)
                .ToHashSet();
            req.ItemWarehouseStockSnapshotMarkers =
                itemWarehouseStockSnapshotMarkers
                    .Where(marker =>
                        currentItemIds.Contains(
                            marker.ItemId))
                    .ToList();
            additionalRentalRequests.AddRange(rentalTenantPayloads.Values);
        }

        var primaryBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(session.SelectedBusinessDatabaseName);
        StampOutgoingMutations(req, req.DeviceId, primaryBusinessDatabaseName);
        await ExcludeBlockedInventoryTransferScopesAsync(req, ct);
        primaryDependencyOnlyKeys = SelectDependencyOnlyKeysForRequest(
            req,
            dependencyOnlyCandidateKeys);
        await PushPreparedRequestAsync(
            apiClient,
            session,
            req,
            businessDatabaseNameOverride: null,
            primaryDependencyOnlyKeys,
            ct);

        foreach (var additionalRequest in additionalRentalRequests)
        {
            ct.ThrowIfCancellationRequested();

            var supplementalRequest = new SyncPushRequest
            {
                DeviceId = req.DeviceId,
                Customers = additionalRequest.Customers,
                PriceGradeOptions = additionalRequest.PriceGradeOptions,
                Items = additionalRequest.Items,
                ItemPriceGrades = additionalRequest.ItemPriceGrades,
                ItemWarehouseStocks = additionalRequest.ItemWarehouseStocks,
                ItemWarehouseStockSnapshotMarkers =
                    itemWarehouseStockSnapshotMarkers
                        .Where(marker =>
                            additionalRequest.Items.Any(
                                item =>
                                    item.Id ==
                                    marker.ItemId))
                        .ToList(),
                RentalManagementCompanies = additionalRequest.ManagementCompanies,
                RentalBillingProfiles = additionalRequest.BillingProfiles,
                RentalAssets = additionalRequest.Assets,
                RentalAssetAssignmentHistories = additionalRequest.AssignmentHistories,
                RentalBillingLogs = additionalRequest.BillingLogs
            };
            StampOutgoingMutations(
                supplementalRequest,
                supplementalRequest.DeviceId,
                additionalRequest.BusinessDatabaseName);
            var dependencyOnlyKeys = SelectDependencyOnlyKeysForRequest(
                    supplementalRequest,
                    dependencyOnlyCandidateKeys)
                .ToHashSet();
            dependencyOnlyKeys.UnionWith(
                supplementalRequest.PriceGradeOptions.Select(option =>
                    new SyncEntityKey(
                        NormalizeSyncEntityName(nameof(LocalPriceGradeOption)),
                        option.Id)));
            await PushPreparedRequestAsync(
                apiClient,
                session,
                supplementalRequest,
                additionalRequest.BusinessDatabaseName,
                dependencyOnlyKeys,
                ct);
        }
    }

    private async Task ExcludeBlockedInventoryTransferScopesAsync(
        SyncPushRequest request,
        CancellationToken ct)
    {
        if (request.InventoryTransfers.Count == 0)
            return;

        var outgoingMutationIds = request.InventoryTransfers
            .Select(transfer => (transfer.MutationId ?? string.Empty).Trim())
            .Where(mutationId => !string.IsNullOrWhiteSpace(mutationId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (outgoingMutationIds.Count == 0)
            return;

        var failedRows = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.Status == "Failed" &&
                outgoingMutationIds.Contains(entry.MutationId))
            .Select(entry => new
            {
                entry.MutationId,
                entry.EntityName,
                entry.ErrorMessage
            })
            .ToListAsync(ct);
        var blockedMutationIds = failedRows
            .Where(entry =>
                string.Equals(
                    NormalizeSyncEntityName(entry.EntityName),
                    "InventoryTransfer",
                    StringComparison.OrdinalIgnoreCase) &&
                ((entry.ErrorMessage ?? string.Empty).StartsWith(
                     InventoryTransferStockAtomicityRollbackOutboxErrorPrefix,
                     StringComparison.Ordinal) ||
                 (entry.ErrorMessage ?? string.Empty).StartsWith(
                     InventoryTransferTombstoneConflictPolicy
                         .OutboxErrorPrefix,
                     StringComparison.Ordinal)))
            .Select(entry => entry.MutationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (blockedMutationIds.Count == 0)
            return;

        var blockedTransfers = request.InventoryTransfers
            .Where(transfer =>
                blockedMutationIds.Contains(
                    (transfer.MutationId ?? string.Empty).Trim()))
            .ToList();
        if (blockedTransfers.Count == 0)
            return;

        var blockedItemIds = blockedTransfers
            .SelectMany(transfer => transfer.Lines ?? [])
            .Where(line =>
                line.ItemId.HasValue &&
                line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .ToHashSet();

        request.InventoryTransfers = request.InventoryTransfers
            .Except(blockedTransfers)
            .ToList();
        if (blockedItemIds.Count > 0)
        {
            request.Items = request.Items
                .Where(item => !blockedItemIds.Contains(item.Id))
                .ToList();
            request.ItemPriceGrades = request.ItemPriceGrades
                .Where(price => !blockedItemIds.Contains(price.ItemId))
                .ToList();
            request.ItemWarehouseStocks =
                request.ItemWarehouseStocks
                    .Where(stock =>
                        !blockedItemIds.Contains(stock.ItemId))
                    .ToList();
            request.ItemWarehouseStockSnapshotMarkers =
                request.ItemWarehouseStockSnapshotMarkers
                    .Where(marker =>
                        !blockedItemIds.Contains(marker.ItemId))
                    .ToList();
        }

        AppLogger.Warn(
            "SYNC",
            $"Blocked inventory-transfer retry scope excluded: transfers={blockedTransfers.Count}, items={blockedItemIds.Count}.");
    }

    private static bool IsPushRequestEmpty(SyncPushRequest req)
        => req.CompanyProfiles.Count +
           req.Units.Count +
           req.CustomerCategories.Count +
           req.PriceGradeOptions.Count +
           req.TradeTypeOptions.Count +
           req.ItemCategoryOptions.Count +
           req.CustomerMasters.Count +
           req.Customers.Count +
           req.CustomerContracts.Count +
           req.Items.Count +
           req.ItemPriceGrades.Count +
           req.ItemWarehouseStocks.Count +
           req.ItemWarehouseStockSnapshotMarkers.Count +
           req.Transactions.Count +
           req.TransactionAttachments.Count +
           req.InventoryTransfers.Count +
           req.RentalManagementCompanies.Count +
           req.RentalBillingProfiles.Count +
           req.RentalAssets.Count +
           req.RentalAssetAssignmentHistories.Count +
           req.RentalBillingLogs.Count +
           req.Invoices.Count +
           req.Payments.Count == 0;

    private async Task PushPreparedRequestAsync(
        ErpApiClient apiClient,
        SessionState session,
        SyncPushRequest req,
        string? businessDatabaseNameOverride,
        IReadOnlySet<SyncEntityKey>? dependencyOnlyKeys,
        CancellationToken ct)
    {
        if (IsPushRequestEmpty(req))
            return;

        var pushOperationOwner =
            CaptureSyncOperationOwnerBoundary(
                session,
                businessDatabaseNameOverride);
        var dependencyOnlyRentalCompanyIdentities =
            SelectDependencyOnlyRentalManagementCompanyIdentitiesForRequest(
                req,
                dependencyOnlyKeys);
        var preparedMutationSnapshots =
            BuildPreparedMutationSnapshots(req, dependencyOnlyKeys);
        var dispatchPreparedMutationSnapshots =
            BuildPreparedMutationSnapshots(req, excludedKeys: null);
        if (_isolatedOperationOwner is null)
        {
            CaptureTrackedChangesBeforePreparedMutationBoundary(
                preparedMutationSnapshots);
        }

        var trackedStateBeforePush = CaptureTrackedStateBeforePush();
        try
        {
            await RecordPreparedMutationsAsync(
                req,
                session,
                businessDatabaseNameOverride,
                dependencyOnlyKeys,
                ct);
        }
        finally
        {
            CaptureNonMutationTrackedChangesAtPushBoundary(
                trackedStateBeforePush,
                includeExistingChanges: false);
        }

        var currentPushReceipts = await CaptureCurrentPushMutationReceiptsAsync(
            req,
            dispatchPreparedMutationSnapshots,
            session,
            businessDatabaseNameOverride,
            dependencyOnlyKeys,
            ct);
        var outgoingForDispatch = EnumerateOutgoingMutations(
                req,
                excludedKeys: null)
            .ToList();
        var dependencyOutgoingForDispatch = dependencyOnlyKeys is null
            ? []
            : outgoingForDispatch
                .Where(entry => dependencyOnlyKeys.Contains(
                    new SyncEntityKey(
                        NormalizeSyncEntityName(entry.EntityName),
                        entry.Entity.Id)))
                .ToList();
        var durableOutgoingForDispatch = outgoingForDispatch
            .Except(dependencyOutgoingForDispatch)
            .ToList();
        if (outgoingForDispatch.Count == 0 ||
            outgoingForDispatch.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Entity.MutationId)) ||
            outgoingForDispatch
                .Select(entry => entry.Entity.MutationId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != outgoingForDispatch.Count ||
            durableOutgoingForDispatch.Any(entry =>
                !currentPushReceipts.TryGetValue(
                    entry.Entity.MutationId,
                    out var receipt) ||
                !receipt.IsDurable) ||
            dependencyOutgoingForDispatch.Any(entry =>
                !currentPushReceipts.TryGetValue(
                    entry.Entity.MutationId,
                    out var receipt) ||
                receipt.IsDurable) ||
            currentPushReceipts.Count !=
                durableOutgoingForDispatch.Count +
                dependencyOutgoingForDispatch.Count)
        {
            throw new SyncPullBlockedException(
                "동기화 전송 영수증이 현재 변경 전체와 정확히 일치하지 않아 서버 전송을 중단했습니다.");
        }

        try
        {
            SyncPushResult? result;
            try
            {
                result = await apiClient.PushAsync(req, businessDatabaseNameOverride, ct);
            }
            finally
            {
                CaptureNonMutationTrackedChangesAtPushBoundary(
                    trackedStateBeforePush,
                    includeExistingChanges: false);
            }
            if (result is null)
            {
                var message = "서버 응답이 비어 있어 동기화를 완료하지 못했습니다.";
                await TryMarkOutboxFailedAsync(
                    req,
                    message,
                    dependencyOnlyKeys,
                    currentPushReceipts,
                    ct);
                throw new HttpRequestException(message);
            }

            TestSeedSyncConflictDiagnostics.WriteAcceptedRevisionsIfEnabled(
                result.AcceptedRevisions,
                Console.Out);

            await MarkOutboxSentAsync(
                req,
                dependencyOnlyKeys,
                currentPushReceipts,
                ct);
            var inventoryTransferPurgeAcknowledgements =
                SelectVerifiedInventoryTransferPurgeAcknowledgements(
                    req,
                    result,
                    pushOperationOwner);
            if (inventoryTransferPurgeAcknowledgements.Count > 0 &&
                !IsSyncOperationOwnerCurrent(
                    pushOperationOwner,
                    session,
                    businessDatabaseNameOverride))
            {
                throw new SyncPullBlockedException(
                    "재고이동 영구삭제 응답 대기 중 로그인·업체 DB 범위가 변경되어 이전 범위의 응답을 폐기했습니다.");
            }

            var handledInventoryTransferPurgeKeys =
                await ApplyInventoryTransferPurgeAcknowledgementsAtomicallyAsync(
                    req,
                    inventoryTransferPurgeAcknowledgements,
                    preparedMutationSnapshots,
                    dependencyOnlyKeys,
                    pushOperationOwner,
                    session,
                    businessDatabaseNameOverride,
                    currentPushReceipts,
                    ct);
            var acceptedSideEffectsApplied = false;
            async Task ApplyAcceptedSideEffectsAsync()
            {
                if (acceptedSideEffectsApplied)
                    return;

                var locallyTrackedAcceptedRevisions = result.AcceptedRevisions
                    .Where(revision =>
                    {
                        var key = new SyncEntityKey(
                            NormalizeSyncEntityName(revision.EntityName),
                            revision.EntityId);
                        return (dependencyOnlyKeys is null ||
                                !dependencyOnlyKeys.Contains(key)) &&
                               !handledInventoryTransferPurgeKeys.Contains(key);
                    })
                    .ToList();
                var locallyModifiedAfterPush = new HashSet<SyncEntityKey>();
                if (locallyTrackedAcceptedRevisions.Count > 0)
                {
                    locallyModifiedAfterPush = await ApplyAcceptedRevisionsAsync(
                        locallyTrackedAcceptedRevisions,
                        preparedMutationSnapshots,
                        ct);
                }

                foreach (var assigned in result.AssignedInvoiceNumbers)
                {
                    await ApplyAssignedInvoiceNumberAsync(
                        assigned.Key,
                        assigned.Value,
                        isTaxInvoiceNumber: false,
                        preparedMutationSnapshots,
                        locallyModifiedAfterPush,
                        ct);
                }

                foreach (var assigned in result.AssignedTaxInvoiceNumbers)
                {
                    await ApplyAssignedInvoiceNumberAsync(
                        assigned.Key,
                        assigned.Value,
                        isTaxInvoiceNumber: true,
                        preparedMutationSnapshots,
                        locallyModifiedAfterPush,
                        ct);
                }

                await MarkOutboxAcknowledgedCoreAsync(
                    req,
                    locallyTrackedAcceptedRevisions,
                    dependencyOnlyKeys,
                    session,
                    businessDatabaseNameOverride,
                    currentPushReceipts,
                    ct);
                acceptedSideEffectsApplied = true;
            }

            if (result.Notices.Count > 0)
            {
                var noticeSummary = BuildSyncNoticeSummary(result.Notices);
                if (!string.IsNullOrWhiteSpace(noticeSummary))
                {
                    AppLogger.Warn("SYNC", noticeSummary);
                    await AppendConflictSummaryAsync(noticeSummary);
                    await TryRecordDiagnosticAsync("push-warning", noticeSummary, severity: "Warning");
                }
            }

            if (await TryHandleInventoryTransferStockAtomicityRollbackAsync(
                    req,
                    result,
                    ct))
            {
                return;
            }

            var itemWarehouseStockAcknowledgementIssue =
                BuildItemWarehouseStockAcknowledgementIssue(
                    req.ItemWarehouseStocks,
                    result);
            if (!string.IsNullOrWhiteSpace(
                    itemWarehouseStockAcknowledgementIssue))
            {
                var itemWarehouseStocksForRetry =
                    SelectItemWarehouseStocksForAcknowledgementRetry(
                        req.ItemWarehouseStocks,
                        result);
                await
                    ApplyPartialWarehouseStockAcknowledgementAtomicallyAsync(
                        ApplyAcceptedSideEffectsAsync,
                        itemWarehouseStocksForRetry,
                        ct);
                AppLogger.Warn(
                    "SYNC",
                    itemWarehouseStockAcknowledgementIssue);
                await AppendConflictSummaryAsync(
                    itemWarehouseStockAcknowledgementIssue);
                await TryRecordDiagnosticAsync(
                    "push",
                    itemWarehouseStockAcknowledgementIssue,
                    severity: "Error");
                throw new SyncPullBlockedException(
                    itemWarehouseStockAcknowledgementIssue);
            }

            if (result.ConflictCount > 0)
            {
                TestSeedSyncConflictDiagnostics.WriteIfEnabled(
                    result.Conflicts,
                    Console.Out);
                var cleanCanonicalRentalCompanyFallbacks =
                    await SelectCleanCanonicalRentalCompanyFallbacksAsync(
                        dependencyOnlyRentalCompanyIdentities,
                        ct);
                var dependencyOnlyConflicts = result.Conflicts
                    .Where(conflict => IsDependencyOnlyConflict(
                        conflict,
                        dependencyOnlyKeys,
                        cleanCanonicalRentalCompanyFallbacks))
                    .ToList();
                if (dependencyOnlyConflicts.Count > 0)
                {
                    await RebasePreservedConcurrentConflictsAsync(
                        dependencyOnlyConflicts,
                        ct);
                }

                var locallyActionableConflicts = result.Conflicts
                    .Where(conflict => !IsDependencyOnlyConflict(
                        conflict,
                        dependencyOnlyKeys,
                        cleanCanonicalRentalCompanyFallbacks))
                    .ToList();
                var preservedConcurrentConflicts = new List<ConflictLogDto>();
                var automaticConflicts = new List<ConflictLogDto>();
                foreach (var conflict in locallyActionableConflicts)
                {
                    if (await ShouldPreserveConcurrentConflictAsync(
                            conflict,
                            preparedMutationSnapshots,
                            ct))
                    {
                        preservedConcurrentConflicts.Add(conflict);
                    }
                    else
                    {
                        automaticConflicts.Add(conflict);
                    }
                }
                if (preservedConcurrentConflicts.Count > 0)
                {
                    await RebasePreservedConcurrentConflictsAsync(
                        preservedConcurrentConflicts,
                        ct);
                }
                var serverNewerConflicts = automaticConflicts
                    .Where(conflict => string.Equals(conflict.Reason, "Server version is newer.", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (serverNewerConflicts.Count > 0)
                {
                    var serverNewerResolution =
                        await ResolvePreparedServerNewerConflictsAsync(
                            serverNewerConflicts,
                            preparedMutationSnapshots,
                            req,
                            session,
                            dependencyOnlyKeys,
                            ct);
                    preservedConcurrentConflicts.AddRange(
                        serverNewerResolution.PreservedConflicts);
                    if (serverNewerResolution.ResolvedConflicts.Count > 0)
                    {
                        AppLogger.Warn("SYNC", $"서버 최신 버전 우선으로 충돌 {serverNewerResolution.ResolvedConflicts.Count}건을 정리했습니다.");
                        await AppendConflictSummaryAsync($"서버 최신값 우선으로 동기화 충돌 {serverNewerResolution.ResolvedConflicts.Count}건을 자동 정리했습니다.");
                    }
                }

                var preparedCompanyProfileRevisionRetryConflicts = await PrepareCompanyProfileRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedCompanyProfileRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Company profile revision retry prepared: {preparedCompanyProfileRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"회사설정 리비전 충돌 {preparedCompanyProfileRevisionRetryConflicts.Count}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                }

                var preparedCustomerRevisionRetryConflicts = await PrepareCustomerRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedCustomerRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Customer revision retry prepared: {preparedCustomerRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"거래처 리비전 충돌 {preparedCustomerRevisionRetryConflicts.Count}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                }

                var preparedInvoiceRevisionRetryConflicts = await PrepareInvoiceRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedInvoiceRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Invoice revision retry prepared: {preparedInvoiceRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"전표 리비전 충돌 {preparedInvoiceRevisionRetryConflicts.Count}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                }

                var preparedPaymentRevisionRetryConflicts = await PreparePaymentRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedPaymentRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Payment revision retry prepared: {preparedPaymentRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"수금/지급 리비전 충돌 {preparedPaymentRevisionRetryConflicts.Count}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                }

                var preparedTransactionRevisionRetryConflicts = await PrepareTransactionRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedTransactionRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Transaction revision retry prepared: {preparedTransactionRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"거래내역 리비전 충돌 {preparedTransactionRevisionRetryConflicts.Count}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                }

                var preparedTransactionAttachmentRevisionRetryConflicts = await PrepareTransactionAttachmentRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedTransactionAttachmentRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Transaction attachment revision retry prepared: {preparedTransactionAttachmentRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"거래내역 첨부 리비전 충돌 {preparedTransactionAttachmentRevisionRetryConflicts.Count}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                }

                var preparedInventoryTransferRevisionRetryConflicts = await PrepareInventoryTransferRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedInventoryTransferRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Inventory transfer revision retry prepared: {preparedInventoryTransferRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"재고이동 리비전 충돌 {preparedInventoryTransferRevisionRetryConflicts.Count}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                }

                var repairedItemRevisionConflicts = await ResolveCanonicalItemRevisionConflictsAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                        .Except(preparedInventoryTransferRevisionRetryConflicts)
                        .ToList(),
                    ct);

                if (repairedItemRevisionConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"중복 품목 자연키/리비전 충돌 {repairedItemRevisionConflicts.Count}건을 서버 기준 품목으로 자동 복구했습니다.");
                    await AppendConflictSummaryAsync($"중복 품목 자연키/리비전 충돌 {repairedItemRevisionConflicts.Count}건을 서버 기준 품목으로 자동 복구했습니다.");
                }

                var preparedItemRevisionRetryConflicts = await PrepareItemRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                        .Except(preparedInventoryTransferRevisionRetryConflicts)
                        .Except(repairedItemRevisionConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedItemRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Item revision retry prepared: {preparedItemRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"Item revision retry prepared: {preparedItemRevisionRetryConflicts.Count} conflict(s).");
                }

                var preparedRentalProfileRevisionRetryConflicts = await PrepareRentalBillingProfileRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                        .Except(preparedInventoryTransferRevisionRetryConflicts)
                        .Except(repairedItemRevisionConflicts)
                        .Except(preparedItemRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedRentalProfileRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Rental profile revision retry prepared: {preparedRentalProfileRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"Rental profile revision retry prepared: {preparedRentalProfileRevisionRetryConflicts.Count} conflict(s).");
                }
                var rentalAssetConflictRepair = await RepairRentalAssetRevisionConflictsAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                        .Except(preparedInventoryTransferRevisionRetryConflicts)
                        .Except(repairedItemRevisionConflicts)
                        .Except(preparedItemRevisionRetryConflicts)
                        .Except(preparedRentalProfileRevisionRetryConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (rentalAssetConflictRepair.ResolvedConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"렌탈 자산 리비전 충돌 {rentalAssetConflictRepair.ResolvedConflicts.Count}건을 서버 기준 자산 정보로 자동 정리했습니다.");
                    await AppendConflictSummaryAsync($"렌탈 자산 리비전 충돌 {rentalAssetConflictRepair.ResolvedConflicts.Count}건을 서버 기준 자산 정보로 자동 정리했습니다.");
                }

                if (rentalAssetConflictRepair.PreparedRetryCount > 0)
                {
                    AppLogger.Warn("SYNC", $"렌탈 자산 리비전 충돌 {rentalAssetConflictRepair.PreparedRetryCount}건을 서버 최신 rev 기준 재시도로 준비했습니다.");
                    await AppendConflictSummaryAsync($"렌탈 자산 리비전 충돌 {rentalAssetConflictRepair.PreparedRetryCount}건을 서버 최신 rev 기준으로 재시도 준비했습니다.");
                }

                var itemWarehouseStockConflictResolution = await ResolveItemWarehouseStockRevisionConflictsAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                        .Except(preparedInventoryTransferRevisionRetryConflicts)
                        .Except(repairedItemRevisionConflicts)
                        .Except(preparedItemRevisionRetryConflicts)
                        .Except(preparedRentalProfileRevisionRetryConflicts)
                        .Except(rentalAssetConflictRepair.ResolvedConflicts)
                        .Except(rentalAssetConflictRepair.PreparedRetryConflicts)
                        .ToList(),
                    ct);

                if (itemWarehouseStockConflictResolution.ResolvedConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Item warehouse stock revision conflicts resolved/rebased: {itemWarehouseStockConflictResolution.ResolvedConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"재고 스냅샷 리비전 충돌 {itemWarehouseStockConflictResolution.ResolvedConflicts.Count}건을 서버 최신 rev 기준으로 자동 정리했습니다.");
                }

                var preparedGenericRevisionRetryConflicts = await PrepareGenericRevisionRetriesAsync(
                    automaticConflicts
                        .Except(serverNewerConflicts)
                        .Except(preparedCompanyProfileRevisionRetryConflicts)
                        .Except(preparedCustomerRevisionRetryConflicts)
                        .Except(preparedInvoiceRevisionRetryConflicts)
                        .Except(preparedPaymentRevisionRetryConflicts)
                        .Except(preparedTransactionRevisionRetryConflicts)
                        .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                        .Except(preparedInventoryTransferRevisionRetryConflicts)
                        .Except(repairedItemRevisionConflicts)
                        .Except(preparedItemRevisionRetryConflicts)
                        .Except(preparedRentalProfileRevisionRetryConflicts)
                        .Except(rentalAssetConflictRepair.ResolvedConflicts)
                        .Except(rentalAssetConflictRepair.PreparedRetryConflicts)
                        .Except(itemWarehouseStockConflictResolution.ResolvedConflicts)
                        .ToList(),
                    req.DeviceId,
                    session,
                    ct);

                if (preparedGenericRevisionRetryConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"Generic revision retry prepared: {preparedGenericRevisionRetryConflicts.Count} conflict(s).");
                    await AppendConflictSummaryAsync($"일반 데이터 리비전 충돌 {preparedGenericRevisionRetryConflicts.Count}건을 서버 최신 rev 기준으로 재시도 준비했습니다.");
                }

                var equivalentRevisionConflicts = automaticConflicts
                    .Except(serverNewerConflicts)
                    .Except(preparedCompanyProfileRevisionRetryConflicts)
                    .Except(preparedCustomerRevisionRetryConflicts)
                    .Except(preparedInvoiceRevisionRetryConflicts)
                    .Except(preparedPaymentRevisionRetryConflicts)
                    .Except(preparedTransactionRevisionRetryConflicts)
                    .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                    .Except(preparedInventoryTransferRevisionRetryConflicts)
                    .Except(repairedItemRevisionConflicts)
                    .Except(preparedItemRevisionRetryConflicts)
                    .Except(preparedRentalProfileRevisionRetryConflicts)
                    .Except(rentalAssetConflictRepair.ResolvedConflicts)
                    .Except(rentalAssetConflictRepair.PreparedRetryConflicts)
                    .Except(itemWarehouseStockConflictResolution.ResolvedConflicts)
                    .Except(preparedGenericRevisionRetryConflicts)
                    .Where(IsEquivalentRevisionConflict)
                    .ToList();

                if (equivalentRevisionConflicts.Count > 0)
                {
                    await ResolveServerNewerConflictsAsync(equivalentRevisionConflicts, ct);
                    AppLogger.Warn("SYNC", $"재시도 중 서버에 이미 반영된 동일 내용 충돌 {equivalentRevisionConflicts.Count}건을 자동 정리했습니다.");
                    await AppendConflictSummaryAsync($"재시도 중 서버에 이미 반영된 동일 내용 충돌 {equivalentRevisionConflicts.Count}건을 자동 정리했습니다.");
                }

                var remainingConflicts = automaticConflicts
                    .Except(serverNewerConflicts)
                    .Except(preparedCompanyProfileRevisionRetryConflicts)
                    .Except(preparedCustomerRevisionRetryConflicts)
                    .Except(preparedInvoiceRevisionRetryConflicts)
                    .Except(preparedPaymentRevisionRetryConflicts)
                    .Except(preparedTransactionRevisionRetryConflicts)
                    .Except(preparedTransactionAttachmentRevisionRetryConflicts)
                    .Except(preparedInventoryTransferRevisionRetryConflicts)
                    .Except(repairedItemRevisionConflicts)
                    .Except(preparedItemRevisionRetryConflicts)
                    .Except(preparedRentalProfileRevisionRetryConflicts)
                    .Except(rentalAssetConflictRepair.ResolvedConflicts)
                    .Except(rentalAssetConflictRepair.PreparedRetryConflicts)
                    .Except(itemWarehouseStockConflictResolution.ResolvedConflicts)
                    .Except(preparedGenericRevisionRetryConflicts)
                    .Except(equivalentRevisionConflicts)
                    .ToList();

                var deferredConflicts = await GetDeferredSyncConflictsAsync(
                    remainingConflicts,
                    ct);

                if (deferredConflicts.Count > 0)
                {
                    await PrepareDeferredSyncConflictsAsync(deferredConflicts, ct);
                    AppLogger.Warn("SYNC", $"동기화를 다른 지점/후속 재시도로 넘긴 충돌 {deferredConflicts.Count}건을 보류했습니다.");
                    await AppendConflictSummaryAsync($"다른 지점 또는 후속 재시도로 넘긴 동기화 충돌 {deferredConflicts.Count}건이 남아 있습니다.");
                }

                var unresolvedConflicts = remainingConflicts
                    .Except(deferredConflicts)
                    .ToList();

                if (unresolvedConflicts.Count > 0)
                {
                    var first = unresolvedConflicts.FirstOrDefault();
                    var detail = first is null
                        ? $"동기화 충돌 {unresolvedConflicts.Count}건"
                        : $"동기화 충돌 {unresolvedConflicts.Count}건: {first.EntityName} {first.EntityId} - {first.Reason}";

                    AppLogger.Warn("SYNC", detail);
                    await AppendConflictSummaryAsync(detail);
                    await TryRecordDiagnosticAsync("push", detail, severity: "Error");
                    await ApplyAcceptedSideEffectsAsync();
                    if (unresolvedConflicts.Any(conflict =>
                            string.Equals(
                                conflict.EntityName,
                                "ItemWarehouseStock",
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SyncPullBlockedException(detail);
                    }

                    throw new InvalidOperationException(detail);
                }

                if (preservedConcurrentConflicts.Count > 0)
                {
                    var first = preservedConcurrentConflicts[0];
                    var detail =
                        $"서버 응답 대기 중 다시 편집된 항목의 충돌 {preservedConcurrentConflicts.Count}건을 자동 덮어쓰기하지 않았습니다. " +
                        $"{first.EntityName} {first.EntityId} - {first.Reason}";
                    AppLogger.Warn("SYNC", detail);
                    await AppendConflictSummaryAsync(detail);
                    await TryRecordDiagnosticAsync("push", detail, severity: "Warning");
                    await ApplyAcceptedSideEffectsAsync();
                    throw new InvalidOperationException(detail);
                }

                if (itemWarehouseStockConflictResolution.RetryRequiredConflicts.Count > 0)
                {
                    var retryCount =
                        itemWarehouseStockConflictResolution.RetryRequiredConflicts.Count;
                    var detail =
                        $"로컬 재고 수량을 보존한 리비전 재기준 {retryCount}건을 pull 전에 다시 업로드합니다.";
                    AppLogger.Warn("SYNC", detail);
                    await AppendConflictSummaryAsync(detail);
                    await ApplyAcceptedSideEffectsAsync();
                    try
                    {
                        await ReplayItemWarehouseStockSnapshotsBeforePullAsync(
                            apiClient,
                            req,
                            session,
                            businessDatabaseNameOverride,
                            itemWarehouseStockConflictResolution
                                .RetryRequiredConflicts,
                            ct);
                    }
                    catch (DesktopClientUpgradeRequiredException)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                        when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (SyncPullBlockedException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new SyncPullBlockedException(
                            "재고 snapshot 제한 재업로드를 안전하게 완료하지 못해 정상 pull을 중단했습니다.",
                            ex);
                    }
                }
            }

            await ApplyAcceptedSideEffectsAsync();
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await TryMarkOutboxFailedAsync(
                req,
                ex.InnerException?.Message ?? ex.Message,
                dependencyOnlyKeys,
                currentPushReceipts,
                ct);
            throw;
        }
    }

    private static Dictionary<string, RentalTenantSyncPayload> BuildAdministrativeRentalTenantPayloads(
        IReadOnlyCollection<CustomerDto> customers,
        IReadOnlyCollection<ItemDto> items,
        IReadOnlyCollection<ItemPriceGradeDto> itemPriceGrades,
        IReadOnlyCollection<ItemWarehouseStockDto> itemWarehouseStocks,
        IReadOnlyDictionary<Guid, string> itemBusinessDatabaseNames,
        IReadOnlyDictionary<Guid, PriceGradeOptionDto> priceGradeOptionsById,
        IReadOnlyDictionary<Guid, CustomerDto> referencedRentalCustomersById,
        IReadOnlyCollection<RentalManagementCompanyDto> managementCompanies,
        IReadOnlyCollection<RentalBillingProfileDto> billingProfiles,
        IReadOnlyCollection<RentalAssetDto> assets,
        IReadOnlyCollection<RentalAssetAssignmentHistoryDto> assignmentHistories,
        IReadOnlyCollection<RentalBillingLogDto> billingLogs)
    {
        var payloads = new Dictionary<string, RentalTenantSyncPayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in customers)
        {
            var payload = GetOrCreateRentalTenantPayload(
                payloads,
                ResolveRentalBusinessDatabaseName(
                    dto.TenantCode,
                    dto.OfficeCode,
                    dto.ResponsibleOfficeCode));
            if (payload.Customers.All(candidate => candidate.Id != dto.Id))
                payload.Customers.Add(dto);
        }

        foreach (var dto in items)
        {
            if (itemBusinessDatabaseNames.TryGetValue(dto.Id, out var businessDatabaseName))
                GetOrCreateRentalTenantPayload(payloads, businessDatabaseName).Items.Add(dto);
        }

        foreach (var dto in itemPriceGrades)
        {
            if (itemBusinessDatabaseNames.TryGetValue(dto.ItemId, out var businessDatabaseName))
            {
                var payload = GetOrCreateRentalTenantPayload(payloads, businessDatabaseName);
                payload.ItemPriceGrades.Add(dto);
                if (priceGradeOptionsById.TryGetValue(dto.PriceGradeOptionId, out var option) &&
                    payload.PriceGradeOptions.All(candidate => candidate.Id != option.Id))
                {
                    payload.PriceGradeOptions.Add(ClonePriceGradeOption(option));
                }
            }
        }

        foreach (var dto in itemWarehouseStocks)
        {
            if (itemBusinessDatabaseNames.TryGetValue(dto.ItemId, out var businessDatabaseName))
                GetOrCreateRentalTenantPayload(payloads, businessDatabaseName).ItemWarehouseStocks.Add(dto);
        }

        foreach (var dto in managementCompanies)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalManagementCompanyBusinessDatabaseName(dto));
            payload.ManagementCompanies.Add(dto);
        }

        foreach (var dto in billingProfiles)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode));
            payload.BillingProfiles.Add(dto);
            AddReferencedRentalCustomer(payload, dto.CustomerId, referencedRentalCustomersById);
        }

        foreach (var dto in assets)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode));
            payload.Assets.Add(dto);
            AddReferencedRentalCustomer(payload, dto.CustomerId, referencedRentalCustomersById);
        }

        foreach (var dto in assignmentHistories)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode));
            payload.AssignmentHistories.Add(dto);
            AddReferencedRentalCustomer(payload, dto.CustomerId, referencedRentalCustomersById);
        }

        foreach (var dto in billingLogs)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode));
            payload.BillingLogs.Add(dto);
        }

        return payloads;
    }

    private static void AddReferencedRentalCustomer(
        RentalTenantSyncPayload payload,
        Guid? customerId,
        IReadOnlyDictionary<Guid, CustomerDto> referencedRentalCustomersById)
    {
        if (!customerId.HasValue ||
            customerId.Value == Guid.Empty ||
            !referencedRentalCustomersById.TryGetValue(customerId.Value, out var customer) ||
            payload.Customers.Any(candidate => candidate.Id == customer.Id))
        {
            return;
        }

        payload.Customers.Add(customer);
    }

    private static RentalTenantSyncPayload GetOrCreateRentalTenantPayload(
        IDictionary<string, RentalTenantSyncPayload> payloads,
        string businessDatabaseName)
    {
        if (payloads.TryGetValue(businessDatabaseName, out var payload))
            return payload;

        payload = new RentalTenantSyncPayload(
            businessDatabaseName,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        payloads[businessDatabaseName] = payload;
        return payload;
    }

    private async Task<Dictionary<Guid, PriceGradeOptionDto>> BuildPriceGradeOptionLookupAsync(
        IReadOnlyCollection<PriceGradeOptionDto> dirtyOptions,
        IReadOnlyCollection<ItemPriceGradeDto> itemPriceGrades,
        CancellationToken ct)
    {
        var lookup = dirtyOptions
            .Where(option => option.Id != Guid.Empty)
            .GroupBy(option => option.Id)
            .ToDictionary(group => group.Key, group => group.Last());
        var missingOptionIds = itemPriceGrades
            .Select(priceGrade => priceGrade.PriceGradeOptionId)
            .Where(optionId => optionId != Guid.Empty && !lookup.ContainsKey(optionId))
            .Distinct()
            .ToList();
        if (missingOptionIds.Count == 0)
            return lookup;

        var referencedOptions = await _db.PriceGradeOptions.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(option =>
                missingOptionIds.Contains(option.Id) &&
                !option.IsDirty &&
                !option.IsDeleted)
            .ToListAsync(ct);
        foreach (var option in referencedOptions)
            lookup[option.Id] = LocalMappings.ToDto(option);

        var unresolvedCount = missingOptionIds.Count - referencedOptions.Count;
        if (unresolvedCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"관리자 품목 가격등급 동기화에서 참조 가격등급 옵션을 찾지 못했습니다. 서버 FK 검증을 위해 누락 참조는 보강하지 않습니다: count={unresolvedCount:N0}");
        }

        return lookup;
    }

    private static PriceGradeOptionDto ClonePriceGradeOption(PriceGradeOptionDto source)
        => new()
        {
            Id = source.Id,
            Name = source.Name,
            PriceSource = source.PriceSource,
            SortOrder = source.SortOrder,
            IsSystemDefault = source.IsSystemDefault,
            IsActive = source.IsActive,
            IsDeleted = source.IsDeleted,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Revision = source.Revision,
            ExpectedRevision = source.ExpectedRevision,
            MutationId = source.MutationId,
            MutationCreatedAtUtc = source.MutationCreatedAtUtc
        };

    private async Task<Dictionary<Guid, string>> BuildItemBusinessDatabaseNameLookupAsync(
        IReadOnlyCollection<ItemDto> items,
        IReadOnlyCollection<ItemPriceGradeDto> itemPriceGrades,
        IReadOnlyCollection<ItemWarehouseStockDto> itemWarehouseStocks,
        CancellationToken ct)
    {
        var lookup = items
            .Where(item => item.Id != Guid.Empty)
            .GroupBy(item => item.Id)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var item = group.Last();
                    return ResolveRentalBusinessDatabaseName(
                        item.TenantCode,
                        item.OfficeCode,
                        item.OfficeCode);
                });

        var missingItemIds = itemPriceGrades
            .Select(priceGrade => priceGrade.ItemId)
            .Concat(itemWarehouseStocks.Select(stock => stock.ItemId))
            .Where(itemId => itemId != Guid.Empty && !lookup.ContainsKey(itemId))
            .Distinct()
            .ToList();
        if (missingItemIds.Count == 0)
            return lookup;

        var parentItems = await _db.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => missingItemIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.TenantCode,
                item.OfficeCode
            })
            .ToListAsync(ct);
        foreach (var item in parentItems)
        {
            lookup[item.Id] = ResolveRentalBusinessDatabaseName(
                item.TenantCode,
                item.OfficeCode,
                item.OfficeCode);
        }

        var unresolvedCount = missingItemIds.Count - parentItems.Count;
        if (unresolvedCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"관리자 품목 종속 동기화에서 부모 품목을 찾지 못해 잘못된 업체 DB 전송을 차단했습니다: count={unresolvedCount:N0}");
        }

        return lookup;
    }

    private static string ResolveRentalManagementCompanyBusinessDatabaseName(RentalManagementCompanyDto dto)
        => ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.Code, dto.Code);

    internal static List<RentalManagementCompanyDto>
        BuildRentalManagementCompanyPushPayload(
            IReadOnlyCollection<LocalRentalManagementCompany> dirtyCompanies,
            IReadOnlyCollection<LocalRentalManagementCompany> referencedCompanies)
    {
        var candidates = dirtyCompanies
            .Select(company => (Company: company, IsDirty: true))
            .Concat(referencedCompanies.Select(company =>
                (Company: company, IsDirty: false)))
            .GroupBy(candidate => candidate.Company.Id)
            .Select(group => group
                .OrderByDescending(candidate => candidate.IsDirty)
                .ThenByDescending(candidate => candidate.Company.Revision)
                .ThenByDescending(candidate => candidate.Company.UpdatedAtUtc)
                .First())
            .Select(candidate => (
                candidate.Company,
                candidate.IsDirty,
                Dto: LocalMappings.ToDto(candidate.Company)))
            .ToList();

        var payload = new List<RentalManagementCompanyDto>();
        foreach (var naturalKeyGroup in candidates.GroupBy(candidate =>
                     BuildRentalManagementCompanyPushNaturalKey(
                         candidate.Dto)))
        {
            var dirtyRows = naturalKeyGroup
                .Where(candidate => candidate.IsDirty)
                .ToList();
            if (dirtyRows.Count > 1)
            {
                var ids = string.Join(
                    ", ",
                    dirtyRows
                        .Select(candidate => candidate.Dto.Id)
                        .OrderBy(id => id)
                        .Select(id => id.ToString("D")));
                throw new InvalidOperationException(
                    "Multiple dirty rental management companies resolve to " +
                    $"the same sync key '{naturalKeyGroup.Key}'. " +
                    "The push was blocked before mutation stamping or server " +
                    $"submission. ids={ids}");
            }

            var selected = dirtyRows.Count == 1
                ? dirtyRows[0]
                : naturalKeyGroup
                    .OrderByDescending(candidate =>
                        candidate.Dto.Revision)
                    .ThenByDescending(candidate =>
                        candidate.Dto.UpdatedAtUtc)
                    .ThenBy(candidate => candidate.Dto.Id)
                    .First();
            payload.Add(selected.Dto);
        }

        return payload;
    }

    private static string BuildRentalManagementCompanyPushNaturalKey(
        RentalManagementCompanyDto dto)
    {
        var businessDatabaseName =
            ResolveRentalManagementCompanyBusinessDatabaseName(dto);
        var canonicalCode =
            OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                dto.Code,
                dto.Code);
        return $"{businessDatabaseName}|{canonicalCode}";
    }

    private static string ResolveRentalBusinessDatabaseName(string? tenantCode, string? officeCode, string? fallbackOfficeCode)
        => TenantScopeCatalog.GetDatabaseName(
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                tenantCode,
                officeCode,
                fallbackOfficeCode: fallbackOfficeCode));

    private async Task<List<LocalRentalBillingProfile>> LoadReferencedRentalBillingProfilesForPushAsync(
        IReadOnlyCollection<LocalRentalAsset> rentalAssets,
        IReadOnlyCollection<LocalRentalAssetAssignmentHistory> dirtyRentalAssetAssignmentHistories,
        IReadOnlyCollection<LocalRentalBillingLog> dirtyRentalBillingLogs,
        IReadOnlyCollection<LocalRentalBillingProfile> dirtyRentalBillingProfiles,
        CancellationToken ct)
    {
        if (rentalAssets.Count == 0 &&
            dirtyRentalAssetAssignmentHistories.Count == 0 &&
            dirtyRentalBillingLogs.Count == 0)
            return [];

        var existingProfileIds = dirtyRentalBillingProfiles
            .Select(profile => profile.Id)
            .ToHashSet();

        var referencedProfileIds = rentalAssets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .Select(asset => asset.BillingProfileId!.Value)
            .Concat(dirtyRentalAssetAssignmentHistories
                .Where(history => history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty)
                .Select(history => history.BillingProfileId!.Value))
            .Concat(dirtyRentalBillingLogs
                .Where(log => log.BillingProfileId != Guid.Empty)
                .Select(log => log.BillingProfileId))
            .Distinct()
            .Where(profileId => !existingProfileIds.Contains(profileId))
            .ToList();

        if (referencedProfileIds.Count == 0)
            return [];

        var referencedProfiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile =>
                referencedProfileIds.Contains(profile.Id) &&
                !profile.IsDirty &&
                !profile.IsDeleted)
            .ToListAsync(ct);

        var missingProfileIds = referencedProfileIds
            .Except(referencedProfiles.Select(profile => profile.Id))
            .ToList();

        if (missingProfileIds.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 렌탈 청구 프로필 누락 감지: 로컬 자산이 참조하지만 로컬 청구 프로필이 없는 항목 {missingProfileIds.Count}건을 확인했습니다. " +
                $"details={string.Join(", ", missingProfileIds.Take(10))}");
        }

        return referencedProfiles;
    }

    private async Task<List<LocalCustomer>> LoadReferencedRentalCustomersForPushAsync(
        IReadOnlyCollection<LocalRentalAsset> rentalAssets,
        IReadOnlyCollection<LocalRentalBillingProfile> rentalBillingProfiles,
        IReadOnlyCollection<LocalRentalAssetAssignmentHistory> dirtyRentalAssetAssignmentHistories,
        IReadOnlyCollection<LocalCustomer> dirtyCustomers,
        CancellationToken ct)
    {
        var existingCustomerIds = dirtyCustomers
            .Select(customer => customer.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var referencedCustomerIds = rentalAssets
            .Select(asset => asset.CustomerId)
            .Concat(rentalBillingProfiles.Select(profile => profile.CustomerId))
            .Concat(dirtyRentalAssetAssignmentHistories.Select(history => history.CustomerId))
            .Where(customerId =>
                customerId.HasValue &&
                customerId.Value != Guid.Empty &&
                !existingCustomerIds.Contains(customerId.Value))
            .Select(customerId => customerId!.Value)
            .Distinct()
            .ToList();

        if (referencedCustomerIds.Count == 0)
            return [];

        var referencedCustomers = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer =>
                referencedCustomerIds.Contains(customer.Id) &&
                !customer.IsDirty &&
                !customer.IsDeleted)
            .ToListAsync(ct);
        var unavailableCustomerIds = referencedCustomerIds
            .Except(referencedCustomers.Select(customer => customer.Id))
            .ToList();
        if (unavailableCustomerIds.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 렌탈 거래처 참조 보강 제외: 로컬 참조가 없거나 별도 dirty 변경인 거래처 {unavailableCustomerIds.Count}건은 참조 전용 payload로 보내지 않습니다. " +
                $"details={string.Join(", ", unavailableCustomerIds.Take(10))}");
        }

        return referencedCustomers;
    }

    private async Task<List<LocalItem>> LoadReferencedRentalItemsForPushAsync(
        IReadOnlyCollection<LocalRentalAsset> rentalAssets,
        IReadOnlyCollection<LocalItem> dirtyItems,
        CancellationToken ct)
    {
        var existingItemIds = dirtyItems
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var referencedItemIds = rentalAssets
            .Select(asset => asset.ItemId)
            .Where(itemId =>
                itemId.HasValue &&
                itemId.Value != Guid.Empty &&
                !existingItemIds.Contains(itemId.Value))
            .Select(itemId => itemId!.Value)
            .Distinct()
            .ToList();

        if (referencedItemIds.Count == 0)
            return [];

        var referencedItems = await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item =>
                referencedItemIds.Contains(item.Id) &&
                !item.IsDirty &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var unavailableItemIds = referencedItemIds
            .Except(referencedItems.Select(item => item.Id))
            .ToList();
        if (unavailableItemIds.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 렌탈 품목 참조 보강 제외: 로컬 참조가 없거나 별도 dirty 변경인 품목 {unavailableItemIds.Count}건은 참조 전용 payload로 보내지 않습니다. " +
                $"details={string.Join(", ", unavailableItemIds.Take(10))}");
        }

        return referencedItems;
    }

    private async Task<List<LocalRentalAsset>> LoadReferencedRentalAssetsForPushAsync(
        IReadOnlyCollection<LocalRentalAssetAssignmentHistory> dirtyRentalAssetAssignmentHistories,
        IReadOnlyCollection<LocalRentalAsset> dirtyRentalAssets,
        CancellationToken ct)
    {
        if (dirtyRentalAssetAssignmentHistories.Count == 0)
            return [];

        var existingAssetIds = dirtyRentalAssets
            .Select(asset => asset.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var referencedAssetIds = dirtyRentalAssetAssignmentHistories
            .Select(history => history.AssetId)
            .Where(assetId => assetId != Guid.Empty && !existingAssetIds.Contains(assetId))
            .Distinct()
            .ToList();

        if (referencedAssetIds.Count == 0)
            return [];

        var referencedAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset =>
                referencedAssetIds.Contains(asset.Id) &&
                !asset.IsDirty &&
                !asset.IsDeleted)
            .ToListAsync(ct);
        var missingAssetIds = referencedAssetIds
            .Except(referencedAssets.Select(asset => asset.Id))
            .ToList();

        if (missingAssetIds.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 렌탈 자산 누락 감지: 로컬 배정 이력이 참조하지만 로컬 자산이 없는 항목 {missingAssetIds.Count}건을 확인했습니다. " +
                $"details={string.Join(", ", missingAssetIds.Take(10))}");
        }

        return referencedAssets;
    }

    private async Task<List<LocalRentalManagementCompany>> LoadReferencedRentalManagementCompaniesForPushAsync(
        IReadOnlyCollection<LocalRentalAsset> rentalAssets,
        IReadOnlyCollection<LocalRentalBillingProfile> referencedRentalBillingProfiles,
        IReadOnlyCollection<LocalRentalManagementCompany> dirtyRentalManagementCompanies,
        CancellationToken ct)
    {
        var existingCodes = dirtyRentalManagementCompanies
            .Select(company => (company.Code ?? string.Empty).Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referencedCodes = rentalAssets
            .Where(asset => !asset.IsDeleted)
            .Select(asset => (asset.ManagementCompanyCode ?? string.Empty).Trim())
            .Concat(referencedRentalBillingProfiles.Select(profile => (profile.ManagementCompanyCode ?? string.Empty).Trim()))
            .Where(code => !string.IsNullOrWhiteSpace(code) && !existingCodes.Contains(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (referencedCodes.Count == 0)
            return [];

        var referencedCompanies = await _db.RentalManagementCompanies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(company =>
                referencedCodes.Contains(company.Code) &&
                !company.IsDirty &&
                !company.IsDeleted)
            .ToListAsync(ct);

        var missingCodes = referencedCodes
            .Except(referencedCompanies.Select(company => company.Code), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingCodes.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 렌탈 관리업체 누락 감지: 로컬 자산/청구 프로필이 참조하지만 로컬 관리업체가 없는 코드 {missingCodes.Count}건을 확인했습니다. " +
                $"details={string.Join(", ", missingCodes.Take(10))}");
        }

        return referencedCompanies;
    }

    private async Task ResolveServerNewerConflictsAsync(IReadOnlyCollection<ConflictLogDto> conflicts, CancellationToken ct)
    {
        foreach (var group in conflicts
                     .Where(conflict => Guid.TryParse(conflict.EntityId, out _))
                     .GroupBy(conflict => conflict.EntityName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(group.Key, "ItemCategoryOption", StringComparison.OrdinalIgnoreCase))
            {
                var unresolvedItemCategoryIds = new List<Guid>();
                foreach (var conflict in group)
                {
                    if (await TryApplyServerNewerItemCategoryOptionSnapshotAsync(conflict, ct))
                        continue;

                    if (Guid.TryParse(conflict.EntityId, out var unresolvedId))
                        unresolvedItemCategoryIds.Add(unresolvedId);
                }

                if (unresolvedItemCategoryIds.Count > 0)
                    await MarkServerNewerConflictsCleanAsync<LocalItemCategoryOption>(unresolvedItemCategoryIds, ct);

                continue;
            }

            var ids = group
                .Select(conflict => Guid.TryParse(conflict.EntityId, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
                continue;

            switch (group.Key)
            {
                case "CompanyProfile":
                    await MarkServerNewerConflictsCleanAsync<LocalCompanyProfile>(ids, ct);
                    break;
                case "Customer":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomer>(ids, ct);
                    break;
                case "CustomerCategory":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomerCategory>(ids, ct);
                    break;
                case "CustomerMaster":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomerMaster>(ids, ct);
                    break;
                case "CustomerContract":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomerContract>(ids, ct);
                    break;
                case "Item":
                    var unresolvedItemIds = new List<Guid>();
                    foreach (var conflict in group)
                    {
                        if (await TryApplyServerItemConflictSnapshotAsync(conflict, ct))
                            continue;

                        if (Guid.TryParse(conflict.EntityId, out var unresolvedId))
                            unresolvedItemIds.Add(unresolvedId);
                    }

                    if (unresolvedItemIds.Count > 0)
                        await MarkServerNewerConflictsCleanAsync<LocalItem>(unresolvedItemIds, ct);
                    break;
                case "ItemCategoryOption":
                    await MarkServerNewerConflictsCleanAsync<LocalItemCategoryOption>(ids, ct);
                    break;
                case "ItemPriceGrade":
                    await MarkServerNewerConflictsCleanAsync<LocalItemPriceGrade>(ids, ct);
                    break;
                case "PriceGradeOption":
                    await MarkServerNewerConflictsCleanAsync<LocalPriceGradeOption>(ids, ct);
                    break;
                case "TradeTypeOption":
                    await MarkServerNewerConflictsCleanAsync<LocalTradeTypeOption>(ids, ct);
                    break;
                case "Unit":
                    await MarkServerNewerConflictsCleanAsync<LocalUnit>(ids, ct);
                    break;
                case "Invoice":
                    await MarkServerNewerConflictsCleanAsync<LocalInvoice>(ids, ct);
                    break;
                case "Payment":
                    await MarkServerNewerConflictsCleanAsync<LocalPayment>(ids, ct);
                    break;
                case "TransactionRecord":
                    await MarkServerNewerConflictsCleanAsync<LocalTransaction>(ids, ct);
                    break;
                case "TransactionAttachment":
                    await MarkServerNewerConflictsCleanAsync<LocalTransactionAttachment>(ids, ct);
                    break;
                case "InventoryTransfer":
                    await MarkServerNewerConflictsCleanAsync<LocalInventoryTransfer>(ids, ct);
                    break;
                case "RentalManagementCompany":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalManagementCompany>(ids, ct);
                    break;
                case "RentalBillingProfile":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalBillingProfile>(ids, ct);
                    break;
                case "RentalAsset":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalAsset>(ids, ct);
                    break;
                case "RentalAssetAssignmentHistory":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalAssetAssignmentHistory>(ids, ct);
                    break;
                case "RentalBillingLog":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalBillingLog>(ids, ct);
                    break;
            }
        }
    }

    private async Task<List<ConflictLogDto>> ResolveCanonicalItemRevisionConflictsAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        CancellationToken ct)
    {
        var resolved = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryApplyServerItemConflictSnapshotAsync(conflict, ct))
                resolved.Add(conflict);
        }

        return resolved;
    }

    private async Task<List<ConflictLogDto>> PrepareCompanyProfileRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareCompanyProfileRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPrepareCompanyProfileRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "CompanyProfile", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var profileId) || profileId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictCompanyProfileDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != profileId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictCompanyProfileDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != profileId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var profile = await _db.CompanyProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null || !profile.IsDirty || profile.IsDeleted)
            return false;

        var localSnapshot = LocalMappings.ToDto(profile);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode))
            return false;

        profile.Revision = serverSnapshot.Revision;
        profile.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(profile);
        await RequeuePreparedMutationAsync(
            nameof(LocalCompanyProfile),
            profileId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<List<ConflictLogDto>> PrepareCustomerRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareCustomerRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPrepareCustomerRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "Customer", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var customerId) || customerId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictCustomerDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != customerId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictCustomerDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != customerId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null || !customer.IsDirty || customer.IsDeleted)
            return false;

        var localSnapshot = LocalMappings.ToDto(customer);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!HaveCompatibleCustomerScope(localSnapshot, serverSnapshot))
            return false;

        customer.Revision = serverSnapshot.Revision;
        customer.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(customer);
        await RequeuePreparedMutationAsync(
            nameof(LocalCustomer),
            customerId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatibleCustomerScope(CustomerDto localSnapshot, CustomerDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.ResponsibleOfficeCode, serverSnapshot.ResponsibleOfficeCode))
            return false;

        return true;
    }

    private async Task<List<ConflictLogDto>> PrepareInvoiceRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareInvoiceRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPrepareInvoiceRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "Invoice", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var invoiceId) || invoiceId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictInvoiceDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != invoiceId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictInvoiceDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != invoiceId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .Include(current => current.Payments)
            .FirstOrDefaultAsync(current => current.Id == invoiceId, ct);
        if (invoice is null || !invoice.IsDirty || invoice.IsDeleted)
            return false;

        var localSnapshot = LocalMappings.ToDto(invoice);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!HaveCompatibleInvoiceScope(localSnapshot, serverSnapshot))
            return false;

        invoice.Revision = serverSnapshot.Revision;
        invoice.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(invoice);
        await RequeuePreparedMutationAsync(
            nameof(LocalInvoice),
            invoiceId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatibleInvoiceScope(InvoiceDto localSnapshot, InvoiceDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (localSnapshot.CustomerId != serverSnapshot.CustomerId)
            return false;

        if (localSnapshot.VoucherType != serverSnapshot.VoucherType)
            return false;

        if (localSnapshot.VersionGroupId != Guid.Empty &&
            serverSnapshot.VersionGroupId != Guid.Empty &&
            localSnapshot.VersionGroupId != serverSnapshot.VersionGroupId)
        {
            return false;
        }

        if (!IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.ResponsibleOfficeCode, serverSnapshot.ResponsibleOfficeCode))
            return false;

        return true;
    }

    private async Task<List<ConflictLogDto>> PreparePaymentRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPreparePaymentRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPreparePaymentRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "Payment", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var paymentId) || paymentId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictPaymentDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != paymentId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictPaymentDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != paymentId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var payment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == paymentId, ct);
        if (payment is null || !payment.IsDirty || payment.IsDeleted)
            return false;

        var localSnapshot = LocalMappings.ToDto(payment);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!HaveCompatiblePaymentScope(localSnapshot, serverSnapshot))
            return false;

        payment.Revision = serverSnapshot.Revision;
        payment.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(payment);
        await RequeuePreparedMutationAsync(
            nameof(LocalPayment),
            paymentId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatiblePaymentScope(PaymentDto localSnapshot, PaymentDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (localSnapshot.InvoiceId == Guid.Empty || serverSnapshot.InvoiceId == Guid.Empty)
            return false;

        return localSnapshot.InvoiceId == serverSnapshot.InvoiceId;
    }

    private async Task<List<ConflictLogDto>> PrepareTransactionRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareTransactionRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPrepareTransactionRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "TransactionRecord", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(conflict.EntityName, "Transaction", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var transactionId) || transactionId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictTransactionDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != transactionId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictTransactionDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != transactionId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (transaction is null || !transaction.IsDirty || transaction.IsDeleted)
            return false;

        var localSnapshot = LocalMappings.ToDto(transaction);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!HaveCompatibleTransactionScope(localSnapshot, serverSnapshot))
            return false;

        transaction.Revision = serverSnapshot.Revision;
        transaction.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(transaction);
        await RequeuePreparedMutationAsync(
            nameof(LocalTransaction),
            transactionId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatibleTransactionScope(TransactionDto localSnapshot, TransactionDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (localSnapshot.CustomerId != serverSnapshot.CustomerId)
            return false;

        if (!string.Equals(
                PaymentFlowConstants.NormalizeTransactionKind(localSnapshot.TransactionKind, PaymentFlowConstants.IsPaymentKind(localSnapshot.TransactionKind)),
                PaymentFlowConstants.NormalizeTransactionKind(serverSnapshot.TransactionKind, PaymentFlowConstants.IsPaymentKind(serverSnapshot.TransactionKind)),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!AreSameOptionalGuid(localSnapshot.LinkedInvoiceId, serverSnapshot.LinkedInvoiceId))
            return false;

        if (!AreSameOptionalGuid(localSnapshot.LinkedRentalBillingProfileId, serverSnapshot.LinkedRentalBillingProfileId))
            return false;

        if (!AreSameOptionalGuid(localSnapshot.LinkedRentalBillingRunId, serverSnapshot.LinkedRentalBillingRunId))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.ResponsibleOfficeCode, serverSnapshot.ResponsibleOfficeCode))
            return false;

        return true;
    }

    private static bool AreSameOptionalGuid(Guid? left, Guid? right)
    {
        var normalizedLeft = left.GetValueOrDefault();
        var normalizedRight = right.GetValueOrDefault();
        return normalizedLeft == normalizedRight;
    }

    private async Task<List<ConflictLogDto>> PrepareTransactionAttachmentRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareTransactionAttachmentRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPrepareTransactionAttachmentRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "TransactionAttachment", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var attachmentId) || attachmentId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictTransactionAttachmentDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != attachmentId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictTransactionAttachmentDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != attachmentId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var attachment = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == attachmentId, ct);
        if (attachment is null || !attachment.IsDirty || attachment.IsDeleted)
            return false;

        var parentTransactionExists = await _db.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(
                transaction =>
                    transaction.Id == attachment.TransactionId &&
                    !transaction.IsDeleted,
                ct);
        if (!parentTransactionExists)
            return false;

        var localSnapshot = LocalMappings.ToDto(attachment);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!HaveCompatibleTransactionAttachmentScope(localSnapshot, serverSnapshot))
            return false;

        attachment.Revision = serverSnapshot.Revision;
        attachment.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(attachment);
        await RequeuePreparedMutationAsync(
            nameof(LocalTransactionAttachment),
            attachmentId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatibleTransactionAttachmentScope(
        TransactionAttachmentDto localSnapshot,
        TransactionAttachmentDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (localSnapshot.TransactionId == Guid.Empty || serverSnapshot.TransactionId == Guid.Empty)
            return false;

        return localSnapshot.TransactionId == serverSnapshot.TransactionId;
    }

    private async Task<List<ConflictLogDto>> PrepareInventoryTransferRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareInventoryTransferRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPrepareInventoryTransferRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "InventoryTransfer", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var transferId) || transferId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictInventoryTransferDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != transferId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictInventoryTransferDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != transferId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null || !transfer.IsDirty || transfer.IsDeleted)
            return false;

        var localSnapshot = LocalMappings.ToDto(transfer);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!HaveCompatibleInventoryTransferScope(localSnapshot, serverSnapshot))
            return false;

        transfer.Revision = serverSnapshot.Revision;
        transfer.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(transfer);
        await RequeuePreparedMutationAsync(
            nameof(LocalInventoryTransfer),
            transferId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatibleInventoryTransferScope(
        InventoryTransferDto localSnapshot,
        InventoryTransferDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.SourceOfficeCode, serverSnapshot.SourceOfficeCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.TargetOfficeCode, serverSnapshot.TargetOfficeCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.FromWarehouseCode, serverSnapshot.FromWarehouseCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.ToWarehouseCode, serverSnapshot.ToWarehouseCode))
            return false;

        if (!string.Equals(
                InventoryTransferStatusNormalizer.Normalize(
                    localSnapshot.TransferStatus,
                    localSnapshot.ReceivedByUsername,
                    localSnapshot.ReceivedAtUtc,
                    localSnapshot.RejectedByUsername,
                    localSnapshot.RejectedAtUtc),
                InventoryTransferStatusNormalizer.Normalize(
                    serverSnapshot.TransferStatus,
                    serverSnapshot.ReceivedByUsername,
                    serverSnapshot.ReceivedAtUtc,
                    serverSnapshot.RejectedByUsername,
                    serverSnapshot.RejectedAtUtc),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task<List<ConflictLogDto>> PrepareItemRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareItemRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private async Task<bool> TryPrepareItemRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "Item", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var itemId) || itemId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictItemDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != itemId ||
            clientSnapshot.IsDeleted)
        {
            return false;
        }

        if (!TryDeserializeConflictItemDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != itemId ||
            serverSnapshot.IsDeleted)
        {
            return false;
        }

        var item = await _db.Items
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == itemId, ct);
        if (item is null || !item.IsDirty || item.IsDeleted)
            return false;

        var localSnapshot = MapItemForOutboundSync(item);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!HaveCompatibleItemScope(localSnapshot, serverSnapshot))
            return false;

        item.Revision = serverSnapshot.Revision;
        item.IsDirty = true;

        var rebasedSnapshot = MapItemForOutboundSync(item);
        await RequeuePreparedMutationAsync(
            nameof(LocalItem),
            itemId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatibleItemScope(ItemDto localSnapshot, ItemDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode))
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode))
            return false;

        return true;
    }

    private static bool IsSameNonEmptyScope(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return true;

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServerConflictActorCurrentSessionOrUnknown(ConflictLogDto conflict, SessionState session)
    {
        var serverUserId = conflict.ServerUserId.GetValueOrDefault();
        var hasServerUserId = conflict.ServerUserId.HasValue && serverUserId != Guid.Empty;
        var serverUsername = (conflict.ServerUsername ?? string.Empty).Trim();
        if (!hasServerUserId && string.IsNullOrWhiteSpace(serverUsername))
            return true;

        var currentUser = session.User;
        if (currentUser is null)
            return false;

        if (hasServerUserId &&
            currentUser.UserId != Guid.Empty &&
            currentUser.UserId == serverUserId)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(serverUsername) &&
               string.Equals(serverUsername, currentUser.Username?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<ConflictLogDto>> PrepareGenericRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareGenericRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private Task<bool> TryPrepareGenericRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var entityName = (conflict.EntityName ?? string.Empty).Trim();
        return entityName switch
        {
            "CompanyProfile" => TryPrepareGenericRevisionRetryAsync<LocalCompanyProfile, CompanyProfileDto>(
                conflict,
                nameof(LocalCompanyProfile),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleCompanyProfileScope,
                null,
                ct),
            "Unit" => TryPrepareGenericRevisionRetryAsync<LocalUnit, UnitDto>(
                conflict,
                nameof(LocalUnit),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleSharedCatalogScope,
                null,
                ct),
            "CustomerCategory" => TryPrepareGenericRevisionRetryAsync<LocalCustomerCategory, CustomerCategoryDto>(
                conflict,
                nameof(LocalCustomerCategory),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleSharedCatalogScope,
                null,
                ct),
            "PriceGradeOption" => TryPrepareGenericRevisionRetryAsync<LocalPriceGradeOption, PriceGradeOptionDto>(
                conflict,
                nameof(LocalPriceGradeOption),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleSharedCatalogScope,
                null,
                ct),
            "TradeTypeOption" => TryPrepareGenericRevisionRetryAsync<LocalTradeTypeOption, TradeTypeOptionDto>(
                conflict,
                nameof(LocalTradeTypeOption),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleSharedCatalogScope,
                null,
                ct),
            "ItemCategoryOption" => TryPrepareGenericRevisionRetryAsync<LocalItemCategoryOption, ItemCategoryOptionDto>(
                conflict,
                nameof(LocalItemCategoryOption),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleSharedCatalogScope,
                null,
                ct),
            "CustomerMaster" => TryPrepareGenericRevisionRetryAsync<LocalCustomerMaster, CustomerMasterDto>(
                conflict,
                nameof(LocalCustomerMaster),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleCustomerMasterScope,
                null,
                ct),
            "Customer" => TryPrepareGenericRevisionRetryAsync<LocalCustomer, CustomerDto>(
                conflict,
                nameof(LocalCustomer),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleCustomerScope,
                null,
                ct),
            "CustomerContract" => TryPrepareGenericRevisionRetryAsync<LocalCustomerContract, CustomerContractDto>(
                conflict,
                nameof(LocalCustomerContract),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleCustomerContractScope,
                HasValidCustomerContractRetryReferencesAsync,
                ct),
            "Item" => TryPrepareGenericRevisionRetryAsync<LocalItem, ItemDto>(
                conflict,
                nameof(LocalItem),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleItemScope,
                null,
                ct),
            "Transaction" or "TransactionRecord" => TryPrepareGenericRevisionRetryAsync<LocalTransaction, TransactionDto>(
                conflict,
                nameof(LocalTransaction),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleTransactionScope,
                null,
                ct),
            "Payment" => TryPrepareGenericRevisionRetryAsync<LocalPayment, PaymentDto>(
                conflict,
                nameof(LocalPayment),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatiblePaymentScope,
                null,
                ct),
            "RentalManagementCompany" => TryPrepareGenericRevisionRetryAsync<LocalRentalManagementCompany, RentalManagementCompanyDto>(
                conflict,
                nameof(LocalRentalManagementCompany),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleRentalManagementCompanyScope,
                null,
                ct),
            "RentalBillingProfile" => TryPrepareGenericRevisionRetryAsync<LocalRentalBillingProfile, RentalBillingProfileDto>(
                conflict,
                nameof(LocalRentalBillingProfile),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleRentalBillingProfileScope,
                null,
                ct),
            "RentalAsset" => TryPrepareGenericRevisionRetryAsync<LocalRentalAsset, RentalAssetDto>(
                conflict,
                nameof(LocalRentalAsset),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleRentalAssetScope,
                HasValidRentalAssetRetryReferencesAsync,
                ct),
            "RentalAssetAssignmentHistory" => TryPrepareGenericRevisionRetryAsync<LocalRentalAssetAssignmentHistory, RentalAssetAssignmentHistoryDto>(
                conflict,
                nameof(LocalRentalAssetAssignmentHistory),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleRentalAssetAssignmentHistoryScope,
                null,
                ct),
            "RentalBillingLog" => TryPrepareGenericRevisionRetryAsync<LocalRentalBillingLog, RentalBillingLogDto>(
                conflict,
                nameof(LocalRentalBillingLog),
                deviceId,
                session,
                LocalMappings.ToDto,
                HaveCompatibleRentalBillingLogScope,
                null,
                ct),
            _ => Task.FromResult(false)
        };
    }

    private async Task<bool> TryPrepareGenericRevisionRetryAsync<TLocal, TDto>(
        ConflictLogDto conflict,
        string localEntityName,
        string deviceId,
        SessionState session,
        Func<TLocal, TDto> mapToDto,
        Func<TDto, TDto, bool> haveCompatibleScope,
        Func<TLocal, CancellationToken, Task<bool>>? canRetryLocal,
        CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var entityId) || entityId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictDto<TDto>(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != entityId)
        {
            return false;
        }

        if (!TryDeserializeConflictDto<TDto>(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != entityId)
        {
            return false;
        }

        if (serverSnapshot.IsDeleted)
            return false;

        var localEntity = await _db.Set<TLocal>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == entityId, ct);
        if (localEntity is null || !localEntity.IsDirty)
            return false;

        var localSnapshot = mapToDto(localEntity);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot, EquivalentConflictIgnoredPropertyNames))
            return false;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localUpdatedAtUtc < serverUpdatedAtUtc)
            return false;

        if (!haveCompatibleScope(localSnapshot, serverSnapshot))
            return false;

        if (canRetryLocal is not null && !await canRetryLocal(localEntity, ct))
            return false;

        localEntity.Revision = serverSnapshot.Revision;
        localEntity.IsDirty = true;

        var rebasedSnapshot = mapToDto(localEntity);
        await RequeuePreparedMutationAsync(
            localEntityName,
            entityId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HaveCompatibleSharedCatalogScope<TDto>(TDto localSnapshot, TDto serverSnapshot)
        where TDto : SyncEntityDto
        => localSnapshot.Id == serverSnapshot.Id;

    private static bool HaveCompatibleCompanyProfileScope(CompanyProfileDto localSnapshot, CompanyProfileDto serverSnapshot)
        => localSnapshot.Id == serverSnapshot.Id &&
           IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode);

    private static bool HaveCompatibleCustomerMasterScope(CustomerMasterDto localSnapshot, CustomerMasterDto serverSnapshot)
        => localSnapshot.Id == serverSnapshot.Id &&
           IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode) &&
           IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode);

    private static bool HaveCompatibleCustomerContractScope(CustomerContractDto localSnapshot, CustomerContractDto serverSnapshot)
        => localSnapshot.Id == serverSnapshot.Id &&
           localSnapshot.CustomerId != Guid.Empty &&
           localSnapshot.CustomerId == serverSnapshot.CustomerId;

    private async Task<bool> HasValidCustomerContractRetryReferencesAsync(LocalCustomerContract contract, CancellationToken ct)
    {
        if (contract.IsDeleted)
            return true;

        return contract.CustomerId != Guid.Empty &&
               await _db.Customers.IgnoreQueryFilters()
                   .AnyAsync(customer => customer.Id == contract.CustomerId && !customer.IsDeleted, ct);
    }

    private static bool HaveCompatibleRentalManagementCompanyScope(
        RentalManagementCompanyDto localSnapshot,
        RentalManagementCompanyDto serverSnapshot)
        => localSnapshot.Id == serverSnapshot.Id &&
           IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode) &&
           IsSameNonEmptyScope(localSnapshot.Code, serverSnapshot.Code);

    private static bool HaveCompatibleRentalBillingProfileScope(
        RentalBillingProfileDto localSnapshot,
        RentalBillingProfileDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode) ||
            !IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode) ||
            !IsSameNonEmptyScope(localSnapshot.ResponsibleOfficeCode, serverSnapshot.ResponsibleOfficeCode))
        {
            return false;
        }

        if (!IsSameNonEmptyScope(localSnapshot.ProfileKey, serverSnapshot.ProfileKey))
            return false;

        if (localSnapshot.CustomerId.HasValue &&
            serverSnapshot.CustomerId.HasValue &&
            localSnapshot.CustomerId.Value != serverSnapshot.CustomerId.Value)
        {
            return false;
        }

        return true;
    }

    private static bool HaveCompatibleRentalAssetScope(RentalAssetDto localSnapshot, RentalAssetDto serverSnapshot)
    {
        if (localSnapshot.Id != serverSnapshot.Id)
            return false;

        if (!IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode) ||
            !IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode) ||
            !IsSameNonEmptyScope(localSnapshot.ResponsibleOfficeCode, serverSnapshot.ResponsibleOfficeCode))
        {
            return false;
        }

        if (!IsSameNonEmptyScope(localSnapshot.AssetKey, serverSnapshot.AssetKey) ||
            !IsSameNonEmptyScope(localSnapshot.ManagementNumber, serverSnapshot.ManagementNumber))
        {
            return false;
        }

        return true;
    }

    private static bool HaveCompatibleRentalAssetAssignmentHistoryScope(
        RentalAssetAssignmentHistoryDto localSnapshot,
        RentalAssetAssignmentHistoryDto serverSnapshot)
        => localSnapshot.Id == serverSnapshot.Id &&
           localSnapshot.AssetId != Guid.Empty &&
           localSnapshot.AssetId == serverSnapshot.AssetId &&
           IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode) &&
           IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode) &&
           IsSameNonEmptyScope(localSnapshot.ResponsibleOfficeCode, serverSnapshot.ResponsibleOfficeCode);

    private static bool HaveCompatibleRentalBillingLogScope(
        RentalBillingLogDto localSnapshot,
        RentalBillingLogDto serverSnapshot)
        => localSnapshot.Id == serverSnapshot.Id &&
           localSnapshot.BillingProfileId != Guid.Empty &&
           localSnapshot.BillingProfileId == serverSnapshot.BillingProfileId &&
           IsSameNonEmptyScope(localSnapshot.BillingYearMonth, serverSnapshot.BillingYearMonth) &&
           IsSameNonEmptyScope(localSnapshot.TenantCode, serverSnapshot.TenantCode) &&
           IsSameNonEmptyScope(localSnapshot.OfficeCode, serverSnapshot.OfficeCode) &&
           IsSameNonEmptyScope(localSnapshot.ResponsibleOfficeCode, serverSnapshot.ResponsibleOfficeCode);

    private async Task<List<ConflictLogDto>> PrepareRentalBillingProfileRevisionRetriesAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var prepared = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            if (await TryPrepareRentalBillingProfileRevisionRetryAsync(conflict, deviceId, session, ct))
                prepared.Add(conflict);
        }

        return prepared;
    }

    private sealed record RentalAssetConflictRepairResult(
        IReadOnlyList<ConflictLogDto> ResolvedConflicts,
        IReadOnlyList<ConflictLogDto> PreparedRetryConflicts)
    {
        public int PreparedRetryCount => PreparedRetryConflicts.Count;
    }

    private async Task<RentalAssetConflictRepairResult> RepairRentalAssetRevisionConflictsAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        var resolved = new List<ConflictLogDto>();
        var preparedRetries = new List<ConflictLogDto>();

        foreach (var conflict in conflicts)
        {
            var outcome = await TryRepairRentalAssetRevisionConflictAsync(conflict, deviceId, session, ct);
            if (outcome is null)
                continue;

            if (outcome.Value.IsResolved)
                resolved.Add(conflict);
            else if (outcome.Value.PreparedRetry)
                preparedRetries.Add(conflict);
        }

        return new RentalAssetConflictRepairResult(resolved, preparedRetries);
    }

    private async Task<(bool IsResolved, bool PreparedRetry)?> TryRepairRentalAssetRevisionConflictAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "RentalAsset", StringComparison.OrdinalIgnoreCase))
            return null;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return null;

        if (!Guid.TryParse(conflict.EntityId, out var assetId) || assetId == Guid.Empty)
            return null;

        if (!TryDeserializeConflictRentalAssetDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != assetId)
        {
            return null;
        }

        if (!TryDeserializeConflictRentalAssetDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != assetId)
        {
            return null;
        }

        if (!AreEquivalentConflictPayloads(
                clientSnapshot,
                serverSnapshot,
                RentalAssetRevisionRetryIgnoredPropertyNames))
        {
            return null;
        }

        var asset = await _db.RentalAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == assetId, ct);
        if (asset is null || !asset.IsDirty || asset.IsDeleted)
            return null;

        var localSnapshot = LocalMappings.ToDto(asset);
        if (!AreEquivalentConflictPayloads(
                localSnapshot,
                clientSnapshot,
                RentalAssetRevisionRetryIgnoredPropertyNames))
            return null;

        var hasLocalCustomerReference = asset.CustomerId.HasValue &&
                                        asset.CustomerId.Value != Guid.Empty &&
                                        await _db.Customers.IgnoreQueryFilters()
                                            .AnyAsync(customer => customer.Id == asset.CustomerId.Value && !customer.IsDeleted, ct);
        var hasServerCustomerReference = serverSnapshot.CustomerId.HasValue &&
                                         serverSnapshot.CustomerId.Value != Guid.Empty &&
                                         await _db.Customers.IgnoreQueryFilters()
                                             .AnyAsync(customer => customer.Id == serverSnapshot.CustomerId.Value && !customer.IsDeleted, ct);
        var hasLocalBillingProfileReference = asset.BillingProfileId.HasValue &&
                                              asset.BillingProfileId.Value != Guid.Empty &&
                                              await _db.RentalBillingProfiles.IgnoreQueryFilters()
                                                  .AnyAsync(profile => profile.Id == asset.BillingProfileId.Value && !profile.IsDeleted, ct);
        var hasServerBillingProfileReference = serverSnapshot.BillingProfileId.HasValue &&
                                               serverSnapshot.BillingProfileId.Value != Guid.Empty &&
                                               await _db.RentalBillingProfiles.IgnoreQueryFilters()
                                                   .AnyAsync(profile => profile.Id == serverSnapshot.BillingProfileId.Value && !profile.IsDeleted, ct);
        var hasLocalItemReference = asset.ItemId.HasValue &&
                                    asset.ItemId.Value != Guid.Empty &&
                                    await _db.Items.IgnoreQueryFilters()
                                        .AnyAsync(item => item.Id == asset.ItemId.Value && !item.IsDeleted, ct);
        var hasServerItemReference = serverSnapshot.ItemId.HasValue &&
                                     serverSnapshot.ItemId.Value != Guid.Empty &&
                                     await _db.Items.IgnoreQueryFilters()
                                         .AnyAsync(item => item.Id == serverSnapshot.ItemId.Value && !item.IsDeleted, ct);

        MergeServerPreferredRentalAssetFields(
            asset,
            serverSnapshot,
            hasLocalCustomerReference,
            hasServerCustomerReference,
            hasLocalBillingProfileReference,
            hasServerBillingProfileReference,
            hasLocalItemReference,
            hasServerItemReference);
        var mergedSnapshot = LocalMappings.ToDto(asset);

        if (AreEquivalentConflictPayloads(mergedSnapshot, serverSnapshot))
        {
            asset.Revision = serverSnapshot.Revision;
            asset.UpdatedAtUtc = serverSnapshot.UpdatedAtUtc;
            asset.IsDeleted = serverSnapshot.IsDeleted;
            asset.IsDirty = false;
            await RemoveSupersededOutboxEntriesAsync(
                nameof(LocalRentalAsset),
                assetId,
                clientSnapshot.MutationId,
                ct);
            await _db.SaveChangesAsync(ct);
            return (IsResolved: true, PreparedRetry: false);
        }

        if (!await HasValidRentalAssetRetryReferencesAsync(asset, ct))
            return null;

        asset.Revision = serverSnapshot.Revision;
        asset.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(asset);
        await RequeuePreparedMutationAsync(
            nameof(LocalRentalAsset),
            assetId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return (IsResolved: false, PreparedRetry: true);
    }

    private async Task RemoveSupersededOutboxEntriesAsync(
        string entityName,
        Guid entityId,
        string? previousMutationId,
        CancellationToken ct)
    {
        var rows = await _db.SyncOutboxEntries
            .Where(entry =>
                entry.Status != "Acknowledged" &&
                ((entry.EntityName == entityName && entry.EntityId == entityId) ||
                 (!string.IsNullOrWhiteSpace(previousMutationId) && entry.MutationId == previousMutationId)))
            .ToListAsync(ct);
        if (rows.Count == 0)
            return;

        _db.SyncOutboxEntries.RemoveRange(rows);
    }

    private async Task<bool> TryPrepareRentalBillingProfileRevisionRetryAsync(
        ConflictLogDto conflict,
        string deviceId,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "RentalBillingProfile", StringComparison.OrdinalIgnoreCase))
            return false;

        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsServerConflictActorCurrentSessionOrUnknown(conflict, session))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var profileId) || profileId == Guid.Empty)
            return false;

        if (!TryDeserializeConflictRentalBillingProfileDto(conflict.ClientJson, out var clientSnapshot) ||
            clientSnapshot is null ||
            clientSnapshot.Id != profileId)
        {
            return false;
        }

        if (!TryDeserializeConflictRentalBillingProfileDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != profileId)
        {
            return false;
        }

        if (!AreEquivalentConflictPayloads(
                clientSnapshot,
                serverSnapshot,
                RentalBillingTemplateOnlyConflictIgnoredPropertyNames))
        {
            return false;
        }

        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null || !profile.IsDirty || profile.IsDeleted)
            return false;

        var localSnapshot = LocalMappings.ToDto(profile);
        if (!AreEquivalentConflictPayloads(localSnapshot, clientSnapshot))
            return false;

        var canonicalTemplateJson = await BuildCanonicalRentalBillingTemplateJsonAsync(profileId, profile, ct);
        if (string.IsNullOrWhiteSpace(canonicalTemplateJson))
            return false;

        if (!AreEquivalentBillingTemplateJson(clientSnapshot.BillingTemplateJson, canonicalTemplateJson))
            return false;

        if (AreEquivalentBillingTemplateJson(serverSnapshot.BillingTemplateJson, canonicalTemplateJson))
            return false;

        if (!string.Equals(profile.BillingTemplateJson ?? string.Empty, canonicalTemplateJson, StringComparison.Ordinal))
            profile.BillingTemplateJson = canonicalTemplateJson;

        profile.Revision = serverSnapshot.Revision;
        profile.IsDirty = true;

        var rebasedSnapshot = LocalMappings.ToDto(profile);
        await RequeuePreparedMutationAsync(
            nameof(LocalRentalBillingProfile),
            profileId,
            clientSnapshot.MutationId,
            rebasedSnapshot,
            deviceId,
            session,
            ct);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> HasValidRentalAssetRetryReferencesAsync(LocalRentalAsset asset, CancellationToken ct)
    {
        if (asset.BillingProfileId.HasValue &&
            asset.BillingProfileId.Value != Guid.Empty &&
            !await _db.RentalBillingProfiles.IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == asset.BillingProfileId.Value && !profile.IsDeleted, ct))
        {
            return false;
        }

        if (asset.CustomerId.HasValue &&
            asset.CustomerId.Value != Guid.Empty &&
            !await _db.Customers.IgnoreQueryFilters()
                .AnyAsync(customer => customer.Id == asset.CustomerId.Value && !customer.IsDeleted, ct))
        {
            return false;
        }

        if (asset.ItemId.HasValue &&
            asset.ItemId.Value != Guid.Empty &&
            !await _db.Items.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == asset.ItemId.Value && !item.IsDeleted, ct))
        {
            return false;
        }

        return true;
    }

    private static void MergeServerPreferredRentalAssetFields(
        LocalRentalAsset asset,
        RentalAssetDto serverSnapshot,
        bool hasLocalCustomerReference,
        bool hasServerCustomerReference,
        bool hasLocalBillingProfileReference,
        bool hasServerBillingProfileReference,
        bool hasLocalItemReference,
        bool hasServerItemReference)
    {
        if ((!asset.CustomerId.HasValue ||
             asset.CustomerId.Value == Guid.Empty ||
             !hasLocalCustomerReference) &&
            hasServerCustomerReference &&
            serverSnapshot.CustomerId.HasValue &&
            serverSnapshot.CustomerId.Value != Guid.Empty)
        {
            asset.CustomerId = serverSnapshot.CustomerId.Value;
            if (!string.IsNullOrWhiteSpace(serverSnapshot.CustomerName))
                asset.CustomerName = serverSnapshot.CustomerName.Trim();
            if (!string.IsNullOrWhiteSpace(serverSnapshot.CurrentCustomerName))
                asset.CurrentCustomerName = serverSnapshot.CurrentCustomerName.Trim();
        }

        if ((!asset.BillingProfileId.HasValue ||
             asset.BillingProfileId.Value == Guid.Empty ||
             !hasLocalBillingProfileReference) &&
            hasServerBillingProfileReference &&
            serverSnapshot.BillingProfileId.HasValue &&
            serverSnapshot.BillingProfileId.Value != Guid.Empty)
        {
            asset.BillingProfileId = serverSnapshot.BillingProfileId.Value;
        }

        if ((!asset.ItemId.HasValue ||
             asset.ItemId.Value == Guid.Empty ||
             !hasLocalItemReference) &&
            hasServerItemReference &&
            serverSnapshot.ItemId.HasValue &&
            serverSnapshot.ItemId.Value != Guid.Empty)
        {
            asset.ItemId = serverSnapshot.ItemId.Value;
        }

        if (string.IsNullOrWhiteSpace(asset.CustomerName) &&
            !string.IsNullOrWhiteSpace(serverSnapshot.CustomerName))
        {
            asset.CustomerName = serverSnapshot.CustomerName.Trim();
        }

        if (string.IsNullOrWhiteSpace(asset.CurrentCustomerName) &&
            !string.IsNullOrWhiteSpace(serverSnapshot.CurrentCustomerName))
        {
            asset.CurrentCustomerName = serverSnapshot.CurrentCustomerName.Trim();
        }

        if (asset.SalePrice <= 0m && serverSnapshot.SalePrice > 0m)
            asset.SalePrice = serverSnapshot.SalePrice;

        if (string.IsNullOrWhiteSpace(asset.InstallLocation) &&
            !string.IsNullOrWhiteSpace(serverSnapshot.InstallLocation))
        {
            asset.InstallLocation = serverSnapshot.InstallLocation.Trim();
        }

        if (string.IsNullOrWhiteSpace(asset.InstallSiteName) &&
            !string.IsNullOrWhiteSpace(serverSnapshot.InstallSiteName))
        {
            asset.InstallSiteName = serverSnapshot.InstallSiteName.Trim();
        }

        if (CollapseWhitespace(asset.Notes)
            .Equals(CollapseWhitespace(serverSnapshot.Notes), StringComparison.Ordinal))
        {
            asset.Notes = serverSnapshot.Notes ?? string.Empty;
        }
    }

    private Task<bool> TryApplyServerItemConflictSnapshotAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
        => _db.ExecuteRuntimeMutationOperationAsync(
            () => TryApplyServerItemConflictSnapshotCoreAsync(conflict, ct),
            ct);

    private async Task<bool> TryApplyServerItemConflictSnapshotCoreAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
    {
        if (!string.Equals(conflict.EntityName, "Item", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var canonicalItemId))
            return false;

        if (!TryDeserializeConflictItemDto(conflict.ServerJson, out var serverSnapshot) ||
            serverSnapshot is null ||
            serverSnapshot.Id != canonicalItemId)
        {
            return false;
        }

        var reason = (conflict.Reason ?? string.Empty).Trim();
        var isServerNewerConflict = string.Equals(reason, "Server version is newer.", StringComparison.OrdinalIgnoreCase);
        var isExpectedRevisionConflict = reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase);
        if (!isServerNewerConflict && !isExpectedRevisionConflict)
            return false;

        var canonicalSnapshot = LocalMappings.ToLocal(serverSnapshot);
        canonicalSnapshot.IsDirty = false;

        var duplicateIds = new HashSet<Guid>();
        if (TryExtractMutationEntityId(conflict, out var mutationEntityId) &&
            mutationEntityId != Guid.Empty &&
            mutationEntityId != canonicalItemId)
        {
            duplicateIds.Add(mutationEntityId);
        }

        var trackedItems = await _db.Items.IgnoreQueryFilters()
            .Where(item => item.Id == canonicalItemId || duplicateIds.Contains(item.Id) || item.IsDirty)
            .ToListAsync(ct);

        foreach (var candidate in trackedItems)
        {
            if (candidate.Id == canonicalItemId || !candidate.IsDirty)
                continue;

            if (ItemsShareRepairIdentity(candidate, serverSnapshot))
                duplicateIds.Add(candidate.Id);
        }

        var duplicateCandidates = trackedItems
            .Where(item => duplicateIds.Contains(item.Id))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        var canonicalExisting = trackedItems.FirstOrDefault(item => item.Id == canonicalItemId);

        if (duplicateCandidates.Count > 0)
        {
            var unsafeDuplicates = duplicateCandidates
                .Where(candidate => !AreEquivalentItemSnapshots(candidate, serverSnapshot))
                .Select(candidate => candidate.Id)
                .ToList();
            if (unsafeDuplicates.Count > 0)
            {
                AppLogger.Warn(
                    "SYNC",
                    $"품목 충돌 자동복구 보류: 서버 기준 품목 {canonicalItemId:D}와 내용이 다른 로컬 중복 품목 {unsafeDuplicates.Count}건이 있어 수동 확인이 필요합니다. " +
                    $"duplicates={string.Join(", ", unsafeDuplicates.Take(10))}");
                return false;
            }
        }

        if (canonicalExisting is not null &&
            canonicalExisting.IsDirty &&
            !isServerNewerConflict &&
            !AreEquivalentItemSnapshots(canonicalExisting, serverSnapshot))
        {
            return false;
        }

        _db.ChangeTracker.Clear();
        canonicalExisting = await _db.Items.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == canonicalItemId, ct);
        if (canonicalExisting is null)
        {
            _db.Items.Add(canonicalSnapshot);
        }
        else
        {
            _db.Entry(canonicalExisting).CurrentValues.SetValues(canonicalSnapshot);
            canonicalExisting.IsDirty = false;
        }

        await _db.SaveChangesAsync(ct);
        SynchronizeTrackedServerSnapshot(canonicalSnapshot);

        if (duplicateCandidates.Count > 0)
        {
            var duplicateToCanonicalIdMap = duplicateCandidates
                .Select(candidate => candidate.Id)
                .Distinct()
                .ToDictionary(id => id, _ => canonicalItemId);
            await RemapLocalItemReferencesAsync(duplicateToCanonicalIdMap, ct);

            _db.ChangeTracker.Clear();
            await _db.Items.IgnoreQueryFilters()
                .Where(item => duplicateToCanonicalIdMap.Keys.Contains(item.Id))
                .ExecuteDeleteAsync(ct);
            _db.ChangeTracker.Clear();

            AppLogger.Warn(
                "SYNC",
                $"품목 충돌 자동복구: 서버 기준 품목 {canonicalItemId:D}에 로컬 중복 품목 {duplicateToCanonicalIdMap.Count}건을 병합했습니다.");
        }

        return true;
    }

    private static bool TryDeserializeConflictItemDto(string? json, out ItemDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<ItemDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictCompanyProfileDto(string? json, out CompanyProfileDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<CompanyProfileDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictCustomerDto(string? json, out CustomerDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<CustomerDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictInvoiceDto(string? json, out InvoiceDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<InvoiceDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictPaymentDto(string? json, out PaymentDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<PaymentDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictTransactionDto(string? json, out TransactionDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<TransactionDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictTransactionAttachmentDto(string? json, out TransactionAttachmentDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<TransactionAttachmentDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictInventoryTransferDto(string? json, out InventoryTransferDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<InventoryTransferDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractMutationEntityId(ConflictLogDto conflict, out Guid entityId)
    {
        entityId = Guid.Empty;

        if (!TryDeserializeConflictItemDto(conflict.ClientJson, out var clientDto) ||
            clientDto is null ||
            string.IsNullOrWhiteSpace(clientDto.MutationId))
        {
            return false;
        }

        var segments = clientDto.MutationId
            .Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5)
            return false;

        var entityName = segments[^5];
        if (!string.Equals(entityName, "Item", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entityName, "LocalItem", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Guid.TryParseExact(segments[^4], "N", out entityId) || Guid.TryParse(segments[^4], out entityId);
    }

    private static bool ItemsShareRepairIdentity(LocalItem local, ItemDto incoming)
    {
        var localMaterial = NormalizeItemIdentityValue(local.MaterialNumber);
        var incomingMaterial = NormalizeItemIdentityValue(incoming.MaterialNumber);
        if (HasMeaningfulItemIdentityValue(localMaterial) && HasMeaningfulItemIdentityValue(incomingMaterial))
            return string.Equals(localMaterial, incomingMaterial, StringComparison.OrdinalIgnoreCase);

        var localSerial = NormalizeItemIdentityValue(local.SerialNumber);
        var incomingSerial = NormalizeItemIdentityValue(incoming.SerialNumber);
        if (HasMeaningfulItemIdentityValue(localSerial) && HasMeaningfulItemIdentityValue(incomingSerial))
            return string.Equals(localSerial, incomingSerial, StringComparison.OrdinalIgnoreCase);

        return string.Equals(
            BuildItemDescriptorKey(local),
            BuildItemDescriptorKey(incoming),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreEquivalentItemSnapshots(LocalItem local, ItemDto server)
        => AreEquivalentConflictPayloads(LocalMappings.ToDto(local), server, ItemCanonicalRepairIgnoredPropertyNames);

    private async Task<ItemWarehouseStockRevisionConflictResolution> ResolveItemWarehouseStockRevisionConflictsAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        CancellationToken ct)
    {
        var resolved = new List<ConflictLogDto>();
        var retryRequired = new List<ConflictLogDto>();
        foreach (var conflict in conflicts)
        {
            var outcome =
                await TryResolveItemWarehouseStockRevisionConflictAsync(
                    conflict,
                    ct);
            if (outcome == ItemWarehouseStockRevisionConflictOutcome.Unresolved)
                continue;

            resolved.Add(conflict);
            if (outcome ==
                ItemWarehouseStockRevisionConflictOutcome.RetryRequired)
            {
                retryRequired.Add(conflict);
            }
        }

        return new ItemWarehouseStockRevisionConflictResolution(
            resolved,
            retryRequired);
    }

    private async Task ReplayItemWarehouseStockSnapshotsBeforePullAsync(
        ErpApiClient apiClient,
        SyncPushRequest originalRequest,
        SessionState session,
        string? businessDatabaseNameOverride,
        IReadOnlyCollection<ConflictLogDto> retryRequiredConflicts,
        CancellationToken ct)
    {
        var conflictServerSnapshots =
            new Dictionary<string, ItemWarehouseStockDto>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in retryRequiredConflicts)
        {
            if (!TryGetItemWarehouseStockRevisionConflictSnapshots(
                    conflict,
                    out var clientSnapshot,
                    out var conflictServerSnapshot))
            {
                throw new SyncPullBlockedException(
                    "재고 snapshot 재업로드에 필요한 충돌 정보를 확인할 수 없어 pull을 중단했습니다.");
            }

            var key = BuildItemWarehouseStockKey(
                clientSnapshot.ItemId,
                clientSnapshot.WarehouseCode);
            if (!conflictServerSnapshots.TryAdd(
                    key,
                    conflictServerSnapshot))
            {
                throw new SyncPullBlockedException(
                    "동일 재고 snapshot 충돌이 중복되어 안전한 재업로드를 중단했습니다.");
            }
        }

        var affectedItemIds = conflictServerSnapshots.Values
            .Select(stock => stock.ItemId)
            .ToHashSet();
        var outgoingStocks = originalRequest.ItemWarehouseStocks
            .Where(stock => affectedItemIds.Contains(stock.ItemId))
            .ToList();
        var outgoingByKey = TryBuildUniqueItemWarehouseStockLookup(
            outgoingStocks);
        if (outgoingByKey is null ||
            affectedItemIds.Any(itemId =>
                outgoingStocks.All(stock => stock.ItemId != itemId)))
        {
            throw new SyncPullBlockedException(
                "영향 품목의 전체 재고 snapshot을 구성할 수 없어 pull을 중단했습니다.");
        }

        var currentServerState = await apiClient.PullAsync(
            0,
            businessDatabaseNameOverride,
            ct);
        if (currentServerState is null)
        {
            throw new SyncPullBlockedException(
                "재고 snapshot 재업로드 전 서버 상태 응답이 비어 있어 pull을 중단했습니다.");
        }

        var currentLocalRows = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => affectedItemIds.Contains(stock.ItemId))
            .ToListAsync(ct);
        var affectedItemsById = await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => affectedItemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, ct);
        if (affectedItemIds.Any(itemId =>
                !affectedItemsById.ContainsKey(itemId)))
        {
            throw new SyncPullBlockedException(
                "영향 품목의 로컬 범위 정보를 찾을 수 없어 제한 재업로드와 pull을 중단했습니다.");
        }

        var currentLocalStocks = currentLocalRows
            .Select(LocalMappings.ToDto)
            .ToList();
        var currentLocalByKey = TryBuildUniqueItemWarehouseStockLookup(
            currentLocalStocks);
        var writableCurrentLocalByKey =
            TryBuildUniqueItemWarehouseStockLookup(
                currentLocalRows
                    .Where(stock =>
                        affectedItemsById.TryGetValue(
                            stock.ItemId,
                            out var item) &&
                        _local.CanWriteItemScope(
                            item,
                            session) &&
                        CanWriteItemWarehouseStockForPush(
                            stock,
                            item,
                            session))
                    .Select(LocalMappings.ToDto)
                    .ToList());
        if (currentLocalByKey is null ||
            writableCurrentLocalByKey is null ||
            !outgoingByKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(writableCurrentLocalByKey.Keys))
        {
            throw new SyncPullBlockedException(
                "서버 상태 확인 중 로컬의 쓰기 가능 재고 warehouse 구성이 변경되어 제한 재업로드와 pull을 중단했습니다.");
        }

        foreach (var (key, outgoingStock) in outgoingByKey)
        {
            if (!AreEquivalentConflictPayloads(
                    writableCurrentLocalByKey[key],
                    outgoingStock))
            {
                throw new SyncPullBlockedException(
                    "서버 상태 확인 중 로컬 재고 snapshot이 변경되어 제한 재업로드와 pull을 중단했습니다.");
            }
        }

        var serverStocks = currentServerState.ItemWarehouseStocks
            .Where(stock => affectedItemIds.Contains(stock.ItemId))
            .ToList();
        var serverByKey = TryBuildUniqueItemWarehouseStockLookup(
            serverStocks);
        var writableServerByKey =
            TryBuildUniqueItemWarehouseStockLookup(
                serverStocks
                    .Where(stock =>
                        affectedItemsById.TryGetValue(
                            stock.ItemId,
                            out var item) &&
                        _local.CanWriteItemScope(
                            item,
                            session) &&
                        CanWriteItemWarehouseCodeForPush(
                            stock.WarehouseCode,
                            item,
                            session))
                    .ToList());
        if (serverByKey is null ||
            writableServerByKey is null ||
            !outgoingByKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(writableServerByKey.Keys))
        {
            throw new SyncPullBlockedException(
                "영향 품목의 서버/로컬 쓰기 가능 재고 warehouse 구성이 달라 안전한 재업로드와 pull을 중단했습니다.");
        }

        foreach (var (key, outgoingStock) in outgoingByKey)
        {
            var serverStock = writableServerByKey[key];
            if (conflictServerSnapshots.TryGetValue(
                    key,
                    out var conflictServerSnapshot))
            {
                if (!AreEquivalentConflictPayloads(
                        serverStock,
                        conflictServerSnapshot))
                {
                    throw new SyncPullBlockedException(
                        "충돌 재고 snapshot이 다시 변경되어 재업로드와 pull을 중단했습니다.");
                }
            }
            else if (!AreEquivalentConflictPayloads(
                         serverStock,
                         outgoingStock))
            {
                throw new SyncPullBlockedException(
                    "동일 품목의 다른 warehouse 재고가 변경되어 재업로드와 pull을 중단했습니다.");
            }
        }

        var replayRequest = new SyncPushRequest
        {
            DeviceId = originalRequest.DeviceId,
            ItemWarehouseStocks = outgoingStocks
                .Select(stock =>
                {
                    var serverRevision = writableServerByKey[
                        BuildItemWarehouseStockKey(
                            stock.ItemId,
                            stock.WarehouseCode)].Revision;
                    return new ItemWarehouseStockDto
                    {
                        ItemId = stock.ItemId,
                        WarehouseCode = stock.WarehouseCode,
                        Quantity = stock.Quantity,
                        UpdatedAtUtc = stock.UpdatedAtUtc,
                        Revision = serverRevision,
                        ExpectedRevision = serverRevision
                    };
                })
                .ToList()
        };
        var replayResult = await apiClient.PushAsync(
            replayRequest,
            businessDatabaseNameOverride,
            ct);
        if (replayResult is null ||
            replayResult.ConflictCount > 0 ||
            replayResult.Conflicts.Count > 0 ||
            replayResult.Notices.Count > 0)
        {
            throw new SyncPullBlockedException(
                "제한 재고 snapshot 재업로드가 완전히 승인되지 않아 정상 pull을 중단했습니다.");
        }

        var submittedReplayKeys = replayRequest.ItemWarehouseStocks
            .Select(stock => BuildItemWarehouseStockKey(
                stock.ItemId,
                stock.WarehouseCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceptedReplayKeys =
            TryBuildUniqueAcceptedItemWarehouseStockKeyLookup(
                replayResult.AcceptedItemWarehouseStockKeys);
        if (acceptedReplayKeys is null ||
            !submittedReplayKeys.SetEquals(acceptedReplayKeys))
        {
            throw new SyncPullBlockedException(
                "제한 재고 snapshot 재업로드의 승인 key가 제출 범위와 일치하지 않아 정상 pull을 중단했습니다.");
        }

        var localRowsAfterReplay = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => affectedItemIds.Contains(stock.ItemId))
            .ToListAsync(ct);
        var localStocksAfterReplay = localRowsAfterReplay
            .Select(LocalMappings.ToDto)
            .ToList();
        var localByKeyAfterReplay =
            TryBuildUniqueItemWarehouseStockLookup(
                localStocksAfterReplay);
        var writableLocalByKeyAfterReplay =
            TryBuildUniqueItemWarehouseStockLookup(
                localRowsAfterReplay
                    .Where(stock =>
                        affectedItemsById.TryGetValue(
                            stock.ItemId,
                            out var item) &&
                        _local.CanWriteItemScope(
                            item,
                            session) &&
                        CanWriteItemWarehouseStockForPush(
                            stock,
                            item,
                            session))
                    .Select(LocalMappings.ToDto)
                    .ToList());
        if (localByKeyAfterReplay is null ||
            writableLocalByKeyAfterReplay is null ||
            !outgoingByKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(writableLocalByKeyAfterReplay.Keys))
        {
            throw new SyncPullBlockedException(
                "제한 재고 snapshot 재업로드 중 로컬의 쓰기 가능 warehouse 구성이 변경되어 정상 pull을 중단했습니다.");
        }

        foreach (var (key, outgoingStock) in outgoingByKey)
        {
            if (!AreEquivalentConflictPayloads(
                    writableLocalByKeyAfterReplay[key],
                    outgoingStock))
            {
                throw new SyncPullBlockedException(
                    "제한 재고 snapshot 재업로드 중 로컬 재고가 변경되어 정상 pull을 중단했습니다.");
            }
        }

        PreserveItemWarehouseStockReplayPullGuard(
            affectedItemIds,
            localByKeyAfterReplay);

        AppLogger.Info(
            "SYNC",
            $"Pull 전 재고 snapshot 제한 재업로드 완료: items={affectedItemIds.Count}, rows={replayRequest.ItemWarehouseStocks.Count}");
    }

    private void PreserveItemWarehouseStockReplayPullGuard(
        IReadOnlySet<Guid> affectedItemIds,
        IReadOnlyDictionary<string, ItemWarehouseStockDto>
            expectedStocksByKey)
    {
        HashSet<Guid> preservedItemIds =
            _itemWarehouseStockReplayPullGuard?.AffectedItemIds
                .ToHashSet() ??
            [];
        preservedItemIds.UnionWith(affectedItemIds);
        var preservedStocks =
            _itemWarehouseStockReplayPullGuard?.ExpectedStocksByKey
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase) ??
            new Dictionary<string, ItemWarehouseStockDto>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var (key, expectedStock) in expectedStocksByKey)
        {
            if (preservedStocks.TryGetValue(
                    key,
                    out var preservedStock) &&
                !AreEquivalentConflictPayloads(
                    preservedStock,
                    expectedStock))
            {
                throw new SyncPullBlockedException(
                    "동일 재고 snapshot에 서로 다른 replay guard가 생성되어 정상 pull을 중단했습니다.");
            }

            preservedStocks[key] = expectedStock;
        }

        _itemWarehouseStockReplayPullGuard =
            new ItemWarehouseStockReplayPullGuard(
                preservedItemIds,
                preservedStocks);
    }

    private static Dictionary<string, ItemWarehouseStockDto>?
        TryBuildUniqueItemWarehouseStockLookup(
            IReadOnlyCollection<ItemWarehouseStockDto> stocks)
    {
        var lookup = new Dictionary<string, ItemWarehouseStockDto>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var stock in stocks)
        {
            if (stock.ItemId == Guid.Empty ||
                string.IsNullOrWhiteSpace(stock.WarehouseCode) ||
                !lookup.TryAdd(
                    BuildItemWarehouseStockKey(
                        stock.ItemId,
                        stock.WarehouseCode),
                    stock))
            {
                return null;
            }
        }

        return lookup;
    }

    private static HashSet<string>?
        TryBuildUniqueAcceptedItemWarehouseStockKeyLookup(
            IReadOnlyCollection<SyncAcceptedItemWarehouseStockKeyDto>?
                acceptedKeys)
    {
        if (acceptedKeys is null)
            return null;

        var lookup = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var acceptedKey in acceptedKeys)
        {
            if (acceptedKey is null ||
                acceptedKey.ItemId == Guid.Empty ||
                string.IsNullOrWhiteSpace(
                    acceptedKey.WarehouseCode) ||
                !lookup.Add(BuildItemWarehouseStockKey(
                    acceptedKey.ItemId,
                    acceptedKey.WarehouseCode)))
            {
                return null;
            }
        }

        return lookup;
    }

    private async Task<bool>
        TryHandleInventoryTransferStockAtomicityRollbackAsync(
            SyncPushRequest request,
            SyncPushResult result,
            CancellationToken ct)
    {
        var blockedTransfers =
            SelectValidatedInventoryTransferStockAtomicityRollbackTransfers(
                request,
                result);
        if (blockedTransfers is null)
            return false;

        var mutationIds = blockedTransfers
            .Select(transfer =>
                (transfer.MutationId ?? string.Empty).Trim())
            .Where(mutationId =>
                !string.IsNullOrWhiteSpace(mutationId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mutationIds.Count != blockedTransfers.Count)
            return false;

        var firstConflictReason = result.Conflicts
            .First(conflict =>
                string.Equals(
                    conflict.EntityName,
                    "InventoryTransfer",
                    StringComparison.OrdinalIgnoreCase))
            .Reason;
        var errorMessage =
            $"{InventoryTransferStockAtomicityRollbackOutboxErrorPrefix} " +
            $"{(firstConflictReason ?? string.Empty).Trim()}";
        var markedCount = await _local.MarkSyncOutboxFailedAsync(
            mutationIds,
            errorMessage,
            ct);
        if (markedCount != mutationIds.Count)
            return false;

        var detail =
            $"Inventory transfer stock rollback isolated: blocked transfers={blockedTransfers.Count}. " +
            "No rolled-back mutation or stock snapshot was acknowledged; unrelated dirty data will retry separately.";
        AppLogger.Warn("SYNC", detail);
        await AppendConflictSummaryAsync(detail);
        await TryRecordDiagnosticAsync(
            "push-conflict",
            detail,
            severity: "Warning",
            recoveryAttempted: true,
            recoverySucceeded: true);
        return true;
    }

    private static List<InventoryTransferDto>?
        SelectValidatedInventoryTransferStockAtomicityRollbackTransfers(
            SyncPushRequest request,
            SyncPushResult result)
    {
        var notice = result.Notices.Count == 1
            ? result.Notices[0]
            : null;
        if (notice is null ||
            !string.Equals(
                notice.EntityName,
                "InventoryTransfer",
                StringComparison.Ordinal) ||
            !string.Equals(
                notice.EntityId,
                string.Empty,
                StringComparison.Ordinal) ||
            !string.Equals(
                notice.Code,
                InventoryTransferStockAtomicityRollbackNoticeCode,
                StringComparison.Ordinal) ||
            result.AcceptedCount != 0 ||
            result.DuplicateMutationCount != 0 ||
            result.AcceptedRevisions.Count != 0 ||
            result.AcceptedItemWarehouseStockKeys.Count != 0 ||
            result.AssignedInvoiceNumbers.Count != 0 ||
            result.AssignedTaxInvoiceNumbers.Count != 0 ||
            result.ConflictCount <= 0 ||
            result.ConflictCount != result.Conflicts.Count)
        {
            return null;
        }

        var conflictTransferIds = new HashSet<Guid>();
        foreach (var conflict in result.Conflicts)
        {
            if (!string.Equals(
                    conflict.EntityName,
                    "InventoryTransfer",
                    StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(conflict.EntityId, out var transferId) ||
                transferId == Guid.Empty)
            {
                return null;
            }

            conflictTransferIds.Add(transferId);
        }

        if (conflictTransferIds.Count == 0)
            return null;

        var requestTransfersById = request.InventoryTransfers
            .Where(transfer => transfer.Id != Guid.Empty)
            .GroupBy(transfer => transfer.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (conflictTransferIds.Any(transferId =>
                !requestTransfersById.TryGetValue(
                    transferId,
                    out var rows) ||
                rows.Count != 1))
        {
            return null;
        }

        return conflictTransferIds
            .Select(transferId =>
                requestTransfersById[transferId][0])
            .ToList();
    }

    private static string?
        BuildItemWarehouseStockAcknowledgementIssue(
            IReadOnlyCollection<ItemWarehouseStockDto> submittedStocks,
            SyncPushResult result)
    {
        var acceptedKeys =
            TryBuildUniqueAcceptedItemWarehouseStockKeyLookup(
                result.AcceptedItemWarehouseStockKeys);
        if (submittedStocks.Count == 0)
        {
            var hasUnexpectedWarehouseStockConflict =
                (result.Conflicts ?? []).Any(conflict =>
                    string.Equals(
                        conflict.EntityName,
                        "ItemWarehouseStock",
                        StringComparison.OrdinalIgnoreCase));
            return result.AcceptedItemWarehouseStockKeys is null ||
                   result.AcceptedItemWarehouseStockKeys.Count > 0 ||
                   hasUnexpectedWarehouseStockConflict
                ? "서버 재고 승인 key 응답이 제출하지 않은 재고 범위와 일치하지 않아 pull을 중단했습니다."
                : null;
        }

        var submittedByKey =
            TryBuildUniqueItemWarehouseStockLookup(
                submittedStocks);
        if (submittedByKey is null)
        {
            return "제출 재고 snapshot에 비어 있거나 중복된 논리 key가 있어 서버 응답을 안전하게 확인할 수 없으므로 pull을 중단했습니다.";
        }

        if (acceptedKeys is null)
        {
            return "서버 재고 승인 key 응답이 없거나 유효하지 않아 제출 재고를 확인할 수 없으므로 pull을 중단했습니다.";
        }

        var submittedKeys = submittedByKey.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!acceptedKeys.IsSubsetOf(submittedKeys))
        {
            return "서버 재고 승인 key에 제출하지 않은 항목이 포함되어 응답 범위를 신뢰할 수 없으므로 pull을 중단했습니다.";
        }

        var unacknowledgedKeys = submittedKeys
            .Except(
                acceptedKeys,
                StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unacknowledgedKeys.Count == 0)
            return null;

        if (result.ConflictCount <= 0)
        {
            return $"서버에서 승인하지 않은 재고 snapshot {unacknowledgedKeys.Count:N0}건이 충돌 정보 없이 남아 pull을 중단했습니다.";
        }

        var conflictKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in result.Conflicts ?? [])
        {
            if (!string.Equals(
                    conflict.EntityName,
                    "ItemWarehouseStock",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseItemWarehouseStockConflictId(
                    conflict.EntityId,
                    out var itemId,
                    out var warehouseCode))
            {
                return "서버 재고 충돌 식별자가 유효하지 않아 미승인 재고 범위를 확인할 수 없으므로 pull을 중단했습니다.";
            }

            conflictKeys.Add(
                BuildItemWarehouseStockKey(
                    itemId,
                    warehouseCode));
        }

        if (conflictKeys.Except(
                submittedKeys,
                StringComparer.OrdinalIgnoreCase).Any() ||
            conflictKeys.Overlaps(acceptedKeys))
        {
            return "서버 재고 충돌 key가 제출·승인 범위와 모순되어 응답을 안전하게 적용할 수 없으므로 pull을 중단했습니다.";
        }

        unacknowledgedKeys.ExceptWith(conflictKeys);
        return unacknowledgedKeys.Count == 0
            ? null
            : $"서버에서 승인하지 않은 재고 snapshot {unacknowledgedKeys.Count:N0}건에 대응하는 충돌 정보가 없어 pull을 중단했습니다.";
    }

    private static IReadOnlyCollection<ItemWarehouseStockDto>
        SelectItemWarehouseStocksForAcknowledgementRetry(
            IReadOnlyCollection<ItemWarehouseStockDto> submittedStocks,
            SyncPushResult result)
    {
        var submittedByKey =
            TryBuildUniqueItemWarehouseStockLookup(
                submittedStocks);
        var acceptedKeys =
            TryBuildUniqueAcceptedItemWarehouseStockKeyLookup(
                result.AcceptedItemWarehouseStockKeys);
        if (submittedByKey is null ||
            acceptedKeys is null ||
            !acceptedKeys.IsSubsetOf(submittedByKey.Keys))
        {
            return submittedStocks.ToList();
        }

        var unacknowledgedKeys = submittedByKey.Keys
            .Except(
                acceptedKeys,
                StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unacknowledgedKeys.Count == 0)
            return submittedStocks.ToList();

        return submittedStocks
            .Where(stock =>
                unacknowledgedKeys.Contains(
                    BuildItemWarehouseStockKey(
                        stock.ItemId,
                        stock.WarehouseCode)))
            .ToList();
    }

    private async Task
        ApplyPartialWarehouseStockAcknowledgementAtomicallyAsync(
            Func<Task> applyAcceptedSideEffectsAsync,
            IReadOnlyCollection<ItemWarehouseStockDto> submittedStocks,
            CancellationToken ct)
    {
        async Task ApplyAsync()
        {
            await applyAcceptedSideEffectsAsync();
            if (AfterPartialWarehouseStockAcceptedSideEffectsAsyncForTesting
                is not null)
            {
                await
                    AfterPartialWarehouseStockAcceptedSideEffectsAsyncForTesting(
                        ct);
            }
            await MarkItemWarehouseStockSnapshotsPendingAsync(
                submittedStocks,
                ct);
        }

        if (_db.Database.CurrentTransaction is not null)
        {
            await ApplyAsync();
            return;
        }

        await using var transaction =
            await _db.BeginRuntimeMutationTransactionAsync(ct);
        try
        {
            await ApplyAsync();
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                AppLogger.Warn(
                    "SYNC",
                    "Partial warehouse-stock acknowledgement rollback " +
                    $"failed: {rollbackException.Message}");
            }

            _db.ChangeTracker.Clear();
            if (ex is SyncPullBlockedException ||
                ex is DesktopClientUpgradeRequiredException ||
                ex is OperationCanceledException &&
                ct.IsCancellationRequested)
            {
                throw;
            }

            throw new SyncPullBlockedException(
                "재고 승인 응답과 재시도 상태를 원자적으로 저장하지 못해 " +
                "정상 pull을 중단했습니다.",
                ex);
        }
    }

    private async Task MarkItemWarehouseStockSnapshotsPendingAsync(
        IReadOnlyCollection<ItemWarehouseStockDto> submittedStocks,
        CancellationToken ct)
    {
        var itemIds = submittedStocks
            .Select(stock => stock.ItemId)
            .Where(itemId => itemId != Guid.Empty)
            .Distinct()
            .ToList();
        if (itemIds.Count == 0)
            return;

        var items = await _db.Items
            .IgnoreQueryFilters()
            .Where(item =>
                itemIds.Contains(item.Id) &&
                !item.IsDeleted)
            .ToListAsync(ct);
        var changed = false;
        foreach (var item in items)
        {
            if (item.IsDirty)
                continue;

            item.IsDirty = true;
            changed = true;
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private async Task
        EnsureItemWarehouseStockReplayPullGuardUnchangedAsync(
            CancellationToken ct)
    {
        var guard = _itemWarehouseStockReplayPullGuard;
        if (guard is null)
            return;

        var persistedStocks = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock =>
                guard.AffectedItemIds.Contains(stock.ItemId))
            .ToListAsync(ct);
        var persistedByKey =
            TryBuildUniqueItemWarehouseStockLookup(
                persistedStocks
                    .Select(LocalMappings.ToDto)
                    .ToList());
        if (persistedByKey is null ||
            !guard.ExpectedStocksByKey.Keys
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(persistedByKey.Keys))
        {
            throw new SyncPullBlockedException(
                "정상 pull 응답 대기 중 로컬 재고 warehouse 구성이 변경되어 반영을 중단했습니다.");
        }

        foreach (var (key, expectedStock) in
                 guard.ExpectedStocksByKey)
        {
            if (!AreEquivalentConflictPayloads(
                    persistedByKey[key],
                    expectedStock))
            {
                throw new SyncPullBlockedException(
                    "정상 pull 응답 대기 중 로컬 재고 snapshot이 변경되어 반영을 중단했습니다.");
            }
        }
    }

    private async Task<ItemWarehouseStockRevisionConflictOutcome> TryResolveItemWarehouseStockRevisionConflictAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
    {
        if (!TryGetItemWarehouseStockRevisionConflictSnapshots(
                conflict,
                out var clientSnapshot,
                out var serverSnapshot))
        {
            return ItemWarehouseStockRevisionConflictOutcome.Unresolved;
        }

        if (serverSnapshot.IsDeleted)
        {
            return await TryResolveItemWarehouseStockTombstoneAsync(
                clientSnapshot,
                ct);
        }

        var logicalKey = BuildItemWarehouseStockKey(
            clientSnapshot.ItemId,
            clientSnapshot.WarehouseCode);
        var itemStocks = await _db.ItemWarehouseStocks
            .Where(current =>
                current.ItemId == clientSnapshot.ItemId)
            .ToListAsync(ct);
        var matchingStocks = itemStocks
            .Where(current =>
                string.Equals(
                    BuildItemWarehouseStockKey(
                        current.ItemId,
                        current.WarehouseCode),
                    logicalKey,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingStocks.Count == 0)
            return ItemWarehouseStockRevisionConflictOutcome.Unresolved;

        if (matchingStocks.Count != 1)
            return ItemWarehouseStockRevisionConflictOutcome.Unresolved;

        var stock = matchingStocks[0];

        var localSnapshot = LocalMappings.ToDto(stock);
        localSnapshot.WarehouseCode =
            NormalizeWarehouseCode(localSnapshot.WarehouseCode);
        var localMatchesClient = AreEquivalentConflictPayloads(localSnapshot, clientSnapshot);
        var localMatchesServer = AreEquivalentConflictPayloads(localSnapshot, serverSnapshot);
        if (!localMatchesClient && !localMatchesServer)
            return ItemWarehouseStockRevisionConflictOutcome.Unresolved;

        var localUpdatedAtUtc = NormalizeMutationUtc(localSnapshot.UpdatedAtUtc);
        var serverUpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        if (localMatchesClient && localUpdatedAtUtc >= serverUpdatedAtUtc)
        {
            stock.Revision = serverSnapshot.Revision;
            await _db.SaveChangesAsync(ct);
            return ItemWarehouseStockRevisionConflictOutcome.RetryRequired;
        }

        stock.Quantity = serverSnapshot.Quantity;
        stock.UpdatedAtUtc = NormalizeMutationUtc(serverSnapshot.UpdatedAtUtc);
        stock.Revision = serverSnapshot.Revision;
        await _db.SaveChangesAsync(ct);
        return ItemWarehouseStockRevisionConflictOutcome.ResolvedFromServer;
    }

    private async Task<ItemWarehouseStockRevisionConflictOutcome>
        TryResolveItemWarehouseStockTombstoneAsync(
            ItemWarehouseStockDto clientSnapshot,
            CancellationToken ct)
    {
        var hasPendingItemMutation = _db.ChangeTracker
            .Entries<LocalItem>()
            .Any(entry =>
                entry.Entity.Id == clientSnapshot.ItemId &&
                entry.State is EntityState.Added or
                    EntityState.Modified or
                    EntityState.Deleted);
        var hasPendingStockMutation = _db.ChangeTracker
            .Entries<LocalItemWarehouseStock>()
            .Any(entry =>
                entry.Entity.ItemId == clientSnapshot.ItemId &&
                entry.State is EntityState.Added or
                    EntityState.Modified or
                    EntityState.Deleted);
        if (hasPendingItemMutation ||
            hasPendingStockMutation)
            return ItemWarehouseStockRevisionConflictOutcome.Unresolved;

        var logicalKey = BuildItemWarehouseStockKey(
            clientSnapshot.ItemId,
            clientSnapshot.WarehouseCode);
        var itemStocks = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Where(current =>
                current.ItemId == clientSnapshot.ItemId)
            .ToListAsync(ct);
        var matchingStocks = itemStocks
            .Where(current =>
                string.Equals(
                    BuildItemWarehouseStockKey(
                        current.ItemId,
                        current.WarehouseCode),
                    logicalKey,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingStocks.Count == 0)
        {
            DetachUnchangedItemWarehouseStocks(
                clientSnapshot.ItemId);
            DetachUnchangedItem(clientSnapshot.ItemId);
            return ItemWarehouseStockRevisionConflictOutcome
                .ResolvedFromServer;
        }

        if (matchingStocks.Count != 1)
            return ItemWarehouseStockRevisionConflictOutcome.Unresolved;

        var stock = matchingStocks[0];
        var localSnapshot = LocalMappings.ToDto(stock);
        localSnapshot.WarehouseCode =
            NormalizeWarehouseCode(localSnapshot.WarehouseCode);
        if (!AreEquivalentConflictPayloads(
                localSnapshot,
                clientSnapshot))
        {
            DetachUnchangedItemWarehouseStocks(
                clientSnapshot.ItemId);
            DetachUnchangedItem(clientSnapshot.ItemId);
            return ItemWarehouseStockRevisionConflictOutcome.Unresolved;
        }

        if (BeforeItemWarehouseStockTombstoneConditionalDeleteAsyncForTesting
            is not null)
        {
            await BeforeItemWarehouseStockTombstoneConditionalDeleteAsyncForTesting(
                ct);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await _db.BeginRuntimeMutationTransactionAsync(ct);
            var deletedRows = await _db.ItemWarehouseStocks
                .Where(current =>
                    current.ItemId == stock.ItemId &&
                    current.WarehouseCode == stock.WarehouseCode &&
                    current.Quantity == stock.Quantity &&
                    current.Revision == stock.Revision &&
                    current.UpdatedAtUtc == stock.UpdatedAtUtc)
                .ExecuteDeleteAsync(ct);
            if (deletedRows != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                DetachUnchangedItemWarehouseStocks(
                    clientSnapshot.ItemId);
                DetachUnchangedItem(clientSnapshot.ItemId);
                return ItemWarehouseStockRevisionConflictOutcome
                    .Unresolved;
            }

            var remainingQuantities = await _db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(current =>
                    current.ItemId == clientSnapshot.ItemId)
                .Select(current => current.Quantity)
                .ToListAsync(ct);
            var remainingQuantity = remainingQuantities.Sum();
            var updatedItems = await _db.Items
                .IgnoreQueryFilters()
                .Where(current =>
                    current.Id == clientSnapshot.ItemId &&
                    !current.IsDeleted)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        current => current.CurrentStock,
                        remainingQuantity),
                    ct);
            if (updatedItems != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                DetachUnchangedItemWarehouseStocks(
                    clientSnapshot.ItemId);
                DetachUnchangedItem(clientSnapshot.ItemId);
                return ItemWarehouseStockRevisionConflictOutcome
                    .Unresolved;
            }

            await transaction.CommitAsync(ct);
            DetachUnchangedItemWarehouseStocks(
                clientSnapshot.ItemId);
            DetachUnchangedItem(clientSnapshot.ItemId);
            return ItemWarehouseStockRevisionConflictOutcome
                .ResolvedFromServer;
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure and fail closed.
                }
            }

            throw new SyncPullBlockedException(
                "로컬 재고 삭제 상태를 원자적으로 확인하지 못해 pull을 중단했습니다.",
                ex);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private void DetachUnchangedItemWarehouseStocks(Guid itemId)
    {
        foreach (var entry in _db.ChangeTracker
                     .Entries<LocalItemWarehouseStock>()
                     .Where(entry =>
                         entry.Entity.ItemId == itemId &&
                         entry.State == EntityState.Unchanged)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private void DetachUnchangedItem(Guid itemId)
    {
        foreach (var entry in _db.ChangeTracker
                     .Entries<LocalItem>()
                     .Where(entry =>
                         entry.Entity.Id == itemId &&
                         entry.State == EntityState.Unchanged)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool TryGetItemWarehouseStockRevisionConflictSnapshots(
        ConflictLogDto conflict,
        out ItemWarehouseStockDto clientSnapshot,
        out ItemWarehouseStockDto serverSnapshot)
    {
        clientSnapshot = null!;
        serverSnapshot = null!;
        if (!string.Equals(
                conflict.EntityName,
                "ItemWarehouseStock",
                StringComparison.OrdinalIgnoreCase) ||
            !(conflict.Reason ?? string.Empty)
                .Trim()
                .StartsWith(
                    "Expected revision mismatch.",
                    StringComparison.OrdinalIgnoreCase) ||
            !TryDeserializeConflictItemWarehouseStockDto(
                conflict.ClientJson,
                out var parsedClientSnapshot) ||
            parsedClientSnapshot is null ||
            !TryDeserializeConflictItemWarehouseStockDto(
                conflict.ServerJson,
                out var parsedServerSnapshot) ||
            parsedServerSnapshot is null ||
            !TryParseItemWarehouseStockConflictId(
                conflict.EntityId,
                out var entityItemId,
                out var entityWarehouseCode))
        {
            return false;
        }

        parsedClientSnapshot.WarehouseCode =
            NormalizeWarehouseCode(
                parsedClientSnapshot.WarehouseCode);
        parsedServerSnapshot.WarehouseCode =
            NormalizeWarehouseCode(
                parsedServerSnapshot.WarehouseCode);
        var isServerTombstone =
            parsedServerSnapshot.IsDeleted &&
            parsedServerSnapshot.Revision == 0 &&
            parsedServerSnapshot.ExpectedRevision > 0 &&
            parsedServerSnapshot.Quantity == 0m &&
            (parsedClientSnapshot.ExpectedRevision > 0
                ? parsedClientSnapshot.ExpectedRevision
                : parsedClientSnapshot.Revision) ==
            parsedServerSnapshot.ExpectedRevision &&
            (conflict.Reason ?? string.Empty).Contains(
                "Server warehouse stock row no longer exists.",
                StringComparison.OrdinalIgnoreCase);
        if (!IsSameItemWarehouseStockIdentity(
                parsedClientSnapshot,
                parsedServerSnapshot) ||
            (!isServerTombstone &&
             (parsedServerSnapshot.IsDeleted ||
              parsedServerSnapshot.Revision <= 0)) ||
            entityItemId != parsedClientSnapshot.ItemId ||
            !string.Equals(
                NormalizeWarehouseCode(entityWarehouseCode),
                parsedClientSnapshot.WarehouseCode,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        clientSnapshot = parsedClientSnapshot;
        serverSnapshot = parsedServerSnapshot;
        return true;
    }

    private static bool IsSameItemWarehouseStockIdentity(ItemWarehouseStockDto left, ItemWarehouseStockDto right)
        => left.ItemId != Guid.Empty &&
           left.ItemId == right.ItemId &&
           string.Equals(NormalizeWarehouseCode(left.WarehouseCode), NormalizeWarehouseCode(right.WarehouseCode), StringComparison.OrdinalIgnoreCase);

    private static bool TryParseItemWarehouseStockConflictId(
        string? value,
        out Guid itemId,
        out string warehouseCode)
    {
        itemId = Guid.Empty;
        warehouseCode = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out itemId) || itemId == Guid.Empty)
            return false;

        warehouseCode = NormalizeWarehouseCode(parts[1]);
        return !string.IsNullOrWhiteSpace(warehouseCode);
    }

    private static string NormalizeWarehouseCode(string? warehouseCode)
        => NormalizeItemWarehouseStockLogicalWarehouseCode(
            warehouseCode);

    private static bool IsEquivalentRevisionConflict(ConflictLogDto conflict)
    {
        var reason = (conflict.Reason ?? string.Empty).Trim();
        if (!reason.StartsWith("Expected revision mismatch.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(conflict.ClientJson) || string.IsNullOrWhiteSpace(conflict.ServerJson))
            return false;

        try
        {
            using var clientDocument = JsonDocument.Parse(conflict.ClientJson);
            using var serverDocument = JsonDocument.Parse(conflict.ServerJson);

            var normalizedClient = NormalizeConflictJson(clientDocument.RootElement, EquivalentConflictIgnoredPropertyNames);
            var normalizedServer = NormalizeConflictJson(serverDocument.RootElement, EquivalentConflictIgnoredPropertyNames);
            return JsonNode.DeepEquals(normalizedClient, normalizedServer);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> BuildCanonicalRentalBillingTemplateJsonAsync(
        Guid profileId,
        LocalRentalBillingProfile profile,
        CancellationToken ct)
    {
        var linkedAssets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset =>
                !asset.IsDeleted &&
                asset.BillingProfileId.HasValue &&
                asset.BillingProfileId.Value == profileId)
            .ToListAsync(ct);
        if (linkedAssets.Count == 0)
            return null;

        var templateItems = _rental.GetBillingTemplateItems(profile, linkedAssets);
        if (templateItems.Count != 1)
            return null;

        var linkedAssetIds = linkedAssets
            .Select(asset => asset.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (linkedAssetIds.Count == 0)
            return null;

        var hasExplicitIncludedAssetIds = templateItems.Any(item =>
            (item.IncludedAssetIds ?? new List<Guid>()).Any(id => id != Guid.Empty));
        if (!hasExplicitIncludedAssetIds)
            templateItems[0].IncludedAssetIds = linkedAssetIds;

        var canonicalTemplateJson = _rental.SerializeBillingTemplateItems(templateItems);
        return string.IsNullOrWhiteSpace(canonicalTemplateJson)
            ? null
            : canonicalTemplateJson;
    }

    private async Task RequeuePreparedMutationAsync<TDto>(
        string entityName,
        Guid entityId,
        string? previousMutationId,
        TDto entity,
        string deviceId,
        SessionState session,
        CancellationToken ct)
        where TDto : SyncEntityDto
    {
        entity.ExpectedRevision = Math.Max(0, entity.Revision);
        entity.MutationCreatedAtUtc = NormalizeMutationUtc(entity.UpdatedAtUtc);
        entity.MutationId = BuildMutationId(deviceId, entityName, entity);

        var rows = await _db.SyncOutboxEntries
            .Where(entry =>
                entry.Status != "Acknowledged" &&
                (entry.EntityName == entityName && entry.EntityId == entityId ||
                 (!string.IsNullOrWhiteSpace(previousMutationId) && entry.MutationId == previousMutationId)))
            .OrderByDescending(entry => entry.PreparedAtUtc)
            .ToListAsync(ct);

        var primary = rows.FirstOrDefault();
        if (primary is null)
        {
            primary = new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                EntityName = entityName,
                EntityId = entityId
            };
            _db.SyncOutboxEntries.Add(primary);
        }

        foreach (var duplicate in rows.Skip(1))
            _db.SyncOutboxEntries.Remove(duplicate);

        var duplicateMutationRows = await _db.SyncOutboxEntries
            .Where(entry =>
                entry.Id != primary.Id &&
                entry.MutationId == entity.MutationId)
            .ToListAsync(ct);
        foreach (var duplicate in duplicateMutationRows)
            _db.SyncOutboxEntries.Remove(duplicate);

        var scope = ResolvePreparedMutationScope(entity, session, new PreparedMutationScopeLookup());
        primary.MutationId = entity.MutationId;
        primary.ExpectedRevision = entity.ExpectedRevision;
        primary.TenantCode = scope.TenantCode;
        primary.OfficeCode = scope.OfficeCode;
        primary.ResponsibleOfficeCode = scope.ResponsibleOfficeCode;
        primary.Status = "Prepared";
        primary.ErrorMessage = string.Empty;
        primary.PreparedAtUtc = DateTime.UtcNow;
        primary.SentAtUtc = null;
        primary.AcknowledgedAtUtc = null;
        primary.AcceptedRevision = 0;
        primary.AcceptedUpdatedAtUtc = null;
    }

    private static bool TryDeserializeConflictDto<TDto>(string? json, out TDto? dto)
        where TDto : class
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<TDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictItemWarehouseStockDto(
        string? json,
        out ItemWarehouseStockDto? dto)
        => TryDeserializeConflictDto(json, out dto);

    private static bool TryDeserializeConflictRentalBillingProfileDto(
        string? json,
        out RentalBillingProfileDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<RentalBillingProfileDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeConflictRentalAssetDto(
        string? json,
        out RentalAssetDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<RentalAssetDto>(json);
            return dto is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool AreEquivalentBillingTemplateJson(string? left, string? right)
    {
        var normalizedLeft = NormalizeConflictJsonFragment(left);
        var normalizedRight = NormalizeConflictJsonFragment(right);
        if (normalizedLeft is not null || normalizedRight is not null)
            return JsonNode.DeepEquals(normalizedLeft, normalizedRight);

        return string.Equals(
            (left ?? string.Empty).Trim(),
            (right ?? string.Empty).Trim(),
            StringComparison.Ordinal);
    }

    private static JsonNode? NormalizeConflictJsonFragment(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return NormalizeConflictJson(document.RootElement, EquivalentConflictIgnoredPropertyNames);
        }
        catch
        {
            return null;
        }
    }

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(
            ' ',
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool AreEquivalentConflictPayloads<TLeft, TRight>(
        TLeft left,
        TRight right,
        ISet<string>? ignoredProperties = null)
    {
        var normalizedLeft = NormalizeConflictJson(
            System.Text.Json.JsonSerializer.SerializeToElement(left),
            ignoredProperties ?? EquivalentConflictIgnoredPropertyNames);
        var normalizedRight = NormalizeConflictJson(
            System.Text.Json.JsonSerializer.SerializeToElement(right),
            ignoredProperties ?? EquivalentConflictIgnoredPropertyNames);

        return JsonNode.DeepEquals(normalizedLeft, normalizedRight);
    }

    private static JsonNode? NormalizeConflictJson(JsonElement element, ISet<string>? ignoredProperties = null)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => NormalizeConflictObject(element, ignoredProperties),
            JsonValueKind.Array => NormalizeConflictArray(element, ignoredProperties),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.Number => JsonValue.Create(NormalizeConflictJsonNumber(element)),
            _ => JsonNode.Parse(element.GetRawText())
        };
    }

    private static JsonObject NormalizeConflictObject(JsonElement element, ISet<string>? ignoredProperties = null)
    {
        var normalized = new JsonObject();
        var ignored = ignoredProperties ?? EquivalentConflictIgnoredPropertyNames;
        foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (ignored.Contains(property.Name))
                continue;

            normalized[property.Name] = NormalizeConflictJson(property.Value, ignored);
        }

        return normalized;
    }

    private static JsonArray NormalizeConflictArray(JsonElement element, ISet<string>? ignoredProperties = null)
    {
        var normalized = new JsonArray();
        foreach (var item in element.EnumerateArray())
            normalized.Add(NormalizeConflictJson(item, ignoredProperties));

        return normalized;
    }

    private static string NormalizeConflictJsonNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);

        if (element.TryGetDecimal(out var number))
            return number.ToString("G29", CultureInfo.InvariantCulture);

        return element.GetRawText();
    }

    private async Task<List<ConflictLogDto>> GetDeferredSyncConflictsAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        CancellationToken ct)
    {
        var deferred = new List<ConflictLogDto>();

        foreach (var conflict in conflicts)
        {
            if (IsDeferredScopeConflict(conflict))
            {
                deferred.Add(conflict);
                continue;
            }

            if (await IsDeferredMissingRentalBillingProfileConflictAsync(conflict, ct))
            {
                deferred.Add(conflict);
                continue;
            }

            if (await IsDeferredMissingCustomerConflictAsync(conflict, ct))
            {
                deferred.Add(conflict);
                continue;
            }

            if (await IsDeferredMissingInvoiceConflictAsync(conflict, ct))
            {
                deferred.Add(conflict);
                continue;
            }

            if (await IsDeferredMissingTransactionConflictAsync(conflict, ct))
                deferred.Add(conflict);
        }

        return deferred;
    }

    private async Task<bool> IsDeferredMissingRentalBillingProfileConflictAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
    {
        var reason = conflict.Reason ?? string.Empty;
        if (!reason.StartsWith("Referenced rental billing profile was not found:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var entityId))
            return false;

        return conflict.EntityName switch
        {
            "RentalAsset" => await _db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id == entityId &&
                                asset.BillingProfileId.HasValue &&
                                asset.BillingProfileId.Value != Guid.Empty)
                .Join(
                    _db.RentalBillingProfiles.IgnoreQueryFilters().Where(profile => !profile.IsDeleted),
                    asset => asset.BillingProfileId!.Value,
                    profile => profile.Id,
                    (asset, profile) => profile.Id)
                .AnyAsync(ct),
            "RentalBillingLog" => await _db.RentalBillingLogs.IgnoreQueryFilters()
                .Where(log => log.Id == entityId)
                .Join(
                    _db.RentalBillingProfiles.IgnoreQueryFilters().Where(profile => !profile.IsDeleted),
                    log => log.BillingProfileId,
                    profile => profile.Id,
                    (log, profile) => profile.Id)
                .AnyAsync(ct),
            _ => false
        };
    }

    private async Task<bool> IsDeferredMissingCustomerConflictAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
    {
        var reason = conflict.Reason ?? string.Empty;
        if (!reason.StartsWith("Referenced customer was not found:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var entityId))
            return false;

        return conflict.EntityName switch
        {
            "Invoice" => await _db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.Id == entityId && invoice.CustomerId != Guid.Empty)
                .Join(
                    _db.Customers.IgnoreQueryFilters().Where(customer => !customer.IsDeleted),
                    invoice => invoice.CustomerId,
                    customer => customer.Id,
                    (invoice, customer) => customer.Id)
                .AnyAsync(ct),
            "TransactionRecord" => await _db.Transactions.IgnoreQueryFilters()
                .Where(transaction => transaction.Id == entityId && transaction.CustomerId != Guid.Empty)
                .Join(
                    _db.Customers.IgnoreQueryFilters().Where(customer => !customer.IsDeleted),
                    transaction => transaction.CustomerId,
                    customer => customer.Id,
                    (transaction, customer) => customer.Id)
                .AnyAsync(ct),
            "CustomerContract" => await _db.CustomerContracts.IgnoreQueryFilters()
                .Where(contract => contract.Id == entityId && contract.CustomerId != Guid.Empty)
                .Join(
                    _db.Customers.IgnoreQueryFilters().Where(customer => !customer.IsDeleted),
                    contract => contract.CustomerId,
                    customer => customer.Id,
                    (contract, customer) => customer.Id)
                .AnyAsync(ct),
            _ => false
        };
    }

    private async Task<bool> IsDeferredMissingInvoiceConflictAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
    {
        var reason = conflict.Reason ?? string.Empty;
        if (!reason.StartsWith("Referenced invoice was not found:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var entityId))
            return false;

        return conflict.EntityName switch
        {
            "Payment" => await _db.Payments.IgnoreQueryFilters()
                .Where(payment => payment.Id == entityId && payment.InvoiceId != Guid.Empty)
                .Join(
                    _db.Invoices.IgnoreQueryFilters().Where(invoice => !invoice.IsDeleted),
                    payment => payment.InvoiceId,
                    invoice => invoice.Id,
                    (payment, invoice) => invoice.Id)
                .AnyAsync(ct),
            "TransactionRecord" => await _db.Transactions.IgnoreQueryFilters()
                .Where(transaction => transaction.Id == entityId &&
                                      transaction.LinkedInvoiceId.HasValue &&
                                      transaction.LinkedInvoiceId.Value != Guid.Empty)
                .Join(
                    _db.Invoices.IgnoreQueryFilters().Where(invoice => !invoice.IsDeleted),
                    transaction => transaction.LinkedInvoiceId!.Value,
                    invoice => invoice.Id,
                    (transaction, invoice) => invoice.Id)
                .AnyAsync(ct),
            _ => false
        };
    }

    private async Task<bool> IsDeferredMissingTransactionConflictAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
    {
        var reason = conflict.Reason ?? string.Empty;
        if (!reason.StartsWith("Referenced transaction was not found:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Guid.TryParse(conflict.EntityId, out var entityId))
            return false;

        return conflict.EntityName switch
        {
            "TransactionAttachment" => await _db.TransactionAttachments.IgnoreQueryFilters()
                .Where(attachment => attachment.Id == entityId && attachment.TransactionId != Guid.Empty)
                .Join(
                    _db.Transactions.IgnoreQueryFilters().Where(transaction => !transaction.IsDeleted),
                    attachment => attachment.TransactionId,
                    transaction => transaction.Id,
                    (attachment, transaction) => transaction.Id)
                .AnyAsync(ct),
            _ => false
        };
    }

    private async Task PrepareDeferredSyncConflictsAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        CancellationToken ct)
    {
        if (conflicts.Count == 0)
            return;

        var referencedProfileIds = new HashSet<Guid>();
        var referencedCustomerIds = new HashSet<Guid>();
        var referencedInvoiceIds = new HashSet<Guid>();
        var referencedTransactionIds = new HashSet<Guid>();

        foreach (var conflict in conflicts)
        {
            var reason = conflict.Reason ?? string.Empty;
            if (!Guid.TryParse(conflict.EntityId, out var entityId))
                continue;

            if (reason.StartsWith("Referenced rental billing profile was not found:", StringComparison.OrdinalIgnoreCase))
            {
                Guid? billingProfileId = conflict.EntityName switch
                {
                    "RentalAsset" => await _db.RentalAssets.IgnoreQueryFilters()
                        .Where(asset => asset.Id == entityId)
                        .Select(asset => asset.BillingProfileId)
                        .FirstOrDefaultAsync(ct),
                    "RentalBillingLog" => await _db.RentalBillingLogs.IgnoreQueryFilters()
                        .Where(log => log.Id == entityId)
                        .Select(log => (Guid?)log.BillingProfileId)
                        .FirstOrDefaultAsync(ct),
                    _ => null
                };

                if (billingProfileId.HasValue && billingProfileId.Value != Guid.Empty)
                    referencedProfileIds.Add(billingProfileId.Value);

                continue;
            }

            if (reason.StartsWith("Referenced customer was not found:", StringComparison.OrdinalIgnoreCase))
            {
                Guid? customerId = conflict.EntityName switch
                {
                    "Invoice" => await _db.Invoices.IgnoreQueryFilters()
                        .Where(invoice => invoice.Id == entityId)
                        .Select(invoice => (Guid?)invoice.CustomerId)
                        .FirstOrDefaultAsync(ct),
                    "TransactionRecord" => await _db.Transactions.IgnoreQueryFilters()
                        .Where(transaction => transaction.Id == entityId)
                        .Select(transaction => (Guid?)transaction.CustomerId)
                        .FirstOrDefaultAsync(ct),
                    "CustomerContract" => await _db.CustomerContracts.IgnoreQueryFilters()
                        .Where(contract => contract.Id == entityId)
                        .Select(contract => (Guid?)contract.CustomerId)
                        .FirstOrDefaultAsync(ct),
                    _ => null
                };

                if (customerId.HasValue && customerId.Value != Guid.Empty)
                    referencedCustomerIds.Add(customerId.Value);

                continue;
            }

            if (reason.StartsWith("Referenced invoice was not found:", StringComparison.OrdinalIgnoreCase))
            {
                Guid? invoiceId = conflict.EntityName switch
                {
                    "Payment" => await _db.Payments.IgnoreQueryFilters()
                        .Where(payment => payment.Id == entityId)
                        .Select(payment => (Guid?)payment.InvoiceId)
                        .FirstOrDefaultAsync(ct),
                    "TransactionRecord" => await _db.Transactions.IgnoreQueryFilters()
                        .Where(transaction => transaction.Id == entityId)
                        .Select(transaction => transaction.LinkedInvoiceId)
                        .FirstOrDefaultAsync(ct),
                    _ => null
                };

                if (invoiceId.HasValue && invoiceId.Value != Guid.Empty)
                    referencedInvoiceIds.Add(invoiceId.Value);

                continue;
            }

            if (reason.StartsWith("Referenced transaction was not found:", StringComparison.OrdinalIgnoreCase))
            {
                Guid? transactionId = conflict.EntityName switch
                {
                    "TransactionAttachment" => await _db.TransactionAttachments.IgnoreQueryFilters()
                        .Where(attachment => attachment.Id == entityId)
                        .Select(attachment => (Guid?)attachment.TransactionId)
                        .FirstOrDefaultAsync(ct),
                    _ => null
                };

                if (transactionId.HasValue && transactionId.Value != Guid.Empty)
                    referencedTransactionIds.Add(transactionId.Value);
            }
        }

        if (referencedProfileIds.Count == 0 &&
            referencedCustomerIds.Count == 0 &&
            referencedInvoiceIds.Count == 0 &&
            referencedTransactionIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var changed = false;
        List<LocalRentalBillingProfile> profiles = [];
        if (referencedProfileIds.Count > 0)
        {
            profiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(profile => referencedProfileIds.Contains(profile.Id) && !profile.IsDeleted)
                .ToListAsync(ct);
            foreach (var profile in profiles)
            {
                if (profile.IsDirty)
                    continue;

                profile.IsDirty = true;
                profile.UpdatedAtUtc = now;
                changed = true;
            }
        }

        if (referencedCustomerIds.Count > 0)
            changed |= await MarkDeferredParentsDirtyAsync<LocalCustomer>(referencedCustomerIds, now, ct);

        if (referencedInvoiceIds.Count > 0)
            changed |= await MarkDeferredParentsDirtyAsync<LocalInvoice>(referencedInvoiceIds, now, ct);

        if (referencedTransactionIds.Count > 0)
            changed |= await MarkDeferredParentsDirtyAsync<LocalTransaction>(referencedTransactionIds, now, ct);

        if (profiles.Count > 0)
        {
            var companyCodes = profiles
                .Select(profile => (profile.ManagementCompanyCode ?? string.Empty).Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (companyCodes.Count > 0)
            {
                var companies = await _db.RentalManagementCompanies.IgnoreQueryFilters()
                    .Where(company => companyCodes.Contains(company.Code) && !company.IsDeleted)
                    .ToListAsync(ct);
                foreach (var company in companies)
                {
                    if (company.IsDirty)
                        continue;

                    company.IsDirty = true;
                    company.UpdatedAtUtc = now;
                    changed = true;
                }
            }
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private async Task<bool> MarkDeferredParentsDirtyAsync<T>(
        IReadOnlyCollection<Guid> ids,
        DateTime now,
        CancellationToken ct)
        where T : class, ILocalSyncEntity
    {
        if (ids.Count == 0)
            return false;

        var entities = await _db.Set<T>().IgnoreQueryFilters()
            .Where(entity => ids.Contains(entity.Id) && !entity.IsDeleted)
            .ToListAsync(ct);
        if (entities.Count == 0)
            return false;

        var changed = false;
        foreach (var entity in entities)
        {
            if (entity.IsDirty)
                continue;

            entity.IsDirty = true;
            entity.UpdatedAtUtc = now;
            changed = true;
        }

        if (changed)
            SynchronizeTrackedDirtyState(ids, now);

        return changed;
    }

    private async Task ResolveScopeConflictsAsync(IReadOnlyCollection<ConflictLogDto> conflicts, CancellationToken ct)
    {
        foreach (var group in conflicts
                     .Where(conflict => Guid.TryParse(conflict.EntityId, out _))
                     .GroupBy(conflict => conflict.EntityName, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group
                .Select(conflict => Guid.TryParse(conflict.EntityId, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
                continue;

            switch (group.Key)
            {
                case "CompanyProfile":
                    await MarkServerNewerConflictsCleanAsync<LocalCompanyProfile>(ids, ct);
                    break;
                case "Customer":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomer>(ids, ct);
                    break;
                case "CustomerCategory":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomerCategory>(ids, ct);
                    break;
                case "CustomerMaster":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomerMaster>(ids, ct);
                    break;
                case "CustomerContract":
                    await MarkServerNewerConflictsCleanAsync<LocalCustomerContract>(ids, ct);
                    break;
                case "Item":
                    await MarkServerNewerConflictsCleanAsync<LocalItem>(ids, ct);
                    break;
                case "ItemCategoryOption":
                    await MarkServerNewerConflictsCleanAsync<LocalItemCategoryOption>(ids, ct);
                    break;
                case "ItemPriceGrade":
                    await MarkServerNewerConflictsCleanAsync<LocalItemPriceGrade>(ids, ct);
                    break;
                case "PriceGradeOption":
                    await MarkServerNewerConflictsCleanAsync<LocalPriceGradeOption>(ids, ct);
                    break;
                case "TradeTypeOption":
                    await MarkServerNewerConflictsCleanAsync<LocalTradeTypeOption>(ids, ct);
                    break;
                case "Unit":
                    await MarkServerNewerConflictsCleanAsync<LocalUnit>(ids, ct);
                    break;
                case "Invoice":
                    await MarkServerNewerConflictsCleanAsync<LocalInvoice>(ids, ct);
                    break;
                case "Payment":
                    await MarkServerNewerConflictsCleanAsync<LocalPayment>(ids, ct);
                    break;
                case "TransactionRecord":
                    await MarkServerNewerConflictsCleanAsync<LocalTransaction>(ids, ct);
                    break;
                case "TransactionAttachment":
                    await MarkServerNewerConflictsCleanAsync<LocalTransactionAttachment>(ids, ct);
                    break;
                case "InventoryTransfer":
                    await MarkServerNewerConflictsCleanAsync<LocalInventoryTransfer>(ids, ct);
                    break;
                case "RentalManagementCompany":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalManagementCompany>(ids, ct);
                    break;
                case "RentalBillingProfile":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalBillingProfile>(ids, ct);
                    break;
                case "RentalAsset":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalAsset>(ids, ct);
                    break;
                case "RentalBillingLog":
                    await MarkServerNewerConflictsCleanAsync<LocalRentalBillingLog>(ids, ct);
                    break;
            }
        }
    }

    private static bool IsDeferredScopeConflict(ConflictLogDto conflict)
    {
        var reason = conflict.Reason ?? string.Empty;
        return string.Equals(reason, "Current account cannot modify this office scope.", StringComparison.OrdinalIgnoreCase)
               || string.Equals(reason, "Current account cannot modify this tenant scope.", StringComparison.OrdinalIgnoreCase)
               || reason.StartsWith("Referenced rental billing profile is outside the readable office scope:", StringComparison.OrdinalIgnoreCase)
               || reason.StartsWith("Referenced customer is outside the readable office scope:", StringComparison.OrdinalIgnoreCase)
               || reason.StartsWith("Referenced customer is outside the writable office scope:", StringComparison.OrdinalIgnoreCase)
               || reason.StartsWith("Referenced invoice is outside the readable office scope:", StringComparison.OrdinalIgnoreCase)
               || reason.StartsWith("Referenced invoice is outside the writable office scope:", StringComparison.OrdinalIgnoreCase)
               || reason.StartsWith("Referenced transaction is outside the writable office scope:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task MarkCleanAsync<T>(IReadOnlyCollection<Guid> ids, CancellationToken ct) where T : class, ILocalSyncEntity
    {
        if (ids.Count == 0)
            return;

        await _db.Set<T>().IgnoreQueryFilters()
            .Where(e => ids.Contains(e.Id) && e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);

        SynchronizeTrackedCleanState<T>(ids);
    }

    private async Task<HashSet<SyncEntityKey>> ApplyAcceptedRevisionsAsync(
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions,
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots,
        CancellationToken ct)
    {
        var locallyModifiedAfterPush = new HashSet<SyncEntityKey>();
        if (acceptedRevisions.Count == 0)
            return locallyModifiedAfterPush;

        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalCompanyProfile>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalCompanyProfile), "CompanyProfile"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalUnit>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalUnit), "Unit"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalCustomerCategory>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalCustomerCategory), "CustomerCategory"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalPriceGradeOption>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalPriceGradeOption), "PriceGradeOption"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalTradeTypeOption>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalTradeTypeOption), "TradeTypeOption"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalItemCategoryOption>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalItemCategoryOption), "ItemCategoryOption"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalCustomerMaster>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalCustomerMaster), "CustomerMaster"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalCustomer>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalCustomer), "Customer"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalCustomerContract>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalCustomerContract), "CustomerContract"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalItem>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalItem), "Item"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalItemPriceGrade>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalItemPriceGrade), "ItemPriceGrade"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalTransaction>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalTransaction), "TransactionRecord", "Transaction"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalTransactionAttachment>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalTransactionAttachment), "TransactionAttachment"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalInventoryTransfer>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalInventoryTransfer), "InventoryTransfer"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalRentalManagementCompany>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalRentalManagementCompany), "RentalManagementCompany"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalRentalBillingProfile>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalRentalBillingProfile), "RentalBillingProfile"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalRentalAsset>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalRentalAsset), "RentalAsset"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalRentalAssetAssignmentHistory>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalRentalAssetAssignmentHistory), "RentalAssetAssignmentHistory"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalRentalBillingLog>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalRentalBillingLog), "RentalBillingLog"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalInvoice>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalInvoice), "Invoice"));
        locallyModifiedAfterPush.UnionWith(
            await ApplyAcceptedRevisionsAsync<LocalPayment>(acceptedRevisions, preparedMutationSnapshots, ct, nameof(LocalPayment), "Payment"));

        if (locallyModifiedAfterPush.Count > 0)
        {
            AppLogger.Info(
                "SYNC",
                $"Preserved {locallyModifiedAfterPush.Count} newer local change(s) after push acknowledgement: " +
                string.Join(
                    ", ",
                    locallyModifiedAfterPush.Select(key =>
                        $"{key.EntityName}/{key.EntityId:N}")));
        }

        return locallyModifiedAfterPush;
    }

    private static IReadOnlyList<InventoryTransferPurgePushAcknowledgement>
        SelectVerifiedInventoryTransferPurgeAcknowledgements(
            SyncPushRequest request,
            SyncPushResult result,
            SyncOperationOwnerBoundary expectedOwner)
    {
        var submittedGroups = request.InventoryTransfers
            .Where(transfer => transfer.Id != Guid.Empty)
            .GroupBy(transfer => transfer.Id)
            .ToList();
        var duplicateSubmission = submittedGroups
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateSubmission is not null)
        {
            throw new SyncPullBlockedException(
                $"Malformed inventory transfer purge response boundary: duplicate request entity. transfer={duplicateSubmission.Key:D}");
        }

        var submittedById = submittedGroups
            .ToDictionary(group => group.Key, group => group.Single());
        if (submittedById.Count == 0)
            return [];

        var responseAcceptedRevisions = result.AcceptedRevisions ??
            throw new SyncPullBlockedException(
                "Malformed inventory transfer purge response: accepted revisions are missing.");
        var responsePurgeRecords = result.PurgeRecords ??
            throw new SyncPullBlockedException(
                "Malformed inventory transfer purge response: purge receipts are missing.");
        var responseConflicts = result.Conflicts ??
            throw new SyncPullBlockedException(
                "Malformed inventory transfer purge response: conflicts are missing.");
        if (responseAcceptedRevisions.Any(revision => revision is null) ||
            responsePurgeRecords.Any(receipt => receipt is null) ||
            responseConflicts.Any(conflict => conflict is null))
        {
            throw new SyncPullBlockedException(
                "Malformed inventory transfer purge response: a response collection contains a null entry.");
        }

        var inventoryTransferEntityName =
            NormalizeSyncEntityName(nameof(LocalInventoryTransfer));
        var relevantAcceptedRevisions = responseAcceptedRevisions
            .Where(revision =>
                revision.EntityId != Guid.Empty &&
                submittedById.ContainsKey(revision.EntityId) &&
                string.Equals(
                    NormalizeSyncEntityName(revision.EntityName),
                    inventoryTransferEntityName,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(revision => revision.EntityId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var receiptsForSubmittedTransfers = responsePurgeRecords
            .Where(receipt => submittedById.ContainsKey(receipt.EntityId))
            .ToList();
        var invalidRelevantReceiptKind = receiptsForSubmittedTransfers
            .FirstOrDefault(receipt =>
                !string.Equals(
                    (receipt.Kind ?? string.Empty).Trim(),
                    "inventory-transfer",
                    StringComparison.OrdinalIgnoreCase));
        if (invalidRelevantReceiptKind is not null)
        {
            throw new SyncPullBlockedException(
                $"Malformed inventory transfer purge response: receipt kind is not canonical. transfer={invalidRelevantReceiptKind.EntityId:D}");
        }

        var candidateEntityIds = receiptsForSubmittedTransfers
            .Select(receipt => receipt.EntityId)
            .Concat(relevantAcceptedRevisions
                .Where(group =>
                    !submittedById[group.Key].IsDeleted &&
                    group.Value.Any(revision => revision.IsDeleted == true))
                .Select(group => group.Key))
            .Distinct()
            .ToList();
        if (candidateEntityIds.Count == 0)
            return [];

        if (result.ConflictCount < 0 ||
            result.ConflictCount != responseConflicts.Count)
        {
            throw new SyncPullBlockedException(
                "Malformed inventory transfer purge response: conflict count does not match the conflict collection.");
        }

        var malformedInventoryTransferConflict = responseConflicts
            .FirstOrDefault(conflict =>
                string.Equals(
                    NormalizeSyncEntityName(conflict.EntityName),
                    inventoryTransferEntityName,
                    StringComparison.OrdinalIgnoreCase) &&
                !Guid.TryParse(conflict.EntityId, out _));
        if (malformedInventoryTransferConflict is not null)
        {
            throw new SyncPullBlockedException(
                "Malformed inventory transfer purge response: an inventory transfer conflict has an invalid entity id.");
        }

        var duplicateReceiptId = responsePurgeRecords
            .Where(receipt => receipt.Id != Guid.Empty)
            .GroupBy(receipt => receipt.Id)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateReceiptId is not null)
        {
            throw new SyncPullBlockedException(
                $"Malformed inventory transfer purge response: duplicate receipt id. receipt={duplicateReceiptId.Key:D}");
        }

        var expectedBusinessTenant =
            TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                expectedOwner.BusinessDatabaseName);
        var acknowledgements =
            new List<InventoryTransferPurgePushAcknowledgement>(
                candidateEntityIds.Count);
        foreach (var entityId in candidateEntityIds)
        {
            if (!relevantAcceptedRevisions.TryGetValue(
                    entityId,
                    out var acceptedCandidates) ||
                acceptedCandidates.Count != 1 ||
                acceptedCandidates[0].IsDeleted != true)
            {
                throw new SyncPullBlockedException(
                    $"Malformed inventory transfer purge response: exactly one accepted tombstone is required. transfer={entityId:D}");
            }

            var matchingReceipts = receiptsForSubmittedTransfers
                .Where(receipt => receipt.EntityId == entityId)
                .ToList();
            if (matchingReceipts.Count != 1)
            {
                throw new SyncPullBlockedException(
                    $"Malformed inventory transfer purge response: exactly one durable receipt is required. transfer={entityId:D}");
            }

            var accepted = acceptedCandidates[0];
            var receipt = matchingReceipts[0];
            var submitted = submittedById[entityId];
            var key = new SyncEntityKey(
                inventoryTransferEntityName,
                entityId);
            if (responseConflicts.Any(conflict =>
                    TryBuildConflictEntityKey(conflict, out var conflictKey) &&
                    conflictKey == key))
            {
                throw new SyncPullBlockedException(
                    $"Malformed inventory transfer purge response: the same transfer is both accepted and conflicted. transfer={entityId:D}");
            }

            var knownSubmittedRevision = Math.Max(
                submitted.ExpectedRevision,
                submitted.Revision);
            var legacyTimestamps = new[]
                {
                    submitted.MutationCreatedAtUtc.GetValueOrDefault(),
                    submitted.UpdatedAtUtc
                }
                .Where(timestamp => timestamp != default)
                .Select(NormalizeMutationUtc)
                .ToList();
            var isPriorIncarnation = knownSubmittedRevision > 0
                ? knownSubmittedRevision <= receipt.Revision
                : submitted.IsDeleted ||
                  legacyTimestamps.Count > 0 &&
                  legacyTimestamps.Max() <=
                      NormalizeMutationUtc(receipt.PurgedAtUtc);
            if (!TenantScopeCatalog.TryNormalizeTenantCode(
                    submitted.TenantCode,
                    out var submittedTenant) ||
                !TenantScopeCatalog.TryNormalizeTenantCode(
                    receipt.TenantCode,
                    out var receiptTenant) ||
                !OfficeCodeCatalog.TryNormalizeOfficeCode(
                    submitted.SourceOfficeCode,
                    out var submittedSourceOffice) ||
                !OfficeCodeCatalog.TryNormalizeOfficeCode(
                    submitted.TargetOfficeCode,
                    out var submittedTargetOffice) ||
                !OfficeCodeCatalog.TryNormalizeOfficeCode(
                    receipt.SourceOfficeCode,
                    out var receiptSourceOffice) ||
                !OfficeCodeCatalog.TryNormalizeOfficeCode(
                    receipt.TargetOfficeCode,
                    out var receiptTargetOffice) ||
                !OfficeCodeCatalog.TryNormalizeScope(
                    receipt.OfficeCode,
                    out var receiptOfficeScope) ||
                receipt.Id == Guid.Empty ||
                receipt.EntityId != entityId ||
                receipt.IsDeleted ||
                accepted.Revision <= 0 ||
                receipt.Revision != accepted.Revision ||
                result.CurrentServerRevision < receipt.Revision ||
                accepted.UpdatedAtUtc == default ||
                receipt.CreatedAtUtc == default ||
                receipt.UpdatedAtUtc == default ||
                receipt.PurgedAtUtc == default ||
                NormalizeMutationUtc(accepted.UpdatedAtUtc) !=
                    NormalizeMutationUtc(receipt.UpdatedAtUtc) ||
                NormalizeMutationUtc(receipt.UpdatedAtUtc) <
                    NormalizeMutationUtc(receipt.CreatedAtUtc) ||
                NormalizeMutationUtc(receipt.PurgedAtUtc) >
                    NormalizeMutationUtc(receipt.UpdatedAtUtc) ||
                !isPriorIncarnation ||
                !string.Equals(
                    submittedTenant,
                    receiptTenant,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    receiptTenant,
                    expectedBusinessTenant,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    receiptOfficeScope,
                    OfficeCodeCatalog.Shared,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    submittedSourceOffice,
                    receiptSourceOffice,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    submittedTargetOffice,
                    receiptTargetOffice,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncPullBlockedException(
                    $"Malformed inventory transfer purge response: identity, scope, revision, or timestamp validation failed. transfer={entityId:D}");
            }

            acknowledgements.Add(
                new InventoryTransferPurgePushAcknowledgement(
                    receipt,
                    accepted,
                    submitted));
        }

        return acknowledgements;
    }

    private LocalInventoryTransfer SelectInventoryTransferConflictSnapshotSource(
        LocalInventoryTransfer persisted,
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot>
            preparedMutationSnapshots)
    {
        var key = new SyncEntityKey(
            NormalizeSyncEntityName(nameof(LocalInventoryTransfer)),
            persisted.Id);
        if (!_trackedMutationsPreservedDuringSync.TryGetValue(
                key,
                out var preservation) ||
            preservation.Entity is not LocalInventoryTransfer tracked ||
            !preparedMutationSnapshots.TryGetValue(key, out var prepared))
        {
            return persisted;
        }

        var trackedPayloadHash = ComputePreparedMutationPayloadHash(
            key.EntityName,
            LocalMappings.ToDto(tracked));
        return string.Equals(
            trackedPayloadHash,
            prepared.PayloadHash,
            StringComparison.Ordinal)
            ? persisted
            : tracked;
    }

    private async Task
        ValidateAndStageVerifiedInventoryTransferPurgeReceiptsAsync(
            IReadOnlyCollection<InventoryTransferPurgePushAcknowledgement>
                acknowledgements,
            DeferredPurgeOwnerScope ownerScope,
            CancellationToken ct)
    {
        var receiptIds = acknowledgements
            .Select(acknowledgement => acknowledgement.PurgeRecord.Id)
            .ToList();
        var existingById = await _db.DeferredRecycleBinPurgeRecords
            .Where(record => receiptIds.Contains(record.Id))
            .ToDictionaryAsync(record => record.Id, ct);
        var now = DateTime.UtcNow;
        foreach (var acknowledgement in acknowledgements)
        {
            var receipt = acknowledgement.PurgeRecord;
            if (existingById.TryGetValue(receipt.Id, out var existing))
            {
                var exactReplay =
                    DeferredPurgeRecordBelongsToOwner(existing, ownerScope) &&
                    string.Equals(
                        NormalizePurgeRecordKind(existing.Kind),
                        "inventory-transfer",
                        StringComparison.OrdinalIgnoreCase) &&
                    existing.EntityId == receipt.EntityId &&
                    existing.Revision == receipt.Revision &&
                    NormalizeMutationUtc(existing.PurgedAtUtc) ==
                        NormalizeMutationUtc(receipt.PurgedAtUtc) &&
                    existing.AppliedAtUtc is null;
                if (!exactReplay)
                {
                    throw new SyncPullBlockedException(
                        $"Verified inventory transfer purge receipt collides with different local receipt state. receipt={receipt.Id:D}");
                }

                continue;
            }

            _db.DeferredRecycleBinPurgeRecords.Add(
                new LocalDeferredRecycleBinPurgeRecord
                {
                    Id = receipt.Id,
                    BusinessDatabaseName = ownerScope.BusinessDatabaseName,
                    TenantCode = ownerScope.TenantCode,
                    OfficeCode = ownerScope.OfficeCode,
                    ResponsibleOfficeCode =
                        ownerScope.ResponsibleOfficeCode,
                    Kind = "inventory-transfer",
                    EntityId = receipt.EntityId,
                    Revision = receipt.Revision,
                    PurgedAtUtc = NormalizeMutationUtc(receipt.PurgedAtUtc),
                    AttemptCount = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task RemoveConsumedInventoryTransferPurgeReceiptsAsync(
        IReadOnlyCollection<InventoryTransferPurgePushAcknowledgement>
            acknowledgements,
        DeferredPurgeOwnerScope ownerScope,
        CancellationToken ct)
    {
        var expectedReceiptIds = acknowledgements
            .Select(acknowledgement =>
                acknowledgement.PurgeRecord.Id)
            .ToHashSet();
        var entityIds = acknowledgements
            .Select(acknowledgement =>
                acknowledgement.AcceptedRevision.EntityId)
            .ToList();
        var consumedRecords = await _db.DeferredRecycleBinPurgeRecords
            .Where(record =>
                record.BusinessDatabaseName ==
                    ownerScope.BusinessDatabaseName &&
                record.TenantCode == ownerScope.TenantCode &&
                record.OfficeCode == ownerScope.OfficeCode &&
                record.ResponsibleOfficeCode ==
                    ownerScope.ResponsibleOfficeCode &&
                entityIds.Contains(record.EntityId))
            .ToListAsync(ct);
        var unrelatedKind = consumedRecords.FirstOrDefault(record =>
            !string.Equals(
                NormalizePurgeRecordKind(record.Kind),
                "inventory-transfer",
                StringComparison.OrdinalIgnoreCase));
        if (unrelatedKind is not null)
        {
            throw new SyncPullBlockedException(
                $"Verified inventory transfer purge receipt removal encountered a different entity kind. receipt={unrelatedKind.Id:D}");
        }

        var unexpectedReceipt = consumedRecords.FirstOrDefault(record =>
            !expectedReceiptIds.Contains(record.Id));
        if (unexpectedReceipt is not null)
        {
            throw new SyncPullBlockedException(
                $"Verified inventory transfer purge receipt removal encountered an unexpected receipt for the same entity. receipt={unexpectedReceipt.Id:D}");
        }

        if (consumedRecords.Count != expectedReceiptIds.Count)
        {
            throw new SyncPullBlockedException(
                "Verified inventory transfer purge receipt removal could not resolve every expected receipt exactly once.");
        }

        _db.DeferredRecycleBinPurgeRecords.RemoveRange(consumedRecords);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<HashSet<SyncEntityKey>>
        ApplyInventoryTransferPurgeAcknowledgementsAtomicallyAsync(
            SyncPushRequest request,
            IReadOnlyCollection<InventoryTransferPurgePushAcknowledgement>
                acknowledgements,
            IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot>
                preparedMutationSnapshots,
            IReadOnlySet<SyncEntityKey>? excludedKeys,
            SyncOperationOwnerBoundary expectedOwner,
            SessionState ownerSession,
            string? businessDatabaseNameOverride,
            IReadOnlyDictionary<string, CurrentPushMutationReceipt>
                currentPushReceipts,
            CancellationToken ct)
    {
        var applicableAcknowledgements = acknowledgements
            .Where(acknowledgement =>
            {
                var key = new SyncEntityKey(
                    NormalizeSyncEntityName(nameof(LocalInventoryTransfer)),
                    acknowledgement.AcceptedRevision.EntityId);
                return excludedKeys is null || !excludedKeys.Contains(key);
            })
            .ToList();
        if (applicableAcknowledgements.Count == 0)
            return [];

        var acceptedRevisions = applicableAcknowledgements
            .Select(acknowledgement => acknowledgement.AcceptedRevision)
            .ToList();
        var handledKeys = acceptedRevisions
            .Select(revision => new SyncEntityKey(
                NormalizeSyncEntityName(revision.EntityName),
                revision.EntityId))
            .ToHashSet();
        var ownerScope = BuildDeferredPurgeOwnerScope(expectedOwner);
        var reservedAcknowledgementOutboxRowIds = request.InventoryTransfers
            .Where(transfer => handledKeys.Contains(new SyncEntityKey(
                NormalizeSyncEntityName(nameof(LocalInventoryTransfer)),
                transfer.Id)))
            .Select(transfer => transfer.MutationId)
            .Where(mutationId =>
                !string.IsNullOrWhiteSpace(mutationId) &&
                currentPushReceipts.ContainsKey(mutationId))
            .Select(mutationId =>
                currentPushReceipts[mutationId].OutboxRowId)
            .ToHashSet();
        await RecoverIncompleteAttachmentFileJournalsAsync(ct);
        await using var transaction =
            await _db.BeginRuntimeMutationTransactionAsync(ct);
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);
        using var inventoryStateChangeCapture =
            _local.CaptureInventoryStateChanges();
        var commitAttempted = false;
        var committed = false;

        try
        {
            await ValidateAndStageVerifiedInventoryTransferPurgeReceiptsAsync(
                applicableAcknowledgements,
                ownerScope,
                ct);
            foreach (var acknowledgement in applicableAcknowledgements)
            {
                var accepted = acknowledgement.AcceptedRevision;
                var receipt = acknowledgement.PurgeRecord;
                var existing = await _db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .Include(transfer => transfer.Lines)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        transfer => transfer.Id == accepted.EntityId,
                        ct);
                if (existing is null || existing.IsDeleted)
                {
                    continue;
                }

                var conflictSource =
                    SelectInventoryTransferConflictSnapshotSource(
                        existing,
                        preparedMutationSnapshots);
                var serverTombstone = LocalMappings.ToDto(conflictSource);
                serverTombstone.TenantCode = receipt.TenantCode;
                serverTombstone.SourceOfficeCode =
                    receipt.SourceOfficeCode;
                serverTombstone.TargetOfficeCode =
                    receipt.TargetOfficeCode;
                serverTombstone.IsDeleted = true;
                serverTombstone.Revision = accepted.Revision;
                serverTombstone.UpdatedAtUtc =
                    accepted.UpdatedAtUtc == default
                        ? NormalizeMutationUtc(receipt.UpdatedAtUtc)
                        : NormalizeMutationUtc(accepted.UpdatedAtUtc);
                serverTombstone.Lines = [];
                await PersistInventoryTransferTombstoneConflictAsync(
                    conflictSource,
                    serverTombstone,
                    ct,
                    expectedOwner.BusinessDatabaseName,
                    ownerSession,
                    reservedAcknowledgementOutboxRowIds);
            }

            await MarkOutboxAcknowledgedCoreAsync(
                request,
                acceptedRevisions,
                excludedKeys,
                ownerSession,
                businessDatabaseNameOverride,
                currentPushReceipts,
                ct);
            foreach (var acknowledgement in applicableAcknowledgements)
            {
                var purgeResult = await _local
                    .ApplyConfirmedInventoryTransferPurgeAsync(
                        acknowledgement.PurgeRecord,
                        expectedOwner.BusinessDatabaseName,
                        attachmentFiles,
                        ct);
                if (!purgeResult.Success && !purgeResult.NotFound)
                {
                    throw new SyncPullBlockedException(
                        string.IsNullOrWhiteSpace(purgeResult.Message)
                            ? "The verified inventory transfer purge could not be applied atomically."
                            : purgeResult.Message);
                }
            }

            await RemoveConsumedInventoryTransferPurgeReceiptsAsync(
                applicableAcknowledgements,
                ownerScope,
                ct);
            if (AfterInventoryTransferPurgePushAppliedAsyncForTesting is not null)
            {
                await AfterInventoryTransferPurgePushAppliedAsyncForTesting(ct);
            }

            committed =
                await CommitAttachmentTransactionUnderOwnerLeaseAsync(
                    transaction,
                    attachmentFiles,
                    expectedOwner,
                    () => commitAttempted = true,
                    ct,
                    ownerSession,
                    businessDatabaseNameOverride);
            if (!committed)
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                throw new SyncPullBlockedException(
                    "재고이동 영구삭제 응답 반영 직전에 로그인·업체 DB 범위가 변경되어 전체 반영을 취소했습니다.");
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await attachmentFiles.CompleteAfterDatabaseCommitAsync(
                _db,
                CancellationToken.None);
        }
        catch
        {
            var commitResolution =
                AttachmentCommitResolution.RolledBack;
            try
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                AppLogger.Error(
                    "ATTACHMENT",
                    "재고이동 push 영구삭제 반영의 DB 롤백 결과를 확정하지 못했습니다.",
                    rollbackException);
            }

            if (!commitAttempted)
            {
                attachmentFiles.Rollback();
            }
            else
            {
                commitResolution =
                    await attachmentFiles.ResolveCommitAmbiguityAsync(
                        _db,
                        CancellationToken.None);
            }

            _db.ChangeTracker.Clear();
            if (commitResolution != AttachmentCommitResolution.Committed)
                throw;

            await transaction.DisposeAsync().ConfigureAwait(false);
            committed = true;
        }

        if (!committed)
        {
            throw new SyncPullBlockedException(
                "재고이동 영구삭제 응답을 원자적으로 반영하지 못했습니다.");
        }

        foreach (var handledKey in handledKeys)
            _trackedMutationsPreservedDuringSync.Remove(handledKey);

        inventoryStateChangeCapture.Dispose();
        if (inventoryStateChangeCapture.HasChanges)
        {
            if (ReferenceEquals(ownerSession, _session) &&
                IsSyncOperationOwnerCurrent(
                    expectedOwner,
                    ownerSession,
                    businessDatabaseNameOverride))
            {
                _local.TryPublishInventoryStateChanged();
            }
            else
            {
                await ScheduleCurrentOwnerRefreshAfterCommittedOwnerChangeAsync();
            }
        }

        AppLogger.Warn(
            "SYNC",
            $"Applied {applicableAcknowledgements.Count} durable inventory transfer purge acknowledgement(s) before pull.");
        return handledKeys;
    }

    private async Task<HashSet<SyncEntityKey>> ApplyAcceptedRevisionsAsync<T>(
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions,
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots,
        CancellationToken ct,
        params string[] entityNames)
        where T : class, ILocalSyncEntity
    {
        var locallyModifiedAfterPush = new HashSet<SyncEntityKey>();
        var revisionsById = acceptedRevisions
            .Where(revision => revision.EntityId != Guid.Empty &&
                               entityNames.Any(name => string.Equals(revision.EntityName, name, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(revision => revision.EntityId)
            .Select(group => group
                .OrderByDescending(revision => revision.Revision)
                .ThenByDescending(revision => revision.UpdatedAtUtc)
                .First())
            .ToDictionary(revision => revision.EntityId);

        if (revisionsById.Count == 0)
            return locallyModifiedAfterPush;

        var canonicalEntityName = NormalizeSyncEntityName(entityNames[0]);
        var cleanedIds = new HashSet<Guid>();
        var preservedDirtyIds = new HashSet<Guid>();
        foreach (var (entityId, accepted) in revisionsById)
        {
            var key = new SyncEntityKey(canonicalEntityName, entityId);
            if (!preparedMutationSnapshots.TryGetValue(key, out var prepared))
            {
                // 서버가 canonical/alias revision을 함께 반환해도 실제 전송한 mutation이 아니면
                // 해당 로컬 행의 dirty 상태를 변경하지 않는다.
                continue;
            }

            var current = await LoadCurrentSyncEntitySnapshotAsync<T>(entityId, ct);
            if (current is null)
                continue;

            var currentPayloadHash = ComputePreparedMutationPayloadHash(
                canonicalEntityName,
                MapLocalEntityToPreparedMutationDto(current));
            var hasPendingTrackedMutation =
                HasPendingTrackedMutationAfterPush<T>(
                    entityId,
                    canonicalEntityName,
                    prepared);
            if (hasPendingTrackedMutation &&
                accepted.Revision > 0 &&
                _trackedMutationsPreservedDuringSync.TryGetValue(key, out var preservation))
            {
                preservation.RebaseAcceptedRevision(accepted.Revision);
            }
            var exactPreparedMutation =
                current.IsDirty &&
                !hasPendingTrackedMutation &&
                IsPreparedMutationCurrent(current, prepared) &&
                string.Equals(
                    currentPayloadHash,
                    prepared.PayloadHash,
                    StringComparison.Ordinal);

            if (exactPreparedMutation)
            {
                var acceptedRevision = accepted.Revision > 0 && accepted.Revision >= current.Revision
                    ? accepted.Revision
                    : current.Revision;
                var acceptedUpdatedAtUtc = accepted.UpdatedAtUtc != default
                    ? NormalizeMutationUtc(accepted.UpdatedAtUtc)
                    : NormalizeMutationUtc(current.UpdatedAtUtc);
                if (BeforeAcceptedRevisionCleanAsyncForTesting is not null)
                {
                    await BeforeAcceptedRevisionCleanAsyncForTesting(ct);
                }

                var affected = await _db.Set<T>()
                    .IgnoreQueryFilters()
                    .Where(row =>
                        row.Id == entityId &&
                        row.IsDirty &&
                        row.Revision == prepared.ExpectedRevision &&
                        row.UpdatedAtUtc == prepared.UpdatedAtUtc &&
                        row.IsDeleted == prepared.IsDeleted)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(row => row.Revision, acceptedRevision)
                            .SetProperty(row => row.UpdatedAtUtc, acceptedUpdatedAtUtc)
                            .SetProperty(row => row.IsDirty, false),
                        ct);
                AcceptedRevisionCleanAffectedRowsForTesting?.Invoke(affected);
                if (affected > 0)
                {
                    cleanedIds.Add(entityId);
                    continue;
                }
            }
            // 비교 이후 다른 DbContext가 다시 수정한 경우에도 사용자 payload와 시각은 건드리지
            // 않고 서버가 승인한 revision만 rebase한다.
            if (accepted.Revision > 0)
            {
                await _db.Set<T>()
                    .IgnoreQueryFilters()
                    .Where(row =>
                        row.Id == entityId &&
                        row.IsDirty &&
                        row.Revision < accepted.Revision)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.Revision, accepted.Revision),
                        ct);
            }

            var remainsDirty = await _db.Set<T>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(row => row.Id == entityId && row.IsDirty, ct);
            if (!remainsDirty)
                continue;

            preservedDirtyIds.Add(entityId);
            locallyModifiedAfterPush.Add(key);
        }

        SynchronizeTrackedAcceptedRevisionState<T>(
            revisionsById,
            cleanedIds,
            preservedDirtyIds);
        return locallyModifiedAfterPush;
    }

    private static bool IsPreparedMutationCurrent(
        ILocalSyncEntity current,
        PreparedMutationSnapshot prepared)
        => current.Revision == prepared.ExpectedRevision &&
           NormalizeMutationUtc(current.UpdatedAtUtc) == prepared.UpdatedAtUtc &&
           current.IsDeleted == prepared.IsDeleted;

    private bool HasPendingTrackedUserChanges()
    {
        _db.ChangeTracker.DetectChanges();
        var hasLocalChanges = _db.ChangeTracker.Entries()
            .Any(entry =>
                entry.Entity is not LocalSyncOutboxEntry &&
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        return hasLocalChanges ||
               (_isolatedOperationOwner is not null &&
                _isolatedOperationOwner.HasPendingTrackedUserChanges());
    }

    private void PreservePendingTrackedChangesForSync()
    {
        var trackedState = CaptureTrackedStateBeforePush();
        CaptureNonMutationTrackedChangesAtPushBoundary(
            trackedState,
            includeExistingChanges: true);
    }

    private bool PreserveTrackedChangesSinceBoundary(
        IReadOnlyDictionary<object, TrackedEntryPushBaseline> trackedState)
    {
        var preservedCountBefore =
            _trackedMutationsPreservedDuringSync.Count +
            _trackedNonMutationChangesPreservedDuringSync.Count;
        CaptureNonMutationTrackedChangesAtPushBoundary(
            trackedState,
            includeExistingChanges: false);
        var preservedCountAfter =
            _trackedMutationsPreservedDuringSync.Count +
            _trackedNonMutationChangesPreservedDuringSync.Count;
        return preservedCountAfter > preservedCountBefore;
    }

    private bool HasTrackedUserChangesSinceBoundary(
        IReadOnlyDictionary<object, TrackedEntryPushBaseline> trackedState)
    {
        _db.ChangeTracker.DetectChanges();
        var hasLocalChanges = _db.ChangeTracker.Entries()
            .Where(entry =>
                entry.Entity is not LocalSyncOutboxEntry &&
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Any(entry =>
                !trackedState.TryGetValue(entry.Entity, out var baseline) ||
                baseline.HasChanged(entry));
        return hasLocalChanges ||
               (_isolatedOperationOwner is not null &&
                _isolatedOperationOwner.HasPendingTrackedUserChanges());
    }

    private IReadOnlyDictionary<object, TrackedEntryPushBaseline>?
        CaptureIsolatedOwnerTrackedState()
        => _isolatedOperationOwner?.CaptureTrackedStateBeforePush();

    private bool HasIsolatedOwnerTrackedChangesSinceBoundary(
        IReadOnlyDictionary<object, TrackedEntryPushBaseline>? trackedState)
        => trackedState is not null &&
           _isolatedOperationOwner is not null &&
           _isolatedOperationOwner.HasTrackedUserChangesSinceBoundary(
               trackedState);

    private IReadOnlyDictionary<object, TrackedEntryPushBaseline> CaptureTrackedStateBeforePush()
    {
        _db.ChangeTracker.DetectChanges();
        return _db.ChangeTracker.Entries()
            .ToDictionary(
                entry => entry.Entity,
                TrackedEntryPushBaseline.Capture,
                ReferenceEqualityComparer.Instance);
    }

    private void CaptureTrackedChangesBeforePreparedMutationBoundary(
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots)
    {
        _db.ChangeTracker.DetectChanges();
        var changedEntries = _db.ChangeTracker.Entries()
            .Where(entry =>
                entry.Entity is not LocalSyncOutboxEntry &&
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (changedEntries.Count == 0)
            return;

        var exactPreparedMutationEntities =
            new HashSet<object>(ReferenceEqualityComparer.Instance);

        void PreserveIfNewerThanPrepared(ILocalSyncEntity root)
        {
            var key = new SyncEntityKey(
                NormalizeSyncEntityName(root.GetType().Name),
                root.Id);
            if (IsExactPreparedMutation(root, key, preparedMutationSnapshots))
            {
                foreach (var entry in GetTrackedMutationGraphEntries(root))
                    exactPreparedMutationEntities.Add(entry.Entity);
                return;
            }

            PreserveTrackedMutationForSync(key, root);
        }

        foreach (var root in changedEntries
                     .Select(entry => entry.Entity)
                     .OfType<ILocalSyncEntity>()
                     .ToList())
        {
            PreserveIfNewerThanPrepared(root);
        }

        foreach (var dependent in changedEntries
                     .Where(entry => entry.State != EntityState.Detached)
                     .Select(entry => entry.Entity)
                     .ToList())
        {
            var root = FindTrackedMutationRootForDependent(dependent);
            if (root is not null)
                PreserveIfNewerThanPrepared(root);
        }

        var preservedMutationEntities = _trackedMutationsPreservedDuringSync.Values
            .SelectMany(preservation => preservation.Entries)
            .Select(preservation => preservation.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var standaloneEntries = changedEntries
            .Where(entry => entry.State != EntityState.Detached)
            .Where(entry => !preservedMutationEntities.Contains(entry.Entity))
            .Where(entry => !exactPreparedMutationEntities.Contains(entry.Entity))
            .Select(entry => TrackedEntityPreservation.Capture(entry, entry.State))
            .ToList();
        _trackedNonMutationChangesPreservedDuringSync.AddRange(standaloneEntries);

        foreach (var preservedEntry in standaloneEntries.AsEnumerable().Reverse())
        {
            var entry = _db.Entry(preservedEntry.Entity);
            if (entry.State != EntityState.Detached)
                entry.State = EntityState.Detached;
        }
    }

    private static bool IsExactPreparedMutation(
        ILocalSyncEntity current,
        SyncEntityKey key,
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots)
    {
        if (!preparedMutationSnapshots.TryGetValue(key, out var prepared) ||
            !IsPreparedMutationCurrent(current, prepared))
        {
            return false;
        }

        var currentPayloadHash = ComputePreparedMutationPayloadHash(
            key.EntityName,
            MapLocalEntityToPreparedMutationDto(current));
        return string.Equals(
            currentPayloadHash,
            prepared.PayloadHash,
            StringComparison.Ordinal);
    }

    private void CaptureTrackedMutationsChangedAfterPush(
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots)
    {
        if (preparedMutationSnapshots.Count == 0)
            return;

        _db.ChangeTracker.DetectChanges();
        var roots = _db.ChangeTracker.Entries()
            .Where(entry => entry.Entity is ILocalSyncEntity)
            .ToList();
        foreach (var rootEntry in roots)
        {
            var root = (ILocalSyncEntity)rootEntry.Entity;
            var key = new SyncEntityKey(
                NormalizeSyncEntityName(root.GetType().Name),
                root.Id);
            if (!preparedMutationSnapshots.ContainsKey(key) ||
                _trackedMutationsPreservedDuringSync.ContainsKey(key))
            {
                continue;
            }

            var graphEntries = GetTrackedMutationGraphEntries(root);
            if (graphEntries.Any(entry =>
                    entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                PreserveTrackedMutationForSync(key, root, graphEntries);
            }
        }
    }

    private void CaptureNonMutationTrackedChangesAtPushBoundary(
        IReadOnlyDictionary<object, TrackedEntryPushBaseline> trackedStateBeforePush,
        bool includeExistingChanges)
    {
        _db.ChangeTracker.DetectChanges();
        var changedEntries = _db.ChangeTracker.Entries()
            .Where(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(entry =>
                includeExistingChanges ||
                !trackedStateBeforePush.TryGetValue(entry.Entity, out var baseline) ||
                baseline.HasChanged(entry))
            .Where(entry => entry.Entity is not LocalSyncOutboxEntry)
            .ToList();
        if (changedEntries.Count == 0)
            return;

        foreach (var root in changedEntries
                     .Select(entry => entry.Entity)
                     .OfType<ILocalSyncEntity>()
                     .ToList())
        {
            var key = new SyncEntityKey(
                NormalizeSyncEntityName(root.GetType().Name),
                root.Id);
            PreserveTrackedMutationForSync(key, root);
        }

        foreach (var dependent in changedEntries
                     .Where(entry => entry.State != EntityState.Detached)
                     .Select(entry => entry.Entity)
                     .ToList())
        {
            var root = FindTrackedMutationRootForDependent(dependent);
            if (root is null)
                continue;

            var key = new SyncEntityKey(
                NormalizeSyncEntityName(root.GetType().Name),
                root.Id);
            PreserveTrackedMutationForSync(key, root);
        }

        var preservedMutationEntities = _trackedMutationsPreservedDuringSync.Values
            .SelectMany(preservation => preservation.Entries)
            .Select(preservation => preservation.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var standaloneEntries = changedEntries
            .Where(entry => entry.State != EntityState.Detached)
            .Where(entry => !preservedMutationEntities.Contains(entry.Entity))
            .Select(entry => TrackedEntityPreservation.Capture(entry, entry.State))
            .ToList();
        _trackedNonMutationChangesPreservedDuringSync.AddRange(standaloneEntries);

        foreach (var preservedEntry in standaloneEntries.AsEnumerable().Reverse())
        {
            var entry = _db.Entry(preservedEntry.Entity);
            if (entry.State != EntityState.Detached)
                entry.State = EntityState.Detached;
        }
    }

    private ILocalSyncEntity? FindTrackedMutationRootForDependent(object dependent)
        => dependent switch
        {
            LocalInvoiceLine line => _db.ChangeTracker.Entries<LocalInvoice>()
                .Select(entry => entry.Entity)
                .FirstOrDefault(invoice => invoice.Id == line.InvoiceId),
            LocalInventoryTransferLine line => _db.ChangeTracker.Entries<LocalInventoryTransfer>()
                .Select(entry => entry.Entity)
                .FirstOrDefault(transfer => transfer.Id == line.TransferId),
            _ => null
        };

    private static bool TryBuildConflictEntityKey(
        ConflictLogDto conflict,
        out SyncEntityKey key)
    {
        key = default;
        if (!Guid.TryParse(conflict.EntityId, out var entityId))
            return false;

        key = new SyncEntityKey(
            NormalizeSyncEntityName(conflict.EntityName),
            entityId);
        return true;
    }

    private async Task<bool> ShouldPreserveConcurrentConflictAsync(
        ConflictLogDto conflict,
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots,
        CancellationToken ct)
    {
        if (IsConflictForPreservedTrackedMutation(conflict))
            return true;
        if (TryGetItemWarehouseStockRevisionConflictSnapshots(
                conflict,
                out _,
                out _))
            return false;
        if (!TryBuildConflictEntityKey(conflict, out var key) ||
            !preparedMutationSnapshots.TryGetValue(key, out var prepared))
        {
            return true;
        }

        return !await IsPreparedMutationStillCurrentAsync(
            key,
            prepared,
            ct);
    }

    private Task<bool> IsPreparedMutationStillCurrentAsync(
        SyncEntityKey key,
        PreparedMutationSnapshot prepared,
        CancellationToken ct)
        => key.EntityName switch
        {
            "CompanyProfile" => IsPreparedMutationStillCurrentAsync<LocalCompanyProfile>(key, prepared, ct),
            "Unit" => IsPreparedMutationStillCurrentAsync<LocalUnit>(key, prepared, ct),
            "CustomerCategory" => IsPreparedMutationStillCurrentAsync<LocalCustomerCategory>(key, prepared, ct),
            "PriceGradeOption" => IsPreparedMutationStillCurrentAsync<LocalPriceGradeOption>(key, prepared, ct),
            "TradeTypeOption" => IsPreparedMutationStillCurrentAsync<LocalTradeTypeOption>(key, prepared, ct),
            "ItemCategoryOption" => IsPreparedMutationStillCurrentAsync<LocalItemCategoryOption>(key, prepared, ct),
            "CustomerMaster" => IsPreparedMutationStillCurrentAsync<LocalCustomerMaster>(key, prepared, ct),
            "Customer" => IsPreparedMutationStillCurrentAsync<LocalCustomer>(key, prepared, ct),
            "CustomerContract" => IsPreparedMutationStillCurrentAsync<LocalCustomerContract>(key, prepared, ct),
            "Item" => IsPreparedMutationStillCurrentAsync<LocalItem>(key, prepared, ct),
            "ItemPriceGrade" => IsPreparedMutationStillCurrentAsync<LocalItemPriceGrade>(key, prepared, ct),
            "TransactionRecord" => IsPreparedMutationStillCurrentAsync<LocalTransaction>(key, prepared, ct),
            "TransactionAttachment" => IsPreparedMutationStillCurrentAsync<LocalTransactionAttachment>(key, prepared, ct),
            "InventoryTransfer" => IsPreparedMutationStillCurrentAsync<LocalInventoryTransfer>(key, prepared, ct),
            "RentalManagementCompany" => IsPreparedMutationStillCurrentAsync<LocalRentalManagementCompany>(key, prepared, ct),
            "RentalBillingProfile" => IsPreparedMutationStillCurrentAsync<LocalRentalBillingProfile>(key, prepared, ct),
            "RentalAsset" => IsPreparedMutationStillCurrentAsync<LocalRentalAsset>(key, prepared, ct),
            "RentalAssetAssignmentHistory" => IsPreparedMutationStillCurrentAsync<LocalRentalAssetAssignmentHistory>(key, prepared, ct),
            "RentalBillingLog" => IsPreparedMutationStillCurrentAsync<LocalRentalBillingLog>(key, prepared, ct),
            "Invoice" => IsPreparedMutationStillCurrentAsync<LocalInvoice>(key, prepared, ct),
            "Payment" => IsPreparedMutationStillCurrentAsync<LocalPayment>(key, prepared, ct),
            _ => Task.FromResult(true)
        };

    private async Task<bool> IsPreparedMutationStillCurrentAsync<T>(
        SyncEntityKey key,
        PreparedMutationSnapshot prepared,
        CancellationToken ct)
        where T : class, ILocalSyncEntity
    {
        var current = await LoadCurrentSyncEntitySnapshotAsync<T>(
            key.EntityId,
            ct);
        if (current is null ||
            !current.IsDirty ||
            !IsPreparedMutationCurrent(current, prepared))
        {
            return false;
        }

        var currentPayloadHash = ComputePreparedMutationPayloadHash(
            key.EntityName,
            MapLocalEntityToPreparedMutationDto(current));
        return string.Equals(
            currentPayloadHash,
            prepared.PayloadHash,
            StringComparison.Ordinal);
    }

    private async Task<ServerNewerConflictResolution>
        ResolvePreparedServerNewerConflictsAsync(
            IReadOnlyCollection<ConflictLogDto> conflicts,
            IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots,
            SyncPushRequest request,
            SessionState session,
            IReadOnlySet<SyncEntityKey>? excludedKeys,
            CancellationToken ct)
    {
        var resolvedConflicts = new List<ConflictLogDto>();
        var preservedConflicts = new List<ConflictLogDto>();

        foreach (var conflict in conflicts)
        {
            if (!TryBuildConflictEntityKey(conflict, out var key) ||
                !preparedMutationSnapshots.TryGetValue(key, out var prepared) ||
                !TryReadConflictServerRevision(
                    conflict.ServerJson,
                    out var serverRevision) ||
                serverRevision <= 0)
            {
                preservedConflicts.Add(conflict);
                continue;
            }

            if (await TryResolvePreparedServerNewerConflictAsync(
                    key,
                    prepared,
                    conflict,
                    request,
                    session,
                    excludedKeys,
                    ct))
            {
                resolvedConflicts.Add(conflict);
                continue;
            }

            preservedConflicts.Add(conflict);
            await RebasePreservedConcurrentConflictsAsync(
                [conflict],
                ct);
        }

        return new ServerNewerConflictResolution(
            resolvedConflicts,
            preservedConflicts);
    }

    private Task<bool> TryResolvePreparedServerNewerConflictAsync(
        SyncEntityKey key,
        PreparedMutationSnapshot prepared,
        ConflictLogDto conflict,
        SyncPushRequest request,
        SessionState session,
        IReadOnlySet<SyncEntityKey>? excludedKeys,
        CancellationToken ct)
        => Task.FromResult(false);

    private async Task RebasePreservedConcurrentConflictsAsync(
        IReadOnlyCollection<ConflictLogDto> conflicts,
        CancellationToken ct)
    {
        foreach (var conflict in conflicts)
        {
            if (!TryBuildConflictEntityKey(conflict, out var key) ||
                !TryReadConflictServerRevision(conflict.ServerJson, out var serverRevision) ||
                serverRevision <= 0)
            {
                continue;
            }

            await RebasePersistedDirtyMutationRevisionAsync(
                key,
                serverRevision,
                ct);
            if (_trackedMutationsPreservedDuringSync.TryGetValue(
                    key,
                    out var preservation))
            {
                preservation.RebaseAcceptedRevision(serverRevision);
            }

        }
    }

    private Task RebasePersistedDirtyMutationRevisionAsync(
        SyncEntityKey key,
        long serverRevision,
        CancellationToken ct)
        => key.EntityName switch
        {
            "CompanyProfile" => RebasePersistedDirtyMutationRevisionAsync<LocalCompanyProfile>(key.EntityId, serverRevision, ct),
            "Unit" => RebasePersistedDirtyMutationRevisionAsync<LocalUnit>(key.EntityId, serverRevision, ct),
            "CustomerCategory" => RebasePersistedDirtyMutationRevisionAsync<LocalCustomerCategory>(key.EntityId, serverRevision, ct),
            "PriceGradeOption" => RebasePersistedDirtyMutationRevisionAsync<LocalPriceGradeOption>(key.EntityId, serverRevision, ct),
            "TradeTypeOption" => RebasePersistedDirtyMutationRevisionAsync<LocalTradeTypeOption>(key.EntityId, serverRevision, ct),
            "ItemCategoryOption" => RebasePersistedDirtyMutationRevisionAsync<LocalItemCategoryOption>(key.EntityId, serverRevision, ct),
            "CustomerMaster" => RebasePersistedDirtyMutationRevisionAsync<LocalCustomerMaster>(key.EntityId, serverRevision, ct),
            "Customer" => RebasePersistedDirtyMutationRevisionAsync<LocalCustomer>(key.EntityId, serverRevision, ct),
            "CustomerContract" => RebasePersistedDirtyMutationRevisionAsync<LocalCustomerContract>(key.EntityId, serverRevision, ct),
            "Item" => RebasePersistedDirtyMutationRevisionAsync<LocalItem>(key.EntityId, serverRevision, ct),
            "ItemPriceGrade" => RebasePersistedDirtyMutationRevisionAsync<LocalItemPriceGrade>(key.EntityId, serverRevision, ct),
            "TransactionRecord" => RebasePersistedDirtyMutationRevisionAsync<LocalTransaction>(key.EntityId, serverRevision, ct),
            "TransactionAttachment" => RebasePersistedDirtyMutationRevisionAsync<LocalTransactionAttachment>(key.EntityId, serverRevision, ct),
            "InventoryTransfer" => RebasePersistedDirtyMutationRevisionAsync<LocalInventoryTransfer>(key.EntityId, serverRevision, ct),
            "RentalManagementCompany" => RebasePersistedDirtyMutationRevisionAsync<LocalRentalManagementCompany>(key.EntityId, serverRevision, ct),
            "RentalBillingProfile" => RebasePersistedDirtyMutationRevisionAsync<LocalRentalBillingProfile>(key.EntityId, serverRevision, ct),
            "RentalAsset" => RebasePersistedDirtyMutationRevisionAsync<LocalRentalAsset>(key.EntityId, serverRevision, ct),
            "RentalAssetAssignmentHistory" => RebasePersistedDirtyMutationRevisionAsync<LocalRentalAssetAssignmentHistory>(key.EntityId, serverRevision, ct),
            "RentalBillingLog" => RebasePersistedDirtyMutationRevisionAsync<LocalRentalBillingLog>(key.EntityId, serverRevision, ct),
            "Invoice" => RebasePersistedDirtyMutationRevisionAsync<LocalInvoice>(key.EntityId, serverRevision, ct),
            "Payment" => RebasePersistedDirtyMutationRevisionAsync<LocalPayment>(key.EntityId, serverRevision, ct),
            _ => Task.CompletedTask
        };

    private async Task RebasePersistedDirtyMutationRevisionAsync<T>(
        Guid entityId,
        long serverRevision,
        CancellationToken ct)
        where T : class, ILocalSyncEntity
    {
        await _db.Set<T>()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.Id == entityId &&
                entity.IsDirty &&
                entity.Revision < serverRevision)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    entity => entity.Revision,
                    serverRevision),
                ct);
    }

    private static bool TryReadConflictServerRevision(
        string? serverJson,
        out long revision)
    {
        revision = 0;
        if (string.IsNullOrWhiteSpace(serverJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(serverJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(
                        property.Name,
                        nameof(SyncEntityDto.Revision),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.TryGetInt64(out revision);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private bool IsConflictForPreservedTrackedMutation(ConflictLogDto conflict)
    {
        if (!TryBuildConflictEntityKey(conflict, out var key))
            return false;

        return _trackedMutationsPreservedDuringSync.ContainsKey(key);
    }

    private void PreserveTrackedMutationForSync(
        SyncEntityKey key,
        ILocalSyncEntity root,
        IReadOnlyCollection<EntityEntry>? capturedGraphEntries = null)
    {
        if (_trackedMutationsPreservedDuringSync.ContainsKey(key))
            return;

        var graphEntries = capturedGraphEntries?.ToList() ??
                           GetTrackedMutationGraphEntries(root);
        if (graphEntries.All(entry => !ReferenceEquals(entry.Entity, root)))
        {
            graphEntries.Insert(0, _db.Entry(root));
        }
        else
        {
            graphEntries = graphEntries
                .OrderBy(entry => ReferenceEquals(entry.Entity, root) ? 0 : 1)
                .ToList();
        }

        var preservedEntries = graphEntries
            .Select(entry => TrackedEntityPreservation.Capture(
                entry,
                ReferenceEquals(entry.Entity, root) &&
                entry.State == EntityState.Unchanged
                    ? EntityState.Modified
                    : entry.State))
            .ToList();
        var preservation = new TrackedMutationPreservation(root, preservedEntries);
        if (!_trackedMutationsPreservedDuringSync.TryAdd(key, preservation))
            return;

        foreach (var preservedEntry in preservedEntries.AsEnumerable().Reverse())
        {
            var entry = _db.Entry(preservedEntry.Entity);
            if (entry.State != EntityState.Detached)
                entry.State = EntityState.Detached;
        }
    }

    private List<EntityEntry> GetTrackedMutationGraphEntries(ILocalSyncEntity root)
        => _db.ChangeTracker.Entries()
            .Where(entry => IsTrackedMutationGraphEntity(root, entry.Entity))
            .ToList();

    private static bool IsTrackedMutationGraphEntity(
        ILocalSyncEntity root,
        object candidate)
        => ReferenceEquals(root, candidate) ||
           root switch
           {
               LocalInvoice invoice =>
                   candidate is LocalInvoiceLine line &&
                   line.InvoiceId == invoice.Id,
               LocalInventoryTransfer transfer =>
                   candidate is LocalInventoryTransferLine line &&
                   line.TransferId == transfer.Id,
               _ => false
           };

    private void DetachTrackedDuplicateIfSafe(object entity)
    {
        var entry = _db.Entry(entity);
        if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
        {
            throw new InvalidOperationException(
                "동일한 동기화 엔터티에 복원 대기 중인 편집과 별도의 미저장 편집이 동시에 존재합니다.");
        }

        entry.State = EntityState.Detached;
    }

    private bool HasPendingTrackedMutationAfterPush<T>(
        Guid entityId,
        string entityName,
        PreparedMutationSnapshot prepared)
        where T : class, ILocalSyncEntity
    {
        _db.ChangeTracker.DetectChanges();
        var key = new SyncEntityKey(
            NormalizeSyncEntityName(entityName),
            entityId);
        var entry = _db.ChangeTracker.Entries<T>()
            .FirstOrDefault(candidate => candidate.Entity.Id == entityId);
        if (_trackedMutationsPreservedDuringSync.TryGetValue(key, out var existing))
        {
            if (entry is not null && !ReferenceEquals(entry.Entity, existing.Entity))
                DetachTrackedDuplicateIfSafe(entry.Entity);
            return true;
        }

        if (entry is null)
            return false;

        var hasPendingMutation =
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

        if (hasPendingMutation)
            PreserveTrackedMutationForSync(key, entry.Entity);

        return hasPendingMutation;
    }

    private async Task<T?> LoadCurrentSyncEntitySnapshotAsync<T>(
        Guid entityId,
        CancellationToken ct)
        where T : class, ILocalSyncEntity
    {
        if (typeof(T) == typeof(LocalInvoice))
        {
            var invoice = await _db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AsSplitQuery()
                .Include(current => current.Lines)
                .Include(current => current.Payments)
                .SingleOrDefaultAsync(current => current.Id == entityId, ct);
            return invoice is null ? null : (T)(object)invoice;
        }

        if (typeof(T) == typeof(LocalInventoryTransfer))
        {
            var transfer = await _db.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AsSplitQuery()
                .Include(current => current.Lines)
                .SingleOrDefaultAsync(current => current.Id == entityId, ct);
            return transfer is null ? null : (T)(object)transfer;
        }

        return await _db.Set<T>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(current => current.Id == entityId, ct);
    }

    private static SyncEntityDto MapLocalEntityToPreparedMutationDto(ILocalSyncEntity entity)
        => entity switch
        {
            LocalItem value => MapItemForOutboundSync(value),
            LocalTransactionAttachment value =>
                LocalMappings.ToDto(value, ReadTransactionAttachmentContent(value)),
            _ => TryMapLocalEntityToDto(entity)
                 ?? throw new InvalidOperationException(
                     $"지원되지 않는 동기화 엔터티입니다. {entity.GetType().Name}")
        };

    private static string ComputePreparedMutationPayloadHash(
        string entityName,
        SyncEntityDto entity)
    {
        var ignoredProperties = string.Equals(
            NormalizeSyncEntityName(entityName),
            "Invoice",
            StringComparison.OrdinalIgnoreCase)
            ? PreparedInvoiceMutationPayloadIgnoredPropertyNames
            : PreparedMutationPayloadIgnoredPropertyNames;
        var element = JsonSerializer.SerializeToElement((object)entity, entity.GetType());
        var normalized = NormalizeConflictJson(element, ignoredProperties);
        var payload = JsonSerializer.SerializeToUtf8Bytes(normalized);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private async Task ApplyAssignedInvoiceNumberAsync(
        Guid invoiceId,
        string assignedNumber,
        bool isTaxInvoiceNumber,
        IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot> preparedMutationSnapshots,
        IReadOnlySet<SyncEntityKey> locallyModifiedAfterPush,
        CancellationToken ct)
    {
        if (invoiceId == Guid.Empty || string.IsNullOrWhiteSpace(assignedNumber))
            return;

        var key = new SyncEntityKey("Invoice", invoiceId);
        if (!preparedMutationSnapshots.TryGetValue(key, out var prepared))
            return;

        var preparedNumber = isTaxInvoiceNumber
            ? prepared.TaxInvoiceNumber ?? string.Empty
            : prepared.InvoiceNumber ?? string.Empty;
        LocalInvoice? preservedInvoice = null;
        if (_trackedMutationsPreservedDuringSync.TryGetValue(key, out var preservation) &&
            preservation.Entity is LocalInvoice candidate)
        {
            var preservedNumber = isTaxInvoiceNumber
                ? candidate.TaxInvoiceNumber
                : candidate.InvoiceNumber;
            if (!string.Equals(
                    preservedNumber ?? string.Empty,
                    preparedNumber,
                    StringComparison.Ordinal))
            {
                return;
            }

            preservedInvoice = candidate;
        }

        _db.ChangeTracker.DetectChanges();
        var trackedEntry = _db.ChangeTracker.Entries<LocalInvoice>()
            .FirstOrDefault(entry => entry.Entity.Id == invoiceId);
        if (trackedEntry is not null)
        {
            var trackedNumber = isTaxInvoiceNumber
                ? trackedEntry.Entity.TaxInvoiceNumber
                : trackedEntry.Entity.InvoiceNumber;
            if (!string.Equals(
                    trackedNumber ?? string.Empty,
                    preparedNumber,
                    StringComparison.Ordinal))
            {
                return;
            }
        }

        var affected = isTaxInvoiceNumber
            ? await _db.Invoices
                .IgnoreQueryFilters()
                .Where(invoice =>
                    invoice.Id == invoiceId &&
                    invoice.TaxInvoiceNumber == preparedNumber)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        invoice => invoice.TaxInvoiceNumber,
                        assignedNumber),
                    ct)
            : await _db.Invoices
                .IgnoreQueryFilters()
                .Where(invoice =>
                    invoice.Id == invoiceId &&
                    invoice.InvoiceNumber == preparedNumber)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        invoice => invoice.InvoiceNumber,
                        assignedNumber),
                    ct);
        if (affected <= 0)
            return;

        if (preservedInvoice is not null)
        {
            if (isTaxInvoiceNumber)
                preservedInvoice.TaxInvoiceNumber = assignedNumber;
            else
                preservedInvoice.InvoiceNumber = assignedNumber;
        }

        SynchronizeTrackedInvoiceAssignment(
            invoiceId,
            assignedNumber,
            isTaxInvoiceNumber,
            locallyModifiedAfterPush.Contains(key));
    }

    private async Task MarkServerNewerConflictsCleanAsync<T>(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where T : class, ILocalSyncEntity
    {
        await _db.Set<T>().IgnoreQueryFilters()
            .Where(entity => ids.Contains(entity.Id) && entity.IsDirty)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsDirty, false), ct);

        SynchronizeTrackedCleanState<T>(ids);
    }

    private async Task MarkCleanInvoicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;

        await _db.Invoices.IgnoreQueryFilters()
            .Where(e => ids.Contains(e.Id) && e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);

        SynchronizeTrackedCleanState<LocalInvoice>(ids);
    }

    private async Task MarkCleanInventoryTransfersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;

        await _db.InventoryTransfers.IgnoreQueryFilters()
            .Where(e => ids.Contains(e.Id) && e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);

        SynchronizeTrackedCleanState<LocalInventoryTransfer>(ids);
    }

    private async Task<bool> TryApplyServerNewerItemCategoryOptionSnapshotAsync(
        ConflictLogDto conflict,
        CancellationToken ct)
    {
        if (!Guid.TryParse(conflict.EntityId, out var entityId))
            return false;

        if (string.IsNullOrWhiteSpace(conflict.ServerJson))
            return false;

        ItemCategoryOptionDto? dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<ItemCategoryOptionDto>(conflict.ServerJson);
        }
        catch
        {
            return false;
        }

        if (dto is null || dto.Id != entityId)
            return false;

        var snapshot = LocalMappings.ToLocal(dto);
        snapshot.IsDirty = false;

        await _db.ItemCategoryOptions.IgnoreQueryFilters()
            .Where(option => option.Id == snapshot.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(option => option.Name, snapshot.Name)
                    .SetProperty(option => option.SortOrder, snapshot.SortOrder)
                    .SetProperty(option => option.IsSystemDefault, snapshot.IsSystemDefault)
                    .SetProperty(option => option.IsActive, snapshot.IsActive)
                    .SetProperty(option => option.IsDeleted, snapshot.IsDeleted)
                    .SetProperty(option => option.CreatedAtUtc, snapshot.CreatedAtUtc)
                    .SetProperty(option => option.UpdatedAtUtc, snapshot.UpdatedAtUtc)
                    .SetProperty(option => option.Revision, snapshot.Revision)
                    .SetProperty(option => option.IsDirty, false),
                ct);

        SynchronizeTrackedServerSnapshot(snapshot);
        return true;
    }

    private void SynchronizeTrackedCleanState<T>(IReadOnlyCollection<Guid> ids)
        where T : class, ILocalSyncEntity
    {
        if (ids.Count == 0)
            return;

        foreach (var entry in _db.ChangeTracker.Entries<T>())
        {
            if (!ids.Contains(entry.Entity.Id))
                continue;

            entry.Entity.IsDirty = false;
            entry.State = EntityState.Unchanged;
        }
    }

    private void DetachTrackedEntities<T>(IReadOnlySet<Guid> ids)
        where T : class, ILocalSyncEntity
    {
        if (ids.Count == 0)
            return;

        foreach (var entry in _db.ChangeTracker.Entries<T>().ToList())
        {
            if (ids.Contains(entry.Entity.Id))
                entry.State = EntityState.Detached;
        }
    }

    private void SynchronizeTrackedAcceptedRevisionState<T>(
        IReadOnlyDictionary<Guid, SyncAcceptedRevisionDto> revisionsById,
        IReadOnlySet<Guid> cleanedIds,
        IReadOnlySet<Guid> preservedDirtyIds)
        where T : class, ILocalSyncEntity
    {
        if (revisionsById.Count == 0)
            return;

        foreach (var entry in _db.ChangeTracker.Entries<T>().ToList())
        {
            if (!revisionsById.TryGetValue(entry.Entity.Id, out var accepted))
                continue;

            if (preservedDirtyIds.Contains(entry.Entity.Id))
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    // 동일 DbContext에만 존재하는 미저장 편집은 절대 detach/clean하지 않는다.
                    // 서버가 승인한 revision만 rebase하고 사용자의 변경 상태를 그대로 유지한다.
                    if (accepted.Revision > 0 && accepted.Revision >= entry.Entity.Revision)
                        entry.Entity.Revision = accepted.Revision;
                    entry.Entity.IsDirty = true;
                }
                else
                {
                    // 이 인스턴스는 push 준비 당시 값일 수 있다. 새 DbContext가 저장한 최신
                    // payload를 뒤이은 SaveChanges가 덮지 않도록 tracker에서 분리한다.
                    entry.State = EntityState.Detached;
                }
                continue;
            }

            if (!cleanedIds.Contains(entry.Entity.Id))
                continue;

            if (accepted.Revision > 0 && accepted.Revision >= entry.Entity.Revision)
                entry.Entity.Revision = accepted.Revision;

            if (accepted.UpdatedAtUtc != default)
                entry.Entity.UpdatedAtUtc = NormalizeMutationUtc(accepted.UpdatedAtUtc);

            entry.Entity.IsDirty = false;
            entry.State = EntityState.Unchanged;
        }
    }

    private void RestoreTrackedMutationsPreservedDuringSync()
    {
        if (_trackedMutationsPreservedDuringSync.Count == 0 &&
            _trackedNonMutationChangesPreservedDuringSync.Count == 0)
            return;

        try
        {
            var preservedEntries = _trackedMutationsPreservedDuringSync.Values
                .SelectMany(preservation => preservation.Entries)
                .Concat(_trackedNonMutationChangesPreservedDuringSync)
                .ToList();
            var unchangedDuplicates = new List<EntityEntry>();
            foreach (var preservedEntry in preservedEntries)
            {
                foreach (var duplicate in FindTrackedDuplicates(preservedEntry))
                {
                    if (duplicate.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    {
                        throw new InvalidOperationException(
                            "동기화 후 로컬 편집을 복구하는 동안 동일 행의 별도 미저장 편집이 발견되었습니다.");
                    }

                    unchangedDuplicates.Add(duplicate);
                }
            }

            foreach (var duplicate in unchangedDuplicates.Distinct())
                duplicate.State = EntityState.Detached;

            foreach (var preservation in _trackedMutationsPreservedDuringSync.Values)
                preservation.Entity.IsDirty = true;

            foreach (var preservedEntry in preservedEntries)
                RestoreTrackedEntityState(preservedEntry);
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 중 보존한 로컬 편집 상태를 복구하지 못했습니다: {ex.Message}");
            throw;
        }

        _trackedMutationsPreservedDuringSync.Clear();
        _trackedNonMutationChangesPreservedDuringSync.Clear();
    }

    private IReadOnlyCollection<EntityEntry> FindTrackedDuplicates(
        TrackedEntityPreservation preserved)
    {
        if (preserved.PrimaryKeyValues.Count == 0)
            return [];

        return _db.ChangeTracker.Entries()
            .Where(entry =>
                !ReferenceEquals(entry.Entity, preserved.Entity) &&
                entry.Entity.GetType() == preserved.Entity.GetType() &&
                preserved.PrimaryKeyValues.All(key =>
                    Equals(entry.Property(key.Key).CurrentValue, key.Value)))
            .ToList();
    }

    private void RestoreTrackedEntityState(TrackedEntityPreservation preserved)
    {
        var entry = _db.Entry(preserved.Entity);
        if (entry.State == EntityState.Detached)
            _db.Attach(preserved.Entity);

        entry = _db.Entry(preserved.Entity);
        var currentValues = entry.Properties.ToDictionary(
            property => property.Metadata.Name,
            property => property.CurrentValue,
            StringComparer.Ordinal);
        var modifiedProperties = preserved.ModifiedProperties.ToHashSet(
            StringComparer.Ordinal);
        foreach (var (propertyName, currentValue) in currentValues)
        {
            if (preserved.OriginalValues.TryGetValue(propertyName, out var originalValue) &&
                !Equals(currentValue, originalValue))
            {
                modifiedProperties.Add(propertyName);
            }
        }

        entry.State = EntityState.Unchanged;
        foreach (var property in entry.Properties)
        {
            var isModified = modifiedProperties.Contains(
                property.Metadata.Name);
            if (isModified &&
                preserved.OriginalValues.TryGetValue(
                    property.Metadata.Name,
                    out var originalValue))
            {
                property.OriginalValue = originalValue;
            }
            else if (currentValues.TryGetValue(
                         property.Metadata.Name,
                         out var currentValue))
            {
                property.OriginalValue = currentValue;
            }

            if (currentValues.TryGetValue(
                    property.Metadata.Name,
                    out var restoredCurrentValue))
            {
                property.CurrentValue = restoredCurrentValue;
            }
        }

        if (preserved.OriginalState == EntityState.Added)
        {
            entry.State = EntityState.Added;
        }
        else if (preserved.OriginalState == EntityState.Deleted)
        {
            entry.State = EntityState.Deleted;
        }
        else if (preserved.OriginalState == EntityState.Modified)
        {
            foreach (var property in entry.Properties)
            {
                property.IsModified = modifiedProperties.Contains(
                    property.Metadata.Name);
            }
        }
    }

    private void SynchronizeTrackedDirtyState(IReadOnlyCollection<Guid> ids, DateTime updatedAtUtc)
    {
        if (ids.Count == 0)
            return;

        foreach (var entry in _db.ChangeTracker.Entries())
        {
            if (entry.Entity is not ILocalSyncEntity entity || !ids.Contains(entity.Id))
                continue;

            entity.IsDirty = true;
            entity.UpdatedAtUtc = updatedAtUtc;
            if (entry.State == EntityState.Unchanged)
                entry.State = EntityState.Modified;
        }
    }

    private void SynchronizeTrackedServerSnapshot<T>(T snapshot)
        where T : class, ILocalSyncEntity
    {
        foreach (var entry in _db.ChangeTracker.Entries<T>())
        {
            if (entry.Entity.Id != snapshot.Id)
                continue;

            entry.CurrentValues.SetValues(snapshot);
            entry.State = EntityState.Unchanged;
        }
    }

    private void SynchronizeTrackedInvoiceAssignment(
        Guid invoiceId,
        string assignedNumber,
        bool isTaxInvoiceNumber,
        bool detachStaleTrackedEntity)
    {
        foreach (var entry in _db.ChangeTracker.Entries<LocalInvoice>().ToList())
        {
            if (entry.Entity.Id != invoiceId)
                continue;

            if (detachStaleTrackedEntity &&
                entry.State is not (
                    EntityState.Added or
                    EntityState.Modified or
                    EntityState.Deleted))
            {
                entry.State = EntityState.Detached;
                continue;
            }

            if (isTaxInvoiceNumber)
                entry.Entity.TaxInvoiceNumber = assignedNumber;
            else
                entry.Entity.InvoiceNumber = assignedNumber;
        }
    }

    private SyncOperationOwnerBoundary CaptureSyncOperationOwnerBoundary()
        => CaptureSyncOperationOwnerBoundary(
            _session,
            businessDatabaseNameOverride: null);

    private SyncOperationOwnerBoundary CaptureSyncOperationOwnerBoundary(
        SessionState ownerSession,
        string? businessDatabaseNameOverride)
    {
        using var scopeLease = ownerSession.AcquireSyncScopeSnapshotLease();
        return CaptureSyncOperationOwnerBoundaryWithLeaseHeld(
            ownerSession,
            businessDatabaseNameOverride);
    }

    private SyncOperationOwnerBoundary
        CaptureSyncOperationOwnerBoundaryWithLeaseHeld()
        => CaptureSyncOperationOwnerBoundaryWithLeaseHeld(
            _session,
            businessDatabaseNameOverride: null);

    private static SyncOperationOwnerBoundary
        CaptureSyncOperationOwnerBoundaryWithLeaseHeld(
            SessionState ownerSession,
            string? businessDatabaseNameOverride)
        => new(
            ownerSession.SyncScopeEpoch,
            ownerSession.SessionId,
            ownerSession.User?.UserId ?? Guid.Empty,
            TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                ownerSession.AuthenticatedTenantCode),
            TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                ownerSession.TenantCode,
                ownerSession.AuthenticatedTenantCode),
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                ownerSession.OfficeCode),
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                ownerSession.BusinessOfficeCode,
                ownerSession.OfficeCode),
            (ownerSession.ScopeType ?? string.Empty).Trim(),
            TenantScopeCatalog.GetDatabaseName(
                string.IsNullOrWhiteSpace(businessDatabaseNameOverride)
                    ? ownerSession.SelectedBusinessDatabaseName
                    : businessDatabaseNameOverride));

    private bool IsSyncOperationOwnerCurrent(
        SyncOperationOwnerBoundary expected,
        bool scopeLeaseHeld = false)
        => IsSyncOperationOwnerCurrent(
            expected,
            _session,
            businessDatabaseNameOverride: null,
            scopeLeaseHeld);

    private bool IsSyncOperationOwnerCurrent(
        SyncOperationOwnerBoundary expected,
        SessionState ownerSession,
        string? businessDatabaseNameOverride,
        bool scopeLeaseHeld = false)
    {
        var current = scopeLeaseHeld
            ? CaptureSyncOperationOwnerBoundaryWithLeaseHeld(
                ownerSession,
                businessDatabaseNameOverride)
            : CaptureSyncOperationOwnerBoundary(
                ownerSession,
                businessDatabaseNameOverride);
        return expected.ScopeEpoch == current.ScopeEpoch &&
               expected.SessionId == current.SessionId &&
               expected.UserId == current.UserId &&
               string.Equals(
                   expected.AuthenticatedTenantCode,
                   current.AuthenticatedTenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   expected.TenantCode,
                   current.TenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   expected.OfficeCode,
                   current.OfficeCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   expected.BusinessOfficeCode,
                   current.BusinessOfficeCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   expected.ScopeType,
                   current.ScopeType,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   expected.BusinessDatabaseName,
                   current.BusinessDatabaseName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task PullNewAsync(CancellationToken ct)
        => _ = await PullNewCoreAsync(
            rejectPulledDirtyCollisions: false,
            ct);

    private Task<bool> PullNewAuthoritativeOnlyAsync(CancellationToken ct)
        => PullNewCoreAsync(
            rejectPulledDirtyCollisions: true,
            ct);

    private async Task<bool> PullNewCoreAsync(
        bool rejectPulledDirtyCollisions,
        CancellationToken ct)
    {
        var serverMirrorRequestBoundary = rejectPulledDirtyCollisions
            ? _local.CaptureServerMirrorRefreshRequestBoundary()
            : (LocalStateService.ServerMirrorRefreshRequestBoundary?)null;
        var revStr = await _local.GetSettingAsync("LastSyncRevision", ct) ?? "0";
        var sinceRev = long.TryParse(revStr, out var r) ? r : 0L;
        var pendingDirtyCount = await _local.CountDirtyAsync(ct);
        var hasPendingDirty = pendingDirtyCount > 0;
        var requiresMirrorRefresh = await _local.IsServerMirrorRefreshRequiredAsync(ct);
        var operationOwner = CaptureSyncOperationOwnerBoundary();

        if (await HasPendingReconciliationForOperationOwnerAsync(operationOwner, ct))
        {
            if (rejectPulledDirtyCollisions)
                return false;

            throw new SyncPullBlockedException(
                "A current-owner non-acknowledged mutation still requires reconciliation before a server pull can be applied.");
        }

        if (rejectPulledDirtyCollisions &&
            (requiresMirrorRefresh ||
             sinceRev <= 0 ||
             serverMirrorRequestBoundary.HasValue &&
             _local.HasServerMirrorRefreshRequestSince(
                 serverMirrorRequestBoundary.Value)))
        {
            return false;
        }

        if (!requiresMirrorRefresh && !hasPendingDirty && await _local.HasLikelyCorruptedPrimaryWorkCacheAsync(_session, ct))
        {
            await _local.MarkServerMirrorRefreshRequiredAsync(ct);
            requiresMirrorRefresh = true;
        }

        if (requiresMirrorRefresh && !hasPendingDirty)
        {
            AppLogger.Info("SYNC", "버전 정비 후 범위 불일치 데이터를 정리하기 위해 중앙 서버 기준 전체 캐시 재구성을 수행합니다.");
            if (!await TryRefreshSharedMirrorCoreAsync(ct, preserveTrackedChanges: true))
                throw new InvalidOperationException("중앙 서버 기준 캐시 재구성에 실패했습니다.");

            return true;
        }

        if (requiresMirrorRefresh && hasPendingDirty)
            AppLogger.Warn("SYNC", "전체 캐시 재구성 예약이 남아 있지만 미동기화 변경이 있어 이번 동기화에서는 유지합니다.");

        if (sinceRev <= 0 && !hasPendingDirty)
        {
            AppLogger.Info("SYNC", "마지막 동기화 리비전이 없어 중앙 서버 기준 전체 캐시 재구성을 사용합니다.");
            if (!await TryRefreshSharedMirrorCoreAsync(ct, preserveTrackedChanges: true))
                throw new InvalidOperationException("중앙 서버 기준 캐시 재구성에 실패했습니다.");

            return true;
        }

        var ownerTrackedStateBeforePull =
            CaptureIsolatedOwnerTrackedState();
        if (rejectPulledDirtyCollisions &&
            HasPendingTrackedUserChanges())
        {
            return false;
        }
        var ownerTrackedChangesArrivedDuringPull = false;
        SyncPullResponse? pull;
        try
        {
            pull = await _api.PullAsync(
                sinceRev,
                operationOwner.BusinessDatabaseName,
                ct);
        }
        finally
        {
            PreservePendingTrackedChangesForSync();
            ownerTrackedChangesArrivedDuringPull =
                HasIsolatedOwnerTrackedChangesSinceBoundary(
                    ownerTrackedStateBeforePull);
        }

        if (pull is null)
            throw new HttpRequestException("서버 응답이 비어 있어 동기화 다운로드를 완료하지 못했습니다.");
        if (!IsSyncOperationOwnerCurrent(operationOwner))
        {
            await DeferPullForChangedOperationOwnerAsync();
            return false;
        }
        if (ownerTrackedChangesArrivedDuringPull)
        {
            await DeferPullForConcurrentOwnerEditAsync();
            return false;
        }
        if (rejectPulledDirtyCollisions &&
            pull.CurrentServerRevision < sinceRev)
        {
            await _local.MarkServerMirrorRefreshRequiredAsync(ct);
            return false;
        }
        if (serverMirrorRequestBoundary.HasValue &&
            _local.HasServerMirrorRefreshRequestSince(
                serverMirrorRequestBoundary.Value))
        {
            return false;
        }
        if (await HasPendingReconciliationForOperationOwnerAsync(operationOwner, ct))
        {
            if (rejectPulledDirtyCollisions)
                return false;

            throw new SyncPullBlockedException(
                "A current-owner non-acknowledged mutation appeared while the server pull was in flight.");
        }

        pendingDirtyCount = await _local.CountDirtyAsync(ct);
        hasPendingDirty = pendingDirtyCount > 0;
        LastPullChangeCount = CountPullChanges(pull);

        try
        {
            PreservePendingTrackedChangesForSync();
            _db.ChangeTracker.Clear();
            using (_local.SuppressSyncDispatch())
            {
                var applied = await TryApplyPullAtomicallyAsync(
                    pull,
                    sinceRev,
                    ct,
                    updateSyncRevision: true,
                    expectedOwner: operationOwner,
                    rejectPulledDirtyCollisions:
                        rejectPulledDirtyCollisions,
                    serverMirrorRequestBoundary:
                        serverMirrorRequestBoundary);
                if (!applied)
                {
                    await DeferPullForChangedOperationOwnerAsync();
                    return false;
                }
            }
        }
        catch (DbUpdateConcurrencyException ex)
            when (_itemWarehouseStockReplayPullGuard is null)
        {
            _db.ChangeTracker.Clear();

            if (hasPendingDirty)
            {
                await DeferPullRefreshUntilDirtyChangesArePushedAsync(pendingDirtyCount, ex);
                return false;
            }

            AppLogger.Info("SYNC", $"증분 pull 반영 중 동시성 충돌이 발생해 전체 캐시 재구성을 수행합니다: {ex.Message}");
            var recovered = await TryRefreshSharedMirrorCoreAsync(ct, preserveTrackedChanges: true);
            await TryRecordDiagnosticAsync(
                phase: "pull",
                rawMessage: $"증분 pull 반영 중 동시성 충돌: {ex.Message}",
                exception: ex,
                severity: recovered ? "Info" : "Warning",
                recoveryAttempted: true,
                recoverySucceeded: recovered);

            if (!recovered)
                throw;
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            if (_itemWarehouseStockReplayPullGuard is not null &&
                ex is not SyncPullBlockedException &&
                !(ex is OperationCanceledException &&
                  ct.IsCancellationRequested))
            {
                throw new SyncPullBlockedException(
                    "재고 snapshot guard가 활성화된 정상 pull 적용 중 DB 동시성 또는 저장 실패가 발생해 후속 pull을 중단했습니다.",
                    ex);
            }

            throw;
        }

        return true;
    }

    private async Task ApplyPullAsync(
        SyncPullResponse pull,
        long sinceRev,
        CancellationToken ct,
        bool updateSyncRevision = true)
    {
        _ = await TryApplyPullAtomicallyAsync(
            pull,
            sinceRev,
            ct,
            updateSyncRevision,
            expectedOwner: null);
    }

    private async Task<bool> TryApplyPullAtomicallyAsync(
        SyncPullResponse pull,
        long sinceRev,
        CancellationToken ct,
        bool updateSyncRevision,
        SyncOperationOwnerBoundary? expectedOwner,
        bool applyCompleteItemWarehouseStockSnapshot = true,
        Action? markOwnerRefreshScheduled = null,
        bool rejectPulledDirtyCollisions = false,
        LocalStateService.ServerMirrorRefreshRequestBoundary?
            serverMirrorRequestBoundary = null)
        => await TryApplyPullAtomicallyCoreAsync(
            pull,
            sinceRev,
            ct,
            updateSyncRevision,
            expectedOwner,
            applyCompleteItemWarehouseStockSnapshot,
            markOwnerRefreshScheduled,
            replaceLocalBusinessCache: false,
            rejectPulledDirtyCollisions:
                rejectPulledDirtyCollisions,
            serverMirrorRequestBoundary:
                serverMirrorRequestBoundary);

    private async Task<bool> TryApplyPullAtomicallyCoreAsync(
        SyncPullResponse pull,
        long sinceRev,
        CancellationToken ct,
        bool updateSyncRevision,
        SyncOperationOwnerBoundary? expectedOwner,
        bool applyCompleteItemWarehouseStockSnapshot = true,
        Action? markOwnerRefreshScheduled = null,
        bool replaceLocalBusinessCache = false,
        bool rejectPulledDirtyCollisions = false,
        LocalStateService.ServerMirrorRefreshRequestBoundary?
            serverMirrorRequestBoundary = null)
    {
        if (expectedOwner is not null &&
            !IsSyncOperationOwnerCurrent(expectedOwner))
        {
            return false;
        }

        await RecoverIncompleteAttachmentFileJournalsAsync(ct);
        await using var transaction =
            await _db.BeginRuntimeMutationTransactionAsync(ct);
        _itemWarehouseStockReplayGuardValidatedForPullTransaction =
            false;
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);
        var commitAttempted = false;
        var itemInvoiceHistoryChanged = false;
        using var inventoryStateChangeCapture =
            _local.CaptureInventoryStateChanges();

        try
        {
            if (expectedOwner is not null &&
                !IsSyncOperationOwnerCurrent(expectedOwner))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                return false;
            }

            if (rejectPulledDirtyCollisions &&
                (HasPendingTrackedUserChanges() ||
                 await _local.IsServerMirrorRefreshRequiredAsync(ct) ||
                 serverMirrorRequestBoundary.HasValue &&
                 _local.HasServerMirrorRefreshRequestSince(
                     serverMirrorRequestBoundary.Value)))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                return false;
            }

            if (expectedOwner is not null &&
                await HasPendingReconciliationForOperationOwnerAsync(
                    expectedOwner,
                    ct))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                if (rejectPulledDirtyCollisions)
                    return false;

                throw new SyncPullBlockedException(
                    "A current-owner non-acknowledged mutation requires reconciliation before pull application.");
            }

            if (rejectPulledDirtyCollisions &&
                await HasAuthoritativePullDirtyCollisionAsync(
                    pull,
                    applyCompleteItemWarehouseStockSnapshot,
                    ct))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                return false;
            }

            if (applyCompleteItemWarehouseStockSnapshot)
            {
                await EnsureItemWarehouseStockReplayPullGuardUnchangedAsync(
                    ct);
                _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                    _itemWarehouseStockReplayPullGuard is not null;
            }

            if (replaceLocalBusinessCache)
            {
                await _local.ResetBusinessDataCacheWithAttachmentJournalAsync(
                    attachmentFiles,
                    ct);
            }

            var purgeOwner =
                expectedOwner ?? CaptureSyncOperationOwnerBoundary();
            itemInvoiceHistoryChanged = await ApplyPullInternalAsync(
                pull,
                sinceRev,
                ct,
                updateSyncRevision,
                attachmentFiles,
                publishRentalStateChanges: false,
                applyCompleteItemWarehouseStockSnapshot:
                    applyCompleteItemWarehouseStockSnapshot,
                purgeOwner);

            if (rejectPulledDirtyCollisions &&
                (HasPendingTrackedUserChanges() ||
                 await _local.IsServerMirrorRefreshRequiredAsync(ct) ||
                 serverMirrorRequestBoundary.HasValue &&
                 _local.HasServerMirrorRefreshRequestSince(
                     serverMirrorRequestBoundary.Value)))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                return false;
            }

            var committed =
                await CommitAttachmentTransactionUnderOwnerLeaseAsync(
                    transaction,
                    attachmentFiles,
                    expectedOwner,
                    () => commitAttempted = true,
                    ct,
                    additionalCommitGuard:
                        rejectPulledDirtyCollisions
                            ? async guardCt =>
                                !HasPendingTrackedUserChanges() &&
                                !await _local.IsServerMirrorRefreshRequiredAsync(guardCt) &&
                                (!serverMirrorRequestBoundary.HasValue ||
                                 !_local.HasServerMirrorRefreshRequestSince(
                                     serverMirrorRequestBoundary.Value))
                            : null);
            _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                false;
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                return false;
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await attachmentFiles.CompleteAfterDatabaseCommitAsync(
                _db,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                false;
            var commitResolution = AttachmentCommitResolution.RolledBack;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                AppLogger.Error(
                    "ATTACHMENT",
                    "증분 pull 실패 후 DB 롤백 결과를 확정하지 못했습니다.",
                    rollbackException);
            }

            if (!commitAttempted)
            {
                attachmentFiles.Rollback();
            }
            else
            {
                commitResolution = await attachmentFiles.ResolveCommitAmbiguityAsync(
                    _db,
                    CancellationToken.None);
            }

            _db.ChangeTracker.Clear();
            if (commitResolution != AttachmentCommitResolution.Committed)
            {
                if (_itemWarehouseStockReplayPullGuard is not null &&
                    ex is not SyncPullBlockedException &&
                    !(ex is OperationCanceledException &&
                      ct.IsCancellationRequested))
                {
                    throw new SyncPullBlockedException(
                        "재고 snapshot guard가 활성화된 pull 적용 중 DB 동시성 또는 저장 실패가 발생해 후속 pull을 중단했습니다.",
                        ex);
                }

                throw;
            }

            // The ambiguity resolver completes the journal through an
            // independent context. Ensure this context is transaction-free
            // before the owner-bound post-commit effects below.
            await transaction.DisposeAsync().ConfigureAwait(false);
        }

        if (expectedOwner is not null)
        {
            var effectsPublished =
                await TryRunPostCommitEffectsForCurrentOwnerAsync(
                    expectedOwner,
                    _ => Task.CompletedTask,
                    () => TryPublishOwnerBoundRentalState(
                        expectedOwner,
                        pull.RentalAssets.Select(asset => asset.Id),
                        pull.RentalBillingProfiles.Select(profile => profile.Id)),
                    () => TryPublishOwnerBoundItemInvoiceHistory(
                        expectedOwner,
                        itemInvoiceHistoryChanged),
                    () => TryPublishOwnerBoundInventoryState(
                        expectedOwner,
                        inventoryStateChangeCapture.HasChanges));
            if (!effectsPublished)
            {
                _db.ChangeTracker.Clear();
                await ScheduleCurrentOwnerRefreshAfterCommittedOwnerChangeAsync();
                markOwnerRefreshScheduled?.Invoke();
            }

            return true;
        }

        _rental.PublishSynchronizedStateChanges(
            pull.RentalAssets.Select(asset => asset.Id),
            pull.RentalBillingProfiles.Select(profile => profile.Id));
        if (itemInvoiceHistoryChanged)
        {
            try
            {
                _local.TryPublishItemInvoiceHistoryChanged();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "SYNC",
                    "커밋된 증분 pull의 품목별 전표이력 변경 알림 중 오류가 발생했습니다.",
                    ex);
            }
        }
        if (inventoryStateChangeCapture.HasChanges)
        {
            try
            {
                _local.TryPublishInventoryStateChanged();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "SYNC",
                    "커밋된 증분 pull의 재고 변경 알림 중 오류가 발생했습니다.",
                    ex);
            }
        }
        return true;
    }

    private async Task<bool> CommitAttachmentTransactionUnderOwnerLeaseAsync(
        IDbContextTransaction transaction,
        AttachmentFileJournal attachmentFiles,
        SyncOperationOwnerBoundary? expectedOwner,
        Action markCommitAttempted,
        CancellationToken ct,
        SessionState? ownerSession = null,
        string? businessDatabaseNameOverride = null,
        Func<CancellationToken, Task<bool>>? additionalCommitGuard = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(attachmentFiles);
        ArgumentNullException.ThrowIfNull(markCommitAttempted);

        var commitOwnerSession = ownerSession ?? _session;
        IDisposable? commitScopeLease = null;
        if (expectedOwner is not null)
        {
            commitScopeLease = await commitOwnerSession
                .AcquireSyncScopeCommitLeaseAsync(ct)
                .ConfigureAwait(false);
        }

        using (commitScopeLease)
        {
            if (_itemWarehouseStockReplayPullGuard is not null &&
                !_itemWarehouseStockReplayGuardValidatedForPullTransaction)
            {
                throw new SyncPullBlockedException(
                    "재고 snapshot guard가 pull 적용 트랜잭션에서 검증되지 않아 커밋을 중단했습니다.");
            }

            if (expectedOwner is not null &&
                !IsSyncOperationOwnerCurrent(
                    expectedOwner,
                    commitOwnerSession,
                    businessDatabaseNameOverride,
                    scopeLeaseHeld: true))
            {
                return false;
            }

            await attachmentFiles
                .StageCommitEvidenceAsync(_db, ct)
                .ConfigureAwait(false);
            attachmentFiles.Promote();
            if (additionalCommitGuard is not null &&
                BeforeStrictPullCommitGuardAsyncForTesting is not null)
            {
                await BeforeStrictPullCommitGuardAsyncForTesting(ct)
                    .ConfigureAwait(false);
            }
            if (additionalCommitGuard is not null &&
                !await additionalCommitGuard(ct).ConfigureAwait(false))
            {
                return false;
            }
            markCommitAttempted();

            // Session mutators are synchronous because UI selection changes are
            // immediate. Do not capture that UI context while holding the commit
            // lease: the continuation must release the lease before a blocked UI
            // mutation can resume.
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        if (AfterAttachmentCommitAsyncForTesting is not null)
        {
            await AfterAttachmentCommitAsyncForTesting(
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> ApplyPullInternalAsync(
        SyncPullResponse pull,
        long sinceRev,
        CancellationToken ct,
        bool updateSyncRevision,
        AttachmentFileJournal? attachmentFileJournal,
        bool publishRentalStateChanges,
        bool applyCompleteItemWarehouseStockSnapshot = true,
        SyncOperationOwnerBoundary? purgeOwner = null)
    {
        await ApplyItemCatalogExtensionCapabilityAsync(
            pull.ItemCatalogExtensionVersion,
            ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledCompanyProfilesAsync(pull.CompanyProfiles, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledUnitsAsync(pull.Units, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.CustomerCategories, _db.CustomerCategories, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledSelectionOptionsAsync(pull.PriceGradeOptions, _db.PriceGradeOptions, LocalMappings.ToLocal, option => option.Name, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledSelectionOptionsAsync(pull.TradeTypeOptions, _db.TradeTypeOptions, LocalMappings.ToLocal, option => option.Name, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledSelectionOptionsAsync(pull.ItemCategoryOptions, _db.ItemCategoryOptions, LocalMappings.ToLocal, option => option.Name, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.CustomerMasters, _db.CustomerMasters, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.Customers, _db.Customers, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledCustomerContractsAsync(pull.CustomerContracts, ct);
        _db.ChangeTracker.Clear();
        var pulledItemAliasMap = await UpsertPulledItemsAsync(
            pull.Items,
            ct,
            preserveExistingInventoryStock:
                !applyCompleteItemWarehouseStockSnapshot);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.ItemPriceGrades, _db.ItemPriceGrades, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        if (applyCompleteItemWarehouseStockSnapshot)
        {
            if (!_itemWarehouseStockReplayGuardValidatedBeforeMirrorReset)
            {
                await EnsureItemWarehouseStockReplayPullGuardUnchangedAsync(
                    ct);
            }
            await UpsertPulledItemWarehouseStocksAsync(
                pull.ItemWarehouseStocks,
                ct);
            _db.ChangeTracker.Clear();
        }
        var transactionSideEffects = await UpsertPulledTransactionsAsync(pull.Transactions, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledTransactionAttachmentsAsync(
            pull.TransactionAttachments,
            ct,
            attachmentFileJournal);
        _db.ChangeTracker.Clear();
        await UpsertPulledInventoryTransfersAsync(pull.InventoryTransfers, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledRentalManagementCompaniesAsync(pull.RentalManagementCompanies, ct);
        _db.ChangeTracker.Clear();
        RemapPulledRentalBillingTemplateCatalogItemReferences(
            pull.RentalBillingProfiles,
            pulledItemAliasMap);
        await UpsertPulledRentalBillingProfilesAsync(pull.RentalBillingProfiles, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledRentalAssetsAsync(pull.RentalAssets, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledRentalAssetAssignmentHistoriesAsync(pull.RentalAssetAssignmentHistories, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.RentalBillingLogs, _db.RentalBillingLogs, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledInvoicesAsync(pull.Invoices, ct);
        _db.ChangeTracker.Clear();
        await ApplyPulledTransactionSideEffectsAsync(transactionSideEffects, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledPaymentsAsync(pull.Payments, ct);
        _db.ChangeTracker.Clear();
        var invoicePurgeApplied = await ApplyPulledPurgeRecordsAsync(
            pull.PurgeRecords,
            purgeOwner ?? CaptureSyncOperationOwnerBoundary(),
            ct,
            attachmentFileJournal);
        if (AfterPulledPurgeRecordsAsyncForTesting is not null)
            await AfterPulledPurgeRecordsAsyncForTesting(ct);
        _db.ChangeTracker.Clear();

        if (publishRentalStateChanges)
        {
            _rental.PublishSynchronizedStateChanges(
                pull.RentalAssets.Select(asset => asset.Id),
                pull.RentalBillingProfiles.Select(profile => profile.Id));
        }

        if (updateSyncRevision && pull.LatestRevision > sinceRev)
        {
            await _local.SetSettingAsync(
                "LastSyncRevision",
                pull.LatestRevision.ToString(CultureInfo.InvariantCulture),
                ct);
        }

        return ShouldPublishItemInvoiceHistoryChanged(
            pull.Invoices.Count > 0,
            invoicePurgeApplied);
    }

    private async Task ApplyItemCatalogExtensionCapabilityAsync(
        int observedVersion,
        CancellationToken ct)
    {
        var rawPreviousVersion = await _local.GetSettingAsync(
            ItemCatalogExtensionVersionSettingKey,
            ct);
        var previousVersion =
            int.TryParse(
                rawPreviousVersion,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedPreviousVersion)
                ? Math.Max(0, parsedPreviousVersion)
                : 0;
        var normalizedObservedVersion = Math.Max(0, observedVersion);

        if (normalizedObservedVersion == 0)
        {
            if (rawPreviousVersion is not null &&
                (previousVersion != 0 ||
                 !string.Equals(
                     rawPreviousVersion.Trim(),
                     "0",
                     StringComparison.Ordinal)))
            {
                await _local.SetSettingAsync(
                    ItemCatalogExtensionVersionSettingKey,
                    "0",
                    ct);
            }

            return;
        }

        if (previousVersion == 0)
        {
            await _db.Items
                .IgnoreQueryFilters()
                .Where(item =>
                    !item.IsDeleted &&
                    !item.IsDirty &&
                    item.CatalogExtensionSyncPending)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.IsDirty,
                        true),
                    ct);
        }

        if (rawPreviousVersion is null ||
            previousVersion != normalizedObservedVersion)
        {
            await _local.SetSettingAsync(
                ItemCatalogExtensionVersionSettingKey,
                normalizedObservedVersion.ToString(CultureInfo.InvariantCulture),
                ct);
        }
    }

    private static int CountPullChanges(SyncPullResponse pull)
        => pull.CompanyProfiles.Count
           + pull.Units.Count
           + pull.CustomerCategories.Count
           + pull.PriceGradeOptions.Count
           + pull.TradeTypeOptions.Count
           + pull.ItemCategoryOptions.Count
           + pull.CustomerMasters.Count
           + pull.Customers.Count
           + pull.CustomerContracts.Count
           + pull.Items.Count
           + pull.ItemPriceGrades.Count
           + pull.ItemWarehouseStocks.Count
           + pull.Transactions.Count
           + pull.TransactionAttachments.Count
           + pull.InventoryTransfers.Count
           + pull.RentalManagementCompanies.Count
           + pull.RentalBillingProfiles.Count
           + pull.RentalAssets.Count
           + pull.RentalAssetAssignmentHistories.Count
           + pull.RentalBillingLogs.Count
           + pull.Invoices.Count
           + pull.Payments.Count
           + pull.PurgeRecords.Count;

    private async Task<bool> HasAuthoritativePullDirtyCollisionAsync(
        SyncPullResponse pull,
        bool applyCompleteItemWarehouseStockSnapshot,
        CancellationToken ct)
    {
        if (pull.ItemCatalogExtensionVersion > 0 &&
            (applyCompleteItemWarehouseStockSnapshot ||
             pull.Items.Count > 0 ||
             pull.ItemPriceGrades.Count > 0 ||
             pull.ItemWarehouseStocks.Count > 0))
        {
            var rawCatalogVersion = await _local.GetSettingAsync(
                ItemCatalogExtensionVersionSettingKey,
                ct);
            var previousCatalogVersion = int.TryParse(
                rawCatalogVersion,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedCatalogVersion)
                    ? Math.Max(0, parsedCatalogVersion)
                    : 0;
            if (previousCatalogVersion == 0 &&
                await _db.Items.IgnoreQueryFilters()
                    .AnyAsync(item =>
                        !item.IsDeleted &&
                        !item.IsDirty &&
                        item.CatalogExtensionSyncPending,
                        ct))
            {
                return true;
            }
        }

        if (await HasDirtyPulledEntityAsync(pull.CompanyProfiles, _db.CompanyProfiles, ct) ||
            await HasDirtyPulledEntityAsync(pull.Units, _db.Units, ct) ||
            await HasDirtyPulledEntityAsync(pull.CustomerCategories, _db.CustomerCategories, ct) ||
            await HasDirtyPulledEntityAsync(pull.PriceGradeOptions, _db.PriceGradeOptions, ct) ||
            await HasDirtyPulledEntityAsync(pull.TradeTypeOptions, _db.TradeTypeOptions, ct) ||
            await HasDirtyPulledEntityAsync(pull.ItemCategoryOptions, _db.ItemCategoryOptions, ct) ||
            await HasDirtyPulledEntityAsync(pull.CustomerMasters, _db.CustomerMasters, ct) ||
            await HasDirtyPulledEntityAsync(pull.Customers, _db.Customers, ct) ||
            await HasDirtyPulledEntityAsync(pull.CustomerContracts, _db.CustomerContracts, ct) ||
            await HasDirtyPulledEntityAsync(pull.Items, _db.Items, ct) ||
            await HasDirtyPulledEntityAsync(pull.ItemPriceGrades, _db.ItemPriceGrades, ct) ||
            await HasDirtyPulledEntityAsync(pull.Transactions, _db.Transactions, ct) ||
            await HasDirtyPulledEntityAsync(pull.TransactionAttachments, _db.TransactionAttachments, ct) ||
            await HasDirtyPulledEntityAsync(pull.InventoryTransfers, _db.InventoryTransfers, ct) ||
            await HasDirtyPulledEntityAsync(pull.RentalManagementCompanies, _db.RentalManagementCompanies, ct) ||
            await HasDirtyPulledEntityAsync(pull.RentalBillingProfiles, _db.RentalBillingProfiles, ct) ||
            await HasDirtyPulledEntityAsync(pull.RentalAssets, _db.RentalAssets, ct) ||
            await HasDirtyPulledEntityAsync(pull.RentalAssetAssignmentHistories, _db.RentalAssetAssignmentHistories, ct) ||
            await HasDirtyPulledEntityAsync(pull.RentalBillingLogs, _db.RentalBillingLogs, ct) ||
            await HasDirtyPulledEntityAsync(pull.Invoices, _db.Invoices, ct) ||
            await HasDirtyPulledEntityAsync(pull.Payments, _db.Payments, ct))
        {
            return true;
        }

        if (applyCompleteItemWarehouseStockSnapshot &&
            await _db.Items.IgnoreQueryFilters()
                .AnyAsync(entity => entity.IsDirty, ct))
        {
            return true;
        }

        if (pull.Invoices.Count > 0 &&
            await _db.Invoices.IgnoreQueryFilters()
                .AnyAsync(entity => entity.IsDirty, ct))
        {
            return true;
        }

        // Strict authoritative roots include direct DTOs plus every local
        // sync entity that pull application can mirror or cascade from them:
        // transaction/payment twins, invoice nested payments and linked
        // transactions, attachment parents, invoice/transfer item roots,
        // rental-profile links, and recycle purge targets.
        var pulledTransactionIds = pull.Transactions
            .Select(transaction => transaction.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (await HasDirtyLocalEntityAsync(
                pulledTransactionIds,
                _db.Payments,
                ct))
        {
            return true;
        }

        var pulledPaymentIds = pull.Payments
            .Concat(pull.Invoices.SelectMany(invoice => invoice.Payments))
            .Select(payment => payment.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (await HasDirtyLocalEntityAsync(
                pulledPaymentIds,
                _db.Payments,
                ct) ||
            await HasDirtyLocalEntityAsync(
                pulledPaymentIds,
                _db.Transactions,
                ct))
        {
            return true;
        }

        var attachmentParentIds = pull.TransactionAttachments
            .Select(attachment => attachment.TransactionId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (await HasDirtyLocalEntityAsync(
                attachmentParentIds,
                _db.Transactions,
                ct))
        {
            return true;
        }

        var pulledInvoiceIds = pull.Invoices
            .Select(invoice => invoice.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        foreach (var batch in pulledInvoiceIds.Chunk(PullQueryContainsBatchSize))
        {
            var ids = batch;
            if (await _db.Payments.IgnoreQueryFilters()
                    .AnyAsync(payment =>
                        ids.Contains(payment.InvoiceId) &&
                        payment.IsDirty,
                        ct) ||
                await _db.Transactions.IgnoreQueryFilters()
                    .AnyAsync(transaction =>
                        transaction.LinkedInvoiceId.HasValue &&
                        ids.Contains(transaction.LinkedInvoiceId.Value) &&
                        transaction.IsDirty,
                        ct))
            {
                return true;
            }
        }

        var pulledItemRootIds = pull.Invoices
            .SelectMany(invoice => invoice.Lines)
            .Select(line => line.ItemId)
            .Concat(pull.InventoryTransfers
                .SelectMany(transfer => transfer.Lines)
                .Select(line => line.ItemId))
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (await HasDirtyLocalEntityAsync(
                pulledItemRootIds,
                _db.Items,
                ct))
        {
            return true;
        }

        if (pull.Items.Count > 0 &&
            (await _db.Items.IgnoreQueryFilters()
                 .AnyAsync(entity => entity.IsDirty, ct) ||
             await _db.ItemPriceGrades.IgnoreQueryFilters()
                 .AnyAsync(entity => entity.IsDirty, ct) ||
             await _db.Invoices.IgnoreQueryFilters()
                 .AnyAsync(entity => entity.IsDirty, ct) ||
             await _db.InventoryTransfers.IgnoreQueryFilters()
                 .AnyAsync(entity => entity.IsDirty, ct) ||
             await _db.RentalAssets.IgnoreQueryFilters()
                 .AnyAsync(entity => entity.IsDirty, ct) ||
             await _db.RentalBillingProfiles.IgnoreQueryFilters()
                 .AnyAsync(entity => entity.IsDirty, ct)))
        {
            return true;
        }

        if ((pull.Transactions.Count > 0 ||
             pull.Invoices.Count > 0 ||
             pull.Payments.Count > 0) &&
            await _db.RentalBillingProfiles.IgnoreQueryFilters()
                .AnyAsync(entity => entity.IsDirty, ct))
        {
            return true;
        }

        // These upserts can canonicalize/default-match a different local row
        // than the incoming ID. Treat any dirty row in that canonical root as
        // a strict collision rather than risk a silent skip or overwrite.
        if (pull.CompanyProfiles.Count > 0 &&
                await _db.CompanyProfiles.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct) ||
            pull.Units.Count > 0 &&
                await _db.Units.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct) ||
            pull.PriceGradeOptions.Count > 0 &&
                await _db.PriceGradeOptions.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct) ||
            pull.TradeTypeOptions.Count > 0 &&
                await _db.TradeTypeOptions.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct) ||
            pull.ItemCategoryOptions.Count > 0 &&
                await _db.ItemCategoryOptions.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct) ||
            pull.RentalManagementCompanies.Count > 0 &&
                await _db.RentalManagementCompanies.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct) ||
            pull.RentalBillingProfiles.Count > 0 &&
                await _db.RentalBillingProfiles.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct) ||
            pull.RentalAssets.Count > 0 &&
                await _db.RentalAssets.IgnoreQueryFilters()
                    .AnyAsync(entity => entity.IsDirty, ct))
        {
            return true;
        }

        var pulledRentalProfileIds = pull.RentalBillingProfiles
            .Select(profile => profile.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        foreach (var batch in pulledRentalProfileIds.Chunk(PullQueryContainsBatchSize))
        {
            var ids = batch;
            if (await _db.Transactions.IgnoreQueryFilters()
                    .AnyAsync(transaction =>
                        transaction.LinkedRentalBillingProfileId.HasValue &&
                        ids.Contains(transaction.LinkedRentalBillingProfileId.Value) &&
                        transaction.IsDirty,
                        ct) ||
                await _db.Invoices.IgnoreQueryFilters()
                    .AnyAsync(invoice =>
                        invoice.LinkedRentalBillingProfileId.HasValue &&
                        ids.Contains(invoice.LinkedRentalBillingProfileId.Value) &&
                        invoice.IsDirty,
                        ct) ||
                await _db.RentalAssets.IgnoreQueryFilters()
                    .AnyAsync(asset =>
                        asset.BillingProfileId.HasValue &&
                        ids.Contains(asset.BillingProfileId.Value) &&
                        asset.IsDirty,
                        ct))
            {
                return true;
            }
        }

        foreach (var purgeRecord in pull.PurgeRecords)
        {
            if (await HasDirtyRecycleBinPurgeTargetAsync(
                    purgeRecord.Kind,
                    purgeRecord.EntityId,
                    ct))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HasDirtyRecycleBinPurgeTargetAsync(
        string? kind,
        Guid entityId,
        CancellationToken ct)
    {
        if (entityId == Guid.Empty)
            return false;

        var normalizedKind = new string((kind ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalizedKind switch
        {
            "customer" => await HasDirtyLocalEntityAsync([entityId], _db.Customers, ct),
            "customercontract" => await HasDirtyLocalEntityAsync([entityId], _db.CustomerContracts, ct),
            "item" => await HasDirtyLocalEntityAsync([entityId], _db.Items, ct),
            "companyprofile" => await HasDirtyLocalEntityAsync([entityId], _db.CompanyProfiles, ct),
            "customercategory" => await HasDirtyLocalEntityAsync([entityId], _db.CustomerCategories, ct),
            "pricegradeoption" => await HasDirtyLocalEntityAsync([entityId], _db.PriceGradeOptions, ct),
            "tradetypeoption" => await HasDirtyLocalEntityAsync([entityId], _db.TradeTypeOptions, ct),
            "itemcategoryoption" => await HasDirtyLocalEntityAsync([entityId], _db.ItemCategoryOptions, ct),
            "invoice" => await HasDirtyLocalEntityAsync([entityId], _db.Invoices, ct),
            "payment" => await HasDirtyLocalEntityAsync([entityId], _db.Payments, ct),
            "transaction" => await HasDirtyLocalEntityAsync([entityId], _db.Transactions, ct),
            "inventorytransfer" => await HasDirtyLocalEntityAsync([entityId], _db.InventoryTransfers, ct),
            "rentalmanagementcompany" => await HasDirtyLocalEntityAsync([entityId], _db.RentalManagementCompanies, ct),
            "rentalbillingprofile" => await HasDirtyLocalEntityAsync([entityId], _db.RentalBillingProfiles, ct),
            "rentalasset" => await HasDirtyLocalEntityAsync([entityId], _db.RentalAssets, ct),
            "rentalbillinglog" => await HasDirtyLocalEntityAsync([entityId], _db.RentalBillingLogs, ct),
            _ => true
        };
    }

    private async Task<bool> HasDirtyPulledEntityAsync<TDto, TLocal>(
        IReadOnlyCollection<TDto> pulled,
        DbSet<TLocal> set,
        CancellationToken ct)
        where TDto : SyncEntityDto
        where TLocal : LocalSyncEntity
        => await HasDirtyLocalEntityAsync(
            pulled.Select(dto => dto.Id),
            set,
            ct);

    private static async Task<bool> HasDirtyLocalEntityAsync<TLocal>(
        IEnumerable<Guid> entityIds,
        DbSet<TLocal> set,
        CancellationToken ct)
        where TLocal : LocalSyncEntity
    {
        foreach (var batch in entityIds
                     .Where(id => id != Guid.Empty)
                     .Distinct()
                     .Chunk(PullQueryContainsBatchSize))
        {
            var ids = batch;
            if (await set.IgnoreQueryFilters()
                    .AnyAsync(entity =>
                        ids.Contains(entity.Id) &&
                        entity.IsDirty,
                        ct))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldPublishItemInvoiceHistoryChanged(
        bool hasPulledInvoices,
        bool invoicePurgeApplied)
        => hasPulledInvoices || invoicePurgeApplied;

    private async Task UpsertPulledCompanyProfilesAsync(
        IReadOnlyList<CompanyProfileDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var incomingProfiles = dtos
            .Select(LocalMappings.ToLocal)
            .Select(local =>
            {
                local.IsDirty = false;
                local.OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(local.OfficeCode, local.OfficeCode);
                return local;
            })
            .ToList();

        var profiles = await _db.CompanyProfiles.IgnoreQueryFilters().ToListAsync(ct);
        var assignmentSettings = await _db.Settings
            .Where(setting => EF.Functions.Like(setting.Key, "CompanyProfile.Assigned.%"))
            .ToListAsync(ct);
        foreach (var incoming in incomingProfiles.Where(profile =>
                     profile.IsDefaultForOffice &&
                     !profile.IsDeleted &&
                     profile.IsActive))
        {
            var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                incoming.OfficeCode,
                incoming.OfficeCode);
            if (profiles.Any(profile =>
                    profile.Id != incoming.Id &&
                    profile.IsDirty &&
                    !profile.IsDeleted &&
                    profile.IsActive &&
                    profile.IsDefaultForOffice &&
                    string.Equals(
                        OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                            profile.OfficeCode,
                            profile.OfficeCode),
                        officeCode,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new SyncPullBlockedException(
                    "회사 프로필 canonicalization이 dirty 기본 프로필을 변경할 수 있어 pull을 중단했습니다.");
            }
        }
        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            GetCompanyProfileCanonicalMutationIds(
                incomingProfiles,
                profiles,
                now),
            [nameof(LocalCompanyProfile), "CompanyProfile"],
            "회사 프로필 canonicalization",
            ct);

        foreach (var local in incomingProfiles)
        {
            var existing = profiles.FirstOrDefault(profile => profile.Id == local.Id);
            if (existing is null)
            {
                if (local.IsDefaultForOffice && !local.IsDeleted && local.IsActive)
                {
                    var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(local.OfficeCode, local.OfficeCode);
                    foreach (var conflict in profiles.Where(profile =>
                                 profile.Id != local.Id &&
                                 !profile.IsDeleted &&
                                 profile.IsActive &&
                                 profile.IsDefaultForOffice &&
                                 string.Equals(
                                     OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(profile.OfficeCode, profile.OfficeCode),
                                     officeCode,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        if (conflict.IsDirty)
                            continue;
                        foreach (var setting in assignmentSettings.Where(setting =>
                                     string.Equals(setting.Value, conflict.Id.ToString(), StringComparison.OrdinalIgnoreCase)))
                        {
                            setting.Value = local.Id.ToString();
                        }

                        conflict.IsDefaultForOffice = false;
                        if (string.Equals(conflict.ProfileName?.Trim(), $"{officeCode} 기본", StringComparison.OrdinalIgnoreCase))
                        {
                            conflict.IsActive = false;
                            conflict.IsDeleted = true;
                        }

                        conflict.IsDirty = false;
                        conflict.UpdatedAtUtc = now;
                    }
                }

                _db.CompanyProfiles.Add(local);
                profiles.Add(local);
                continue;
            }

            if (!existing.IsDirty)
            {
                var incomingIsNewer = local.Revision > existing.Revision ||
                                      (local.Revision == existing.Revision && local.UpdatedAtUtc >= existing.UpdatedAtUtc);
                if (!incomingIsNewer)
                    continue;

                _db.Entry(existing).CurrentValues.SetValues(local);
                existing.OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(existing.OfficeCode, existing.OfficeCode);
                existing.IsDirty = false;
            }
        }

        foreach (var group in profiles
                     .Where(profile => !profile.IsDeleted && profile.IsActive)
                     .GroupBy(
                         profile => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(profile.OfficeCode, OfficeCodeCatalog.Usenet),
                         StringComparer.OrdinalIgnoreCase))
        {
            var canonicalId = OfficeCodeCatalog.GetDefaultCompanyProfileId(group.Key);
            var canonical = group.FirstOrDefault(profile => profile.Id == canonicalId)
                ?? group.OrderByDescending(profile => profile.IsDefaultForOffice)
                    .ThenByDescending(profile => profile.UpdatedAtUtc)
                    .ThenBy(profile => profile.Id)
                    .First();

            foreach (var profile in group)
            {
                if (profile.IsDirty)
                    continue;
                var shouldBeDefault = profile.Id == canonical.Id;
                if (profile.IsDefaultForOffice != shouldBeDefault)
                {
                    profile.IsDefaultForOffice = shouldBeDefault;
                    profile.IsDirty = false;
                    profile.UpdatedAtUtc = now;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private static IReadOnlyCollection<Guid> GetCompanyProfileCanonicalMutationIds(
        IReadOnlyList<LocalCompanyProfile> incomingProfiles,
        IReadOnlyList<LocalCompanyProfile> existingProfiles,
        DateTime now)
    {
        static LocalCompanyProfile CloneCanonicalState(LocalCompanyProfile profile)
            => new()
            {
                Id = profile.Id,
                OfficeCode = profile.OfficeCode,
                ProfileName = profile.ProfileName,
                IsDefaultForOffice = profile.IsDefaultForOffice,
                IsActive = profile.IsActive,
                IsDeleted = profile.IsDeleted,
                IsDirty = profile.IsDirty,
                Revision = profile.Revision,
                CreatedAtUtc = profile.CreatedAtUtc,
                UpdatedAtUtc = profile.UpdatedAtUtc
            };

        static void ApplyIncomingBaseline(
            LocalCompanyProfile target,
            LocalCompanyProfile incoming)
        {
            target.OfficeCode = incoming.OfficeCode;
            target.ProfileName = incoming.ProfileName;
            target.IsDefaultForOffice = incoming.IsDefaultForOffice;
            target.IsActive = incoming.IsActive;
            target.IsDeleted = incoming.IsDeleted;
            target.IsDirty = false;
            target.Revision = incoming.Revision;
            target.UpdatedAtUtc = incoming.UpdatedAtUtc;
        }

        var baseline = existingProfiles
            .Select(CloneCanonicalState)
            .ToList();
        foreach (var local in incomingProfiles)
        {
            var existing = baseline.FirstOrDefault(profile => profile.Id == local.Id);
            if (existing is null)
            {
                baseline.Add(CloneCanonicalState(local));
                continue;
            }

            if (existing.IsDirty)
                continue;
            var incomingIsNewer = local.Revision > existing.Revision ||
                                  local.Revision == existing.Revision &&
                                  local.UpdatedAtUtc >= existing.UpdatedAtUtc;
            if (incomingIsNewer)
                ApplyIncomingBaseline(existing, local);
        }

        var projected = existingProfiles
            .Select(CloneCanonicalState)
            .ToList();

        foreach (var local in incomingProfiles)
        {
            var existing = projected.FirstOrDefault(profile => profile.Id == local.Id);
            if (existing is null)
            {
                if (local.IsDefaultForOffice && !local.IsDeleted && local.IsActive)
                {
                    var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                        local.OfficeCode,
                        local.OfficeCode);
                    foreach (var conflict in projected.Where(profile =>
                                 !profile.IsDeleted &&
                                 profile.IsActive &&
                                 profile.IsDefaultForOffice &&
                                 string.Equals(
                                     OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                                         profile.OfficeCode,
                                         profile.OfficeCode),
                                     officeCode,
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        if (conflict.IsDirty)
                            continue;
                        conflict.IsDefaultForOffice = false;
                        if (string.Equals(
                                conflict.ProfileName?.Trim(),
                                $"{officeCode} 기본",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            conflict.IsActive = false;
                            conflict.IsDeleted = true;
                        }

                        conflict.IsDirty = false;
                        conflict.UpdatedAtUtc = now;
                    }
                }

                projected.Add(new LocalCompanyProfile
                {
                    Id = local.Id,
                    OfficeCode = local.OfficeCode,
                    ProfileName = local.ProfileName,
                    IsDefaultForOffice = local.IsDefaultForOffice,
                    IsActive = local.IsActive,
                    IsDeleted = local.IsDeleted,
                    IsDirty = false,
                    Revision = local.Revision,
                    CreatedAtUtc = local.CreatedAtUtc,
                    UpdatedAtUtc = local.UpdatedAtUtc
                });
                continue;
            }

            if (existing.IsDirty)
                continue;
            var incomingIsNewer = local.Revision > existing.Revision ||
                                  local.Revision == existing.Revision &&
                                  local.UpdatedAtUtc >= existing.UpdatedAtUtc;
            if (!incomingIsNewer)
                continue;

            ApplyIncomingBaseline(existing, local);
        }

        foreach (var group in projected
                     .Where(profile => !profile.IsDeleted && profile.IsActive)
                     .GroupBy(
                         profile => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                             profile.OfficeCode,
                             OfficeCodeCatalog.Usenet),
                         StringComparer.OrdinalIgnoreCase))
        {
            var canonicalId = OfficeCodeCatalog.GetDefaultCompanyProfileId(group.Key);
            var canonical = group.FirstOrDefault(profile => profile.Id == canonicalId)
                ?? group.OrderByDescending(profile => profile.IsDefaultForOffice)
                    .ThenByDescending(profile => profile.UpdatedAtUtc)
                    .ThenBy(profile => profile.Id)
                    .First();
            foreach (var profile in group)
            {
                if (profile.IsDirty)
                    continue;
                var shouldBeDefault = profile.Id == canonical.Id;
                if (profile.IsDefaultForOffice != shouldBeDefault)
                {
                    profile.IsDefaultForOffice = shouldBeDefault;
                    profile.IsDirty = false;
                    profile.UpdatedAtUtc = now;
                }
            }
        }

        var baselineById = baseline.ToDictionary(profile => profile.Id);
        return existingProfiles
            .Where(original =>
            {
                var expected = baselineById[original.Id];
                var final = projected.Single(profile => profile.Id == original.Id);
                return expected.IsDefaultForOffice != final.IsDefaultForOffice ||
                       expected.IsActive != final.IsActive ||
                       expected.IsDeleted != final.IsDeleted ||
                       expected.IsDirty != final.IsDirty;
            })
            .Select(profile => profile.Id)
            .Distinct()
            .ToList();
    }

    private async Task UpsertPulledCustomerContractsAsync(
        IReadOnlyList<CustomerContractDto> dtos,
        CancellationToken ct)
    {
        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;
            var existing = await _db.CustomerContracts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == local.Id, ct);
            if (existing is null)
            {
                _db.CustomerContracts.Add(local);
                continue;
            }

            if (existing.IsDirty)
                continue;

            var existingFileContent = existing.FileContent;
            var existingFileHash = existing.FileHash;
            var existingFileSize = existing.FileSize;
            var incomingFileContent = local.FileContent ?? [];
            var canPreserveLocalContent =
                !local.IsDeleted &&
                incomingFileContent.Length == 0 &&
                existingFileContent is { Length: > 0 } &&
                local.FileSize > 0 &&
                existingFileSize == local.FileSize &&
                existingFileContent.LongLength == local.FileSize &&
                (string.IsNullOrWhiteSpace(local.FileHash) ||
                 string.Equals(existingFileHash, local.FileHash, StringComparison.OrdinalIgnoreCase));

            _db.Entry(existing).CurrentValues.SetValues(local);

            if (canPreserveLocalContent)
            {
                existing.FileContent = existingFileContent;
                existing.IsDirty = false;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertPulledAsync<TLocal, TDto>(
        IReadOnlyList<TDto> dtos,
        DbSet<TLocal> set,
        Func<TDto, TLocal> toLocal,
        CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
        where TDto : class
    {
        foreach (var dto in dtos)
        {
            var local = toLocal(dto);
            local.IsDirty = false;
            var existing = await set.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == local.Id, ct);
            if (existing is null)
            {
                set.Add(local);
            }
            else
            {
                if (!existing.IsDirty)
                    _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertPulledPaymentsAsync(IReadOnlyList<PaymentDto> dtos, CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var invoiceIdsToRecalculate = dtos
            .Select(dto => dto.InvoiceId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var paymentIds = dtos
            .Select(dto => dto.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var existingPaymentsById = new Dictionary<Guid, LocalPayment>();
        foreach (var paymentIdBatch in paymentIds.Chunk(PullQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedPaymentIds = paymentIdBatch;
            var existingPayments = await _db.Payments
                .IgnoreQueryFilters()
                .Where(payment => scopedPaymentIds.Contains(payment.Id))
                .ToListAsync(ct);
            foreach (var existingPayment in existingPayments)
                existingPaymentsById[existingPayment.Id] = existingPayment;
            invoiceIdsToRecalculate.AddRange(existingPayments
                .Where(payment => payment.InvoiceId != Guid.Empty)
                .Select(payment => payment.InvoiceId));
        }

        invoiceIdsToRecalculate = invoiceIdsToRecalculate.Distinct().ToList();

        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;
            if (existingPaymentsById.TryGetValue(local.Id, out var existingPayment))
            {
                if (!existingPayment.IsDirty)
                    _db.Entry(existingPayment).CurrentValues.SetValues(local);
            }
            else
            {
                _db.Payments.Add(local);
                existingPaymentsById[local.Id] = local;
            }
        }
        await _db.SaveChangesAsync(ct);
        await ReconcilePulledPaymentTransactionMirrorsAsync(paymentIds, invoiceIdsToRecalculate, ct);
        invoiceIdsToRecalculate = invoiceIdsToRecalculate.Distinct().ToList();
        var affectedRentalProfileIds = new HashSet<Guid>();
        foreach (var invoiceIdBatch in invoiceIdsToRecalculate.Chunk(PullQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedInvoiceIds = invoiceIdBatch;
            var batchProfileIds = await _db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice =>
                    scopedInvoiceIds.Contains(invoice.Id) &&
                    invoice.LinkedRentalBillingProfileId.HasValue &&
                    invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
                .Select(invoice => invoice.LinkedRentalBillingProfileId!.Value)
                .Distinct()
                .ToListAsync(ct);
            affectedRentalProfileIds.UnionWith(batchProfileIds);
        }
        await _local.RecalculateRentalSettlementForInvoicePaymentsAsync(
            invoiceIdsToRecalculate,
            ct,
            preserveDirtyProfiles: true);
        _db.ChangeTracker.Clear();
        await _local.RefreshRentalProfileSummariesFromAuthoritativeRunEvidenceAsync(
            affectedRentalProfileIds,
            ct,
            markDirty: false);
    }

    private async Task ReconcilePulledPaymentTransactionMirrorsAsync(
        IReadOnlyList<Guid> paymentIds,
        List<Guid> invoiceIdsToRecalculate,
        CancellationToken ct)
    {
        if (paymentIds.Count == 0)
            return;

        var payments = new List<LocalPayment>();
        foreach (var paymentIdBatch in paymentIds.Chunk(PullQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedPaymentIds = paymentIdBatch;
            payments.AddRange(await _db.Payments
                .IgnoreQueryFilters()
                .Where(payment => scopedPaymentIds.Contains(payment.Id))
                .ToListAsync(ct));
        }
        if (payments.Count == 0)
            return;

        var transactionsById = new Dictionary<Guid, LocalTransaction>();
        foreach (var paymentIdBatch in paymentIds.Chunk(PullQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedPaymentIds = paymentIdBatch;
            var transactions = await _db.Transactions
                .IgnoreQueryFilters()
                .Where(transaction => scopedPaymentIds.Contains(transaction.Id))
                .ToListAsync(ct);
            foreach (var transaction in transactions)
                transactionsById[transaction.Id] = transaction;
        }

        var invoicesById = new Dictionary<Guid, LocalInvoice>();
        var activeInvoiceIds = payments
            .Where(payment => !payment.IsDirty && !payment.IsDeleted && payment.InvoiceId != Guid.Empty)
            .Select(payment => payment.InvoiceId)
            .Distinct()
            .ToList();
        foreach (var invoiceIdBatch in activeInvoiceIds.Chunk(PullQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedInvoiceIds = invoiceIdBatch;
            var invoices = await _db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => scopedInvoiceIds.Contains(invoice.Id))
                .ToListAsync(ct);
            foreach (var invoice in invoices)
                invoicesById[invoice.Id] = invoice;
        }

        foreach (var payment in payments)
        {
            if (payment.IsDirty)
                continue;

            transactionsById.TryGetValue(payment.Id, out var transaction);
            if (transaction?.LinkedInvoiceId is Guid previousLinkedInvoiceId && previousLinkedInvoiceId != Guid.Empty)
                invoiceIdsToRecalculate.Add(previousLinkedInvoiceId);

            if (transaction?.IsDirty == true)
                continue;

            if (payment.IsDeleted)
            {
                if (transaction != null)
                {
                    transaction.IsDeleted = true;
                    transaction.IsDirty = false;
                    transaction.UpdatedAtUtc = payment.UpdatedAtUtc;
                    transaction.Revision = Math.Max(transaction.Revision, payment.Revision);
                }

                continue;
            }

            invoicesById.TryGetValue(payment.InvoiceId, out var invoice);
            if (invoice == null || invoice.IsDeleted)
            {
                if (transaction != null)
                {
                    transaction.IsDeleted = true;
                    transaction.IsDirty = false;
                    transaction.UpdatedAtUtc = payment.UpdatedAtUtc;
                    transaction.Revision = Math.Max(transaction.Revision, payment.Revision);
                }

                continue;
            }

            invoiceIdsToRecalculate.Add(invoice.Id);
            if (transaction == null)
            {
                transaction = new LocalTransaction
                {
                    Id = payment.Id,
                    CreatedAtUtc = payment.CreatedAtUtc,
                    Revision = 0
                };
                _db.Transactions.Add(transaction);
                transactionsById[transaction.Id] = transaction;
            }

            ApplyPulledPaymentToTransactionMirror(payment, invoice, transaction);
        }

        await _db.SaveChangesAsync(ct);
    }

    private static void ApplyPulledPaymentToTransactionMirror(
        LocalPayment payment,
        LocalInvoice invoice,
        LocalTransaction transaction)
    {
        transaction.CustomerId = invoice.CustomerId;
        transaction.TenantCode = invoice.TenantCode;
        transaction.OfficeCode = invoice.OfficeCode;
        transaction.ResponsibleOfficeCode = invoice.ResponsibleOfficeCode;
        transaction.TransactionDate = payment.PaymentDate;
        var transactionKind = ResolvePulledPaymentTransactionKind(invoice);
        transaction.TransactionKind = transactionKind;
        transaction.LinkedInvoiceId = invoice.Id;
        transaction.LinkedInvoiceNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.LocalTempNumber
            : invoice.InvoiceNumber;
        transaction.LinkedRentalBillingProfileId = invoice.LinkedRentalBillingProfileId;
        transaction.LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId;
        transaction.SettlementAmount = Math.Max(0m, payment.Amount);
        transaction.AdvanceDelta = 0m;
        transaction.PrepaidDelta = 0m;
        transaction.CashReceipt = 0m;
        transaction.CardReceipt = 0m;
        transaction.BankReceipt = 0m;
        transaction.DiscountApplied = 0m;
        transaction.ReceiptTotal = 0m;
        transaction.CashPayment = 0m;
        transaction.CardPayment = 0m;
        transaction.BankPayment = 0m;
        transaction.DiscountReceived = 0m;
        transaction.PaymentTotal = 0m;
        if (invoice.VoucherType == VoucherType.Purchase)
        {
            transaction.BankPayment = Math.Max(0m, payment.Amount);
            transaction.PaymentTotal = Math.Max(0m, payment.Amount);
        }
        else
        {
            transaction.BankReceipt = Math.Max(0m, payment.Amount);
            transaction.ReceiptTotal = Math.Max(0m, payment.Amount);
        }

        transaction.Note = PaymentFlowConstants.NormalizeLinkedPaymentNote(payment.Note, transactionKind);
        transaction.IsDeleted = false;
        transaction.IsDirty = false;
        transaction.UpdatedAtUtc = payment.UpdatedAtUtc;
    }

    private static string ResolvePulledPaymentTransactionKind(LocalInvoice invoice)
    {
        if (invoice.LinkedRentalBillingProfileId.HasValue && invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
            return PaymentFlowConstants.TransactionKindRentalReceipt;

        return invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement
            ? PaymentFlowConstants.TransactionKindInvoicePayment
            : PaymentFlowConstants.TransactionKindInvoiceReceipt;
    }

    private async Task<PulledTransactionSideEffectState> UpsertPulledTransactionsAsync(IReadOnlyList<TransactionDto> dtos, CancellationToken ct)
    {
        if (dtos.Count == 0)
            return PulledTransactionSideEffectState.Empty;

        var appliedTransactionIds = new List<Guid>();
        var previousRentalTargets = new List<(Guid ProfileId, Guid? RunId)>();
        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;
            var existing = await _db.Transactions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(transaction => transaction.Id == local.Id, ct);
            if (existing is null)
            {
                _db.Transactions.Add(local);
                appliedTransactionIds.Add(local.Id);
            }
            else if (!existing.IsDirty)
            {
                if (existing.LinkedRentalBillingProfileId is Guid previousProfileId && previousProfileId != Guid.Empty)
                {
                    previousRentalTargets.Add((previousProfileId, existing.LinkedRentalBillingRunId));
                }

                _db.Entry(existing).CurrentValues.SetValues(local);
                appliedTransactionIds.Add(local.Id);
            }
        }

        await _db.SaveChangesAsync(ct);
        return new PulledTransactionSideEffectState
        {
            AppliedTransactionIds = appliedTransactionIds,
            PreviousRentalTargets = previousRentalTargets
        };
    }

    private async Task ApplyPulledTransactionSideEffectsAsync(PulledTransactionSideEffectState sideEffects, CancellationToken ct)
    {
        if (sideEffects.AppliedTransactionIds.Count == 0 &&
            sideEffects.PreviousRentalTargets.Count == 0)
        {
            return;
        }

        var affectedBillingProfileIds = await _local.ReconcilePulledTransactionSideEffectsAsync(
            sideEffects.AppliedTransactionIds,
            ct);
        await _local.RefreshRentalProfileSummariesFromAuthoritativeRunEvidenceAsync(
            affectedBillingProfileIds
                .Concat(sideEffects.PreviousRentalTargets.Select(target => target.ProfileId))
                .Distinct(),
            ct,
            markDirty: false);
    }

    private sealed class PulledTransactionSideEffectState
    {
        public static PulledTransactionSideEffectState Empty { get; } = new();

        public IReadOnlyList<Guid> AppliedTransactionIds { get; init; } = Array.Empty<Guid>();
        public IReadOnlyList<(Guid ProfileId, Guid? RunId)> PreviousRentalTargets { get; init; } =
            Array.Empty<(Guid ProfileId, Guid? RunId)>();
    }

    private async Task<IReadOnlyDictionary<Guid, Guid>> UpsertPulledItemsAsync(
        IReadOnlyList<ItemDto> dtos,
        CancellationToken ct,
        bool preserveExistingInventoryStock = false)
    {
        if (dtos.Count == 0)
            return new Dictionary<Guid, Guid>();

        var conflictResolution = await RemoveStalePulledItemConflictsAsync(dtos, ct);
        var skippedIncomingIds = conflictResolution.SkippedIncomingIds;
        var preservedWarehouseStockTotals =
            new Dictionary<Guid, decimal>();
        if (preserveExistingInventoryStock)
        {
            var incomingItemIds = dtos
                .Where(dto => !skippedIncomingIds.Contains(dto.Id))
                .Select(dto => dto.Id)
                .Where(itemId => itemId != Guid.Empty)
                .Distinct()
                .ToList();
            var preservedWarehouseStocks =
                await _db.ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock =>
                        incomingItemIds.Contains(stock.ItemId))
                    .ToListAsync(ct);
            preservedWarehouseStockTotals =
                preservedWarehouseStocks
                    .GroupBy(stock => stock.ItemId)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(stock => stock.Quantity));
        }
        var failSafeRequeues =
            new Dictionary<Guid, (long Revision, DateTime UpdatedAtUtc)>();

        foreach (var dto in dtos)
        {
            if (skippedIncomingIds.Contains(dto.Id))
                continue;

            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;
            var hasPreservedWarehouseStockTotal =
                preservedWarehouseStockTotals.TryGetValue(
                    local.Id,
                    out var preservedWarehouseStockTotal);
            if (hasPreservedWarehouseStockTotal)
            {
                local.CurrentStock =
                    preservedWarehouseStockTotal;
            }

            var existing = await _db.Items.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == local.Id, ct);
            if (existing is null)
            {
                local.CatalogExtensionSyncPending = false;
                _db.Items.Add(local);
            }
            else if (!existing.IsDirty)
            {
                if (preserveExistingInventoryStock &&
                    !hasPreservedWarehouseStockTotal)
                {
                    local.CurrentStock = existing.CurrentStock;
                }

                if (existing.IsDeleted || dto.IsDeleted)
                {
                    local.CatalogExtensionSyncPending = false;
                }
                else if (!HasFullItemCatalogExtensionShape(dto))
                {
                    PreserveLocalItemCatalogExtensions(local, existing);
                    local.CatalogExtensionSyncPending =
                        existing.CatalogExtensionSyncPending ||
                        HasMeaningfulItemCatalogExtensions(existing);
                }
                else if (existing.CatalogExtensionSyncPending)
                {
                    if (ItemCatalogExtensionsMatch(existing, dto))
                    {
                        local.CatalogExtensionSyncPending = false;
                    }
                    else
                    {
                        PreserveLocalItemCatalogExtensions(local, existing);
                        local.CatalogExtensionSyncPending = true;
                        failSafeRequeues[existing.Id] =
                            (local.Revision, local.UpdatedAtUtc);
                    }
                }
                else
                {
                    local.CatalogExtensionSyncPending = false;
                }

                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
        if (failSafeRequeues.Count > 0 &&
            BeforePulledItemCatalogMismatchRequeueAsyncForTesting is not null)
        {
            await BeforePulledItemCatalogMismatchRequeueAsyncForTesting(ct);
        }

        foreach (var (itemId, preserved) in failSafeRequeues)
        {
            var requeuedRowCount = await _db.Items
                .IgnoreQueryFilters()
                .Where(item =>
                    item.Id == itemId &&
                    !item.IsDeleted &&
                    !item.IsDirty &&
                    item.Revision == preserved.Revision &&
                    item.UpdatedAtUtc == preserved.UpdatedAtUtc)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.IsDirty, true)
                        .SetProperty(
                            item => item.CatalogExtensionSyncPending,
                            true),
                    ct);

            var trackedEntry = _db.ChangeTracker
                .Entries<LocalItem>()
                .FirstOrDefault(entry => entry.Entity.Id == itemId);
            if (requeuedRowCount > 0 && trackedEntry is not null)
            {
                trackedEntry.Entity.IsDirty = true;
                trackedEntry.Entity.CatalogExtensionSyncPending = true;
                trackedEntry.Entity.Revision = preserved.Revision;
                trackedEntry.Entity.UpdatedAtUtc = preserved.UpdatedAtUtc;
                trackedEntry.State = EntityState.Unchanged;
            }
            else if (trackedEntry is not null)
            {
                trackedEntry.State = EntityState.Detached;
            }
        }

        return conflictResolution.DuplicateToCanonicalIdMap;
    }

    private static bool HasFullItemCatalogExtensionShape(ItemDto dto)
        => dto.BoxQuantity.HasValue &&
           dto.StorageLocation is not null &&
           dto.LastPurchaseDateSpecified == true &&
           dto.LastSaleDateSpecified == true;

    private static bool HasMeaningfulItemCatalogExtensions(LocalItem item)
        => item.BoxQuantity != 0m ||
           !string.IsNullOrWhiteSpace(item.StorageLocation) ||
           item.LastPurchaseDate.HasValue ||
           item.LastSaleDate.HasValue;

    private static bool ItemCatalogExtensionsMatch(
        LocalItem local,
        ItemDto incoming)
        => incoming.BoxQuantity == local.BoxQuantity &&
           string.Equals(
               incoming.StorageLocation,
               local.StorageLocation,
               StringComparison.Ordinal) &&
           incoming.LastPurchaseDate == local.LastPurchaseDate &&
           incoming.LastSaleDate == local.LastSaleDate;

    private static void PreserveLocalItemCatalogExtensions(
        LocalItem target,
        LocalItem source)
    {
        target.BoxQuantity = source.BoxQuantity;
        target.StorageLocation = source.StorageLocation;
        target.LastPurchaseDate = source.LastPurchaseDate;
        target.LastSaleDate = source.LastSaleDate;
    }

    private async Task UpsertPulledUnitsAsync(
        IReadOnlyList<UnitDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        await NormalizeActiveUnitsAsync(DateTime.UtcNow, ct);

        var dedupedDtos = DeduplicatePulledUnits(dtos);
        var incomingActiveByNormalizedName = dedupedDtos
            .Where(dto => !dto.IsDeleted && dto.IsActive)
            .Select(dto => new
            {
                Dto = dto,
                NormalizedName = UnitCatalogNormalizer.Normalize(dto.Name)
            })
            .Where(current => !string.IsNullOrWhiteSpace(current.NormalizedName))
            .GroupBy(current => current.NormalizedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single().Dto, StringComparer.Ordinal);

        var conflictingExisting = await _db.Units.IgnoreQueryFilters()
            .Where(unit => !unit.IsDeleted && unit.IsActive)
            .ToListAsync(ct);

        var unitsToDelete = conflictingExisting
            .Where(unit =>
            {
                var normalizedName = UnitCatalogNormalizer.Normalize(unit.Name);
                return incomingActiveByNormalizedName.TryGetValue(normalizedName, out var incoming)
                       && incoming.Id != unit.Id;
            })
            .ToList();

        if (unitsToDelete.Count > 0)
        {
            await ThrowIfPendingCanonicalDeleteOutboxAsync(
                unitsToDelete.Select(unit => unit.Id),
                [nameof(LocalUnit), "Unit"],
                "단위 충돌",
                ct);
            AppLogger.Warn(
                "SYNC",
                $"pull Units 충돌 정리: incomingGroups={incomingActiveByNormalizedName.Count}, removedExisting={unitsToDelete.Count}");
            _db.Units.RemoveRange(unitsToDelete);
            await _db.SaveChangesAsync(ct);
        }

        foreach (var dto in dedupedDtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;
            local.Name = UnitCatalogNormalizer.Normalize(local.Name);

            var existing = await _db.Units.IgnoreQueryFilters().FirstOrDefaultAsync(unit => unit.Id == local.Id, ct);
            if (existing is null)
            {
                _db.Units.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
        await NormalizeActiveUnitsAsync(DateTime.UtcNow, ct);
    }

    private async Task EnsureUnitCatalogSyncSafetyAsync(CancellationToken ct)
        => await NormalizeActiveUnitsAsync(DateTime.UtcNow, ct);

    private async Task NormalizeActiveUnitsAsync(DateTime now, CancellationToken ct)
    {
        var activeUnits = await _db.Units.IgnoreQueryFilters()
            .Where(unit => !unit.IsDeleted && unit.IsActive)
            .OrderBy(unit => unit.CreatedAtUtc)
            .ThenBy(unit => unit.Name)
            .ToListAsync(ct);

        var normalizationDeleteIds = activeUnits
            .GroupBy(
                unit => UnitCatalogNormalizer.Normalize(unit.Name),
                StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .SelectMany(group =>
            {
                var canonical = UnitCatalogNormalizer.CanonicalDefinitions
                    .FirstOrDefault(definition =>
                        string.Equals(
                            definition.Name,
                            group.Key,
                            StringComparison.Ordinal));
                if (canonical is not null)
                {
                    return group
                        .Where(unit => unit.Id != canonical.Id)
                        .Select(unit => unit.Id);
                }

                var kept = group
                    .OrderByDescending(unit =>
                        string.Equals(
                            unit.Name,
                            group.Key,
                            StringComparison.Ordinal))
                    .ThenByDescending(unit => unit.Revision)
                    .ThenByDescending(unit => unit.UpdatedAtUtc)
                    .ThenBy(unit => unit.CreatedAtUtc)
                    .ThenBy(unit => unit.Id)
                    .First();
                return group
                    .Where(unit => unit.Id != kept.Id)
                    .Select(unit => unit.Id);
            })
            .Distinct()
            .ToList();
        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            normalizationDeleteIds,
            [nameof(LocalUnit), "Unit"],
            "단위 정규화",
            ct);

        var canonicalDefinitionByName = UnitCatalogNormalizer.CanonicalDefinitions
            .ToDictionary(current => current.Name, StringComparer.Ordinal);
        var changed = false;
        foreach (var definition in UnitCatalogNormalizer.CanonicalDefinitions)
        {
            var exact = activeUnits.FirstOrDefault(unit => unit.Id == definition.Id);
            var sameName = activeUnits
                .Where(unit => string.Equals(UnitCatalogNormalizer.Normalize(unit.Name), definition.Name, StringComparison.Ordinal))
                .OrderByDescending(unit => unit.Id == definition.Id)
                .ThenBy(unit => unit.CreatedAtUtc)
                .ThenBy(unit => unit.Id)
                .ToList();

            if (exact is null && sameName.Count > 0)
            {
                var source = sameName[0];
                var replacement = new LocalUnit
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAtUtc = source.CreatedAtUtc,
                    UpdatedAtUtc = source.UpdatedAtUtc,
                    Revision = source.Revision,
                    IsDirty = source.IsDirty
                };
                _db.Units.Add(replacement);
                activeUnits.Add(replacement);
                _db.Units.Remove(source);
                activeUnits.Remove(source);
                exact = replacement;
                changed = true;
            }
            else if (exact is null && sameName.Count == 0)
            {
                var created = new LocalUnit
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Revision = 0,
                    IsDirty = false
                };
                _db.Units.Add(created);
                activeUnits.Add(created);
                exact = created;
                changed = true;
            }

            if (exact is null)
                continue;

            if (!string.Equals(exact.Name, definition.Name, StringComparison.Ordinal))
            {
                exact.Name = definition.Name;
                exact.UpdatedAtUtc = now;
                changed = true;
            }
        }

        foreach (var group in activeUnits
                     .GroupBy(unit => UnitCatalogNormalizer.Normalize(unit.Name), StringComparer.Ordinal)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key)))
        {
            var canonicalName = group.Key;
            var canonical = canonicalDefinitionByName.TryGetValue(canonicalName, out var definition)
                ? group
                    .OrderByDescending(unit => unit.Id == definition.Id)
                    .ThenByDescending(unit => string.Equals(unit.Name, canonicalName, StringComparison.Ordinal))
                    .ThenByDescending(unit => unit.Revision)
                    .ThenByDescending(unit => unit.UpdatedAtUtc)
                    .ThenBy(unit => unit.CreatedAtUtc)
                    .ThenBy(unit => unit.Id)
                    .First()
                : group
                    .OrderByDescending(unit => string.Equals(unit.Name, canonicalName, StringComparison.Ordinal))
                    .ThenByDescending(unit => unit.Revision)
                    .ThenByDescending(unit => unit.UpdatedAtUtc)
                    .ThenBy(unit => unit.CreatedAtUtc)
                    .ThenBy(unit => unit.Id)
                    .First();

            if (!string.Equals(canonical.Name, canonicalName, StringComparison.Ordinal))
            {
                canonical.Name = canonicalName;
                canonical.UpdatedAtUtc = now;
                changed = true;
            }

            foreach (var duplicate in group.Where(unit => unit.Id != canonical.Id))
            {
                _db.Units.Remove(duplicate);
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private static IReadOnlyList<UnitDto> DeduplicatePulledUnits(IReadOnlyList<UnitDto> dtos)
    {
        var latestById = dtos
            .GroupBy(dto => dto.Id)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First())
            .ToList();

        var canonicalActiveIds = latestById
            .Where(dto => !dto.IsDeleted && dto.IsActive)
            .GroupBy(dto => UnitCatalogNormalizer.Normalize(dto.Name), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .OrderByDescending(dto => string.Equals(dto.Name, group.Key, StringComparison.Ordinal))
                .ThenByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First()
                .Id)
            .ToHashSet();

        var deduped = latestById
            .Where(dto => dto.IsDeleted || !dto.IsActive || canonicalActiveIds.Contains(dto.Id))
            .ToList();

        var droppedActiveDuplicates = latestById.Count - deduped.Count;
        if (droppedActiveDuplicates > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"pull Units 중복 수신 정리: received={dtos.Count}, byId={latestById.Count}, droppedActiveDuplicates={droppedActiveDuplicates}");
        }

        return deduped;
    }

    private async Task UpsertPulledRentalAssetsAsync(
        IReadOnlyList<RentalAssetDto> dtos,
        CancellationToken ct)
    {
        var dedupedDtos = DeduplicatePulledRentalAssets(dtos);
        var skippedIncomingIds = await RemoveStalePulledRentalAssetConflictsAsync(dedupedDtos, ct);

        foreach (var dto in dedupedDtos)
        {
            if (skippedIncomingIds.Contains(dto.Id))
                continue;

            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(asset => asset.Id == local.Id, ct);
            if (existing is null)
            {
                _db.RentalAssets.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertPulledRentalAssetAssignmentHistoriesAsync(
        IReadOnlyList<RentalAssetAssignmentHistoryDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var dedupedDtos = dtos
            .Where(dto => dto.Id != Guid.Empty && dto.AssetId != Guid.Empty)
            .GroupBy(dto => dto.Id)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.IsCurrent)
                .ThenByDescending(dto => dto.UnlinkedAtUtc ?? dto.LinkedAtUtc)
                .ThenByDescending(dto => dto.LinkedAtUtc)
                .First())
            .ToList();
        if (dedupedDtos.Count == 0)
            return;

        var historyIds = dedupedDtos
            .Select(dto => dto.Id)
            .Distinct()
            .ToList();
        var existingRows = await _db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .Where(history => historyIds.Contains(history.Id))
            .ToListAsync(ct);

        foreach (var dto in dedupedDtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;
            var existing = existingRows.FirstOrDefault(history => history.Id == local.Id);

            if (existing is null)
            {
                _db.RentalAssetAssignmentHistories.Add(local);
                existingRows.Add(local);
                continue;
            }

            if (!existing.IsDirty || local.Revision >= existing.Revision)
                _db.Entry(existing).CurrentValues.SetValues(local);
        }

        await _db.SaveChangesAsync(ct);
    }

    private static IReadOnlyList<RentalAssetDto> DeduplicatePulledRentalAssets(IReadOnlyList<RentalAssetDto> dtos)
    {
        if (dtos.Count == 0)
            return dtos;

        var latestById = dtos
            .GroupBy(dto => dto.Id)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First())
            .ToDictionary(dto => dto.Id);

        var kept = latestById.Values.ToDictionary(dto => dto.Id);
        PruneDuplicateActiveRentalAssets(kept, dto => dto.ManagementNumber);
        PruneDuplicateActiveRentalAssets(kept, dto => dto.ManagementId);
        PruneDuplicateActiveRentalAssets(kept, dto => dto.AssetKey);

        var droppedDuplicates = latestById.Count - kept.Count;
        if (droppedDuplicates > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"렌탈 자산 pull 중복 수신 정리: received={dtos.Count}, byId={latestById.Count}, droppedActiveDuplicates={droppedDuplicates}");
        }

        return kept.Values
            .OrderBy(dto => dto.Revision)
            .ThenBy(dto => dto.UpdatedAtUtc)
            .ThenBy(dto => dto.Id)
            .ToList();
    }

    private static void PruneDuplicateActiveRentalAssets(
        Dictionary<Guid, RentalAssetDto> kept,
        Func<RentalAssetDto, string?> keySelector)
    {
        foreach (var group in kept.Values
                     .Where(dto => !dto.IsDeleted)
                     .GroupBy(dto => BuildScopedRentalAssetNaturalKey(dto, keySelector(dto)), StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
                     .ToList())
        {
            var canonical = group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First();

            foreach (var duplicate in group.Where(dto => dto.Id != canonical.Id))
                kept.Remove(duplicate.Id);
        }
    }

    private sealed record PulledItemConflictResolution(
        HashSet<Guid> SkippedIncomingIds,
        IReadOnlyDictionary<Guid, Guid> DuplicateToCanonicalIdMap);

    private async Task<PulledItemConflictResolution> RemoveStalePulledItemConflictsAsync(
        IReadOnlyList<ItemDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return new PulledItemConflictResolution(
                [],
                new Dictionary<Guid, Guid>());

        var incomingItems = dtos
            .GroupBy(dto => dto.Id)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First())
            .ToList();
        var incomingCanonicalItems = incomingItems
            .Where(dto => !dto.IsDeleted)
            .ToList();
        var incomingCanonicalIds = incomingCanonicalItems
            .Select(dto => dto.Id)
            .ToHashSet();
        var candidates = await _db.Items.IgnoreQueryFilters().ToListAsync(ct);
        if (candidates.Count == 0)
            return new PulledItemConflictResolution(
                [],
                new Dictionary<Guid, Guid>());

        var duplicateToCanonicalIdMap = new Dictionary<Guid, Guid>();
        var skippedIncomingIds = new HashSet<Guid>();
        var dirtyConflictDetails = new List<string>();
        var ambiguousConflictDetails = new List<string>();
        var candidateMatches = new List<(LocalItem Candidate, List<Guid> IncomingIds)>();

        foreach (var candidate in candidates)
        {
            // An active server row is already authoritative for its own ID. It
            // must never be treated as an alias of a same-key tombstone (or of
            // another active row), otherwise a full pull can build A->B/B->A
            // remap cycles and move canonical references onto the tombstone.
            if (incomingCanonicalIds.Contains(candidate.Id))
                continue;

            var matchingIncomingIds = incomingCanonicalItems
                .Where(dto => dto.Id != candidate.Id)
                .Where(dto => ItemsSharePullNaturalKey(candidate, dto))
                .Select(dto => dto.Id)
                .Distinct()
                .ToList();

            if (matchingIncomingIds.Count == 0)
                continue;

            candidateMatches.Add((candidate, matchingIncomingIds));

            if (matchingIncomingIds.Count > 1)
            {
                skippedIncomingIds.UnionWith(matchingIncomingIds);
                ambiguousConflictDetails.Add($"{candidate.MaterialNumber}/{candidate.SerialNumber} -> {candidate.Id}");
                continue;
            }

            var incomingId = matchingIncomingIds[0];
            if (candidate.IsDirty)
            {
                skippedIncomingIds.Add(incomingId);
                dirtyConflictDetails.Add($"{candidate.MaterialNumber}/{candidate.SerialNumber} -> {candidate.Id}");
            }
        }

        // Decide per incoming canonical ID before mutating anything. If one
        // matching local row is dirty or one local row matches multiple
        // server candidates, every alias move targeting those incoming IDs is
        // withheld. This prevents a clean alias from being deleted and its
        // references moved to an incoming row that the same pass must skip.
        foreach (var (candidate, matchingIncomingIds) in candidateMatches)
        {
            if (candidate.IsDirty || matchingIncomingIds.Count != 1)
                continue;

            var incomingId = matchingIncomingIds[0];
            if (!skippedIncomingIds.Contains(incomingId))
                duplicateToCanonicalIdMap[candidate.Id] = incomingId;
        }

        if (duplicateToCanonicalIdMap.Count > 0)
        {
            await ThrowIfPendingCanonicalDeleteOutboxAsync(
                duplicateToCanonicalIdMap.Keys,
                [nameof(LocalItem), "Item"],
                "품목 별칭",
                ct);
            await ThrowIfPendingItemAliasReferenceOutboxesAsync(
                duplicateToCanonicalIdMap,
                ct);
            await RemapLocalItemReferencesAsync(duplicateToCanonicalIdMap, ct);

            _db.ChangeTracker.Clear();
            await _db.Items.IgnoreQueryFilters()
                .Where(item => duplicateToCanonicalIdMap.Keys.Contains(item.Id))
                .ExecuteDeleteAsync(ct);
            _db.ChangeTracker.Clear();

            AppLogger.Warn(
                "SYNC",
                $"품목 pull 충돌 복구: 자산 식별값이 같은 로컬 품목 {duplicateToCanonicalIdMap.Count}건을 서버 기준 ID로 정리했습니다.");
        }

        if (dirtyConflictDetails.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"품목 pull 충돌 보류: 로컬 수정 중인 품목 {dirtyConflictDetails.Count}건은 덮어쓰지 않았습니다. " +
                $"details={string.Join(", ", dirtyConflictDetails.Take(10))}");
        }

        if (ambiguousConflictDetails.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"품목 pull 충돌 보류: 중앙 서버에서 동일 식별값 후보가 여러 건 감지돼 자동 정리를 건너뛴 로컬 품목 {ambiguousConflictDetails.Count}건이 있습니다. " +
                $"details={string.Join(", ", ambiguousConflictDetails.Take(10))}");
        }

        return new PulledItemConflictResolution(
            skippedIncomingIds,
            duplicateToCanonicalIdMap);
    }

    private static void RemapPulledRentalBillingTemplateCatalogItemReferences(
        IReadOnlyList<RentalBillingProfileDto> profiles,
        IReadOnlyDictionary<Guid, Guid> duplicateToCanonicalIdMap)
    {
        if (profiles.Count == 0 || duplicateToCanonicalIdMap.Count == 0)
            return;

        foreach (var profile in profiles)
        {
            if (!TryRemapRentalBillingTemplateCatalogItemReferences(
                    profile.BillingTemplateJson,
                    duplicateToCanonicalIdMap,
                    out var remappedTemplateJson))
            {
                throw new SyncPullBlockedException(
                    $"품목 별칭을 참조하는 청구 템플릿을 안전하게 해석할 수 없어 pull을 중단했습니다. profile={profile.Id:D}");
            }

            if (!string.Equals(
                    profile.BillingTemplateJson ?? string.Empty,
                    remappedTemplateJson,
                    StringComparison.Ordinal))
            {
                profile.BillingTemplateJson = remappedTemplateJson;
            }
        }
    }

    private static bool TryRemapRentalBillingTemplateCatalogItemReferences(
        string? billingTemplateJson,
        IReadOnlyDictionary<Guid, Guid> duplicateToCanonicalIdMap,
        out string remappedTemplateJson)
    {
        var original = billingTemplateJson ?? string.Empty;
        remappedTemplateJson = original;
        if (string.IsNullOrWhiteSpace(original) ||
            duplicateToCanonicalIdMap.Count == 0)
        {
            return true;
        }

        try
        {
            if (JsonNode.Parse(original) is not JsonArray templateItems)
                return !ContainsAnyItemAlias(original, duplicateToCanonicalIdMap.Keys);

            var changed = false;
            foreach (var templateNode in templateItems)
            {
                if (templateNode is not JsonObject item)
                {
                    if (templateNode is not null &&
                        ContainsAnyItemAlias(
                            templateNode.ToJsonString(),
                            duplicateToCanonicalIdMap.Keys))
                    {
                        return false;
                    }

                    continue;
                }

                var catalogItemProperties = item
                    .Where(property => string.Equals(
                        property.Key,
                        nameof(RentalBillingTemplateItemModel.CatalogItemId),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (catalogItemProperties.Count == 0)
                    continue;

                if (catalogItemProperties.Count != 1)
                {
                    if (ContainsAnyItemAlias(
                            item.ToJsonString(),
                            duplicateToCanonicalIdMap.Keys))
                    {
                        return false;
                    }

                    continue;
                }

                var catalogItemProperty = catalogItemProperties[0];
                if (catalogItemProperty.Value is not JsonValue catalogItemValue ||
                    !catalogItemValue.TryGetValue<string>(out var rawCatalogItemId) ||
                    !Guid.TryParse(rawCatalogItemId, out var catalogItemId))
                {
                    if (catalogItemProperty.Value is not null &&
                        ContainsAnyItemAlias(
                            catalogItemProperty.Value.ToJsonString(),
                            duplicateToCanonicalIdMap.Keys))
                    {
                        return false;
                    }

                    continue;
                }

                if (!duplicateToCanonicalIdMap.TryGetValue(
                        catalogItemId,
                        out var canonicalItemId))
                {
                    continue;
                }

                item[catalogItemProperty.Key] = canonicalItemId.ToString("D");
                changed = true;
            }

            var serializedTemplateJson = templateItems.ToJsonString();
            if (ContainsAnyItemAlias(
                    serializedTemplateJson,
                    duplicateToCanonicalIdMap.Keys))
            {
                return false;
            }

            if (changed)
                remappedTemplateJson = serializedTemplateJson;
            return true;
        }
        catch (JsonException)
        {
            return !ContainsAnyItemAlias(original, duplicateToCanonicalIdMap.Keys);
        }
        catch (InvalidOperationException)
        {
            return !ContainsAnyItemAlias(original, duplicateToCanonicalIdMap.Keys);
        }
    }

    private static bool ContainsAnyItemAlias(
        string value,
        IEnumerable<Guid> duplicateItemIds)
        => JsonGuidTokenSafety.ContainsExactGuidToken(value, duplicateItemIds);

    private async Task UpsertPulledRentalBillingProfilesAsync(
        IReadOnlyList<RentalBillingProfileDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var dedupedDtos = DeduplicatePulledRentalBillingProfiles(dtos);
        var skippedIncomingIds = await RemoveStalePulledRentalBillingProfileConflictsAsync(dedupedDtos, ct);
        var profiles = await _db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(ct);
        var dirtyConflictDetails = new List<string>();

        foreach (var dto in dedupedDtos)
        {
            if (skippedIncomingIds.Contains(dto.Id))
                continue;

            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = profiles.FirstOrDefault(profile => profile.Id == local.Id);
            if (existing is not null && existing.IsDirty)
                continue;

            var conflictingProfiles = FindConflictingLocalRentalBillingProfiles(profiles, local.ProfileKey, local.Id);
            if (conflictingProfiles.Count > 0)
            {
                var dirtyConflicts = conflictingProfiles
                    .Where(profile => profile.IsDirty)
                    .ToList();

                if (dirtyConflicts.Count > 0)
                {
                    skippedIncomingIds.Add(dto.Id);
                    dirtyConflictDetails.Add(
                        $"{local.ProfileKey} -> {string.Join(", ", dirtyConflicts.Select(profile => profile.Id))}");
                    continue;
                }

                _db.RentalBillingProfiles.RemoveRange(conflictingProfiles);
                foreach (var conflict in conflictingProfiles)
                    profiles.Remove(conflict);
            }

            if (existing is null)
            {
                _db.RentalBillingProfiles.Add(local);
                profiles.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
                existing.IsDirty = false;
            }
        }

        if (dirtyConflictDetails.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"렌탈 청구 프로필 pull 적용 보류: 로컬 수정 중인 동일 키 프로필 {dirtyConflictDetails.Count}건은 덮어쓰지 않았습니다. " +
                $"details={string.Join(", ", dirtyConflictDetails.Take(10))}");
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<HashSet<Guid>> RemoveStalePulledRentalBillingProfileConflictsAsync(
        IReadOnlyList<RentalBillingProfileDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return [];

        var incomingByProfileKey = BuildIncomingRentalBillingProfileLookup(dtos, dto => dto.ProfileKey);
        if (incomingByProfileKey.Count == 0)
            return [];

        var candidates = await _db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(ct);

        if (candidates.Count == 0)
            return [];

        var staleConflictIds = new HashSet<Guid>();
        var skippedIncomingIds = new HashSet<Guid>();
        var dirtyConflictDetails = new List<string>();
        var recoveredDirtyConflictDetails = new List<string>();

        foreach (var candidate in candidates)
        {
            var matchingIncomingIds = GetMatchingIncomingRentalBillingProfileIds(candidate, incomingByProfileKey);
            if (matchingIncomingIds.Count == 0 || matchingIncomingIds.Contains(candidate.Id))
                continue;

            if (candidate.IsDirty)
            {
                foreach (var incomingId in matchingIncomingIds)
                    skippedIncomingIds.Add(incomingId);

                dirtyConflictDetails.Add($"{candidate.ProfileKey} -> {candidate.Id}");
                continue;
            }

            staleConflictIds.Add(candidate.Id);
        }

        if (staleConflictIds.Count > 0)
        {
            await ThrowIfPendingCanonicalDeleteOutboxAsync(
                staleConflictIds,
                [nameof(LocalRentalBillingProfile), "RentalBillingProfile"],
                "렌탈 청구 프로필 충돌",
                ct);
            await ThrowIfDirtyRentalBillingProfileDependenciesAsync(
                staleConflictIds,
                ct);
            _db.ChangeTracker.Clear();
            await _db.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(profile => staleConflictIds.Contains(profile.Id))
                .ExecuteDeleteAsync(ct);
            _db.ChangeTracker.Clear();

            AppLogger.Warn(
                "SYNC",
                $"렌탈 청구 프로필 pull 충돌 복구: 프로필 키가 같은 로컬 프로필 {staleConflictIds.Count}건을 서버 기준으로 정리했습니다.");
        }

        if (dirtyConflictDetails.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"렌탈 청구 프로필 pull 충돌 보류: 로컬 수정 중인 프로필 {dirtyConflictDetails.Count}건은 덮어쓰지 않았습니다. " +
                $"details={string.Join(", ", dirtyConflictDetails.Take(10))}");
        }

        return skippedIncomingIds;
    }

    private static IReadOnlyList<RentalBillingProfileDto> DeduplicatePulledRentalBillingProfiles(
        IReadOnlyList<RentalBillingProfileDto> dtos)
    {
        var latestById = dtos
            .GroupBy(dto => dto.Id)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First())
            .ToList();

        var canonicalIdsByProfileKey = latestById
            .Where(dto => !string.IsNullOrWhiteSpace(NormalizeRentalBillingProfileNaturalKey(dto.ProfileKey)))
            .GroupBy(dto => NormalizeRentalBillingProfileNaturalKey(dto.ProfileKey), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First()
                .Id)
            .ToHashSet();

        var deduped = latestById
            .Where(dto =>
            {
                var normalizedKey = NormalizeRentalBillingProfileNaturalKey(dto.ProfileKey);
                return string.IsNullOrWhiteSpace(normalizedKey) || canonicalIdsByProfileKey.Contains(dto.Id);
            })
            .ToList();

        var droppedDuplicates = latestById.Count - deduped.Count;
        if (droppedDuplicates > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"렌탈 청구 프로필 pull 중복 수신 정리: received={dtos.Count}, byId={latestById.Count}, droppedByProfileKey={droppedDuplicates}");
        }

        return deduped;
    }

    private async Task RemapLocalItemReferencesAsync(
        IReadOnlyDictionary<Guid, Guid> duplicateToCanonicalIdMap,
        CancellationToken ct)
    {
        if (duplicateToCanonicalIdMap.Count == 0)
            return;

        var duplicateIds = duplicateToCanonicalIdMap.Keys.Distinct().ToList();
        var canonicalIds = duplicateToCanonicalIdMap.Values.Distinct().ToList();

        var rentalBillingProfiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .ToListAsync(ct);
        var remappedBillingTemplates = new Dictionary<Guid, string>();
        foreach (var profile in rentalBillingProfiles)
        {
            if (!TryRemapRentalBillingTemplateCatalogItemReferences(
                    profile.BillingTemplateJson,
                    duplicateToCanonicalIdMap,
                    out var remappedTemplateJson))
            {
                throw new SyncPullBlockedException(
                    $"품목 별칭을 참조하는 로컬 청구 템플릿을 안전하게 해석할 수 없어 pull을 중단했습니다. profile={profile.Id:D}");
            }

            if (!string.Equals(
                    profile.BillingTemplateJson ?? string.Empty,
                    remappedTemplateJson,
                    StringComparison.Ordinal))
            {
                remappedBillingTemplates[profile.Id] = remappedTemplateJson;
            }
        }

        var itemPriceGrades = await _db.ItemPriceGrades.IgnoreQueryFilters()
            .Where(grade => duplicateIds.Contains(grade.ItemId) || canonicalIds.Contains(grade.ItemId))
            .ToListAsync(ct);
        var conflictingPriceGradeGroup = itemPriceGrades
            .GroupBy(grade => new
            {
                ItemId = duplicateToCanonicalIdMap.TryGetValue(grade.ItemId, out var canonicalId)
                    ? canonicalId
                    : grade.ItemId,
                grade.PriceGradeOptionId
            })
            .FirstOrDefault(group =>
                group.Count() > 1 &&
                group.Any(grade => duplicateIds.Contains(grade.ItemId)));
        if (conflictingPriceGradeGroup is not null)
        {
            throw new InvalidOperationException(
                $"Item alias remap would create a duplicate price-grade key: " +
                $"ItemId={conflictingPriceGradeGroup.Key.ItemId:D}, " +
                $"PriceGradeOptionId={conflictingPriceGradeGroup.Key.PriceGradeOptionId:D}.");
        }

        var priceGradeRemapUpdatedAtUtc = DateTime.UtcNow;
        foreach (var grade in itemPriceGrades.Where(current => duplicateIds.Contains(current.ItemId)))
        {
            if (!duplicateToCanonicalIdMap.TryGetValue(grade.ItemId, out var canonicalId))
                continue;

            grade.ItemId = canonicalId;
            if (grade.IsDirty)
            {
                grade.UpdatedAtUtc = priceGradeRemapUpdatedAtUtc > grade.UpdatedAtUtc
                    ? priceGradeRemapUpdatedAtUtc
                    : grade.UpdatedAtUtc == DateTime.MaxValue
                        ? grade.UpdatedAtUtc.AddTicks(-1)
                        : grade.UpdatedAtUtc.AddTicks(1);
            }
        }

        var invoiceLines = await _db.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(ct);
        var remappedInvoiceIds = new HashSet<Guid>();
        foreach (var line in invoiceLines)
        {
            if (line.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(line.ItemId.Value, out var canonicalId))
            {
                line.ItemId = canonicalId;
                if (line.InvoiceId != Guid.Empty)
                    remappedInvoiceIds.Add(line.InvoiceId);
            }
        }
        if (remappedInvoiceIds.Count > 0)
        {
            await _db.Invoices.IgnoreQueryFilters()
                .Where(invoice =>
                    invoice.IsDirty &&
                    remappedInvoiceIds.Contains(invoice.Id))
                .LoadAsync(ct);
        }

        var invoiceLineSerials = await _db.InvoiceLineSerials
            .Where(serial => serial.ItemId.HasValue && duplicateIds.Contains(serial.ItemId.Value))
            .ToListAsync(ct);
        foreach (var serial in invoiceLineSerials)
        {
            if (serial.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(serial.ItemId.Value, out var canonicalId))
                serial.ItemId = canonicalId;
        }

        var rentalAssets = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.ItemId.HasValue && duplicateIds.Contains(asset.ItemId.Value))
            .ToListAsync(ct);
        foreach (var asset in rentalAssets)
        {
            if (asset.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(asset.ItemId.Value, out var canonicalId))
                asset.ItemId = canonicalId;
        }

        foreach (var profile in rentalBillingProfiles)
        {
            if (remappedBillingTemplates.TryGetValue(
                    profile.Id,
                    out var remappedTemplateJson))
                profile.BillingTemplateJson = remappedTemplateJson;
        }

        var serialLedgers = await _db.SerialLedgers
            .Where(ledger => ledger.ItemId.HasValue && duplicateIds.Contains(ledger.ItemId.Value))
            .ToListAsync(ct);
        foreach (var ledger in serialLedgers)
        {
            if (ledger.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(ledger.ItemId.Value, out var canonicalId))
                ledger.ItemId = canonicalId;
        }

        var inventoryTransferLines = await _db.InventoryTransferLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(ct);
        var remappedTransferIds = new HashSet<Guid>();
        foreach (var line in inventoryTransferLines)
        {
            if (line.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(line.ItemId.Value, out var canonicalId))
            {
                line.ItemId = canonicalId;
                if (line.TransferId != Guid.Empty)
                    remappedTransferIds.Add(line.TransferId);
            }
        }
        if (remappedTransferIds.Count > 0)
        {
            await _db.InventoryTransfers.IgnoreQueryFilters()
                .Where(transfer =>
                    transfer.IsDirty &&
                    remappedTransferIds.Contains(transfer.Id))
                .LoadAsync(ct);
        }

        var inventoryMovements = await _db.InventoryMovements
            .Where(movement => movement.ItemId.HasValue && duplicateIds.Contains(movement.ItemId.Value))
            .ToListAsync(ct);
        foreach (var movement in inventoryMovements)
        {
            if (movement.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(movement.ItemId.Value, out var canonicalId))
                movement.ItemId = canonicalId;
        }

        var stockLayers = await _db.StockLayers
            .Where(layer => layer.ItemId.HasValue && duplicateIds.Contains(layer.ItemId.Value))
            .ToListAsync(ct);
        foreach (var layer in stockLayers)
        {
            if (layer.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(layer.ItemId.Value, out var canonicalId))
                layer.ItemId = canonicalId;
        }

        var warehouseStocks = await _db.ItemWarehouseStocks
            .Where(stock => duplicateIds.Contains(stock.ItemId) || canonicalIds.Contains(stock.ItemId))
            .ToListAsync(ct);
        var canonicalStockLookup = warehouseStocks
            .Where(stock => canonicalIds.Contains(stock.ItemId))
            .GroupBy(stock => BuildItemWarehouseStockKey(stock.ItemId, stock.WarehouseCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var stock in warehouseStocks.Where(current => duplicateIds.Contains(current.ItemId)).ToList())
        {
            if (!duplicateToCanonicalIdMap.TryGetValue(stock.ItemId, out var canonicalId))
                continue;

            var stockKey = BuildItemWarehouseStockKey(canonicalId, stock.WarehouseCode);
            if (canonicalStockLookup.TryGetValue(stockKey, out var canonicalStock))
            {
                canonicalStock.Quantity += stock.Quantity;
                if (stock.UpdatedAtUtc > canonicalStock.UpdatedAtUtc)
                    canonicalStock.UpdatedAtUtc = stock.UpdatedAtUtc;

                _db.ItemWarehouseStocks.Remove(stock);
                continue;
            }

            var migratedStock = new LocalItemWarehouseStock
            {
                ItemId = canonicalId,
                WarehouseCode = stock.WarehouseCode,
                Quantity = stock.Quantity,
                UpdatedAtUtc = stock.UpdatedAtUtc,
                Revision = stock.Revision
            };
            canonicalStockLookup[stockKey] = migratedStock;
            _db.ItemWarehouseStocks.Add(migratedStock);
            _db.ItemWarehouseStocks.Remove(stock);
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task ThrowIfPendingItemAliasReferenceOutboxesAsync(
        IReadOnlyDictionary<Guid, Guid> duplicateToCanonicalIdMap,
        CancellationToken ct)
    {
        if (duplicateToCanonicalIdMap.Count == 0)
            return;

        var duplicateIds = duplicateToCanonicalIdMap.Keys.Distinct().ToList();
        var priceGradeIds = await _db.ItemPriceGrades.IgnoreQueryFilters()
            .Where(grade => duplicateIds.Contains(grade.ItemId))
            .Select(grade => grade.Id)
            .Distinct()
            .ToListAsync(ct);
        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            priceGradeIds,
            [nameof(LocalItemPriceGrade), "ItemPriceGrade"],
            "품목 별칭 가격등급 remap",
            ct);

        var invoiceIds = await _db.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .Select(line => line.InvoiceId)
            .Concat(_db.InvoiceLineSerials
                .Where(serial => serial.ItemId.HasValue && duplicateIds.Contains(serial.ItemId.Value))
                .Select(serial => serial.InvoiceId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToListAsync(ct);
        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            invoiceIds,
            [nameof(LocalInvoice), "Invoice"],
            "품목 별칭 전표 remap",
            ct);

        var rentalAssetIds = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.ItemId.HasValue && duplicateIds.Contains(asset.ItemId.Value))
            .Select(asset => asset.Id)
            .Distinct()
            .ToListAsync(ct);
        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            rentalAssetIds,
            [nameof(LocalRentalAsset), "RentalAsset"],
            "품목 별칭 렌탈 자산 remap",
            ct);

        var profileIds = new List<Guid>();
        foreach (var profile in await _db.RentalBillingProfiles.IgnoreQueryFilters()
                     .AsNoTracking()
                     .ToListAsync(ct))
        {
            if (!TryRemapRentalBillingTemplateCatalogItemReferences(
                    profile.BillingTemplateJson,
                    duplicateToCanonicalIdMap,
                    out var remappedTemplateJson))
            {
                throw new SyncPullBlockedException(
                    $"품목 별칭을 참조하는 로컬 청구 템플릿을 안전하게 해석할 수 없어 pull을 중단했습니다. profile={profile.Id:D}");
            }

            if (!string.Equals(
                    profile.BillingTemplateJson ?? string.Empty,
                    remappedTemplateJson,
                    StringComparison.Ordinal))
            {
                profileIds.Add(profile.Id);
            }
        }
        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            profileIds,
            [nameof(LocalRentalBillingProfile), "RentalBillingProfile"],
            "품목 별칭 청구 프로필 remap",
            ct);

        var transferIds = await _db.InventoryTransferLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .Select(line => line.TransferId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToListAsync(ct);
        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            transferIds,
            [nameof(LocalInventoryTransfer), "InventoryTransfer"],
            "품목 별칭 재고이동 remap",
            ct);
    }

    private async Task<HashSet<Guid>> RemoveStalePulledRentalAssetConflictsAsync(
        IReadOnlyList<RentalAssetDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return [];

        var incomingByManagementNumber = BuildIncomingRentalAssetLookup(
            dtos,
            dto => BuildScopedRentalAssetNaturalKey(dto, dto.ManagementNumber));
        var incomingByManagementId = BuildIncomingRentalAssetLookup(
            dtos,
            dto => BuildScopedRentalAssetNaturalKey(dto, dto.ManagementId));
        var incomingByAssetKey = BuildIncomingRentalAssetLookup(
            dtos,
            dto => BuildScopedRentalAssetNaturalKey(dto, dto.AssetKey));

        if (incomingByManagementNumber.Count == 0 &&
            incomingByManagementId.Count == 0 &&
            incomingByAssetKey.Count == 0)
        {
            return [];
        }

        var managementNumbers = BuildIncomingRentalAssetCandidateKeys(dtos, dto => dto.ManagementNumber);
        var managementIds = BuildIncomingRentalAssetCandidateKeys(dtos, dto => dto.ManagementId);
        var assetKeys = BuildIncomingRentalAssetCandidateKeys(dtos, dto => dto.AssetKey);

        var candidateQuery = _db.RentalAssets.IgnoreQueryFilters().Where(asset =>
            (managementNumbers.Count > 0 && managementNumbers.Contains(asset.ManagementNumber)) ||
            (managementIds.Count > 0 && managementIds.Contains(asset.ManagementId)) ||
            (assetKeys.Count > 0 && assetKeys.Contains(asset.AssetKey)));

        var candidates = await candidateQuery.ToListAsync(ct);
        if (candidates.Count == 0)
            return [];

        var staleConflictIds = new HashSet<Guid>();
        var skippedIncomingIds = new HashSet<Guid>();
        var dirtyConflictDetails = new List<string>();
        var recoveredDirtyConflictDetails = new List<string>();

        foreach (var candidate in candidates)
        {
            var matchingIncomingIds = GetMatchingIncomingRentalAssetIds(
                candidate,
                incomingByManagementNumber,
                incomingByManagementId,
                incomingByAssetKey);

            if (matchingIncomingIds.Count == 0)
                continue;
            if (matchingIncomingIds.Count != 1)
            {
                throw new SyncPullBlockedException(
                    $"렌탈 자산 natural key가 여러 incoming ID와 교차 일치하여 pull을 중단했습니다. asset={candidate.Id:D}");
            }
            if (matchingIncomingIds.Contains(candidate.Id))
                continue;

            if (candidate.IsDirty)
            {
                if (CanRecoverDirtyRentalAssetPullConflict(candidate, matchingIncomingIds, dtos))
                {
                    staleConflictIds.Add(candidate.Id);
                    recoveredDirtyConflictDetails.Add(
                        $"{candidate.ManagementNumber}/{candidate.ManagementId} -> {candidate.Id}");
                    continue;
                }

                foreach (var incomingId in matchingIncomingIds)
                    skippedIncomingIds.Add(incomingId);

                dirtyConflictDetails.Add(
                    $"{candidate.ManagementNumber}/{candidate.ManagementId} -> {candidate.Id}");
                continue;
            }

            staleConflictIds.Add(candidate.Id);
        }

        if (staleConflictIds.Count > 0)
        {
            await ThrowIfPendingCanonicalDeleteOutboxAsync(
                staleConflictIds,
                [nameof(LocalRentalAsset), "RentalAsset"],
                "렌탈 자산 충돌",
                ct);
            if (await _db.RentalAssetAssignmentHistories
                    .IgnoreQueryFilters()
                    .AnyAsync(history =>
                        staleConflictIds.Contains(history.AssetId),
                        ct))
            {
                throw new SyncPullBlockedException(
                    "렌탈 자산 canonical delete가 배정 이력을 orphan 처리할 수 있어 pull을 중단했습니다.");
            }
            _db.ChangeTracker.Clear();
            await _db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => staleConflictIds.Contains(asset.Id))
                .ExecuteDeleteAsync(ct);
            _db.ChangeTracker.Clear();

            AppLogger.Warn(
                "SYNC",
                $"렌탈 자산 pull 충돌 복구: 관리번호/관리ID가 같은 로컬 자산 {staleConflictIds.Count}건을 서버 기준으로 정리했습니다.");
        }

        if (recoveredDirtyConflictDetails.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"렌탈 자산 pull 충돌 자동 복구: 서버 반영된 휴지통/동기화 결과와 같은 식별값의 로컬 dirty 자산 {recoveredDirtyConflictDetails.Count}건을 서버 기준으로 정리했습니다. " +
                $"details={string.Join(", ", recoveredDirtyConflictDetails.Take(10))}");
        }

        if (dirtyConflictDetails.Count > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"렌탈 자산 pull 충돌 보류: 로컬 수정 중인 자산 {dirtyConflictDetails.Count}건은 덮어쓰지 않았습니다. " +
                $"details={string.Join(", ", dirtyConflictDetails.Take(10))}");
        }

        return skippedIncomingIds;
    }

    private static bool CanRecoverDirtyRentalAssetPullConflict(
        LocalRentalAsset candidate,
        IReadOnlyCollection<Guid> matchingIncomingIds,
        IReadOnlyList<RentalAssetDto> incomingDtos)
    {
        if (matchingIncomingIds.Count == 0)
            return false;

        var matchingIncoming = incomingDtos
            .Where(dto => matchingIncomingIds.Contains(dto.Id))
            .ToList();
        if (matchingIncoming.Count == 0)
            return false;

        return matchingIncoming.Any(dto =>
            !dto.IsDeleted &&
            RentalAssetBusinessDatabaseMatches(candidate, dto) &&
            (
                NaturalKeysMatch(candidate.ManagementNumber, dto.ManagementNumber) ||
                NaturalKeysMatch(candidate.ManagementId, dto.ManagementId) ||
                NaturalKeysMatch(candidate.AssetKey, dto.AssetKey)));
    }

    private static bool NaturalKeysMatch(string? left, string? right)
    {
        var normalizedLeft = NormalizeRentalAssetNaturalKey(left);
        var normalizedRight = NormalizeRentalAssetNaturalKey(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, HashSet<Guid>> BuildIncomingRentalBillingProfileLookup(
        IReadOnlyList<RentalBillingProfileDto> dtos,
        Func<RentalBillingProfileDto, string?> keySelector)
    {
        var lookup = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in dtos)
        {
            var normalizedKey = NormalizeRentalBillingProfileNaturalKey(keySelector(dto));
            if (string.IsNullOrWhiteSpace(normalizedKey))
                continue;

            if (!lookup.TryGetValue(normalizedKey, out var ids))
            {
                ids = [];
                lookup[normalizedKey] = ids;
            }

            ids.Add(dto.Id);
        }

        return lookup;
    }

    private static Dictionary<string, HashSet<Guid>> BuildIncomingRentalAssetLookup(
        IReadOnlyList<RentalAssetDto> dtos,
        Func<RentalAssetDto, string?> keySelector)
    {
        var lookup = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in dtos)
        {
            var normalizedKey = NormalizeRentalAssetNaturalKey(keySelector(dto));
            if (string.IsNullOrWhiteSpace(normalizedKey))
                continue;

            if (!lookup.TryGetValue(normalizedKey, out var ids))
            {
                ids = [];
                lookup[normalizedKey] = ids;
            }

            ids.Add(dto.Id);
        }

        return lookup;
    }

    private static List<string> BuildIncomingRentalAssetCandidateKeys(
        IReadOnlyList<RentalAssetDto> dtos,
        Func<RentalAssetDto, string?> keySelector)
        => dtos
            .Select(dto => NormalizeRentalAssetNaturalKey(keySelector(dto)))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildScopedRentalAssetNaturalKey(RentalAssetDto dto, string? value)
    {
        var normalizedKey = NormalizeRentalAssetNaturalKey(value);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return string.Empty;

        var businessDatabaseName = ResolveRentalBusinessDatabaseName(
            dto.TenantCode,
            dto.OfficeCode,
            dto.ResponsibleOfficeCode);
        return $"{businessDatabaseName}|{normalizedKey}";
    }

    private static string BuildScopedRentalAssetNaturalKey(LocalRentalAsset asset, string? value)
    {
        var normalizedKey = NormalizeRentalAssetNaturalKey(value);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return string.Empty;

        var businessDatabaseName = ResolveRentalBusinessDatabaseName(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ResponsibleOfficeCode);
        return $"{businessDatabaseName}|{normalizedKey}";
    }

    private static bool RentalAssetBusinessDatabaseMatches(LocalRentalAsset candidate, RentalAssetDto dto)
        => string.Equals(
            ResolveRentalBusinessDatabaseName(candidate.TenantCode, candidate.OfficeCode, candidate.ResponsibleOfficeCode),
            ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode),
            StringComparison.OrdinalIgnoreCase);

    private static HashSet<Guid> GetMatchingIncomingRentalAssetIds(
        LocalRentalAsset candidate,
        IReadOnlyDictionary<string, HashSet<Guid>> incomingByManagementNumber,
        IReadOnlyDictionary<string, HashSet<Guid>> incomingByManagementId,
        IReadOnlyDictionary<string, HashSet<Guid>> incomingByAssetKey)
    {
        var matchingIds = new HashSet<Guid>();

        AddIncomingRentalAssetIds(
            matchingIds,
            incomingByManagementNumber,
            BuildScopedRentalAssetNaturalKey(candidate, candidate.ManagementNumber));
        AddIncomingRentalAssetIds(
            matchingIds,
            incomingByManagementId,
            BuildScopedRentalAssetNaturalKey(candidate, candidate.ManagementId));
        AddIncomingRentalAssetIds(
            matchingIds,
            incomingByAssetKey,
            BuildScopedRentalAssetNaturalKey(candidate, candidate.AssetKey));

        return matchingIds;
    }

    private static HashSet<Guid> GetMatchingIncomingRentalBillingProfileIds(
        LocalRentalBillingProfile candidate,
        IReadOnlyDictionary<string, HashSet<Guid>> incomingByProfileKey)
    {
        var matchingIds = new HashSet<Guid>();
        AddIncomingRentalBillingProfileIds(matchingIds, incomingByProfileKey, candidate.ProfileKey);
        return matchingIds;
    }

    private static void AddIncomingRentalAssetIds(
        HashSet<Guid> target,
        IReadOnlyDictionary<string, HashSet<Guid>> lookup,
        string? value)
    {
        var normalizedKey = NormalizeRentalAssetNaturalKey(value);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return;

        if (!lookup.TryGetValue(normalizedKey, out var ids))
            return;

        foreach (var id in ids)
            target.Add(id);
    }

    private static void AddIncomingRentalBillingProfileIds(
        HashSet<Guid> target,
        IReadOnlyDictionary<string, HashSet<Guid>> lookup,
        string? value)
    {
        var normalizedKey = NormalizeRentalBillingProfileNaturalKey(value);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return;

        if (!lookup.TryGetValue(normalizedKey, out var ids))
            return;

        foreach (var id in ids)
            target.Add(id);
    }

    private static List<LocalRentalBillingProfile> FindConflictingLocalRentalBillingProfiles(
        IEnumerable<LocalRentalBillingProfile> profiles,
        string? profileKey,
        Guid incomingId)
    {
        var normalizedKey = NormalizeRentalBillingProfileNaturalKey(profileKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return [];

        return profiles
            .Where(profile => profile.Id != incomingId)
            .Where(profile => string.Equals(
                NormalizeRentalBillingProfileNaturalKey(profile.ProfileKey),
                normalizedKey,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string NormalizeRentalBillingProfileNaturalKey(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static bool ItemsSharePullNaturalKey(LocalItem local, ItemDto incoming)
    {
        if (!string.Equals(
                TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(local.TenantCode, local.OfficeCode),
                TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(incoming.TenantCode, incoming.OfficeCode),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(local.OfficeCode, OfficeCodeCatalog.Shared),
                OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(incoming.OfficeCode, OfficeCodeCatalog.Shared),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var localMaterial = NormalizeItemIdentityValue(local.MaterialNumber);
        var incomingMaterial = NormalizeItemIdentityValue(incoming.MaterialNumber);
        if (HasMeaningfulItemIdentityValue(localMaterial) && HasMeaningfulItemIdentityValue(incomingMaterial))
            return string.Equals(localMaterial, incomingMaterial, StringComparison.OrdinalIgnoreCase);

        var localSerial = NormalizeItemIdentityValue(local.SerialNumber);
        var incomingSerial = NormalizeItemIdentityValue(incoming.SerialNumber);
        if (HasMeaningfulItemIdentityValue(localSerial) && HasMeaningfulItemIdentityValue(incomingSerial))
            return string.Equals(localSerial, incomingSerial, StringComparison.OrdinalIgnoreCase);

        if (HasMeaningfulItemIdentityValue(localMaterial) ||
            HasMeaningfulItemIdentityValue(incomingMaterial) ||
            HasMeaningfulItemIdentityValue(localSerial) ||
            HasMeaningfulItemIdentityValue(incomingSerial))
        {
            return false;
        }

        return string.Equals(
            BuildItemDescriptorKey(local),
            BuildItemDescriptorKey(incoming),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildItemDescriptorKey(LocalItem item)
        => BuildItemDescriptorKey(
            item.NameMatchKey,
            item.NameOriginal,
            item.SpecificationMatchKey,
            item.SpecificationOriginal,
            item.CategoryName,
            item.ItemKind,
            item.TrackingType,
            item.IsRental);

    private static string BuildItemDescriptorKey(ItemDto item)
        => BuildItemDescriptorKey(
            item.NameMatchKey,
            item.NameOriginal,
            item.SpecificationMatchKey,
            item.SpecificationOriginal,
            item.CategoryName,
            item.ItemKind,
            item.TrackingType,
            item.IsRental);

    private static string BuildItemDescriptorKey(
        string? nameMatchKey,
        string? nameOriginal,
        string? specificationMatchKey,
        string? specificationOriginal,
        string? categoryName,
        string? itemKind,
        string? trackingType,
        bool isRental)
    {
        var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
            trackingType,
            itemKind,
            categoryName,
            isRental);
        var normalizedItemKind = ItemOperationalPolicy.NormalizeItemKind(
            itemKind,
            trackingType,
            categoryName,
            isRental);

        return string.Join('|', new[]
        {
            string.IsNullOrWhiteSpace(nameMatchKey)
                ? RentalCatalogValueNormalizer.NormalizeLooseKey(nameOriginal)
                : RentalCatalogValueNormalizer.NormalizeLooseKey(nameMatchKey),
            string.IsNullOrWhiteSpace(specificationMatchKey)
                ? RentalCatalogValueNormalizer.NormalizeLooseKey(specificationOriginal)
                : RentalCatalogValueNormalizer.NormalizeLooseKey(specificationMatchKey),
            RentalCatalogValueNormalizer.NormalizeLooseKey(categoryName),
            normalizedItemKind.Trim().ToUpperInvariant(),
            normalizedTrackingType.Trim().ToUpperInvariant()
        });
    }

    private Task<List<LocalItemWarehouseStock>>
        LoadInventoryTrackedItemWarehouseStocksForPushAsync(
            CancellationToken ct)
        => LoadInventoryTrackedItemWarehouseStocksForPushForSessionAsync(
            _session,
            itemIds: null,
            ct: ct);

    private async Task<List<LocalItemWarehouseStock>>
        LoadInventoryTrackedItemWarehouseStocksForPushForSessionAsync(
            SessionState session,
            IReadOnlySet<Guid>? itemIds,
            CancellationToken ct)
    {
        var rows = await (from stock in _db.ItemWarehouseStocks.AsNoTracking()
                          join item in _db.Items.IgnoreQueryFilters().AsNoTracking()
                              on stock.ItemId equals item.Id
                          select new { Stock = stock, Item = item })
            .ToListAsync(ct);

        return rows
            .Where(row =>
                !row.Item.IsDeleted &&
                (itemIds is null ||
                 itemIds.Contains(row.Item.Id)) &&
                SupportsInventoryTracking(row.Item) &&
                _local.CanWriteItemScope(row.Item, session) &&
                CanWriteItemWarehouseStockForPush(
                    row.Stock,
                    row.Item,
                    session))
            .Select(row => row.Stock)
            .ToList();
    }

    private async Task<int> PruneNonInventoryItemWarehouseStocksAsync(CancellationToken ct)
    {
        var rows = await (from stock in _db.ItemWarehouseStocks
                          join item in _db.Items.IgnoreQueryFilters()
                              on stock.ItemId equals item.Id
                          select new { Stock = stock, Item = item })
            .ToListAsync(ct);

        var targetRows = rows
            .Where(row => row.Item.IsDeleted || !SupportsInventoryTracking(row.Item))
            .ToList();
        if (targetRows.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var removedCount = 0;
        foreach (var group in targetRows.GroupBy(row => row.Item.Id))
        {
            var item = group.First().Item;
            var stocks = group.Select(row => row.Stock).ToList();
            if (stocks.Count > 0)
            {
                _db.ItemWarehouseStocks.RemoveRange(stocks);
                removedCount += stocks.Count;
            }

            if (item.IsDeleted)
                continue;

            var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
                item.TrackingType,
                item.ItemKind,
                item.CategoryName,
                item.IsRental);
            var normalizedItemKind = ItemOperationalPolicy.NormalizeItemKind(
                item.ItemKind,
                normalizedTrackingType,
                item.CategoryName,
                item.IsRental);
            var expectedIsRental = string.Equals(normalizedTrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal);
            var expectedIsSale = !expectedIsRental;
            var changed = false;

            if (!string.Equals(item.TrackingType, normalizedTrackingType, StringComparison.Ordinal))
            {
                item.TrackingType = normalizedTrackingType;
                changed = true;
            }

            if (!string.Equals(item.ItemKind, normalizedItemKind, StringComparison.Ordinal))
            {
                item.ItemKind = normalizedItemKind;
                changed = true;
            }

            if (item.IsRental != expectedIsRental)
            {
                item.IsRental = expectedIsRental;
                changed = true;
            }

            if (item.IsSale != expectedIsSale)
            {
                item.IsSale = expectedIsSale;
                changed = true;
            }

            if (item.CurrentStock != 0m)
            {
                item.CurrentStock = 0m;
                changed = true;
            }

            if (item.SafetyStock != 0m)
            {
                item.SafetyStock = 0m;
                changed = true;
            }

            if (changed)
                item.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
        return removedCount;
    }

    private static bool SupportsInventoryTracking(LocalItem item)
    {
        var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
            item.TrackingType,
            item.ItemKind,
            item.CategoryName,
            item.IsRental);
        return ItemOperationalPolicy.SupportsInventory(normalizedTrackingType);
    }

    private bool CanWriteItemWarehouseStockForPush(LocalItemWarehouseStock stock, LocalItem item)
        => CanWriteItemWarehouseStockForPush(
            stock,
            item,
            _session);

    private bool CanWriteItemWarehouseStockForPush(
        LocalItemWarehouseStock stock,
        LocalItem item,
        SessionState session)
        => CanWriteItemWarehouseCodeForPush(
            stock.WarehouseCode,
            item,
            session);

    private bool CanWriteItemWarehouseCodeForPush(
        string? warehouseCode,
        LocalItem item)
        => CanWriteItemWarehouseCodeForPush(
            warehouseCode,
            item,
            _session);

    private bool CanWriteItemWarehouseCodeForPush(
        string? warehouseCode,
        LocalItem item,
        SessionState session)
    {
        if (session.HasGlobalDataScope)
            return true;

        var normalizedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            warehouseCode,
            item.OfficeCode,
            session.OfficeCode);
        var writableWarehouseCodes = _local
            .GetWritableOfficeCodesForSession(session)
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return writableWarehouseCodes.Contains(normalizedWarehouseCode);
    }

    private static string NormalizeItemIdentityValue(string? value)
        => RentalCatalogValueNormalizer.NormalizeLooseKey(value);

    private static bool HasMeaningfulItemIdentityValue(string? value)
    {
        var normalized = NormalizeItemIdentityValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized != "미상" &&
               normalized != "UNKNOWN" &&
               normalized != "NONE" &&
               normalized != "NA" &&
               normalized != "N/A" &&
               normalized != "없음";
    }

    private static string BuildItemWarehouseStockKey(Guid itemId, string? warehouseCode)
        => $"{itemId:D}|{NormalizeItemWarehouseStockLogicalWarehouseCode(warehouseCode)}";

    private static string
        NormalizeItemWarehouseStockLogicalWarehouseCode(
            string? warehouseCode)
        => OfficeCodeCatalog.TryNormalizeWarehouseCode(
                warehouseCode,
                out var canonicalWarehouseCode)
            ? canonicalWarehouseCode
            : (warehouseCode ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

    private async Task UpsertPulledSelectionOptionsAsync<TLocal, TDto>(
        IReadOnlyList<TDto> dtos,
        DbSet<TLocal> set,
        Func<TDto, TLocal> toLocal,
        Func<TLocal, string> nameSelector,
        CancellationToken ct,
        bool allowRetry = true)
        where TLocal : class, ILocalSyncEntity
        where TDto : class
    {
        try
        {
            if (dtos.Count == 0)
                return;

            var incomingOptions = dtos
                .Select(toLocal)
                .Select(local =>
                {
                    local.IsDirty = false;
                    return local;
                })
                .Where(local => !string.IsNullOrWhiteSpace(NormalizeOptionName(nameSelector(local))))
                .GroupBy(local => local.Id)
                .Select(group => group
                    .OrderByDescending(entity => entity.Revision)
                    .ThenByDescending(entity => entity.UpdatedAtUtc)
                    .ThenByDescending(entity => entity.CreatedAtUtc)
                    .First())
                .GroupBy(local => NormalizeOptionName(nameSelector(local)), StringComparer.CurrentCultureIgnoreCase)
                .Select(group => group
                    .OrderByDescending(entity => entity.Revision)
                    .ThenByDescending(entity => entity.UpdatedAtUtc)
                    .ThenByDescending(entity => entity.CreatedAtUtc)
                    .First())
                .ToList();

            var initialExistingEntities = await set.IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync(ct);
            var logicalMutationIds = new HashSet<Guid>();
            foreach (var local in incomingOptions)
            {
                var normalizedName = NormalizeOptionName(nameSelector(local));
                var existing = initialExistingEntities.FirstOrDefault(entity => entity.Id == local.Id);
                if (existing is not null)
                {
                    if (existing.IsDirty &&
                        !CanAcceptServerSelectionOptionSnapshot(existing, local, nameSelector))
                    {
                        continue;
                    }

                    if (!existing.IsDirty && existing.UpdatedAtUtc > local.UpdatedAtUtc)
                        continue;
                }

                var localDeletion = initialExistingEntities
                    .Where(entity =>
                        string.Equals(
                            NormalizeOptionName(nameSelector(entity)),
                            normalizedName,
                            StringComparison.CurrentCultureIgnoreCase) &&
                        (entity.IsDeleted || !GetOptionalBoolProperty(entity, "IsActive", true)))
                    .OrderByDescending(entity => entity.UpdatedAtUtc)
                    .FirstOrDefault();
                if (localDeletion is not null && localDeletion.UpdatedAtUtc >= local.UpdatedAtUtc)
                    continue;

                var conflicts = initialExistingEntities
                    .Where(entity =>
                        entity.Id != local.Id &&
                        !entity.IsDeleted &&
                        string.Equals(
                            NormalizeOptionName(nameSelector(entity)),
                            normalizedName,
                            StringComparison.CurrentCultureIgnoreCase))
                    .ToList();
                if (conflicts.Any(entity => entity.IsDirty))
                    continue;

                logicalMutationIds.UnionWith(conflicts.Select(entity => entity.Id));
            }

            var optionOutboxEntityNames = typeof(TLocal) == typeof(LocalPriceGradeOption)
                ? new[] { nameof(LocalPriceGradeOption), "PriceGradeOption" }
                : typeof(TLocal) == typeof(LocalTradeTypeOption)
                    ? new[] { nameof(LocalTradeTypeOption), "TradeTypeOption" }
                    : typeof(TLocal) == typeof(LocalItemCategoryOption)
                        ? new[] { nameof(LocalItemCategoryOption), "ItemCategoryOption" }
                        : [];
            if (typeof(TLocal) == typeof(LocalPriceGradeOption) &&
                logicalMutationIds.Count > 0 &&
                await _db.ItemPriceGrades.IgnoreQueryFilters()
                    .AnyAsync(grade =>
                        logicalMutationIds.Contains(grade.PriceGradeOptionId),
                        ct))
            {
                throw new SyncPullBlockedException(
                    "가격등급 옵션 canonicalization이 품목 가격등급 참조를 orphan 처리할 수 있어 pull을 중단했습니다.");
            }
            await ThrowIfPendingCanonicalDeleteOutboxAsync(
                logicalMutationIds,
                optionOutboxEntityNames,
                $"{typeof(TLocal).Name} canonicalization",
                ct);

            foreach (var local in incomingOptions)
            {
                _db.ChangeTracker.Clear();
                var existingEntities = await set.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
                var normalizedName = NormalizeOptionName(nameSelector(local));
                if (string.IsNullOrWhiteSpace(normalizedName))
                    continue;

                var existing = existingEntities.FirstOrDefault(entity => entity.Id == local.Id);
                if (existing is not null)
                {
                    if (existing.IsDirty)
                    {
                        if (!CanAcceptServerSelectionOptionSnapshot(existing, local, nameSelector))
                            continue;

                        await ApplySelectionOptionServerSnapshotAsync(local, ct);
                        continue;
                    }

                    if (existing.UpdatedAtUtc > local.UpdatedAtUtc)
                        continue;
                }

                var localDeletion = existingEntities
                    .Where(entity =>
                        string.Equals(
                            NormalizeOptionName(nameSelector(entity)),
                            normalizedName,
                            StringComparison.CurrentCultureIgnoreCase) &&
                        (entity.IsDeleted || !GetOptionalBoolProperty(entity, "IsActive", true)))
                    .OrderByDescending(entity => entity.UpdatedAtUtc)
                    .FirstOrDefault();

                if (localDeletion is not null && localDeletion.UpdatedAtUtc >= local.UpdatedAtUtc)
                {
                    AppLogger.Warn(
                        "SYNC",
                        $"선택옵션 pull 삭제상태 유지: {typeof(TLocal).Name} '{nameSelector(local)}' 서버값보다 로컬 삭제가 최신이라 복구하지 않습니다.");
                    continue;
                }

                var conflictingEntities = existingEntities
                    .Where(entity =>
                        entity.Id != local.Id &&
                        !entity.IsDeleted &&
                        string.Equals(
                            NormalizeOptionName(nameSelector(entity)),
                            normalizedName,
                            StringComparison.CurrentCultureIgnoreCase))
                    .ToList();

                if (conflictingEntities.Any(entity => entity.IsDirty))
                {
                    AppLogger.Warn(
                        "SYNC",
                        $"선택옵션 pull 충돌 보류: {typeof(TLocal).Name} '{nameSelector(local)}' 이름이 로컬 수정 중 데이터와 충돌해 서버값 적용을 건너뜁니다.");
                    continue;
                }

                if (conflictingEntities.Count > 0)
                {
                    var conflictingIds = conflictingEntities.Select(entity => entity.Id).ToList();
                    var trackedConflicts = await set.IgnoreQueryFilters()
                        .Where(entity => conflictingIds.Contains(entity.Id))
                        .ToListAsync(ct);
                    foreach (var conflict in trackedConflicts)
                    {
                        conflict.IsDeleted = true;
                        conflict.IsDirty = false;
                        conflict.UpdatedAtUtc = local.UpdatedAtUtc;
                        conflict.Revision = local.Revision;
                        SetOptionalBoolProperty(conflict, "IsActive", false);
                    }

                    await _db.SaveChangesAsync(ct);
                    _db.ChangeTracker.Clear();
                    existingEntities = await set.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
                    existing = existingEntities.FirstOrDefault(entity => entity.Id == local.Id);

                    AppLogger.Warn(
                        "SYNC",
                        $"선택옵션 pull 충돌 복구: {typeof(TLocal).Name} '{nameSelector(local)}' 이름 충돌 {conflictingEntities.Count}건을 정리했습니다.");
                }

                if (existing is null)
                {
                    set.Add(local);
                    await _db.SaveChangesAsync(ct);
                }
                else if (!existing.IsDirty)
                {
                    await ApplySelectionOptionServerSnapshotAsync(local, ct);
                }
            }
        }
        catch (DbUpdateConcurrencyException) when (allowRetry)
        {
            _db.ChangeTracker.Clear();
            await UpsertPulledSelectionOptionsAsync(dtos, set, toLocal, nameSelector, ct, allowRetry: false);
        }
    }

    private async Task UpsertPulledRentalManagementCompaniesAsync(
        IReadOnlyList<RentalManagementCompanyDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var incomingCompanies = dtos
            .Select(LocalMappings.ToLocal)
            .Select(local =>
            {
                local.IsDirty = false;
                return local;
            })
            .Where(local => !string.IsNullOrWhiteSpace(NormalizeRentalManagementCompanyCode(local.Code)))
            .GroupBy(local => local.Id)
            .Select(group => group
                .OrderByDescending(entity => entity.Revision)
                .ThenByDescending(entity => entity.UpdatedAtUtc)
                .ThenByDescending(entity => entity.CreatedAtUtc)
                .First())
            .GroupBy(local => NormalizeRentalManagementCompanyCode(local.Code), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entity => entity.Revision)
                .ThenByDescending(entity => entity.UpdatedAtUtc)
                .ThenByDescending(entity => entity.CreatedAtUtc)
                .First())
            .ToList();

        var existingCompanies = await _db.RentalManagementCompanies.IgnoreQueryFilters().ToListAsync(ct);

        var pendingPhysicalDeleteIds = new HashSet<Guid>();
        foreach (var local in incomingCompanies)
        {
            var normalizedCode = NormalizeRentalManagementCompanyCode(local.Code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                continue;

            var conflictingCompanies = existingCompanies
                .Where(company =>
                    company.Id != local.Id &&
                    string.Equals(
                        NormalizeRentalManagementCompanyCode(company.Code),
                        normalizedCode,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (conflictingCompanies.Any(company => company.IsDirty))
                continue;

            foreach (var conflict in conflictingCompanies)
                pendingPhysicalDeleteIds.Add(conflict.Id);

            var exactExisting = existingCompanies.FirstOrDefault(company => company.Id == local.Id);
            if (local.IsDeleted &&
                exactExisting is not null &&
                !exactExisting.IsDirty)
            {
                pendingPhysicalDeleteIds.Add(exactExisting.Id);
            }
        }

        await ThrowIfPendingCanonicalDeleteOutboxAsync(
            pendingPhysicalDeleteIds,
            [nameof(LocalRentalManagementCompany), "RentalManagementCompany"],
            "관리업체 canonical pull",
            ct);

        foreach (var local in incomingCompanies)
        {
            var normalizedCode = NormalizeRentalManagementCompanyCode(local.Code);
            if (string.IsNullOrWhiteSpace(normalizedCode))
                continue;

            var conflictingCompanies = existingCompanies
                .Where(company =>
                    company.Id != local.Id &&
                    string.Equals(
                        NormalizeRentalManagementCompanyCode(company.Code),
                        normalizedCode,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (conflictingCompanies.Any(company => company.IsDirty))
            {
                AppLogger.Warn(
                    "SYNC",
                    $"렌탈 관리업체 pull 충돌 보류: 코드 '{local.Code}' 가 로컬 수정 중 데이터와 충돌해 서버값 적용을 건너뜁니다.");
                continue;
            }

            if (conflictingCompanies.Count > 0)
            {
                var staleConflictIds = conflictingCompanies
                    .Select(company => company.Id)
                    .Distinct()
                    .ToList();

                _db.ChangeTracker.Clear();
                await _db.RentalManagementCompanies.IgnoreQueryFilters()
                    .Where(company => staleConflictIds.Contains(company.Id))
                    .ExecuteDeleteAsync(ct);
                _db.ChangeTracker.Clear();

                existingCompanies = await _db.RentalManagementCompanies.IgnoreQueryFilters().ToListAsync(ct);

                AppLogger.Warn(
                    "SYNC",
                    $"렌탈 관리업체 pull 충돌 복구: 코드 '{local.Code}' 충돌 {staleConflictIds.Count}건을 서버 기준으로 정리했습니다.");
            }

            var existing = existingCompanies.FirstOrDefault(company => company.Id == local.Id);

            if (local.IsDeleted)
            {
                if (existing is not null)
                {
                    if (existing.IsDirty)
                    {
                        AppLogger.Warn(
                            "SYNC",
                            $"렌탈 관리업체 pull 삭제 보류: 코드 '{local.Code}' 삭제가 로컬 수정 중 데이터와 충돌해 적용을 건너뜁니다.");
                        continue;
                    }

                    _db.Entry(existing).CurrentValues.SetValues(local);
                }

                continue;
            }

            if (existing is null)
            {
                _db.RentalManagementCompanies.Add(local);
                existingCompanies.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private void SetOptionalBoolProperty<TEntity>(TEntity entity, string propertyName, bool value)
        where TEntity : class
    {
        var entry = _db.Entry(entity);
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null || property.ClrType != typeof(bool))
            return;

        entry.Property(propertyName).CurrentValue = value;
    }

    private bool GetOptionalBoolProperty<TEntity>(TEntity entity, string propertyName, bool defaultValue)
        where TEntity : class
    {
        var entry = _db.Entry(entity);
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null || property.ClrType != typeof(bool))
            return defaultValue;

        var currentValue = entry.Property(propertyName).CurrentValue;
        return currentValue is bool value ? value : defaultValue;
    }

    private bool CanAcceptServerSelectionOptionSnapshot<TEntity>(
        TEntity existing,
        TEntity incoming,
        Func<TEntity, string> nameSelector)
        where TEntity : class, ILocalSyncEntity
    {
        if (existing.Id != incoming.Id)
            return false;

        if (!string.Equals(
                NormalizeOptionName(nameSelector(existing)),
                NormalizeOptionName(nameSelector(incoming)),
                StringComparison.CurrentCultureIgnoreCase))
            return false;

        if (existing.IsDeleted != incoming.IsDeleted)
            return false;

        if (GetOptionalBoolProperty(existing, "IsActive", true) != GetOptionalBoolProperty(incoming, "IsActive", true))
            return false;

        if (GetOptionalBoolProperty(existing, "IsSystemDefault", false) != GetOptionalBoolProperty(incoming, "IsSystemDefault", false))
            return false;

        if (GetOptionalIntProperty(existing, "SortOrder", 0) != GetOptionalIntProperty(incoming, "SortOrder", 0))
            return false;

        return existing.Revision <= incoming.Revision;
    }

    private async Task ApplySelectionOptionServerSnapshotAsync<TLocal>(TLocal snapshot, CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
    {
        switch (snapshot)
        {
            case LocalPriceGradeOption priceGrade:
                await _db.PriceGradeOptions.IgnoreQueryFilters()
                    .Where(option => option.Id == priceGrade.Id)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(option => option.Name, priceGrade.Name)
                            .SetProperty(option => option.PriceSource, priceGrade.PriceSource)
                            .SetProperty(option => option.SortOrder, priceGrade.SortOrder)
                            .SetProperty(option => option.IsSystemDefault, priceGrade.IsSystemDefault)
                            .SetProperty(option => option.IsActive, priceGrade.IsActive)
                            .SetProperty(option => option.IsDeleted, priceGrade.IsDeleted)
                            .SetProperty(option => option.CreatedAtUtc, priceGrade.CreatedAtUtc)
                            .SetProperty(option => option.UpdatedAtUtc, priceGrade.UpdatedAtUtc)
                            .SetProperty(option => option.Revision, priceGrade.Revision)
                            .SetProperty(option => option.IsDirty, false),
                        ct);
                return;

            case LocalTradeTypeOption tradeType:
                await _db.TradeTypeOptions.IgnoreQueryFilters()
                    .Where(option => option.Id == tradeType.Id)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(option => option.Name, tradeType.Name)
                            .SetProperty(option => option.AllowsSales, tradeType.AllowsSales)
                            .SetProperty(option => option.AllowsPurchase, tradeType.AllowsPurchase)
                            .SetProperty(option => option.SortOrder, tradeType.SortOrder)
                            .SetProperty(option => option.IsSystemDefault, tradeType.IsSystemDefault)
                            .SetProperty(option => option.IsActive, tradeType.IsActive)
                            .SetProperty(option => option.IsDeleted, tradeType.IsDeleted)
                            .SetProperty(option => option.CreatedAtUtc, tradeType.CreatedAtUtc)
                            .SetProperty(option => option.UpdatedAtUtc, tradeType.UpdatedAtUtc)
                            .SetProperty(option => option.Revision, tradeType.Revision)
                            .SetProperty(option => option.IsDirty, false),
                        ct);
                return;

            case LocalItemCategoryOption itemCategory:
                await _db.ItemCategoryOptions.IgnoreQueryFilters()
                    .Where(option => option.Id == itemCategory.Id)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(option => option.Name, itemCategory.Name)
                            .SetProperty(option => option.SortOrder, itemCategory.SortOrder)
                            .SetProperty(option => option.IsSystemDefault, itemCategory.IsSystemDefault)
                            .SetProperty(option => option.IsActive, itemCategory.IsActive)
                            .SetProperty(option => option.IsDeleted, itemCategory.IsDeleted)
                            .SetProperty(option => option.CreatedAtUtc, itemCategory.CreatedAtUtc)
                            .SetProperty(option => option.UpdatedAtUtc, itemCategory.UpdatedAtUtc)
                            .SetProperty(option => option.Revision, itemCategory.Revision)
                            .SetProperty(option => option.IsDirty, false),
                        ct);
                return;

            default:
                throw new InvalidOperationException($"지원하지 않는 선택옵션 snapshot 형식입니다: {typeof(TLocal).Name}");
        }
    }

    private int GetOptionalIntProperty<TEntity>(TEntity entity, string propertyName, int defaultValue)
        where TEntity : class
    {
        var entry = _db.Entry(entity);
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null || property.ClrType != typeof(int))
            return defaultValue;

        var currentValue = entry.Property(propertyName).CurrentValue;
        return currentValue is int value ? value : defaultValue;
    }

    private async Task UpsertPulledItemWarehouseStocksAsync(IReadOnlyList<ItemWarehouseStockDto> dtos, CancellationToken ct)
    {
        var incomingItemIds = dtos
            .Where(dto => dto.ItemId != Guid.Empty)
            .Select(dto => dto.ItemId)
            .ToHashSet();
        var incomingItems = incomingItemIds.Count == 0
            ? new List<LocalItem>()
            : await _db.Items
                .IgnoreQueryFilters()
                .Where(item => incomingItemIds.Contains(item.Id))
                .ToListAsync(ct);
        var nonInventoryIncomingItemIds = incomingItems
            .Where(item => item.IsDeleted || !SupportsInventoryTracking(item))
            .Select(item => item.Id)
            .ToHashSet();
        IReadOnlyList<ItemWarehouseStockDto> filteredDtos = nonInventoryIncomingItemIds.Count == 0
            ? dtos
            : dtos
                .Where(dto => !nonInventoryIncomingItemIds.Contains(dto.ItemId))
                .ToList();
        var logicalDtos = filteredDtos
            .GroupBy(
                dto => BuildItemWarehouseStockKey(
                    dto.ItemId,
                    dto.WarehouseCode),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .First())
            .ToList();
        var pulledKeys = logicalDtos
            .Select(dto => BuildItemWarehouseStockKey(dto.ItemId, dto.WarehouseCode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affectedItemIds = logicalDtos
            .Where(dto => dto.ItemId != Guid.Empty)
            .Select(dto => dto.ItemId)
            .ToHashSet();
        affectedItemIds.UnionWith(nonInventoryIncomingItemIds);

        if (nonInventoryIncomingItemIds.Count > 0)
        {
            var staleStocks = await _db.ItemWarehouseStocks
                .Where(stock => nonInventoryIncomingItemIds.Contains(stock.ItemId))
                .ToListAsync(ct);
            if (staleStocks.Count > 0)
                _db.ItemWarehouseStocks.RemoveRange(staleStocks);

            var now = DateTime.UtcNow;
            foreach (var item in incomingItems.Where(item => nonInventoryIncomingItemIds.Contains(item.Id) && !item.IsDeleted))
            {
                var changed = false;
                if (item.CurrentStock != 0m)
                {
                    item.CurrentStock = 0m;
                    changed = true;
                }
                if (item.SafetyStock != 0m)
                {
                    item.SafetyStock = 0m;
                    changed = true;
                }
                if (changed)
                    item.UpdatedAtUtc = now;
            }
        }

        List<LocalItemWarehouseStock> existingStocks =
            affectedItemIds.Count == 0
            ? []
            : await _db.ItemWarehouseStocks
                .Where(stock =>
                    affectedItemIds.Contains(stock.ItemId))
                .ToListAsync(ct);
        var existingStocksByLogicalKey = existingStocks
            .GroupBy(
                stock => BuildItemWarehouseStockKey(
                    stock.ItemId,
                    stock.WarehouseCode),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var dto in logicalDtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.WarehouseCode =
                NormalizeItemWarehouseStockLogicalWarehouseCode(
                    dto.WarehouseCode);
            var logicalKey = BuildItemWarehouseStockKey(
                local.ItemId,
                local.WarehouseCode);
            var logicalMatches =
                existingStocksByLogicalKey.GetValueOrDefault(
                    logicalKey) ??
                [];
            var canonicalExisting = logicalMatches
                .FirstOrDefault(stock => string.Equals(
                    stock.WarehouseCode,
                    local.WarehouseCode,
                    StringComparison.Ordinal));
            if (canonicalExisting is null)
            {
                if (logicalMatches.Count > 0)
                    _db.ItemWarehouseStocks.RemoveRange(logicalMatches);
                _db.ItemWarehouseStocks.Add(local);
            }
            else
            {
                _db.Entry(canonicalExisting)
                    .CurrentValues.SetValues(local);
                var aliases = logicalMatches
                    .Where(stock =>
                        !ReferenceEquals(
                            stock,
                            canonicalExisting))
                    .ToList();
                if (aliases.Count > 0)
                    _db.ItemWarehouseStocks.RemoveRange(aliases);
            }
        }

        var removedItemIds = await RemovePulledItemWarehouseStocksMissingFromServerAsync(pulledKeys, ct);
        affectedItemIds.UnionWith(removedItemIds);
        await _db.SaveChangesAsync(ct);
        await RecalculatePulledItemCurrentStocksAsync(affectedItemIds, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<HashSet<Guid>> RemovePulledItemWarehouseStocksMissingFromServerAsync(
        IReadOnlySet<string> pulledKeys,
        CancellationToken ct)
    {
        var affectedItemIds = new HashSet<Guid>();
        if (!_session.IsLoggedIn)
            return affectedItemIds;

        var candidates = await (from stock in _db.ItemWarehouseStocks
                                join item in _db.Items.IgnoreQueryFilters() on stock.ItemId equals item.Id
                                select new
                                {
                                    Stock = stock,
                                    Item = item
                                })
                                 .ToListAsync(ct);
        if (candidates.Count == 0)
            return affectedItemIds;

        var sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            _session.TenantCode,
            _session.OfficeCode);
        var readableOfficeCodes = _local
            .GetReadableOfficeCodesForSession(_session)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readableWarehouseCodes = readableOfficeCodes
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var key = BuildItemWarehouseStockKey(candidate.Stock.ItemId, candidate.Stock.WarehouseCode);
            if (pulledKeys.Contains(key))
                continue;

            if (candidate.Item.IsDirty)
                continue;

            if (!IsItemWarehouseStockInCurrentPullScope(
                    candidate.Item,
                    candidate.Stock,
                    sessionTenantCode,
                    readableOfficeCodes,
                    readableWarehouseCodes))
                continue;

            _db.ItemWarehouseStocks.Remove(candidate.Stock);
            affectedItemIds.Add(candidate.Stock.ItemId);
        }

        return affectedItemIds;
    }

    private async Task RecalculatePulledItemCurrentStocksAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        if (itemIds.Count == 0)
            return;

        var stockRows = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => itemIds.Contains(stock.ItemId))
            .Select(stock => new { stock.ItemId, stock.Quantity })
            .ToListAsync(ct);
        var stockTotals = stockRows
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(stock => stock.Quantity));
        var items = await _db.Items.IgnoreQueryFilters()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted && !item.IsDirty)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            var recalculated = ItemOperationalPolicy.SupportsInventory(item.TrackingType) &&
                               stockTotals.TryGetValue(item.Id, out var stockTotal)
                ? stockTotal
                : 0m;

            item.CurrentStock = recalculated;
        }
    }

    private bool IsItemWarehouseStockInCurrentPullScope(
        LocalItem item,
        LocalItemWarehouseStock stock,
        string sessionTenantCode,
        IReadOnlySet<string> readableOfficeCodes,
        IReadOnlySet<string> readableWarehouseCodes)
    {
        if (_session.HasGlobalDataScope)
            return true;

        if (item.IsDeleted ||
            !TenantScopeCatalog.TryNormalizeTenantCode(
                item.TenantCode,
                out var itemTenantCode))
        {
            return false;
        }

        if (!string.Equals(itemTenantCode, sessionTenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(
                item.OfficeCode,
                OfficeCodeCatalog.Shared,
                StringComparison.OrdinalIgnoreCase) &&
            !readableOfficeCodes.Contains(item.OfficeCode))
        {
            return false;
        }

        if (!OfficeCodeCatalog.TryNormalizeWarehouseCode(
                stock.WarehouseCode,
                out var normalizedWarehouseCode))
            return false;

        return readableWarehouseCodes.Contains(normalizedWarehouseCode);
    }

    private async Task UpsertPulledTransactionAttachmentsAsync(
        IReadOnlyList<TransactionAttachmentDto> dtos,
        CancellationToken ct,
        AttachmentFileJournal? attachmentFileJournal)
    {
        var ownsFileJournal = attachmentFileJournal is null;
        if (ownsFileJournal && _db.Database.CurrentTransaction is null)
            await RecoverIncompleteAttachmentFileJournalsAsync(ct);

        if (dtos.Count == 0)
            return;

        if (ownsFileJournal && _db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "외부 DB 트랜잭션에서 첨부파일 pull을 적용하려면 동일 범위의 파일 저널이 필요합니다.");
        }

        var ownedFileJournal = ownsFileJournal
            ? new AttachmentFileJournal(
                AppPaths.AttachmentFileJournalDir,
                AppPaths.AttachmentsDir)
            : null;
        var fileJournal = attachmentFileJournal ?? ownedFileJournal!;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? ownedDatabaseTransaction = null;
        var commitAttempted = false;

        try
        {
            if (ownsFileJournal)
                ownedDatabaseTransaction = await _db.BeginRuntimeMutationTransactionAsync(ct);

            foreach (var dto in dtos)
            {
                var existing = await _db.TransactionAttachments.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(current => current.Id == dto.Id, ct);
                if (existing?.IsDirty == true)
                    continue;

                var existingPath = existing?.StoredPath;
                var attachmentPath = string.Empty;
                if (!dto.IsDeleted)
                {
                    var verifiedContent = ValidatePulledTransactionAttachmentContent(dto);
                    attachmentPath = ResolvePulledTransactionAttachmentPath(
                        dto,
                        verifiedContent);
                    await fileJournal.StageWriteAsync(
                        attachmentPath,
                        verifiedContent,
                        ct);
                }

                var local = LocalMappings.ToLocal(
                    dto,
                    storedFileName: Path.GetFileName(attachmentPath),
                    storedPath: attachmentPath);
                local.IsDirty = false;

                if (existing is null)
                {
                    _db.TransactionAttachments.Add(local);
                }
                else
                {
                    _db.Entry(existing).CurrentValues.SetValues(local);
                    if (dto.IsDeleted)
                    {
                        TryStageTransactionAttachmentDelete(fileJournal, existingPath);
                    }
                    else if (!string.Equals(
                                 existingPath,
                                 attachmentPath,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        TryStageTransactionAttachmentDelete(fileJournal, existingPath);
                    }
                }
            }

            await _db.SaveChangesAsync(ct);

            if (ownsFileJournal)
            {
                await fileJournal.StageCommitEvidenceAsync(_db, ct);
                fileJournal.Promote();
                commitAttempted = true;
                await ownedDatabaseTransaction!.CommitAsync(ct);
                await ownedDatabaseTransaction.DisposeAsync().ConfigureAwait(false);
                await fileJournal.CompleteAfterDatabaseCommitAsync(
                    _db,
                    CancellationToken.None);
            }
        }
        catch
        {
            var commitResolution = AttachmentCommitResolution.RolledBack;
            if (ownedDatabaseTransaction is not null)
            {
                try
                {
                    await ownedDatabaseTransaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    AppLogger.Error(
                        "ATTACHMENT",
                        "첨부파일 pull 실패 후 DB 롤백을 완료하지 못했습니다.",
                        rollbackException);
                }
            }

            if (ownsFileJournal)
            {
                if (!commitAttempted)
                {
                    fileJournal.Rollback();
                }
                else
                {
                    commitResolution = await fileJournal.ResolveCommitAmbiguityAsync(
                        _db,
                        CancellationToken.None);
                }
            }

            _db.ChangeTracker.Clear();
            if (commitResolution != AttachmentCommitResolution.Committed)
                throw;
        }
        finally
        {
            if (ownedDatabaseTransaction is not null)
                await ownedDatabaseTransaction.DisposeAsync();
            ownedFileJournal?.Dispose();
        }
    }

    private async Task RecoverIncompleteAttachmentFileJournalsAsync(
        CancellationToken ct)
    {
        await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
            _db,
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir,
            ct);
    }

    private async Task UpsertPulledInventoryTransfersAsync(
        IReadOnlyList<InventoryTransferDto> dtos,
        CancellationToken ct)
    {
        foreach (var dto in dtos)
        {
            await UpsertPulledInventoryTransferAsync(dto, ct);
        }
    }

    private async Task DeferPullRefreshUntilDirtyChangesArePushedAsync(
        int pendingDirtyCount,
        DbUpdateConcurrencyException exception)
    {
        var deferredMessage =
            $"증분 pull 반영 중 동시성 충돌이 발생했지만 미동기화 변경 {pendingDirtyCount:N0}건을 보존하기 위해 전체 캐시 재구성을 보류했습니다. " +
            "대기 변경이 서버에 반영된 뒤 자동으로 다시 불러옵니다.";
        AppLogger.Warn("SYNC", $"{deferredMessage} {exception.Message}");
        await _local.MarkServerMirrorRefreshRequiredAsync(CancellationToken.None);
        await TryRecordDiagnosticAsync(
            phase: "pull",
            rawMessage: $"{deferredMessage} detail={exception.Message}",
            exception: exception,
            severity: "Warning",
            recoveryAttempted: true,
            recoverySucceeded: false);
        SetStatus(deferredMessage);
        ScheduleTransientFailureRetry();
    }

    private async Task DeferPullForConcurrentOwnerEditAsync()
    {
        const string deferredMessage =
            "서버 응답 대기 중 새 편집이 발생해 이번 다운로드 반영을 보류했습니다. 편집을 저장하거나 취소한 뒤 자동으로 다시 불러옵니다.";
        AppLogger.Warn("SYNC", deferredMessage);
        await _local.MarkServerMirrorRefreshRequiredAsync(
            CancellationToken.None);
        await TryRecordDiagnosticAsync(
            phase: "pull",
            rawMessage: deferredMessage,
            severity: "Info",
            recoveryAttempted: true,
            recoverySucceeded: false);
        SetStatus(deferredMessage);
        ScheduleTransientFailureRetry();
    }

    private async Task DeferPullForChangedOperationOwnerAsync()
    {
        const string deferredMessage =
            "서버 응답 대기 또는 반영 중 로그인·업체 DB 범위가 변경되어 이전 범위의 다운로드를 폐기했습니다. 현재 범위로 자동 재시도합니다.";
        AppLogger.Warn("SYNC", deferredMessage);
        await _local.MarkServerMirrorRefreshRequiredAsync(
            CancellationToken.None);
        await TryRecordDiagnosticAsync(
            phase: "pull-owner",
            rawMessage: deferredMessage,
            severity: "Info",
            recoveryAttempted: true,
            recoverySucceeded: true);
        SetStatus(deferredMessage);
        ScheduleTransientFailureRetry();
    }

    private async Task ScheduleCurrentOwnerRefreshAfterCommittedOwnerChangeAsync()
    {
        AppLogger.Info(
            "SYNC",
            "The prior owner transaction committed after the active session scope changed. " +
            "Prior-owner post-commit notifications were suppressed and a current-owner refresh was queued.");
        try
        {
            await _local.MarkServerMirrorRefreshRequiredAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "SYNC",
                "The current-owner full refresh requirement could not be persisted after an owner change.",
                ex);
        }

        CurrentOwnerRefreshScheduledForTesting?.Invoke();
        _dispatcher.RequestDebouncedSync();
    }

    private async Task<bool> TryRunPostCommitEffectsForCurrentOwnerAsync(
        SyncOperationOwnerBoundary expectedOwner,
        Func<CancellationToken, Task> protectedEffects,
        params Func<bool>[] synchronousNotifications)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(protectedEffects);
        ArgumentNullException.ThrowIfNull(synchronousNotifications);

        using var scopeLease = await _session
            .AcquireSyncScopeCommitLeaseAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (!IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true))
        {
            return false;
        }

        if (AfterPostCommitOwnerCheckAsyncForTesting is not null)
        {
            await AfterPostCommitOwnerCheckAsyncForTesting(
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (!IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true))
        {
            return false;
        }

        await protectedEffects(CancellationToken.None).ConfigureAwait(false);
        if (!IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true))
        {
            return false;
        }

        foreach (var notification in synchronousNotifications)
        {
            ArgumentNullException.ThrowIfNull(notification);
            if (!IsSyncOperationOwnerCurrent(
                    expectedOwner,
                    scopeLeaseHeld: true))
            {
                return false;
            }

            try
            {
                if (!notification())
                    return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "SYNC",
                    "커밋된 동기화의 UI 변경 알림 중 오류가 발생했습니다.",
                    ex);
            }
        }

        return IsSyncOperationOwnerCurrent(
            expectedOwner,
            scopeLeaseHeld: true);
    }

    private bool TryPublishOwnerBoundRentalState(
        SyncOperationOwnerBoundary expectedOwner,
        IEnumerable<Guid>? assetIds,
        IEnumerable<Guid>? billingProfileIds)
        => _rental.TryPublishSynchronizedStateChanges(
            assetIds,
            billingProfileIds,
            () => IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true),
            EnterOwnerBoundCallbackScope);

    private bool TryPublishOwnerBoundItemInvoiceHistory(
        SyncOperationOwnerBoundary expectedOwner,
        bool hasPulledInvoices)
    {
        if (!hasPulledInvoices)
        {
            return IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true);
        }

        return _local.TryPublishItemInvoiceHistoryChanged(
            () => IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true),
            EnterOwnerBoundCallbackScope);
    }

    private bool TryPublishOwnerBoundInventoryState(
        SyncOperationOwnerBoundary expectedOwner,
        bool inventoryStateChanged)
    {
        if (!inventoryStateChanged)
        {
            return IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true);
        }

        return _local.TryPublishInventoryStateChanged(
            () => IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true),
            EnterOwnerBoundCallbackScope);
    }

    private bool TryPublishOwnerBoundStatus(
        SyncOperationOwnerBoundary expectedOwner,
        string message)
    {
        if (SyncStatusChanged is not { } handlers)
        {
            return IsSyncOperationOwnerCurrent(
                expectedOwner,
                scopeLeaseHeld: true);
        }

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            if (!IsSyncOperationOwnerCurrent(
                    expectedOwner,
                    scopeLeaseHeld: true))
            {
                return false;
            }

            using var callbackScope = EnterOwnerBoundCallbackScope();
            handler(message);
        }

        return IsSyncOperationOwnerCurrent(
            expectedOwner,
            scopeLeaseHeld: true);
    }

    private IDisposable EnterOwnerBoundCallbackScope()
        => new OwnerBoundCallbackScope(
            _session.EnterSyncScopeSynchronousCallback(),
            LocalDbContext.SuppressRuntimeMutationOwnerForCallback());

    private sealed class OwnerBoundCallbackScope(
        IDisposable sessionScope,
        IDisposable runtimeMutationScope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                runtimeMutationScope.Dispose();
            }
            finally
            {
                sessionScope.Dispose();
            }
        }
    }

    private async Task<bool> TryRefreshSharedMirrorCoreAsync(
        CancellationToken ct,
        bool preserveTrackedChanges = false)
    {
        if (!_session.HasGlobalDataScope)
        {
            SetStatus("전체 공유 미러를 초기화하지 않고 현재 권한 범위의 서버 데이터를 갱신합니다.");
            return await TryRefreshCurrentBusinessScopeCoreAsync(
                ct,
                preserveTrackedChanges);
        }

        if (!preserveTrackedChanges && HasPendingTrackedUserChanges())
        {
            SetStatus("저장되지 않은 편집이 있어 서버 캐시 새로고침을 중단했습니다.");
            return false;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var operationOwner =
                CaptureSyncOperationOwnerBoundary();
            var trackedStateBeforePull = CaptureTrackedStateBeforePush();
            var ownerTrackedStateBeforePull =
                CaptureIsolatedOwnerTrackedState();
            var trackedChangesArrivedDuringPull = false;
            SyncPullResponse? pull;
            try
            {
                pull = await _api.PullAsync(
                    0,
                    operationOwner.BusinessDatabaseName,
                    ct);
            }
            finally
            {
                if (preserveTrackedChanges)
                {
                    PreservePendingTrackedChangesForSync();
                    trackedChangesArrivedDuringPull =
                        HasIsolatedOwnerTrackedChangesSinceBoundary(
                            ownerTrackedStateBeforePull);
                }
                else
                {
                    trackedChangesArrivedDuringPull =
                        PreserveTrackedChangesSinceBoundary(trackedStateBeforePull) ||
                        HasIsolatedOwnerTrackedChangesSinceBoundary(
                            ownerTrackedStateBeforePull);
                }
            }

            if (pull is null)
                return false;
            if (!IsSyncOperationOwnerCurrent(operationOwner))
            {
                SetStatus(
                    "서버 응답 대기 중 로그인·업체 DB 범위가 변경되어 이전 범위의 전체 캐시 응답을 폐기했습니다.");
                return false;
            }
            if (trackedChangesArrivedDuringPull)
            {
                SetStatus("서버 응답 대기 중 저장되지 않은 편집이 발생해 서버 캐시 새로고침을 중단했습니다.");
                return false;
            }
            if (await _local.CountDirtyAsync(ct) > 0)
            {
                SetStatus("서버 응답 대기 중 저장된 미동기화 변경이 발생해 서버 캐시 새로고침을 중단했습니다.");
                return false;
            }

            if (await HasPendingReconciliationForOperationOwnerAsync(
                    operationOwner,
                    ct))
            {
                SetStatus("Pending local mutation reconciliation blocked the full mirror refresh.");
                return false;
            }

            LastPullChangeCount = Math.Max(1, CountPullChanges(pull));

            try
            {
                if (preserveTrackedChanges)
                {
                    PreservePendingTrackedChangesForSync();
                }
                else if (HasPendingTrackedUserChanges())
                {
                    SetStatus("저장되지 않은 편집이 있어 서버 캐시 새로고침을 중단했습니다.");
                    return false;
                }

                _db.ChangeTracker.Clear();
                if (await ShouldRejectEmptyMirrorPullAsync(pull, ct))
                    return false;

                await RecoverIncompleteAttachmentFileJournalsAsync(ct);
                if (BeforeSharedMirrorResetAsyncForTesting is not null)
                    await BeforeSharedMirrorResetAsyncForTesting(ct);

                if (!IsSyncOperationOwnerCurrent(operationOwner))
                {
                    SetStatus(
                        "전체 캐시 반영 직전에 로그인·업체 DB 범위가 변경되어 이전 범위의 응답을 폐기했습니다.");
                    return false;
                }

                await using var transaction = await _db.BeginRuntimeMutationTransactionAsync(ct);
                _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                    false;
                if (HasPendingTrackedUserChanges() ||
                    await _local.CountDirtyAsync(ct) > 0 ||
                    await HasPendingReconciliationForOperationOwnerAsync(
                        operationOwner,
                        ct))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    SetStatus(
                        "전체 캐시 재구성 직전에 새 편집 또는 미동기화 변경이 확인되어 데이터 보존을 위해 반영을 보류했습니다.");
                    return false;
                }

                using var attachmentFiles = new AttachmentFileJournal(
                    AppPaths.AttachmentFileJournalDir,
                    AppPaths.AttachmentsDir);
                var itemInvoiceHistoryChanged = false;
                using var inventoryStateChangeCapture =
                    _local.CaptureInventoryStateChanges();
                using (_local.SuppressSyncDispatch())
                {
                    await EnsureItemWarehouseStockReplayPullGuardUnchangedAsync(
                        ct);
                    _itemWarehouseStockReplayGuardValidatedBeforeMirrorReset =
                        _itemWarehouseStockReplayPullGuard is not null;
                    _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                        _itemWarehouseStockReplayPullGuard is not null;
                    try
                    {
                        await _local.ResetSharedMirrorCacheWithAttachmentJournalAsync(
                            attachmentFiles,
                            ct);
                        itemInvoiceHistoryChanged = await ApplyPullInternalAsync(
                            pull,
                            0L,
                            ct,
                            updateSyncRevision: true,
                            attachmentFileJournal: attachmentFiles,
                            publishRentalStateChanges: false);
                        // A successful full mirror replaces the entire invoice
                        // snapshot. Even an empty response can remove history
                        // that an open item screen is still displaying.
                        itemInvoiceHistoryChanged = true;
                    }
                    finally
                    {
                        _itemWarehouseStockReplayGuardValidatedBeforeMirrorReset =
                            false;
                    }
                }

                if (!IsSyncOperationOwnerCurrent(operationOwner))
                {
                    _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                        false;
                    await transaction.RollbackAsync(CancellationToken.None);
                    attachmentFiles.Rollback();
                    _db.ChangeTracker.Clear();
                    SetStatus(
                        "전체 캐시 반영 중 로그인·업체 DB 범위가 변경되어 DB와 첨부파일 변경을 모두 롤백했습니다.");
                    return false;
                }

                var commitAttempted = false;
                try
                {
                    var committed =
                        await CommitAttachmentTransactionUnderOwnerLeaseAsync(
                            transaction,
                            attachmentFiles,
                            operationOwner,
                            () => commitAttempted = true,
                            ct);
                    _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                        false;
                    if (!committed)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        attachmentFiles.Rollback();
                        _db.ChangeTracker.Clear();
                        SetStatus(
                            "전체 캐시 커밋 직전에 로그인·업체 DB 범위가 변경되어 DB와 첨부파일 변경을 모두 롤백했습니다.");
                        return false;
                    }

                    await transaction.DisposeAsync().ConfigureAwait(false);
                    await attachmentFiles.CompleteAfterDatabaseCommitAsync(
                        _db,
                        CancellationToken.None);
                }
                catch
                {
                    _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                        false;
                    var commitResolution = AttachmentCommitResolution.RolledBack;
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        AppLogger.Error(
                            "ATTACHMENT",
                            "전체 미러 커밋 실패 후 DB 롤백 결과를 확정하지 못했습니다.",
                            rollbackException);
                    }

                    if (commitAttempted)
                    {
                        commitResolution = await attachmentFiles.ResolveCommitAmbiguityAsync(
                            _db,
                            CancellationToken.None);
                    }
                    else
                    {
                        attachmentFiles.Rollback();
                    }

                    if (commitResolution != AttachmentCommitResolution.Committed)
                        throw;

                    // The ambiguity resolver verifies the commit with an
                    // independent context and finalizes the durable journal.
                    // Release the original context transaction before any
                    // post-commit settings or diagnostics access.
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }

                var postCommitEffectsApplied =
                    await TryRunPostCommitEffectsForCurrentOwnerAsync(
                        operationOwner,
                        async _ =>
                        {
                            await _local.ClearServerMirrorRefreshRequiredAsync(
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await TrySetSettingSafeAsync(
                                    "Sync.LastSuccessAt",
                                    DateTime.Now.ToString("O"),
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await TrySetSettingSafeAsync(
                                    "Sync.LastError",
                                    string.Empty,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await _diagnostics.ResolveOpenIssuesAsync(
                                    ct: CancellationToken.None)
                                .ConfigureAwait(false);
                            _lastSyncCompletedUtc = DateTime.UtcNow;
                        },
                        () => TryPublishOwnerBoundRentalState(
                            operationOwner,
                            pull.RentalAssets.Select(asset => asset.Id),
                            pull.RentalBillingProfiles.Select(profile => profile.Id)),
                        () => TryPublishOwnerBoundItemInvoiceHistory(
                            operationOwner,
                            itemInvoiceHistoryChanged),
                        () => TryPublishOwnerBoundInventoryState(
                            operationOwner,
                            inventoryStateChanged: true),
                        () => TryPublishOwnerBoundStatus(
                            operationOwner,
                            $"중앙 서버 기준 캐시 재구성 완료 {DateTime.Now:HH:mm:ss}"));
                if (!postCommitEffectsApplied)
                {
                    _db.ChangeTracker.Clear();
                    await ScheduleCurrentOwnerRefreshAfterCommittedOwnerChangeAsync();
                    return true;
                }

                return true;
            }
            catch (DbUpdateConcurrencyException ex)
                when (attempt == 0 &&
                      _itemWarehouseStockReplayPullGuard is null)
            {
                _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                    false;
                AppLogger.Warn("SYNC", $"공유 캐시 재구성 중 동시성 충돌 재시도: {ex.Message}");
                await TryRecordDiagnosticAsync(
                    phase: "shared-refresh",
                    rawMessage: $"공유 캐시 재구성 중 동시성 충돌: {ex.Message}",
                    exception: ex,
                    severity: "Warning",
                    recoveryAttempted: true,
                    recoverySucceeded: false);
                _db.ChangeTracker.Clear();
            }
            catch (Exception ex)
            {
                _itemWarehouseStockReplayGuardValidatedForPullTransaction =
                    false;
                _db.ChangeTracker.Clear();
                if (_itemWarehouseStockReplayPullGuard is not null &&
                    ex is not SyncPullBlockedException &&
                    !(ex is OperationCanceledException &&
                      ct.IsCancellationRequested))
                {
                    throw new SyncPullBlockedException(
                        "재고 snapshot guard가 활성화된 전체 mirror pull 적용 중 DB 동시성 또는 저장 실패가 발생해 후속 pull을 중단했습니다.",
                        ex);
                }

                throw;
            }
        }

        return false;
    }

    private async Task<bool> ShouldRejectEmptyMirrorPullAsync(SyncPullResponse pull, CancellationToken ct)
    {
        if (HasOperationalRows(pull))
            return false;

        var existingOperationalRows = await CountExistingOperationalRowsAsync(ct);
        if (existingOperationalRows <= 0)
            return false;

        var message =
            $"서버 전체 캐시 응답에 거래처/전표/품목 데이터가 없어 기존 로컬 표시 데이터 {existingOperationalRows:N0}건을 지우지 않았습니다. " +
            "서버 데이터 범위, 로그인 계정, 업체 DB 선택을 확인한 뒤 다시 동기화하세요.";

        AppLogger.Warn("SYNC", message);
        await TryRecordDiagnosticAsync(
            phase: "shared-refresh",
            rawMessage: message,
            severity: "Warning",
            recoveryAttempted: true,
            recoverySucceeded: true);
        await TrySetSettingSafeAsync(
            "Sync.LastError",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}",
            CancellationToken.None);
        SetStatus(message);
        return true;
    }

    private async Task<int> CountExistingOperationalRowsAsync(CancellationToken ct)
    {
        var count = 0;
        count += await _db.Customers.IgnoreQueryFilters().CountAsync(ct);
        count += await _db.CustomerMasters.IgnoreQueryFilters().CountAsync(ct);
        count += await _db.Items.IgnoreQueryFilters().CountAsync(ct);
        count += await _db.Invoices.IgnoreQueryFilters().CountAsync(ct);
        count += await _db.Transactions.IgnoreQueryFilters().CountAsync(ct);
        count += await _db.RentalBillingProfiles.IgnoreQueryFilters().CountAsync(ct);
        count += await _db.RentalAssets.IgnoreQueryFilters().CountAsync(ct);
        count += await _db.RentalBillingLogs.IgnoreQueryFilters().CountAsync(ct);
        return count;
    }

    private static bool HasOperationalRows(SyncPullResponse pull)
        => pull.Customers.Count > 0
           || pull.CustomerMasters.Count > 0
           || pull.Items.Count > 0
           || pull.Invoices.Count > 0
           || pull.Transactions.Count > 0
           || pull.RentalBillingProfiles.Count > 0
           || pull.RentalAssets.Count > 0
           || pull.RentalBillingLogs.Count > 0;

    private async Task<bool> TryRefreshCurrentBusinessScopeCoreAsync(
        CancellationToken ct,
        bool preserveTrackedChanges = false)
        => await TryRefreshCurrentBusinessScopeCoreInternalAsync(
            ct,
            preserveTrackedChanges,
            replaceLocalBusinessCache: false);

    private async Task<bool> TryRefreshCurrentBusinessScopeCoreInternalAsync(
        CancellationToken ct,
        bool preserveTrackedChanges = false,
        bool replaceLocalBusinessCache = false)
    {
        if (!preserveTrackedChanges && HasPendingTrackedUserChanges())
        {
            SetStatus("저장되지 않은 편집이 있어 현재 업체 캐시 새로고침을 중단했습니다.");
            return false;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var operationOwner =
                CaptureSyncOperationOwnerBoundary();
            var trackedStateBeforePull = CaptureTrackedStateBeforePush();
            var ownerTrackedStateBeforePull =
                CaptureIsolatedOwnerTrackedState();
            var trackedChangesArrivedDuringPull = false;
            SyncPullResponse? pull;
            try
            {
                pull = await _api.PullAsync(
                    0,
                    operationOwner.BusinessDatabaseName,
                    ct);
            }
            finally
            {
                if (preserveTrackedChanges)
                {
                    PreservePendingTrackedChangesForSync();
                    trackedChangesArrivedDuringPull =
                        HasIsolatedOwnerTrackedChangesSinceBoundary(
                            ownerTrackedStateBeforePull);
                }
                else
                {
                    trackedChangesArrivedDuringPull =
                        PreserveTrackedChangesSinceBoundary(trackedStateBeforePull) ||
                        HasIsolatedOwnerTrackedChangesSinceBoundary(
                            ownerTrackedStateBeforePull);
                }
            }

            if (pull is null)
                return false;
            if (!IsSyncOperationOwnerCurrent(operationOwner))
            {
                SetStatus(
                    "서버 응답 대기 중 로그인·업체 DB 범위가 변경되어 이전 범위의 현재 업체 응답을 폐기했습니다.");
                return false;
            }
            if (trackedChangesArrivedDuringPull)
            {
                SetStatus("서버 응답 대기 중 저장되지 않은 편집이 발생해 현재 업체 캐시 새로고침을 중단했습니다.");
                return false;
            }
            if (await _local.CountDirtyAsync(_session, ct) > 0)
            {
                SetStatus("서버 응답 대기 중 저장된 미동기화 변경이 발생해 현재 업체 캐시 새로고침을 중단했습니다.");
                return false;
            }

            LastPullChangeCount = Math.Max(1, CountPullChanges(pull));

            try
            {
                if (preserveTrackedChanges)
                {
                    PreservePendingTrackedChangesForSync();
                }
                else if (HasPendingTrackedUserChanges())
                {
                    SetStatus("저장되지 않은 편집이 있어 현재 업체 캐시 새로고침을 중단했습니다.");
                    return false;
                }

                _db.ChangeTracker.Clear();
                var ownerRefreshScheduled = false;
                using (_local.SuppressSyncDispatch())
                {
                    var applied = await TryApplyPullAtomicallyCoreAsync(
                        pull,
                        0L,
                        ct,
                        updateSyncRevision: true,
                        expectedOwner: operationOwner,
                        markOwnerRefreshScheduled: () =>
                            ownerRefreshScheduled = true,
                        replaceLocalBusinessCache: replaceLocalBusinessCache);
                    if (!applied)
                    {
                        SetStatus(
                            "현재 업체 캐시 반영 중 로그인·업체 DB 범위가 변경되어 DB와 첨부파일 변경을 모두 롤백했습니다.");
                        return false;
                    }
                }

                if (ownerRefreshScheduled)
                    return true;

                var postCommitEffectsApplied =
                    await TryRunPostCommitEffectsForCurrentOwnerAsync(
                        operationOwner,
                        async _ =>
                        {
                            await TrySetSettingSafeAsync(
                                    "Sync.LastSuccessAt",
                                    DateTime.Now.ToString("O"),
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await TrySetSettingSafeAsync(
                                    "Sync.LastError",
                                    string.Empty,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await _diagnostics.ResolveOpenIssuesAsync(
                                    ct: CancellationToken.None)
                                .ConfigureAwait(false);
                            _lastSyncCompletedUtc = DateTime.UtcNow;
                        },
                        () => TryPublishOwnerBoundStatus(
                            operationOwner,
                            $"현재 업체 DB 기준 캐시 재구성 완료 {DateTime.Now:HH:mm:ss}"));
                if (!postCommitEffectsApplied)
                {
                    _db.ChangeTracker.Clear();
                    await ScheduleCurrentOwnerRefreshAfterCommittedOwnerChangeAsync();
                }

                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt == 0)
            {
                AppLogger.Warn("SYNC", $"현재 업체 DB 기준 캐시 재구성 중 동시성 충돌 재시도: {ex.Message}");
                await TryRecordDiagnosticAsync(
                    phase: "scoped-refresh",
                    rawMessage: $"현재 업체 DB 기준 캐시 재구성 중 동시성 충돌: {ex.Message}",
                    exception: ex,
                    severity: "Warning",
                    recoveryAttempted: true,
                    recoverySucceeded: false);
                _db.ChangeTracker.Clear();
            }
            catch
            {
                _db.ChangeTracker.Clear();
                throw;
            }
        }

        return false;
    }

    private async Task TrySetSettingSafeAsync(string key, string value, CancellationToken ct)
    {
        try
        {
            await _local.SetSyncMetadataSettingIndependentAsync(key, value, ct);
        }
        catch (Exception ex)
        {
            var sqliteException = ex as Microsoft.Data.Sqlite.SqliteException
                ?? ex.InnerException as Microsoft.Data.Sqlite.SqliteException;
            var safeFailure = sqliteException is null
                ? ex.GetType().Name
                : $"{ex.GetType().Name}; SQLite={sqliteException.SqliteErrorCode}; Extended={sqliteException.SqliteExtendedErrorCode}";
            AppLogger.Warn("SYNC", $"설정값 저장 실패 무시 ({key}): {safeFailure}");
        }
    }

    private async Task<string> GetOrCreateDeviceIdAsync(CancellationToken ct)
    {
        var current = (await _local.GetSettingAsync(DeviceIdSettingKey, ct) ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(current))
            return current;

        current = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        await _local.SetSettingAsync(DeviceIdSettingKey, current, ct);
        return current;
    }

    internal Task<string> EnsureDeviceIdAsync(CancellationToken ct = default)
    {
        return GetOrCreateDeviceIdAsync(ct);
    }

    private static void StampOutgoingMutations(
        SyncPushRequest request,
        string deviceId,
        string businessDatabaseName)
    {
        StampOutgoingMutations(request.CompanyProfiles, nameof(LocalCompanyProfile), deviceId);
        StampOutgoingMutations(request.Units, nameof(LocalUnit), deviceId);
        StampOutgoingMutations(request.CustomerCategories, nameof(LocalCustomerCategory), deviceId);
        StampOutgoingMutations(request.PriceGradeOptions, nameof(LocalPriceGradeOption), deviceId);
        StampOutgoingMutations(request.TradeTypeOptions, nameof(LocalTradeTypeOption), deviceId);
        StampOutgoingMutations(request.ItemCategoryOptions, nameof(LocalItemCategoryOption), deviceId);
        StampOutgoingMutations(request.CustomerMasters, nameof(LocalCustomerMaster), deviceId);
        StampOutgoingMutations(request.Customers, nameof(LocalCustomer), deviceId);
        StampOutgoingMutations(request.CustomerContracts, nameof(LocalCustomerContract), deviceId);
        StampOutgoingMutations(request.Items, nameof(LocalItem), deviceId);
        StampOutgoingMutations(request.ItemPriceGrades, nameof(LocalItemPriceGrade), deviceId);
        StampOutgoingMutations(request.Transactions, nameof(LocalTransaction), deviceId);
        StampOutgoingMutations(request.TransactionAttachments, nameof(LocalTransactionAttachment), deviceId);
        StampOutgoingMutations(request.InventoryTransfers, nameof(LocalInventoryTransfer), deviceId);
        StampOutgoingMutations(request.RentalManagementCompanies, nameof(LocalRentalManagementCompany), deviceId);
        StampOutgoingMutations(request.RentalBillingProfiles, nameof(LocalRentalBillingProfile), deviceId);
        StampOutgoingMutations(request.RentalAssets, nameof(LocalRentalAsset), deviceId);
        StampOutgoingMutations(request.RentalAssetAssignmentHistories, nameof(LocalRentalAssetAssignmentHistory), deviceId);
        StampOutgoingMutations(request.RentalBillingLogs, nameof(LocalRentalBillingLog), deviceId);
        StampOutgoingMutations(request.Invoices, nameof(LocalInvoice), deviceId);
        StampOutgoingMutations(request.Payments, nameof(LocalPayment), deviceId);

        var normalizedBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(businessDatabaseName);
        foreach (var option in request.PriceGradeOptions)
            option.MutationId = $"{option.MutationId}:{normalizedBusinessDatabaseName.ToUpperInvariant()}";
    }

    private static void StampOutgoingMutations<TDto>(IEnumerable<TDto> entities, string entityName, string deviceId)
        where TDto : SyncEntityDto
    {
        foreach (var entity in entities)
        {
            entity.ExpectedRevision = entity.ExpectedRevision > 0
                ? entity.ExpectedRevision
                : Math.Max(0, entity.Revision);
            entity.MutationCreatedAtUtc = NormalizeMutationUtc(entity.UpdatedAtUtc);
            entity.MutationId = BuildMutationId(deviceId, entityName, entity);
        }
    }

    private static IReadOnlySet<SyncEntityKey> SelectDependencyOnlyKeysForRequest(
        SyncPushRequest request,
        IReadOnlySet<SyncEntityKey> dependencyOnlyCandidateKeys)
        => EnumerateAllOutgoingMutations(request)
            .Select(entry => new SyncEntityKey(
                NormalizeSyncEntityName(entry.EntityName),
                entry.Entity.Id))
            .Where(dependencyOnlyCandidateKeys.Contains)
            .ToHashSet();

    private static IReadOnlyDictionary<
            string,
            RentalManagementCompanyDependencyIdentity>
        SelectDependencyOnlyRentalManagementCompanyIdentitiesForRequest(
        SyncPushRequest request,
        IReadOnlySet<SyncEntityKey>? dependencyOnlyKeys)
    {
        if (dependencyOnlyKeys is null || dependencyOnlyKeys.Count == 0)
        {
            return new Dictionary<
                string,
                RentalManagementCompanyDependencyIdentity>(
                StringComparer.OrdinalIgnoreCase);
        }

        return request.RentalManagementCompanies
            .Where(company => dependencyOnlyKeys.Contains(
                new SyncEntityKey(
                    NormalizeSyncEntityName(
                        nameof(LocalRentalManagementCompany)),
                    company.Id)))
            .Select(company => new
            {
                Company = company,
                MutationId = (company.MutationId ?? string.Empty).Trim()
            })
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.MutationId) &&
                entry.Company.Id != Guid.Empty)
            .GroupBy(
                entry => entry.MutationId,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var company = group.Single().Company;
                    return new RentalManagementCompanyDependencyIdentity(
                        company.Id,
                        TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                            company.TenantCode),
                        OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                            company.Code,
                            company.Code));
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<
            string,
            RentalManagementCompanyDependencyIdentity>>
        SelectCleanCanonicalRentalCompanyFallbacksAsync(
            IReadOnlyDictionary<
                string,
                RentalManagementCompanyDependencyIdentity> candidates,
            CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        var candidateIds = candidates.Values
            .Select(identity => identity.OriginalEntityId)
            .Distinct()
            .ToList();
        var cleanEntityIds = await _db.RentalManagementCompanies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(company =>
                candidateIds.Contains(company.Id) &&
                !company.IsDirty)
            .Select(company => company.Id)
            .ToListAsync(ct);
        var cleanEntityIdSet = cleanEntityIds.ToHashSet();

        _db.ChangeTracker.DetectChanges();
        var locallyChangedEntityIds = _db.ChangeTracker
            .Entries<LocalRentalManagementCompany>()
            .Where(entry =>
                entry.State != EntityState.Unchanged ||
                entry.Entity.IsDirty)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        return candidates
            .Where(entry =>
                cleanEntityIdSet.Contains(entry.Value.OriginalEntityId) &&
                !locallyChangedEntityIds.Contains(
                    entry.Value.OriginalEntityId))
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDependencyOnlyConflict(
        ConflictLogDto conflict,
        IReadOnlySet<SyncEntityKey>? dependencyOnlyKeys,
        IReadOnlyDictionary<
            string,
            RentalManagementCompanyDependencyIdentity>
            cleanCanonicalRentalCompanyFallbacks)
    {
        if (dependencyOnlyKeys is not null &&
            TryBuildConflictEntityKey(conflict, out var key) &&
            dependencyOnlyKeys.Contains(key))
        {
            return true;
        }

        if (cleanCanonicalRentalCompanyFallbacks.Count == 0 ||
            !string.Equals(
                NormalizeSyncEntityName(conflict.EntityName),
                NormalizeSyncEntityName(
                    nameof(LocalRentalManagementCompany)),
                StringComparison.OrdinalIgnoreCase) ||
            !IsCanonicalRentalManagementCompanyCompatibilityReason(
                conflict.Reason) ||
            !TryReadConflictRentalManagementCompanyIdentity(
                conflict.ClientJson,
                out var mutationId,
                out var tenantCode,
                out var companyCode) ||
            !cleanCanonicalRentalCompanyFallbacks.TryGetValue(
                mutationId,
                out var expected))
        {
            return false;
        }

        return string.Equals(
                   TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                       tenantCode),
                   expected.TenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                       companyCode,
                       companyCode),
                   expected.CompanyCode,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCanonicalRentalManagementCompanyCompatibilityReason(
        string? reason)
    {
        var normalized = (reason ?? string.Empty).Trim();
        return normalized.StartsWith(
                   "Expected revision mismatch.",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   normalized,
                   "Server version is newer.",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadConflictRentalManagementCompanyIdentity(
        string? clientJson,
        out string mutationId,
        out string tenantCode,
        out string companyCode)
    {
        mutationId = string.Empty;
        tenantCode = string.Empty;
        companyCode = string.Empty;
        if (string.IsNullOrWhiteSpace(clientJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(clientJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value =
                    (property.Value.GetString() ?? string.Empty).Trim();
                if (string.Equals(
                        property.Name,
                        nameof(SyncEntityDto.MutationId),
                        StringComparison.OrdinalIgnoreCase))
                {
                    mutationId = value;
                }
                else if (string.Equals(
                             property.Name,
                             nameof(RentalManagementCompanyDto.TenantCode),
                             StringComparison.OrdinalIgnoreCase))
                {
                    tenantCode = value;
                }
                else if (string.Equals(
                             property.Name,
                             nameof(RentalManagementCompanyDto.Code),
                             StringComparison.OrdinalIgnoreCase))
                {
                    companyCode = value;
                }
            }

            return !string.IsNullOrWhiteSpace(mutationId) &&
                   !string.IsNullOrWhiteSpace(companyCode);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private IEnumerable<(string EntityName, SyncEntityDto Entity)> EnumerateOutgoingMutations(
        SyncPushRequest request,
        IReadOnlySet<SyncEntityKey>? excludedKeys = null)
    {
        foreach (var entry in EnumerateAllOutgoingMutations(request))
        {
            var key = new SyncEntityKey(NormalizeSyncEntityName(entry.EntityName), entry.Entity.Id);
            if (excludedKeys is null || !excludedKeys.Contains(key))
                yield return entry;
        }
    }

    private static IEnumerable<(string EntityName, SyncEntityDto Entity)> EnumerateAllOutgoingMutations(
        SyncPushRequest request)
    {
        foreach (var entity in request.CompanyProfiles)
            yield return (nameof(LocalCompanyProfile), entity);
        foreach (var entity in request.Units)
            yield return (nameof(LocalUnit), entity);
        foreach (var entity in request.CustomerCategories)
            yield return (nameof(LocalCustomerCategory), entity);
        foreach (var entity in request.PriceGradeOptions)
            yield return (nameof(LocalPriceGradeOption), entity);
        foreach (var entity in request.TradeTypeOptions)
            yield return (nameof(LocalTradeTypeOption), entity);
        foreach (var entity in request.ItemCategoryOptions)
            yield return (nameof(LocalItemCategoryOption), entity);
        foreach (var entity in request.CustomerMasters)
            yield return (nameof(LocalCustomerMaster), entity);
        foreach (var entity in request.Customers)
            yield return (nameof(LocalCustomer), entity);
        foreach (var entity in request.CustomerContracts)
            yield return (nameof(LocalCustomerContract), entity);
        foreach (var entity in request.Items)
            yield return (nameof(LocalItem), entity);
        foreach (var entity in request.ItemPriceGrades)
            yield return (nameof(LocalItemPriceGrade), entity);
        foreach (var entity in request.Transactions)
            yield return (nameof(LocalTransaction), entity);
        foreach (var entity in request.TransactionAttachments)
            yield return (nameof(LocalTransactionAttachment), entity);
        foreach (var entity in request.InventoryTransfers)
            yield return (nameof(LocalInventoryTransfer), entity);
        foreach (var entity in request.RentalManagementCompanies)
            yield return (nameof(LocalRentalManagementCompany), entity);
        foreach (var entity in request.RentalBillingProfiles)
            yield return (nameof(LocalRentalBillingProfile), entity);
        foreach (var entity in request.RentalAssets)
            yield return (nameof(LocalRentalAsset), entity);
        foreach (var entity in request.RentalAssetAssignmentHistories)
            yield return (nameof(LocalRentalAssetAssignmentHistory), entity);
        foreach (var entity in request.RentalBillingLogs)
            yield return (nameof(LocalRentalBillingLog), entity);
        foreach (var entity in request.Invoices)
            yield return (nameof(LocalInvoice), entity);
        foreach (var entity in request.Payments)
            yield return (nameof(LocalPayment), entity);
    }

    private IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot>
        BuildPreparedMutationSnapshots(
            SyncPushRequest request,
            IReadOnlySet<SyncEntityKey>? excludedKeys)
    {
        return EnumerateOutgoingMutations(request, excludedKeys)
            .Where(entry => entry.Entity.Id != Guid.Empty)
            .GroupBy(entry => new SyncEntityKey(
                NormalizeSyncEntityName(entry.EntityName),
                entry.Entity.Id))
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var entry = group.Last();
                    var mutationUpdatedAtUtc =
                        entry.Entity.MutationCreatedAtUtc is { } preparedAtUtc &&
                        preparedAtUtc != default
                            ? preparedAtUtc
                            : entry.Entity.UpdatedAtUtc;
                    return new PreparedMutationSnapshot(
                        entry.Entity.ExpectedRevision,
                        NormalizeMutationUtc(mutationUpdatedAtUtc),
                        entry.Entity.IsDeleted,
                        ComputePreparedMutationPayloadHash(
                            entry.EntityName,
                            entry.Entity),
                        InvoiceNumber: entry.Entity is InvoiceDto invoice
                            ? invoice.InvoiceNumber
                            : null,
                        TaxInvoiceNumber: entry.Entity is InvoiceDto taxInvoice
                            ? taxInvoice.TaxInvoiceNumber
                            : null);
                });
    }

    private async Task RecordPreparedMutationsAsync(
        SyncPushRequest request,
        SessionState session,
        string? businessDatabaseNameOverride,
        IReadOnlySet<SyncEntityKey>? excludedKeys,
        CancellationToken ct)
    {
        var outgoing = EnumerateOutgoingMutations(request, excludedKeys)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Entity.MutationId))
            .ToList();
        if (outgoing.Count == 0)
            return;

        var outboxOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(
                _db.Database.GetDbConnection(),
                sqliteOptions => sqliteOptions.CommandTimeout(30))
            .Options;
        await using var outboxDb = new LocalDbContext(outboxOptions);
        var scopeLookup = await BuildPreparedMutationScopeLookupAsync(
            outboxDb,
            request,
            session,
            ct);
        var mutationIds = outgoing.Select(entry => entry.Entity.MutationId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingIds = await outboxDb.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => mutationIds.Contains(entry.MutationId))
            .Select(entry => entry.MutationId)
            .ToListAsync(ct);
        var existingIdSet = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
            businessDatabaseNameOverride ?? session.SelectedBusinessDatabaseName);
        var userId = session.User?.UserId ?? Guid.Empty;
        var addedCount = 0;

        foreach (var (entityName, entity) in outgoing)
        {
            if (existingIdSet.Contains(entity.MutationId))
                continue;

            var scope = ResolvePreparedMutationScope(entity, session, scopeLookup);
            var tenantCode = entity is PriceGradeOptionDto
                ? TenantScopeCatalog.NormalizeTenantCodeOrDefault(businessDatabaseName, scope.TenantCode)
                : scope.TenantCode;
            outboxDb.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = entity.MutationId,
                DeviceId = request.DeviceId,
                EntityName = entityName,
                EntityId = entity.Id,
                ExpectedRevision = entity.ExpectedRevision,
                TenantCode = tenantCode,
                OfficeCode = scope.OfficeCode,
                ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = session.SessionId,
                UserId = userId,
                Status = "Prepared",
                PreparedAtUtc = DateTime.UtcNow
            });
            addedCount++;
        }

        if (addedCount == 0)
            return;

        if (BeforePreparedOutboxSaveAsyncForTesting is not null)
            await BeforePreparedOutboxSaveAsyncForTesting(ct);

        await outboxDb.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyDictionary<string, CurrentPushMutationReceipt>>
        CaptureCurrentPushMutationReceiptsAsync(
            SyncPushRequest request,
            IReadOnlyDictionary<SyncEntityKey, PreparedMutationSnapshot>
                preparedMutationSnapshots,
            SessionState ownerSession,
            string? businessDatabaseNameOverride,
            IReadOnlySet<SyncEntityKey>? excludedKeys,
            CancellationToken ct)
    {
        var outgoingByMutationId = EnumerateOutgoingMutations(request, excludedKeys)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Entity.MutationId))
            .GroupBy(entry => entry.Entity.MutationId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        var mutationIds = outgoingByMutationId.Keys.ToList();
        var rows = mutationIds.Count == 0
            ? []
            : await _db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry => mutationIds.Contains(entry.MutationId))
                .ToListAsync(ct);
        var uniqueRows = rows
            .GroupBy(row => row.MutationId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        var scopeLookup = await BuildPreparedMutationScopeLookupAsync(
            _db,
            request,
            ownerSession,
            ct);
        var expectedBusinessDatabase = TenantScopeCatalog.GetDatabaseName(
            businessDatabaseNameOverride ?? ownerSession.SelectedBusinessDatabaseName);
        var expectedUserId = ownerSession.User?.UserId ?? Guid.Empty;
        var receipts = new Dictionary<string, CurrentPushMutationReceipt>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (mutationId, outgoing) in outgoingByMutationId)
        {
            var key = new SyncEntityKey(
                NormalizeSyncEntityName(outgoing.EntityName),
                outgoing.Entity.Id);
            if (!uniqueRows.TryGetValue(mutationId, out var row) ||
                !preparedMutationSnapshots.TryGetValue(key, out var prepared) ||
                prepared.ExpectedRevision != outgoing.Entity.ExpectedRevision)
            {
                continue;
            }

            var payloadHash = ComputePreparedMutationPayloadHash(
                outgoing.EntityName,
                outgoing.Entity);
            if (!string.Equals(
                    payloadHash,
                    prepared.PayloadHash,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var scope = ResolvePreparedMutationScope(
                outgoing.Entity,
                ownerSession,
                scopeLookup);
            var expectedTenantCode = outgoing.Entity is PriceGradeOptionDto
                ? TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                    expectedBusinessDatabase,
                    scope.TenantCode)
                : scope.TenantCode;
            if (string.Equals(row.Status, "Acknowledged", StringComparison.Ordinal) ||
                !MatchesCurrentPushOutboxAnchor(
                    row,
                    outgoing,
                    request.DeviceId,
                    ownerSession,
                    expectedBusinessDatabase,
                    expectedUserId,
                    expectedTenantCode,
                    scope))
            {
                continue;
            }

            receipts[mutationId] = new CurrentPushMutationReceipt(
                row.Id,
                mutationId,
                key,
                outgoing.Entity.ExpectedRevision,
                payloadHash,
                row.DeviceId,
                row.BusinessDatabaseName,
                row.TenantCode,
                row.OfficeCode,
                row.ResponsibleOfficeCode,
                row.SessionId,
                row.UserId,
                NormalizeMutationUtc(row.PreparedAtUtc),
                IsDurable: true);
        }

        if (excludedKeys is not null && excludedKeys.Count > 0)
        {
            var dependencyOutgoing = EnumerateOutgoingMutations(
                    request,
                    excludedKeys: null)
                .Where(entry => excludedKeys.Contains(new SyncEntityKey(
                    NormalizeSyncEntityName(entry.EntityName),
                    entry.Entity.Id)))
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Entity.MutationId))
                .GroupBy(
                    entry => entry.Entity.MutationId,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single());
            foreach (var outgoing in dependencyOutgoing)
            {
                var key = new SyncEntityKey(
                    NormalizeSyncEntityName(outgoing.EntityName),
                    outgoing.Entity.Id);
                if (!preparedMutationSnapshots.TryGetValue(
                        key,
                        out var prepared) ||
                    prepared.ExpectedRevision !=
                        outgoing.Entity.ExpectedRevision)
                {
                    continue;
                }

                var payloadHash = ComputePreparedMutationPayloadHash(
                    outgoing.EntityName,
                    outgoing.Entity);
                if (!string.Equals(
                        payloadHash,
                        prepared.PayloadHash,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var scope = ResolvePreparedMutationScope(
                    outgoing.Entity,
                    ownerSession,
                    scopeLookup);
                var tenantCode = outgoing.Entity is PriceGradeOptionDto
                    ? TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                        expectedBusinessDatabase,
                        scope.TenantCode)
                    : scope.TenantCode;
                var mutationPreparedAtUtc =
                    outgoing.Entity.MutationCreatedAtUtc is { } preparedAtUtc &&
                    preparedAtUtc != default
                        ? preparedAtUtc
                        : outgoing.Entity.UpdatedAtUtc;
                receipts[outgoing.Entity.MutationId] =
                    new CurrentPushMutationReceipt(
                        Guid.Empty,
                        outgoing.Entity.MutationId,
                        key,
                        outgoing.Entity.ExpectedRevision,
                        payloadHash,
                        request.DeviceId,
                        expectedBusinessDatabase,
                        tenantCode,
                        scope.OfficeCode,
                        scope.ResponsibleOfficeCode,
                        ownerSession.SessionId,
                        expectedUserId,
                        NormalizeMutationUtc(mutationPreparedAtUtc),
                        IsDurable: false);
            }
        }

        return receipts;
    }

    private sealed record PreparedMutationScope(string TenantCode, string OfficeCode, string ResponsibleOfficeCode);

    private sealed class PreparedMutationScopeLookup
    {
        public Dictionary<Guid, PreparedMutationScope> CustomerScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> ItemScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> InvoiceScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> TransactionScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> VerifiedCustomerScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> VerifiedItemScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> VerifiedInvoiceScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> VerifiedTransactionScopeById { get; } = new();
    }

    private async Task<PreparedMutationScopeLookup> BuildPreparedMutationScopeLookupAsync(
        LocalDbContext db,
        SyncPushRequest request,
        SessionState session,
        CancellationToken ct)
    {
        var lookup = new PreparedMutationScopeLookup();

        var customerIds = request.CustomerContracts
            .Where(contract => contract.CustomerId != Guid.Empty)
            .Select(contract => contract.CustomerId)
            .Distinct()
            .ToList();
        if (customerIds.Count > 0)
        {
            var customers = await db.Customers.IgnoreQueryFilters()
                .Where(customer => customerIds.Contains(customer.Id))
                .Select(customer => new
                {
                    customer.Id,
                    customer.TenantCode,
                    customer.OfficeCode,
                    customer.ResponsibleOfficeCode
                })
                .ToListAsync(ct);

            foreach (var customer in customers)
            {
                lookup.CustomerScopeById[customer.Id] = NormalizePreparedMutationScope(
                    customer.TenantCode,
                    customer.OfficeCode,
                    customer.ResponsibleOfficeCode,
                    session,
                    customer.OfficeCode);
                if (TryNormalizeOutboxReconciliationEntityScope(
                        customer.TenantCode,
                        customer.OfficeCode,
                        customer.ResponsibleOfficeCode,
                        out var verifiedScope))
                {
                    lookup.VerifiedCustomerScopeById[customer.Id] = verifiedScope;
                }
            }
        }

        var itemIds = request.ItemPriceGrades
            .Where(price => price.ItemId != Guid.Empty)
            .Select(price => price.ItemId)
            .Distinct()
            .ToList();
        if (itemIds.Count > 0)
        {
            var items = await db.Items.IgnoreQueryFilters()
                .Where(item => itemIds.Contains(item.Id))
                .Select(item => new
                {
                    item.Id,
                    item.TenantCode,
                    item.OfficeCode
                })
                .ToListAsync(ct);

            foreach (var item in items)
            {
                var itemResponsibleOfficeCode = OfficeCodeCatalog.IsSharedOfficeCode(item.OfficeCode)
                    ? session.OfficeCode
                    : item.OfficeCode;
                lookup.ItemScopeById[item.Id] = NormalizePreparedMutationScope(
                    item.TenantCode,
                    item.OfficeCode,
                    itemResponsibleOfficeCode,
                    session,
                    item.OfficeCode);
                if (TryNormalizeOutboxReconciliationEntityScope(
                        item.TenantCode,
                        item.OfficeCode,
                        itemResponsibleOfficeCode,
                        out var verifiedScope))
                {
                    lookup.VerifiedItemScopeById[item.Id] = verifiedScope;
                }
            }
        }

        var invoiceIds = request.Payments
            .Where(payment => payment.InvoiceId != Guid.Empty)
            .Select(payment => payment.InvoiceId)
            .Distinct()
            .ToList();
        if (invoiceIds.Count > 0)
        {
            var invoices = await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoiceIds.Contains(invoice.Id))
                .Select(invoice => new
                {
                    invoice.Id,
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode
                })
                .ToListAsync(ct);

            foreach (var invoice in invoices)
            {
                lookup.InvoiceScopeById[invoice.Id] = NormalizePreparedMutationScope(
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode,
                    session,
                    invoice.OfficeCode);
                if (TryNormalizeOutboxReconciliationEntityScope(
                        invoice.TenantCode,
                        invoice.OfficeCode,
                        invoice.ResponsibleOfficeCode,
                        out var verifiedScope))
                {
                    lookup.VerifiedInvoiceScopeById[invoice.Id] = verifiedScope;
                }
            }
        }

        var transactionIds = request.TransactionAttachments
            .Where(attachment => attachment.TransactionId != Guid.Empty)
            .Select(attachment => attachment.TransactionId)
            .Distinct()
            .ToList();
        if (transactionIds.Count > 0)
        {
            var transactions = await db.Transactions.IgnoreQueryFilters()
                .Where(transaction => transactionIds.Contains(transaction.Id))
                .Select(transaction => new
                {
                    transaction.Id,
                    transaction.TenantCode,
                    transaction.OfficeCode,
                    transaction.ResponsibleOfficeCode
                })
                .ToListAsync(ct);

            foreach (var transaction in transactions)
            {
                lookup.TransactionScopeById[transaction.Id] = NormalizePreparedMutationScope(
                    transaction.TenantCode,
                    transaction.OfficeCode,
                    transaction.ResponsibleOfficeCode,
                    session,
                    transaction.OfficeCode);
                if (TryNormalizeOutboxReconciliationEntityScope(
                        transaction.TenantCode,
                        transaction.OfficeCode,
                        transaction.ResponsibleOfficeCode,
                        out var verifiedScope))
                {
                    lookup.VerifiedTransactionScopeById[transaction.Id] = verifiedScope;
                }
            }
        }

        return lookup;
    }

    private PreparedMutationScope ResolvePreparedMutationScope(
        SyncEntityDto entity,
        SessionState session,
        PreparedMutationScopeLookup lookup)
    {
        return entity switch
        {
            CompanyProfileDto dto => NormalizePreparedMutationScope(session.TenantCode, dto.OfficeCode, dto.OfficeCode, session, dto.OfficeCode),
            UnitDto => NormalizePreparedMutationScope(session.TenantCode, OfficeCodeCatalog.Shared, session.OfficeCode, session, OfficeCodeCatalog.Shared),
            CustomerCategoryDto => NormalizePreparedMutationScope(session.TenantCode, OfficeCodeCatalog.Shared, session.OfficeCode, session, OfficeCodeCatalog.Shared),
            PriceGradeOptionDto => NormalizePreparedMutationScope(session.TenantCode, OfficeCodeCatalog.Shared, session.OfficeCode, session, OfficeCodeCatalog.Shared),
            TradeTypeOptionDto => NormalizePreparedMutationScope(session.TenantCode, OfficeCodeCatalog.Shared, session.OfficeCode, session, OfficeCodeCatalog.Shared),
            ItemCategoryOptionDto => NormalizePreparedMutationScope(session.TenantCode, OfficeCodeCatalog.Shared, session.OfficeCode, session, OfficeCodeCatalog.Shared),
            CustomerMasterDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, session.OfficeCode, session, dto.OfficeCode),
            CustomerDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            CustomerContractDto dto when lookup.CustomerScopeById.TryGetValue(dto.CustomerId, out var customerScope) => customerScope,
            CustomerContractDto => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode),
            ItemDto dto => NormalizePreparedMutationScope(
                dto.TenantCode,
                dto.OfficeCode,
                OfficeCodeCatalog.IsSharedOfficeCode(dto.OfficeCode) ? session.OfficeCode : dto.OfficeCode,
                session,
                dto.OfficeCode),
            ItemPriceGradeDto dto when lookup.ItemScopeById.TryGetValue(dto.ItemId, out var itemScope) => itemScope,
            ItemPriceGradeDto => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode),
            TransactionDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            TransactionAttachmentDto dto when lookup.TransactionScopeById.TryGetValue(dto.TransactionId, out var transactionScope) => transactionScope,
            TransactionAttachmentDto => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode),
            InventoryTransferDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.SourceOfficeCode, dto.TargetOfficeCode, session, dto.SourceOfficeCode),
            RentalManagementCompanyDto dto => NormalizePreparedMutationScope(dto.TenantCode, OfficeCodeCatalog.Shared, session.OfficeCode, session, OfficeCodeCatalog.Shared),
            RentalBillingProfileDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            RentalAssetDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            RentalAssetAssignmentHistoryDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            RentalBillingLogDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            InvoiceDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            PaymentDto dto when lookup.InvoiceScopeById.TryGetValue(dto.InvoiceId, out var invoiceScope) => invoiceScope,
            PaymentDto => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode),
            _ => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode)
        };
    }

    private static bool TryResolveOutboxReconciliationScope(
        SyncEntityDto entity,
        SessionState session,
        PreparedMutationScopeLookup lookup,
        out PreparedMutationScope scope)
    {
        scope = default!;
        return entity switch
        {
            CompanyProfileDto dto => TryNormalizeOutboxReconciliationEntityScope(
                session.TenantCode,
                dto.OfficeCode,
                dto.OfficeCode,
                out scope),
            UnitDto or CustomerCategoryDto or PriceGradeOptionDto or TradeTypeOptionDto or ItemCategoryOptionDto or RentalManagementCompanyDto
                => TryBuildSharedOutboxReconciliationScope(session, out scope),
            CustomerMasterDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                session.OfficeCode,
                out scope),
            CustomerDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                out scope),
            CustomerContractDto dto =>
                TryGetVerifiedOutboxReconciliationScope(
                    lookup.VerifiedCustomerScopeById,
                    dto.CustomerId,
                    out scope),
            ItemDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                OfficeCodeCatalog.IsSharedOfficeCode(dto.OfficeCode) ? session.OfficeCode : dto.OfficeCode,
                out scope),
            ItemPriceGradeDto dto =>
                TryGetVerifiedOutboxReconciliationScope(
                    lookup.VerifiedItemScopeById,
                    dto.ItemId,
                    out scope),
            TransactionDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                out scope),
            TransactionAttachmentDto dto =>
                TryGetVerifiedOutboxReconciliationScope(
                    lookup.VerifiedTransactionScopeById,
                    dto.TransactionId,
                    out scope),
            InventoryTransferDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.SourceOfficeCode,
                dto.TargetOfficeCode,
                out scope),
            RentalBillingProfileDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                out scope),
            RentalAssetDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                out scope),
            RentalAssetAssignmentHistoryDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                out scope),
            RentalBillingLogDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                out scope),
            InvoiceDto dto => TryNormalizeOutboxReconciliationEntityScope(
                dto.TenantCode,
                dto.OfficeCode,
                dto.ResponsibleOfficeCode,
                out scope),
            PaymentDto dto =>
                TryGetVerifiedOutboxReconciliationScope(
                    lookup.VerifiedInvoiceScopeById,
                    dto.InvoiceId,
                    out scope),
            _ => false
        };
    }

    private static bool TryGetVerifiedOutboxReconciliationScope(
        IReadOnlyDictionary<Guid, PreparedMutationScope> scopes,
        Guid entityId,
        out PreparedMutationScope scope)
    {
        if (entityId != Guid.Empty &&
            scopes.TryGetValue(entityId, out var verifiedScope) &&
            verifiedScope is not null)
        {
            scope = verifiedScope;
            return true;
        }

        scope = default!;
        return false;
    }

    private static bool TryBuildSharedOutboxReconciliationScope(
        SessionState session,
        out PreparedMutationScope scope)
    {
        scope = default!;
        if (!TenantScopeCatalog.TryNormalizeTenantCode(session.TenantCode, out var tenantCode) ||
            !OfficeCodeCatalog.TryNormalizeOfficeCode(session.OfficeCode, out var responsibleOfficeCode) ||
            !TenantScopeCatalog.TenantContainsOffice(tenantCode, responsibleOfficeCode))
        {
            return false;
        }

        scope = new PreparedMutationScope(
            tenantCode,
            OfficeCodeCatalog.Shared,
            responsibleOfficeCode);
        return true;
    }

    private static bool TryNormalizeOutboxReconciliationEntityScope(
        string? tenantCode,
        string? officeCode,
        string? responsibleOfficeCode,
        out PreparedMutationScope scope)
    {
        scope = default!;
        if (!TenantScopeCatalog.TryNormalizeTenantCode(tenantCode, out var normalizedTenant) ||
            !OfficeCodeCatalog.TryNormalizeScope(officeCode, out var normalizedOffice))
        {
            return false;
        }

        string normalizedResponsibleOffice;
        if (string.IsNullOrWhiteSpace(responsibleOfficeCode))
        {
            if (string.Equals(
                    normalizedOffice,
                    OfficeCodeCatalog.Shared,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            normalizedResponsibleOffice = normalizedOffice;
        }
        else if (!OfficeCodeCatalog.TryNormalizeOfficeCode(
                     responsibleOfficeCode,
                     out normalizedResponsibleOffice))
        {
            return false;
        }

        if ((!string.Equals(
                 normalizedOffice,
                 OfficeCodeCatalog.Shared,
                 StringComparison.OrdinalIgnoreCase) &&
             !TenantScopeCatalog.TenantContainsOffice(normalizedTenant, normalizedOffice)) ||
            !TenantScopeCatalog.TenantContainsOffice(normalizedTenant, normalizedResponsibleOffice))
        {
            return false;
        }

        scope = new PreparedMutationScope(
            normalizedTenant,
            normalizedOffice,
            normalizedResponsibleOffice);
        return true;
    }

    private static PreparedMutationScope NormalizePreparedMutationScope(
        string? tenantCode,
        string? officeCode,
        string? responsibleOfficeCode,
        SessionState session,
        string? fallbackOfficeCode)
    {
        var fallbackOffice = !string.IsNullOrWhiteSpace(fallbackOfficeCode)
            ? fallbackOfficeCode
            : session.OfficeCode;
        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, fallbackOffice);
        var normalizedResponsibleOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            responsibleOfficeCode,
            string.IsNullOrWhiteSpace(normalizedOffice) ? fallbackOffice : normalizedOffice);
        var normalizedTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            tenantCode,
            normalizedOffice,
            session.TenantCode,
            normalizedResponsibleOffice);
        return new PreparedMutationScope(normalizedTenant, normalizedOffice, normalizedResponsibleOffice);
    }

    private static bool MatchesCurrentPushOutboxAnchor(
        LocalSyncOutboxEntry row,
        (string EntityName, SyncEntityDto Entity) outgoing,
        string requestDeviceId,
        SessionState ownerSession,
        string expectedBusinessDatabase,
        Guid expectedUserId,
        string expectedTenantCode,
        PreparedMutationScope scope)
        => row.Id != Guid.Empty &&
           !string.IsNullOrWhiteSpace(row.MutationId) &&
           string.Equals(
               row.MutationId,
               outgoing.Entity.MutationId,
               StringComparison.OrdinalIgnoreCase) &&
           new SyncEntityKey(
               NormalizeSyncEntityName(row.EntityName),
               row.EntityId) == new SyncEntityKey(
               NormalizeSyncEntityName(outgoing.EntityName),
               outgoing.Entity.Id) &&
           row.ExpectedRevision == outgoing.Entity.ExpectedRevision &&
           string.Equals(
               row.DeviceId,
               requestDeviceId,
               StringComparison.OrdinalIgnoreCase) &&
           row.SessionId == ownerSession.SessionId &&
           row.UserId == expectedUserId &&
           string.Equals(
               TenantScopeCatalog.GetDatabaseName(row.BusinessDatabaseName),
               expectedBusinessDatabase,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                   row.TenantCode,
                   row.TenantCode),
               TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                   expectedTenantCode,
                   expectedTenantCode),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                   row.OfficeCode,
                   row.OfficeCode),
               OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                   scope.OfficeCode,
                   scope.OfficeCode),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                   row.ResponsibleOfficeCode,
                   row.ResponsibleOfficeCode),
               OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                   scope.ResponsibleOfficeCode,
                   scope.ResponsibleOfficeCode),
               StringComparison.OrdinalIgnoreCase);

    private static bool MatchesCurrentPushReceipt(
        LocalSyncOutboxEntry row,
        CurrentPushMutationReceipt receipt)
        => row.Id == receipt.OutboxRowId &&
           string.Equals(
               row.MutationId,
               receipt.MutationId,
               StringComparison.OrdinalIgnoreCase) &&
           new SyncEntityKey(
               NormalizeSyncEntityName(row.EntityName),
               row.EntityId) == receipt.EntityKey &&
           row.ExpectedRevision == receipt.ExpectedRevision &&
           string.Equals(
               row.DeviceId,
               receipt.DeviceId,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               TenantScopeCatalog.GetDatabaseName(row.BusinessDatabaseName),
               TenantScopeCatalog.GetDatabaseName(receipt.BusinessDatabaseName),
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               NormalizeOutboxScopeText(row.TenantCode),
               NormalizeOutboxScopeText(receipt.TenantCode),
               StringComparison.Ordinal) &&
           string.Equals(
               NormalizeOutboxScopeText(row.OfficeCode),
               NormalizeOutboxScopeText(receipt.OfficeCode),
               StringComparison.Ordinal) &&
           string.Equals(
               NormalizeOutboxScopeText(row.ResponsibleOfficeCode),
               NormalizeOutboxScopeText(receipt.ResponsibleOfficeCode),
               StringComparison.Ordinal) &&
           row.SessionId == receipt.SessionId &&
           row.UserId == receipt.UserId &&
           NormalizeMutationUtc(row.PreparedAtUtc) == receipt.PreparedAtUtc;

    private async Task MarkOutboxSentAsync(
        SyncPushRequest request,
        IReadOnlySet<SyncEntityKey>? excludedKeys,
        IReadOnlyDictionary<string, CurrentPushMutationReceipt>
            currentPushReceipts,
        CancellationToken ct)
    {
        var outgoingByMutationId = EnumerateOutgoingMutations(request, excludedKeys)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Entity.MutationId))
            .GroupBy(entry => entry.Entity.MutationId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        if (outgoingByMutationId.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var rowIds = new List<Guid>();
        foreach (var (mutationId, outgoing) in outgoingByMutationId)
        {
            if (!currentPushReceipts.TryGetValue(mutationId, out var receipt) ||
                !receipt.IsDurable ||
                receipt.EntityKey != new SyncEntityKey(
                    NormalizeSyncEntityName(outgoing.EntityName),
                    outgoing.Entity.Id) ||
                receipt.ExpectedRevision != outgoing.Entity.ExpectedRevision ||
                !string.Equals(
                    receipt.PayloadHash,
                    ComputePreparedMutationPayloadHash(
                        outgoing.EntityName,
                        outgoing.Entity),
                    StringComparison.Ordinal))
            {
                throw new SyncPullBlockedException(
                    "동기화 전송 영수증과 현재 변경 payload가 달라 응답 반영을 중단했습니다.");
            }

            var affected = await _db.SyncOutboxEntries
                .Where(entry =>
                    entry.Id == receipt.OutboxRowId &&
                    entry.Status != "Acknowledged" &&
                    entry.MutationId == receipt.MutationId &&
                    entry.EntityName == outgoing.EntityName &&
                    entry.EntityId == outgoing.Entity.Id &&
                    entry.ExpectedRevision == receipt.ExpectedRevision &&
                    entry.DeviceId == receipt.DeviceId &&
                    entry.BusinessDatabaseName == receipt.BusinessDatabaseName &&
                    entry.TenantCode == receipt.TenantCode &&
                    entry.OfficeCode == receipt.OfficeCode &&
                    entry.ResponsibleOfficeCode == receipt.ResponsibleOfficeCode &&
                    entry.SessionId == receipt.SessionId &&
                    entry.UserId == receipt.UserId &&
                    entry.PreparedAtUtc == receipt.PreparedAtUtc)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(entry => entry.Status, "Sent")
                        .SetProperty(entry => entry.SentAtUtc, now)
                        .SetProperty(entry => entry.AcknowledgedAtUtc, (DateTime?)null)
                        .SetProperty(entry => entry.AcceptedRevision, 0L)
                        .SetProperty(entry => entry.AcceptedUpdatedAtUtc, (DateTime?)null),
                    ct);
            if (affected != 1)
            {
                throw new SyncPullBlockedException(
                    "동기화 전송 영수증의 outbox 행이 변경되어 응답 반영을 중단했습니다.");
            }

            rowIds.Add(receipt.OutboxRowId);
        }

        DetachTrackedOutboxEntries(rowIds);
    }

    private async Task MarkOutboxAcknowledgedAsync(
        SyncPushRequest request,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions,
        CancellationToken ct)
    {
        var preparedMutationSnapshots = BuildPreparedMutationSnapshots(
            request,
            excludedKeys: null);
        var currentPushReceipts = await CaptureCurrentPushMutationReceiptsAsync(
            request,
            preparedMutationSnapshots,
            _session,
            businessDatabaseNameOverride: null,
            excludedKeys: null,
            ct);
        await MarkOutboxAcknowledgedCoreAsync(
            request,
            acceptedRevisions,
            excludedKeys: null,
            _session,
            businessDatabaseNameOverride: null,
            currentPushReceipts,
            ct);
    }

    private async Task MarkOutboxAcknowledgedCoreAsync(
        SyncPushRequest request,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions,
        IReadOnlySet<SyncEntityKey>? excludedKeys,
        SessionState ownerSession,
        string? businessDatabaseNameOverride,
        IReadOnlyDictionary<string, CurrentPushMutationReceipt>
            currentPushReceipts,
        CancellationToken ct)
    {
        if (acceptedRevisions.Count == 0)
            return;

        var acceptedRevisionByKey = acceptedRevisions
            .Where(revision => revision.EntityId != Guid.Empty)
            .GroupBy(revision => new SyncEntityKey(
                NormalizeSyncEntityName(revision.EntityName),
                revision.EntityId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(revision => revision.Revision)
                    .ThenByDescending(revision => revision.UpdatedAtUtc)
                    .First());
        var acceptedKeys = acceptedRevisionByKey.Keys.ToHashSet();

        if (acceptedKeys.Count == 0)
            return;

        var acceptedMutations = EnumerateOutgoingMutations(request, excludedKeys)
            .Where(entry => acceptedKeys.Contains(new SyncEntityKey(NormalizeSyncEntityName(entry.EntityName), entry.Entity.Id)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Entity.MutationId))
            .GroupBy(entry => entry.Entity.MutationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);
        var mutationIds = acceptedMutations.Keys.ToList();
        if (mutationIds.Count == 0)
            return;

        var expectedBusinessDatabase = TenantScopeCatalog.GetDatabaseName(
            businessDatabaseNameOverride ?? ownerSession.SelectedBusinessDatabaseName);
        var expectedUserId = ownerSession.User?.UserId ?? Guid.Empty;

        var currentRows = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => mutationIds.Contains(entry.MutationId))
            .ToListAsync(ct);
        var verifiedCurrentRows = currentRows
            .GroupBy(row => row.MutationId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .Where(row =>
            {
                if (!acceptedMutations.TryGetValue(row.MutationId, out var outgoing))
                    return false;

                var expectedKey = new SyncEntityKey(
                    NormalizeSyncEntityName(outgoing.EntityName),
                    outgoing.Entity.Id);
                if (!currentPushReceipts.TryGetValue(
                        row.MutationId,
                        out var currentPushReceipt) ||
                    !MatchesCurrentPushReceipt(row, currentPushReceipt) ||
                    !currentPushReceipt.IsDurable ||
                    currentPushReceipt.EntityKey != expectedKey ||
                    currentPushReceipt.ExpectedRevision !=
                        outgoing.Entity.ExpectedRevision ||
                    !string.Equals(
                        currentPushReceipt.PayloadHash,
                        ComputePreparedMutationPayloadHash(
                            outgoing.EntityName,
                            outgoing.Entity),
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return string.Equals(row.Status, "Sent", StringComparison.Ordinal) &&
                       string.Equals(
                           currentPushReceipt.DeviceId,
                           request.DeviceId,
                           StringComparison.OrdinalIgnoreCase) &&
                       currentPushReceipt.SessionId == ownerSession.SessionId &&
                       currentPushReceipt.UserId == expectedUserId &&
                       string.Equals(
                           TenantScopeCatalog.GetDatabaseName(
                               currentPushReceipt.BusinessDatabaseName),
                           expectedBusinessDatabase,
                           StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        var acknowledgedAnchors = verifiedCurrentRows
            .GroupBy(row => new SyncEntityKey(NormalizeSyncEntityName(row.EntityName), row.EntityId))
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single());
        if (acknowledgedAnchors.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var acknowledgedRows = acknowledgedAnchors.Values.ToList();
        if (acknowledgedRows.Count == 0)
            return;

        var acknowledgedEntityIds = acknowledgedAnchors.Keys
            .Select(key => key.EntityId)
            .Distinct()
            .ToList();
        var supersedeCandidates = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                acknowledgedEntityIds.Contains(entry.EntityId) &&
                entry.Status != "Acknowledged")
            .ToListAsync(ct);

        var acknowledgedRowIds = new List<Guid>();
        foreach (var row in acknowledgedRows)
        {
            var key = new SyncEntityKey(
                NormalizeSyncEntityName(row.EntityName),
                row.EntityId);
            var accepted = acceptedRevisionByKey[key];
            var affected = await _db.SyncOutboxEntries
                .Where(entry =>
                    entry.Id == row.Id &&
                    entry.Status == "Sent" &&
                    entry.MutationId == row.MutationId &&
                    entry.EntityName == row.EntityName &&
                    entry.EntityId == row.EntityId &&
                    entry.ExpectedRevision == row.ExpectedRevision &&
                    entry.DeviceId == row.DeviceId &&
                    entry.BusinessDatabaseName == row.BusinessDatabaseName &&
                    entry.TenantCode == row.TenantCode &&
                    entry.OfficeCode == row.OfficeCode &&
                    entry.ResponsibleOfficeCode == row.ResponsibleOfficeCode &&
                    entry.SessionId == row.SessionId &&
                    entry.UserId == row.UserId &&
                    entry.PreparedAtUtc == row.PreparedAtUtc)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(entry => entry.Status, "Acknowledged")
                        .SetProperty(entry => entry.AcknowledgedAtUtc, now)
                        .SetProperty(entry => entry.AcceptedRevision, accepted.Revision)
                        .SetProperty(
                            entry => entry.AcceptedUpdatedAtUtc,
                            (DateTime?)NormalizeMutationUtc(accepted.UpdatedAtUtc))
                        .SetProperty(entry => entry.ErrorMessage, string.Empty),
                    ct);
            if (affected != 1)
                continue;

            acknowledgedRowIds.Add(row.Id);
            var olderSameScopeRows = supersedeCandidates
                .Where(candidate =>
                    candidate.Id != row.Id &&
                    new SyncEntityKey(
                        NormalizeSyncEntityName(candidate.EntityName),
                        candidate.EntityId) == key &&
                    NormalizeMutationUtc(candidate.PreparedAtUtc) <
                        NormalizeMutationUtc(row.PreparedAtUtc) &&
                    HasProvablySameOutboxSupersedeScope(candidate, row))
                .ToList();
            foreach (var older in olderSameScopeRows)
            {
                var refreshedOlder = await _db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleOrDefaultAsync(entry => entry.Id == older.Id, ct);
                if (refreshedOlder is null ||
                    string.Equals(
                        refreshedOlder.Status,
                        "Acknowledged",
                        StringComparison.Ordinal) ||
                    NormalizeMutationUtc(refreshedOlder.PreparedAtUtc) >=
                        NormalizeMutationUtc(row.PreparedAtUtc) ||
                    !HasProvablySameOutboxSupersedeScope(
                        refreshedOlder,
                        row))
                {
                    continue;
                }

                if (BeforeOutboxSupersedeUpdateAsyncForTesting is not null)
                {
                    await BeforeOutboxSupersedeUpdateAsyncForTesting(
                        refreshedOlder.Id,
                        ct);
                }

                var superseded = await _db.SyncOutboxEntries
                    .Where(entry =>
                        entry.Id == refreshedOlder.Id &&
                        entry.Status == refreshedOlder.Status &&
                        entry.MutationId == refreshedOlder.MutationId &&
                        entry.EntityName == refreshedOlder.EntityName &&
                        entry.EntityId == refreshedOlder.EntityId &&
                        entry.ExpectedRevision == refreshedOlder.ExpectedRevision &&
                        entry.DeviceId == refreshedOlder.DeviceId &&
                        entry.BusinessDatabaseName == refreshedOlder.BusinessDatabaseName &&
                        entry.TenantCode == refreshedOlder.TenantCode &&
                        entry.OfficeCode == refreshedOlder.OfficeCode &&
                        entry.ResponsibleOfficeCode == refreshedOlder.ResponsibleOfficeCode &&
                        entry.SessionId == refreshedOlder.SessionId &&
                        entry.UserId == refreshedOlder.UserId &&
                        entry.PreparedAtUtc == refreshedOlder.PreparedAtUtc)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(entry => entry.Status, "Acknowledged")
                            .SetProperty(entry => entry.AcknowledgedAtUtc, now)
                            .SetProperty(entry => entry.AcceptedRevision, accepted.Revision)
                            .SetProperty(
                                entry => entry.AcceptedUpdatedAtUtc,
                                (DateTime?)NormalizeMutationUtc(accepted.UpdatedAtUtc))
                            .SetProperty(entry => entry.ErrorMessage, string.Empty),
                        ct);
                if (superseded == 1)
                    acknowledgedRowIds.Add(older.Id);
            }
        }

        DetachTrackedOutboxEntries(acknowledgedRowIds);
    }

    private void DetachTrackedOutboxEntries(IReadOnlyCollection<Guid> rowIds)
    {
        if (rowIds.Count == 0)
            return;

        foreach (var entry in _db.ChangeTracker.Entries<LocalSyncOutboxEntry>().ToList())
        {
            if (rowIds.Contains(entry.Entity.Id))
                entry.State = EntityState.Detached;
        }
    }

    private readonly record struct SyncEntityKey(string EntityName, Guid EntityId);

    private sealed record InventoryTransferPurgePushAcknowledgement(
        RecycleBinPurgeRecordDto PurgeRecord,
        SyncAcceptedRevisionDto AcceptedRevision,
        InventoryTransferDto? SubmittedTransfer = null);

    private readonly record struct
        RentalManagementCompanyDependencyIdentity(
            Guid OriginalEntityId,
            string TenantCode,
            string CompanyCode);
    private sealed class TrackedMutationPreservation(
        ILocalSyncEntity entity,
        IReadOnlyList<TrackedEntityPreservation> entries)
    {
        public ILocalSyncEntity Entity { get; } = entity;
        public IReadOnlyList<TrackedEntityPreservation> Entries { get; } = entries;

        public void RebaseAcceptedRevision(long acceptedRevision)
        {
            if (acceptedRevision <= 0 || acceptedRevision < Entity.Revision)
                return;

            Entity.Revision = acceptedRevision;
            var root = Entries.FirstOrDefault(entry =>
                ReferenceEquals(entry.Entity, Entity));
            if (root is null)
                return;

            root.OriginalValues[nameof(ILocalSyncEntity.Revision)] = acceptedRevision;
            root.ModifiedProperties.Remove(nameof(ILocalSyncEntity.Revision));
        }
    }

    private sealed class TrackedEntityPreservation(
        object entity,
        EntityState originalState,
        Dictionary<string, object?> originalValues,
        HashSet<string> modifiedProperties,
        Dictionary<string, object?> primaryKeyValues)
    {
        public object Entity { get; } = entity;
        public EntityState OriginalState { get; } = originalState;
        public Dictionary<string, object?> OriginalValues { get; } = originalValues;
        public HashSet<string> ModifiedProperties { get; } = modifiedProperties;
        public Dictionary<string, object?> PrimaryKeyValues { get; } = primaryKeyValues;

        public static TrackedEntityPreservation Capture(
            EntityEntry entry,
            EntityState originalState)
            => new(
                entry.Entity,
                originalState,
                entry.Properties.ToDictionary(
                    property => property.Metadata.Name,
                    property => property.OriginalValue,
                    StringComparer.Ordinal),
                entry.Properties
                    .Where(property =>
                        property.IsModified ||
                        !Equals(property.CurrentValue, property.OriginalValue))
                    .Select(property => property.Metadata.Name)
                    .ToHashSet(StringComparer.Ordinal),
                entry.Metadata.FindPrimaryKey()?.Properties.ToDictionary(
                    property => property.Name,
                    property => entry.Property(property.Name).CurrentValue,
                    StringComparer.Ordinal) ??
                new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    private sealed class TrackedEntryPushBaseline(
        EntityState state,
        Dictionary<string, object?> currentValues)
    {
        public EntityState State { get; } = state;
        public Dictionary<string, object?> CurrentValues { get; } = currentValues;

        public bool HasChanged(EntityEntry entry)
        {
            if (entry.State != State)
                return true;

            return entry.Properties.Any(property =>
                !CurrentValues.TryGetValue(property.Metadata.Name, out var previousValue) ||
                !Equals(previousValue, property.CurrentValue));
        }

        public static TrackedEntryPushBaseline Capture(EntityEntry entry)
            => new(
                entry.State,
                entry.Properties.ToDictionary(
                    property => property.Metadata.Name,
                    property => property.CurrentValue,
                    StringComparer.Ordinal));
    }

    private readonly record struct PreparedMutationSnapshot(
        long ExpectedRevision,
        DateTime UpdatedAtUtc,
        bool IsDeleted,
        string PayloadHash,
        string? InvoiceNumber,
        string? TaxInvoiceNumber);
    private sealed record CurrentPushMutationReceipt(
        Guid OutboxRowId,
        string MutationId,
        SyncEntityKey EntityKey,
        long ExpectedRevision,
        string PayloadHash,
        string DeviceId,
        string BusinessDatabaseName,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        Guid SessionId,
        Guid UserId,
        DateTime PreparedAtUtc,
        bool IsDurable);
    private sealed record ServerNewerConflictResolution(
        IReadOnlyList<ConflictLogDto> ResolvedConflicts,
        IReadOnlyList<ConflictLogDto> PreservedConflicts);
    private readonly record struct OutboxSupersedeScope(
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string BusinessDatabaseName,
        string DeviceId,
        Guid SessionId,
        Guid UserId);
    private readonly record struct OutboxReconciliationOwnerScope(
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string BusinessDatabaseName,
        string DeviceId,
        Guid UserId);
    private readonly record struct DeferredPurgeOwnerScope(
        string BusinessDatabaseName,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode);

    private static int CompareOutboxPreparedAt(LocalSyncOutboxEntry left, LocalSyncOutboxEntry right)
        => NormalizeMutationUtc(left.PreparedAtUtc)
            .CompareTo(NormalizeMutationUtc(right.PreparedAtUtc));

    private static bool HasProvablySameOutboxSupersedeScope(
        LocalSyncOutboxEntry candidate,
        LocalSyncOutboxEntry acceptedCurrent)
        => TryBuildOutboxSupersedeScope(candidate, out var candidateScope) &&
           TryBuildOutboxSupersedeScope(acceptedCurrent, out var currentScope) &&
           candidateScope == currentScope;

    private static bool TryBuildOutboxSupersedeScope(
        LocalSyncOutboxEntry entry,
        out OutboxSupersedeScope scope)
    {
        scope = default;
        if (string.IsNullOrWhiteSpace(entry.TenantCode) ||
            string.IsNullOrWhiteSpace(entry.OfficeCode) ||
            string.IsNullOrWhiteSpace(entry.ResponsibleOfficeCode) ||
            string.IsNullOrWhiteSpace(entry.BusinessDatabaseName) ||
            string.IsNullOrWhiteSpace(entry.DeviceId) ||
            string.IsNullOrWhiteSpace(entry.MutationId) ||
            entry.SessionId == Guid.Empty ||
            entry.UserId == Guid.Empty)
        {
            return false;
        }

        scope = new OutboxSupersedeScope(
            NormalizeOutboxScopeText(entry.TenantCode),
            NormalizeOutboxScopeText(entry.OfficeCode),
            NormalizeOutboxScopeText(entry.ResponsibleOfficeCode),
            TenantScopeCatalog.GetDatabaseName(entry.BusinessDatabaseName).ToUpperInvariant(),
            NormalizeOutboxScopeText(entry.DeviceId),
            entry.SessionId,
            entry.UserId);
        return true;
    }

    private static bool TryBuildOutboxReconciliationOwnerScope(
        LocalSyncOutboxEntry entry,
        out OutboxReconciliationOwnerScope scope)
    {
        scope = default;
        if (string.IsNullOrWhiteSpace(entry.TenantCode) ||
            string.IsNullOrWhiteSpace(entry.OfficeCode) ||
            string.IsNullOrWhiteSpace(entry.ResponsibleOfficeCode) ||
            string.IsNullOrWhiteSpace(entry.BusinessDatabaseName) ||
            string.IsNullOrWhiteSpace(entry.DeviceId) ||
            string.IsNullOrWhiteSpace(entry.MutationId) ||
            entry.SessionId == Guid.Empty ||
            entry.UserId == Guid.Empty)
        {
            return false;
        }

        scope = new OutboxReconciliationOwnerScope(
            NormalizeOutboxScopeText(entry.TenantCode),
            NormalizeOutboxScopeText(entry.OfficeCode),
            NormalizeOutboxScopeText(entry.ResponsibleOfficeCode),
            TenantScopeCatalog.GetDatabaseName(entry.BusinessDatabaseName)
                .ToUpperInvariant(),
            NormalizeOutboxScopeText(entry.DeviceId),
            entry.UserId);
        return true;
    }

    private static string NormalizeOutboxScopeText(string value)
        => value.Trim().ToUpperInvariant();

    private static string NormalizeSyncEntityName(string? entityName)
    {
        var normalized = (entityName ?? string.Empty).Trim();
        if (normalized.StartsWith("Local", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[5..];

        return normalized switch
        {
            "Transaction" => "TransactionRecord",
            _ => normalized
        };
    }

    private async Task TryMarkOutboxFailedAsync(
        SyncPushRequest request,
        string? errorMessage,
        IReadOnlySet<SyncEntityKey>? excludedKeys,
        IReadOnlyDictionary<string, CurrentPushMutationReceipt>
            currentPushReceipts,
        CancellationToken ct)
    {
        try
        {
            var outgoingByMutationId = EnumerateOutgoingMutations(
                    request,
                    excludedKeys)
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Entity.MutationId))
                .GroupBy(
                    entry => entry.Entity.MutationId,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(
                    group => group.Key,
                    group => group.Single(),
                    StringComparer.OrdinalIgnoreCase);
            var rowIds = new List<Guid>();
            foreach (var (mutationId, outgoing) in outgoingByMutationId)
            {
                if (!currentPushReceipts.TryGetValue(
                        mutationId,
                        out var receipt) ||
                    !receipt.IsDurable ||
                    receipt.EntityKey != new SyncEntityKey(
                        NormalizeSyncEntityName(outgoing.EntityName),
                        outgoing.Entity.Id) ||
                    receipt.ExpectedRevision != outgoing.Entity.ExpectedRevision ||
                    !string.Equals(
                        receipt.PayloadHash,
                        ComputePreparedMutationPayloadHash(
                            outgoing.EntityName,
                            outgoing.Entity),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var affected = await _db.SyncOutboxEntries
                    .Where(entry =>
                        entry.Id == receipt.OutboxRowId &&
                        entry.Status != "Acknowledged" &&
                        entry.MutationId == receipt.MutationId &&
                        entry.EntityName == outgoing.EntityName &&
                        entry.EntityId == outgoing.Entity.Id &&
                        entry.ExpectedRevision == receipt.ExpectedRevision &&
                        entry.DeviceId == receipt.DeviceId &&
                        entry.BusinessDatabaseName == receipt.BusinessDatabaseName &&
                        entry.TenantCode == receipt.TenantCode &&
                        entry.OfficeCode == receipt.OfficeCode &&
                        entry.ResponsibleOfficeCode == receipt.ResponsibleOfficeCode &&
                        entry.SessionId == receipt.SessionId &&
                        entry.UserId == receipt.UserId &&
                        entry.PreparedAtUtc == receipt.PreparedAtUtc)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(entry => entry.Status, "Failed")
                            .SetProperty(
                                entry => entry.ErrorMessage,
                                errorMessage ?? string.Empty),
                        ct);
                if (affected == 1)
                    rowIds.Add(receipt.OutboxRowId);
            }

            DetachTrackedOutboxEntries(rowIds);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"outbox 실패 기록 저장 중 추가 오류가 발생했습니다. {ex.Message}");
        }
    }

    private static string BuildMutationId(string deviceId, string entityName, SyncEntityDto entity)
    {
        var updatedAtTicks = NormalizeMutationUtc(entity.UpdatedAtUtc).Ticks;
        return $"{deviceId}:{entityName}:{entity.Id:N}:{entity.ExpectedRevision}:{updatedAtTicks}:{(entity.IsDeleted ? 1 : 0)}";
    }

    private static DateTime NormalizeMutationUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    private async Task AppendConflictSummaryAsync(string summary)
    {
        var normalizedSummary = (summary ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedSummary))
            return;

        try
        {
            var current = await _local.GetSettingAsync(LastConflictSummarySettingKey, CancellationToken.None);
            var merged = string.IsNullOrWhiteSpace(current)
                ? normalizedSummary
                : current.Contains(normalizedSummary, StringComparison.Ordinal)
                    ? current
                    : current + Environment.NewLine + normalizedSummary;

            await _local.SetSyncMetadataSettingIndependentAsync(
                LastConflictSummarySettingKey,
                merged,
                CancellationToken.None);
            SetStatus(normalizedSummary);
            await TryRecordDiagnosticAsync(
                phase: "push-conflict",
                rawMessage: normalizedSummary,
                severity: "Warning",
                recoveryAttempted: true,
                recoverySucceeded: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"충돌 요약 저장 실패 무시: {ex.Message}");
        }
    }

    private static string BuildSyncNoticeSummary(IReadOnlyCollection<SyncNoticeDto> notices)
    {
        if (notices.Count == 0)
            return string.Empty;

        var messages = notices
            .Select(notice => (notice.Message ?? string.Empty).Trim())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        if (messages.Count == 0)
            return string.Empty;

        var summary = notices.Count == 1
            ? $"동기화 보정 1건: {messages[0]}"
            : $"동기화 보정 {notices.Count:N0}건: {string.Join(" / ", messages)}";

        var remaining = notices.Count - messages.Count;
        return remaining > 0
            ? $"{summary} / 외 {remaining:N0}건"
            : summary;
    }

    private async Task UpsertPulledInventoryTransferAsync(
        InventoryTransferDto dto,
        CancellationToken ct,
        bool allowRetry = true)
    {
        var ownsDatabaseTransaction = _db.Database.CurrentTransaction is null;
        try
        {
            await using var transaction = ownsDatabaseTransaction
                ? await _db.BeginRuntimeMutationTransactionAsync(ct)
                : null;
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.InventoryTransfers.IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .AsNoTracking()
                .FirstOrDefaultAsync(transfer => transfer.Id == local.Id, ct);

            if (existing is not null)
            {
                var incomingIsNewer = local.Revision > existing.Revision ||
                                      (local.Revision == existing.Revision && local.UpdatedAtUtc >= existing.UpdatedAtUtc);
                if (existing.IsDirty)
                {
                    if (!existing.IsDeleted &&
                        local.IsDeleted &&
                        incomingIsNewer)
                    {
                        await PersistInventoryTransferTombstoneConflictAsync(
                            existing,
                            dto,
                            ct);
                    }
                    else
                    {
                        return;
                    }
                }
                else if (!incomingIsNewer)
                {
                    return;
                }
            }

            if (existing is null || !existing.IsDirty)
            {
                await _local
                    .RecordInventoryTransferTombstoneConflictServerStateAsync(
                        local.Id,
                        TenantScopeCatalog.GetDatabaseName(
                            _session.SelectedBusinessDatabaseName),
                        local.Revision,
                        NormalizeMutationUtc(local.UpdatedAtUtc),
                        local.IsDeleted,
                        JsonSerializer.Serialize(dto),
                        ct);
            }

            _db.ChangeTracker.Clear();

            await _db.InventoryTransferLines
                .Where(line => line.TransferId == local.Id)
                .ExecuteDeleteAsync(ct);
            await _db.InventoryTransfers.IgnoreQueryFilters()
                .Where(transfer => transfer.Id == local.Id)
                .ExecuteDeleteAsync(ct);

            _db.InventoryTransfers.Add(local);
            await _db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            _local.RecordInventoryStateChanged();
        }
        catch (DbUpdateConcurrencyException) when (allowRetry && ownsDatabaseTransaction)
        {
            _db.ChangeTracker.Clear();
            await UpsertPulledInventoryTransferAsync(dto, ct, allowRetry: false);
        }
    }

    private async Task PersistInventoryTransferTombstoneConflictAsync(
        LocalInventoryTransfer existing,
        InventoryTransferDto serverTombstone,
        CancellationToken ct,
        string? businessDatabaseNameOverride = null,
        SessionState? ownerSessionOverride = null,
        IReadOnlySet<Guid>? outboxRowsReservedForAcknowledgement = null)
    {
        var now = DateTime.UtcNow;
        var localSnapshot = LocalMappings.ToDto(existing);
        var ownerSession = ownerSessionOverride ?? _session;
        var businessDatabaseName =
            TenantScopeCatalog.GetDatabaseName(
                string.IsNullOrWhiteSpace(businessDatabaseNameOverride)
                    ? ownerSession.SelectedBusinessDatabaseName
                    : businessDatabaseNameOverride);
        var tenantCode =
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                localSnapshot.TenantCode,
                localSnapshot.SourceOfficeCode);
        var sourceOfficeCode =
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                localSnapshot.SourceOfficeCode,
                serverTombstone.SourceOfficeCode);
        var targetOfficeCode =
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                localSnapshot.TargetOfficeCode,
                serverTombstone.TargetOfficeCode);
        var allowLegacyDefaultDatabaseName =
            string.Equals(
                businessDatabaseName,
                TenantScopeCatalog.GetDatabaseName(
                    ownerSession.AuthenticatedTenantCode),
                StringComparison.OrdinalIgnoreCase);
        var outboxEntityNames = new[]
        {
            nameof(LocalInventoryTransfer),
            "InventoryTransfer"
        };
        var pendingOutboxRows = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.EntityId == existing.Id &&
                entry.Status != "Acknowledged" &&
                outboxEntityNames.Contains(entry.EntityName) &&
                (entry.BusinessDatabaseName == businessDatabaseName ||
                 (allowLegacyDefaultDatabaseName &&
                  entry.BusinessDatabaseName == string.Empty)) &&
                (entry.TenantCode == tenantCode ||
                 entry.TenantCode == string.Empty) &&
                (entry.OfficeCode == sourceOfficeCode ||
                 entry.OfficeCode == string.Empty) &&
                (entry.ResponsibleOfficeCode == targetOfficeCode ||
                 entry.ResponsibleOfficeCode == string.Empty))
            .OrderBy(entry => entry.PreparedAtUtc)
            .ToListAsync(ct);
        var outboxMutationIds = pendingOutboxRows
            .Select(entry => entry.MutationId)
            .Where(mutationId => !string.IsNullOrWhiteSpace(mutationId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var conflict = await _db.InventoryTransferTombstoneConflicts
            .FirstOrDefaultAsync(
                current =>
                    current.TransferId == existing.Id &&
                    current.BusinessDatabaseName ==
                    businessDatabaseName,
                ct);
        if (conflict is null)
        {
            conflict = new LocalInventoryTransferTombstoneConflict
            {
                TransferId = existing.Id,
                BusinessDatabaseName = businessDatabaseName
            };
            _db.InventoryTransferTombstoneConflicts.Add(conflict);
        }

        var archivedEvidencePath = string.Empty;
        if (!string.IsNullOrWhiteSpace(existing.ReceiveEvidencePath))
        {
            try
            {
                archivedEvidencePath =
                    Path.GetFullPath(existing.ReceiveEvidencePath);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or
                PathTooLongException)
            {
                throw new SyncPullBlockedException(
                    "The inventory transfer conflict evidence path is invalid.");
            }

            if (!AppPaths.IsTransactionAttachmentPath(
                    archivedEvidencePath) ||
                !File.Exists(archivedEvidencePath))
            {
                throw new SyncPullBlockedException(
                    "The inventory transfer conflict evidence file is missing or outside the managed attachment root.");
            }
        }

        if (!string.IsNullOrWhiteSpace(
                conflict.ArchivedReceiveEvidencePath))
        {
            var existingArchivePath = Path.GetFullPath(
                conflict.ArchivedReceiveEvidencePath);
            if (!AppPaths.IsTransactionAttachmentPath(
                    existingArchivePath) ||
                !File.Exists(existingArchivePath) ||
                !string.IsNullOrWhiteSpace(archivedEvidencePath) &&
                !string.Equals(
                    existingArchivePath,
                    archivedEvidencePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncPullBlockedException(
                    "The inventory transfer conflict already owns a different or invalid evidence file.");
            }

            archivedEvidencePath = existingArchivePath;
        }

        conflict.ArchivedReceiveEvidencePath = archivedEvidencePath;
        localSnapshot.ReceiveEvidencePath = archivedEvidencePath;
        conflict.BusinessDatabaseName = businessDatabaseName;
        conflict.TenantCode = tenantCode;
        conflict.SourceOfficeCode = sourceOfficeCode;
        conflict.TargetOfficeCode = targetOfficeCode;
        conflict.LocalSnapshotJson =
            JsonSerializer.Serialize(localSnapshot);
        conflict.ServerTombstoneJson =
            JsonSerializer.Serialize(serverTombstone);
        conflict.OutboxMutationIdsJson =
            JsonSerializer.Serialize(outboxMutationIds);
        conflict.LocalRevision = existing.Revision;
        conflict.ServerRevision = serverTombstone.Revision;
        conflict.ServerUpdatedAtUtc =
            NormalizeMutationUtc(serverTombstone.UpdatedAtUtc);
        conflict.Status =
            InventoryTransferTombstoneConflictPolicy.UnresolvedStatus;
        conflict.DetectedAtUtc = now;
        conflict.UpdatedAtUtc = now;
        conflict.ResolvedAtUtc = null;
        conflict.Resolution = string.Empty;
        conflict.RecoveredTransferId = null;

        if (pendingOutboxRows.Count > 0)
        {
            var pendingOutboxIds = pendingOutboxRows
                .Select(entry => entry.Id)
                .Where(id =>
                    outboxRowsReservedForAcknowledgement?.Contains(id) != true)
                .ToList();
            var outboxError =
                $"{InventoryTransferTombstoneConflictPolicy.OutboxErrorPrefix} " +
                "서버에서 삭제된 재고이동 문서의 로컬 초안을 별도 보관했습니다. 재고이동 화면에서 초안을 복구하거나 폐기하세요.";
            if (pendingOutboxIds.Count > 0)
            {
                await _db.SyncOutboxEntries
                    .Where(entry => pendingOutboxIds.Contains(entry.Id))
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(entry => entry.Status, "Failed")
                            .SetProperty(entry => entry.ErrorMessage, outboxError)
                            .SetProperty(
                                entry => entry.AcknowledgedAtUtc,
                                (DateTime?)null)
                            .SetProperty(entry => entry.AcceptedRevision, 0L)
                            .SetProperty(
                                entry => entry.AcceptedUpdatedAtUtc,
                                (DateTime?)null),
                        ct);
            }
        }

        await _db.SaveChangesAsync(ct);
        AppLogger.Warn(
            "SYNC",
            $"재고이동 원격삭제 충돌 초안을 보존하고 서버 tombstone을 적용합니다: transfer={existing.Id:D}, localRevision={existing.Revision}, serverRevision={serverTombstone.Revision}");
    }

    private static byte[] ReadTransactionAttachmentContent(LocalTransactionAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath))
            return [];

        try
        {
            return File.ReadAllBytes(attachment.StoredPath);
        }
        catch
        {
            return [];
        }
    }

    private void CancelPendingImmediateSync()
    {
        lock (_immediateSyncGate)
        {
            _immediateSyncCts?.Cancel();
            _immediateSyncCts?.Dispose();
            _immediateSyncCts = null;
            _resyncRequested = false;
        }
    }

    private static string ResolvePulledTransactionAttachmentPath(
        TransactionAttachmentDto dto,
        ReadOnlySpan<byte> verifiedContent)
    {
        if (dto.IsDeleted)
            return string.Empty;

        var attachmentDir = Path.Combine(AppPaths.TransactionAttachmentsDir, dto.TransactionId.ToString("N"));
        var originalFileName = SanitizeAttachmentFileName(dto.FileName, dto.Id);
        var contentHash = Convert.ToHexString(SHA256.HashData(verifiedContent));
        var fileName = $"{dto.Id:N}_{dto.Revision}_{contentHash[..16]}_{originalFileName}";
        var storedPath = Path.Combine(attachmentDir, fileName);
        return storedPath;
    }

    private static byte[] ValidatePulledTransactionAttachmentContent(
        TransactionAttachmentDto dto)
    {
        var content = dto.FileContent ?? [];
        if (content.Length == 0)
        {
            throw new InvalidDataException(
                $"서버 첨부파일 내용이 비어 있어 기존 파일을 보존했습니다. attachmentId={dto.Id:D}");
        }

        if (dto.FileSize > 0 && content.LongLength != dto.FileSize)
        {
            throw new InvalidDataException(
                $"서버 첨부파일 크기가 메타데이터와 일치하지 않습니다. attachmentId={dto.Id:D}");
        }

        var expectedHash = dto.FileHash?.Trim() ?? string.Empty;
        if (expectedHash.Length > 0)
        {
            if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    $"서버 첨부파일 해시 형식이 올바르지 않습니다. attachmentId={dto.Id:D}");
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(content));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"서버 첨부파일 해시가 메타데이터와 일치하지 않습니다. attachmentId={dto.Id:D}");
            }
        }

        return content;
    }

    private static void TryStageTransactionAttachmentDelete(
        AttachmentFileJournal fileJournal,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            fileJournal.StageDelete(path);
        }
        catch (AttachmentFileJournalContentionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "ATTACHMENT",
                $"허용된 첨부파일 저장 경로 밖의 기존 파일은 삭제하지 않습니다. {ex.Message}");
        }
    }

    private static void TryDeleteTransactionAttachmentFile(string? path, bool deleteEmptyDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (File.Exists(path))
                File.Delete(path);

            if (!deleteEmptyDirectory)
                return;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch
        {
            // 파일 정리 실패는 DB 동기화 결과를 되돌리지 않는다.
        }
    }

    private static string SanitizeAttachmentFileName(string? fileName, Guid attachmentId)
    {
        var safeName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = $"{attachmentId:N}.bin";

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(invalidChar, '_');

        return safeName;
    }

    private static string NormalizeOptionName(string? value)
        => (value ?? string.Empty).Trim();

    private static string NormalizeRentalManagementCompanyCode(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private async Task ThrowIfPendingCanonicalDeleteOutboxAsync(
        IEnumerable<Guid> deleteTargetIds,
        IReadOnlyCollection<string> allowedEntityNames,
        string operationLabel,
        CancellationToken ct)
    {
        var targetIds = deleteTargetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (targetIds.Count == 0 || allowedEntityNames.Count == 0)
            return;

        if (await _db.SyncOutboxEntries
                .AsNoTracking()
                .AnyAsync(entry =>
                    entry.Status != "Acknowledged" &&
                    targetIds.Contains(entry.EntityId) &&
                    allowedEntityNames.Contains(entry.EntityName),
                    ct))
        {
            throw new SyncPullBlockedException(
                $"{operationLabel} delete가 accepted 증거 없는 pending outbox payload를 잃게 할 수 있어 pull을 중단했습니다.");
        }
    }

    private async Task ThrowIfDirtyRentalBillingProfileDependenciesAsync(
        IReadOnlyCollection<Guid> staleProfileIds,
        CancellationToken ct)
    {
        if (staleProfileIds.Count == 0)
            return;

        if (await _db.Transactions.IgnoreQueryFilters()
                .AnyAsync(transaction =>
                    transaction.LinkedRentalBillingProfileId.HasValue &&
                    staleProfileIds.Contains(transaction.LinkedRentalBillingProfileId.Value),
                    ct) ||
            await _db.Invoices.IgnoreQueryFilters()
                .AnyAsync(invoice =>
                    invoice.LinkedRentalBillingProfileId.HasValue &&
                    staleProfileIds.Contains(invoice.LinkedRentalBillingProfileId.Value),
                    ct) ||
            await _db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(asset =>
                    (asset.BillingProfileId.HasValue &&
                     staleProfileIds.Contains(asset.BillingProfileId.Value) ||
                     asset.LastBillingProfileId.HasValue &&
                     staleProfileIds.Contains(asset.LastBillingProfileId.Value)),
                    ct) ||
            await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .AnyAsync(history =>
                    history.BillingProfileId.HasValue &&
                    staleProfileIds.Contains(history.BillingProfileId.Value),
                    ct) ||
            await _db.RentalBillingLogs.IgnoreQueryFilters()
                .AnyAsync(log =>
                    staleProfileIds.Contains(log.BillingProfileId),
                    ct))
        {
            throw new SyncPullBlockedException(
                "렌탈 청구 프로필 canonical delete가 참조 payload를 orphan 처리할 수 있어 pull을 중단했습니다.");
        }
    }

    private static string NormalizeRentalAssetNaturalKey(string? value)
        => (value ?? string.Empty).Trim();

    private async Task UpsertPulledInvoicesAsync(IReadOnlyList<InvoiceDto> dtos, CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var deletedInvoiceSideEffects = new List<(Guid InvoiceId, DateTime UpdatedAtUtc, long Revision)>();
        var rentalSettlementTargets = new List<(Guid ProfileId, Guid? RunId)>();
        var touchedVersionGroupIds = new HashSet<Guid>();

        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;
            var versionGroupId = local.VersionGroupId == Guid.Empty ? local.Id : local.VersionGroupId;
            if (versionGroupId != Guid.Empty)
                touchedVersionGroupIds.Add(versionGroupId);

            var existing = await _db.Invoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == local.Id, ct);

            if (existing is null)
            {
                _db.Invoices.Add(local);
                AddPulledInvoiceRentalTarget(local, rentalSettlementTargets);
                if (local.IsDeleted)
                    deletedInvoiceSideEffects.Add((local.Id, local.UpdatedAtUtc, local.Revision));
            }
            else if (!existing.IsDirty)
            {
                AddPulledInvoiceRentalTarget(existing, rentalSettlementTargets);
                _db.Entry(existing).CurrentValues.SetValues(local);

                foreach (var line in local.Lines)
                {
                    var exLine = existing.Lines.FirstOrDefault(l => l.Id == line.Id);
                    if (exLine is null)
                        existing.Lines.Add(line);
                    else
                        _db.Entry(exLine).CurrentValues.SetValues(line);
                }

                foreach (var exLine in existing.Lines.Where(l => !local.Lines.Any(ll => ll.Id == l.Id)))
                    exLine.IsDeleted = true;

                foreach (var pay in local.Payments)
                {
                    var exPay = existing.Payments.FirstOrDefault(p => p.Id == pay.Id);
                    if (exPay is null)
                        existing.Payments.Add(pay);
                    else if (!exPay.IsDirty)
                        _db.Entry(exPay).CurrentValues.SetValues(pay);
                }

                AddPulledInvoiceRentalTarget(local, rentalSettlementTargets);
                if (local.IsDeleted)
                    deletedInvoiceSideEffects.Add((local.Id, local.UpdatedAtUtc, local.Revision));
            }
        }
        await _db.SaveChangesAsync(ct);
        await _local.NormalizeLatestInvoiceVersionGroupsAsync(touchedVersionGroupIds, ct: ct);

        await _local.ApplyPulledInvoiceDeleteSideEffectsAsync(deletedInvoiceSideEffects, ct);
        await _local.RecalculateRentalSettlementsAsync(
            rentalSettlementTargets,
            ct,
            markDirty: false,
            preserveDirtyProfiles: true);
    }

    private static void AddPulledInvoiceRentalTarget(
        LocalInvoice invoice,
        ICollection<(Guid ProfileId, Guid? RunId)> targets)
    {
        if (invoice.LinkedRentalBillingProfileId is Guid profileId && profileId != Guid.Empty)
            targets.Add((profileId, invoice.LinkedRentalBillingRunId));
    }

    private async Task RetryDeferredPurgeRecordsAsync(
        CancellationToken ct)
    {
        var operationOwner = CaptureSyncOperationOwnerBoundary();
        var ownerScope = BuildDeferredPurgeOwnerScope(operationOwner);
        if (!await HasDeferredPurgeRecordsAsync(ownerScope, ct))
            return;

        await RecoverIncompleteAttachmentFileJournalsAsync(ct);
        await using var transaction =
            await _db.BeginRuntimeMutationTransactionAsync(ct);
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);
        var commitAttempted = false;
        using var inventoryStateChangeCapture =
            _local.CaptureInventoryStateChanges();
        var invoicePurgeApplied = false;
        var committed = false;

        try
        {
            invoicePurgeApplied =
                await ApplyDeferredPurgeRecordsCoreAsync(
                    ownerScope,
                    ct,
                    attachmentFiles);
            committed =
                await CommitAttachmentTransactionUnderOwnerLeaseAsync(
                    transaction,
                    attachmentFiles,
                    operationOwner,
                    () => commitAttempted = true,
                    ct);
            if (!committed)
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
                attachmentFiles.Rollback();
                _db.ChangeTracker.Clear();
                return;
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await attachmentFiles.CompleteAfterDatabaseCommitAsync(
                _db,
                CancellationToken.None);
        }
        catch
        {
            var commitResolution =
                AttachmentCommitResolution.RolledBack;
            try
            {
                await transaction.RollbackAsync(
                    CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                AppLogger.Error(
                    "ATTACHMENT",
                    "지연된 서버 영구삭제 재시도 실패 후 DB 롤백 결과를 확정하지 못했습니다.",
                    rollbackException);
            }

            if (!commitAttempted)
            {
                attachmentFiles.Rollback();
            }
            else
            {
                commitResolution =
                    await attachmentFiles
                        .ResolveCommitAmbiguityAsync(
                            _db,
                            CancellationToken.None);
            }

            _db.ChangeTracker.Clear();
            if (commitResolution !=
                AttachmentCommitResolution.Committed)
            {
                throw;
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            committed = true;
        }

        if (!committed)
            return;

        inventoryStateChangeCapture.Dispose();
        PublishCommittedDeferredPurgeEvents(
            operationOwner,
            invoicePurgeApplied,
            inventoryStateChangeCapture.HasChanges);
    }

    private void PublishCommittedDeferredPurgeEvents(
        SyncOperationOwnerBoundary operationOwner,
        bool invoicePurgeApplied,
        bool hasInventoryChanges)
    {
        if (invoicePurgeApplied &&
            IsSyncOperationOwnerCurrent(operationOwner))
        {
            try
            {
                _local.TryPublishItemInvoiceHistoryChanged();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "SYNC",
                    "커밋된 지연 전표 영구삭제의 품목별 전표이력 변경 알림 중 오류가 발생했습니다.",
                    ex);
            }
        }

        if (!hasInventoryChanges ||
            !IsSyncOperationOwnerCurrent(operationOwner))
        {
            return;
        }

        try
        {
            _local.TryPublishInventoryStateChanged();
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "SYNC",
                "커밋된 지연 서버 영구삭제의 재고 변경 알림 중 오류가 발생했습니다.",
                ex);
        }
    }

    private async Task<bool> ApplyPulledPurgeRecordsAsync(
        IReadOnlyList<RecycleBinPurgeRecordDto> dtos,
        SyncOperationOwnerBoundary operationOwner,
        CancellationToken ct,
        AttachmentFileJournal? attachmentFileJournal)
    {
        var ownerScope = BuildDeferredPurgeOwnerScope(
            operationOwner);
        await UpsertDeferredPurgeRecordsAsync(
            dtos,
            ownerScope,
            ct);
        return await ApplyDeferredPurgeRecordsCoreAsync(
            ownerScope,
            ct,
            attachmentFileJournal);
    }

    private async Task UpsertDeferredPurgeRecordsAsync(
        IReadOnlyList<RecycleBinPurgeRecordDto> dtos,
        DeferredPurgeOwnerScope ownerScope,
        CancellationToken ct)
    {
        var validReceipts = dtos
            .Where(current =>
                current.Id != Guid.Empty &&
                current.EntityId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(current.Kind))
            .Select(current => new
            {
                Dto = current,
                Kind = NormalizePurgeRecordKind(current.Kind)
            })
            .GroupBy(current => current.Dto.Id)
            .Select(group => group
                .OrderByDescending(current =>
                    current.Dto.PurgedAtUtc)
                .ThenByDescending(current =>
                    current.Dto.Revision)
                .First())
            .ToList();
        if (validReceipts.Count == 0)
            return;

        var receiptIds = validReceipts
            .Select(current => current.Dto.Id)
            .ToList();
        var existingById = await _db
            .DeferredRecycleBinPurgeRecords
            .Where(current => receiptIds.Contains(current.Id))
            .ToDictionaryAsync(current => current.Id, ct);
        var now = DateTime.UtcNow;
        foreach (var receipt in validReceipts)
        {
            if (existingById.TryGetValue(
                    receipt.Dto.Id,
                    out var existing))
            {
                if (!DeferredPurgeRecordBelongsToOwner(
                        existing,
                        ownerScope))
                {
                    AppLogger.Warn(
                        "SYNC",
                        $"서버 영구삭제 영수증 범위 충돌로 기존 지연 항목을 보존합니다: {receipt.Dto.Id:D}");
                    continue;
                }

                if (receipt.Dto.Revision <
                        existing.Revision &&
                    receipt.Dto.PurgedAtUtc <=
                        existing.PurgedAtUtc)
                {
                    continue;
                }

                existing.Kind = receipt.Kind;
                existing.EntityId = receipt.Dto.EntityId;
                existing.Revision = Math.Max(
                    existing.Revision,
                    receipt.Dto.Revision);
                existing.PurgedAtUtc =
                    NormalizeMutationUtc(
                        receipt.Dto.PurgedAtUtc);
                existing.UpdatedAtUtc = now;
                existing.AppliedAtUtc = null;
                existing.NextAttemptAtUtc = null;
                continue;
            }

            _db.DeferredRecycleBinPurgeRecords.Add(
                new LocalDeferredRecycleBinPurgeRecord
                {
                    Id = receipt.Dto.Id,
                    BusinessDatabaseName =
                        ownerScope.BusinessDatabaseName,
                    TenantCode = ownerScope.TenantCode,
                    OfficeCode = ownerScope.OfficeCode,
                    ResponsibleOfficeCode =
                        ownerScope.ResponsibleOfficeCode,
                    Kind = receipt.Kind,
                    EntityId = receipt.Dto.EntityId,
                    Revision = receipt.Dto.Revision,
                    PurgedAtUtc =
                        NormalizeMutationUtc(
                            receipt.Dto.PurgedAtUtc),
                    AttemptCount = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<bool>
        ApplyDeferredPurgeRecordsCoreAsync(
            DeferredPurgeOwnerScope ownerScope,
            CancellationToken ct,
            AttachmentFileJournal? attachmentFileJournal)
    {
        var deferredRecords = await _db
            .DeferredRecycleBinPurgeRecords
            .Where(current =>
                current.BusinessDatabaseName ==
                    ownerScope.BusinessDatabaseName &&
                current.TenantCode == ownerScope.TenantCode &&
                current.OfficeCode == ownerScope.OfficeCode &&
                current.ResponsibleOfficeCode ==
                    ownerScope.ResponsibleOfficeCode &&
                current.AppliedAtUtc == null)
            .ToListAsync(ct);
        if (deferredRecords.Count == 0)
            return false;

        var latestRecords = deferredRecords
            .GroupBy(current =>
                (current.Kind, current.EntityId))
            .Select(group => group
                .OrderByDescending(current =>
                    current.PurgedAtUtc)
                .ThenByDescending(current =>
                    current.Revision)
                .ThenByDescending(current =>
                    current.UpdatedAtUtc)
                .First())
            .OrderBy(current =>
                GetPurgeApplyOrder(current.Kind))
            .ToList();
        var latestRecordIds = latestRecords
            .Select(current => current.Id)
            .ToHashSet();
        _db.DeferredRecycleBinPurgeRecords.RemoveRange(
            deferredRecords.Where(current =>
                !latestRecordIds.Contains(current.Id)));

        var invoicePurgeApplied = false;
        foreach (var record in latestRecords)
        {
            ct.ThrowIfCancellationRequested();
            var attemptedAtUtc = DateTime.UtcNow;
            record.AttemptCount++;
            record.LastAttemptedAtUtc = attemptedAtUtc;
            record.UpdatedAtUtc = attemptedAtUtc;
            record.NextAttemptAtUtc = null;
            record.LastErrorMessage = string.Empty;

            if (!TryParseRecycleBinEntityKind(
                    record.Kind,
                    out var entityKind))
            {
                record.LastErrorMessage =
                    "현재 클라이언트가 지원하지 않는 서버 영구삭제 항목 종류입니다.";
                AppLogger.Warn(
                    "SYNC",
                    $"서버 영구삭제 영수증 반영 보류: {record.Kind} / {record.EntityId:D} / {record.LastErrorMessage}");
                continue;
            }

            if (await IsPurgeReceiptSupersededByNewerLocalEntityAsync(
                    entityKind,
                    record.EntityId,
                    record.Revision,
                    ct))
            {
                AppLogger.Warn(
                    "SYNC",
                    $"서버 영구삭제 영수증 폐기: 더 최신인 로컬 엔터티가 확인되었습니다. {record.Kind} / {record.EntityId:D} / receipt={record.Revision}");
                _db.DeferredRecycleBinPurgeRecords.Remove(record);
                continue;
            }

            if (await IsPurgeTargetDirtyAsync(
                    entityKind,
                    record.EntityId,
                    ct) ||
                await HasPendingPurgeTargetOutboxAsync(
                    entityKind,
                    record.EntityId,
                    ownerScope,
                    ct))
            {
                record.LastErrorMessage =
                    "서버로 전송되지 않은 로컬 변경 또는 처리 중인 동기화 작업이 있어 서버 영구삭제 영수증 반영을 보류했습니다.";
                AppLogger.Warn(
                    "SYNC",
                    $"서버 영구삭제 영수증 반영 보류: {record.Kind} / {record.EntityId:D} / {record.LastErrorMessage}");
                continue;
            }

            var result = attachmentFileJournal is null
                ? await _local
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        entityKind,
                        record.EntityId,
                        record.Revision,
                        ownerScope.BusinessDatabaseName,
                        ct)
                : await _local
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        entityKind,
                        record.EntityId,
                        record.Revision,
                        ownerScope.BusinessDatabaseName,
                        attachmentFileJournal,
                        ct);
            if (result.Success || result.NotFound)
            {
                if (IsInvoicePurgeRecordKind(record.Kind))
                    invoicePurgeApplied = true;
                _db.DeferredRecycleBinPurgeRecords.Remove(
                    record);
                continue;
            }

            record.LastErrorMessage =
                string.IsNullOrWhiteSpace(result.Message)
                    ? "로컬 영구삭제 반영이 보류되었습니다."
                    : result.Message;
            AppLogger.Warn(
                "SYNC",
                $"서버 영구삭제 영수증 반영 보류: {record.Kind} / {record.EntityId:D} / {record.LastErrorMessage}");
        }

        await _db.SaveChangesAsync(ct);
        return invoicePurgeApplied;
    }

    private Task<bool> HasDeferredPurgeRecordsAsync(
        DeferredPurgeOwnerScope ownerScope,
        CancellationToken ct)
        => _db.DeferredRecycleBinPurgeRecords
            .AsNoTracking()
            .AnyAsync(current =>
                current.BusinessDatabaseName ==
                    ownerScope.BusinessDatabaseName &&
                current.TenantCode == ownerScope.TenantCode &&
                current.OfficeCode == ownerScope.OfficeCode &&
                current.ResponsibleOfficeCode ==
                    ownerScope.ResponsibleOfficeCode &&
                current.AppliedAtUtc == null,
                ct);

    private static DeferredPurgeOwnerScope
        BuildDeferredPurgeOwnerScope(
            SyncOperationOwnerBoundary owner)
        => new(
            TenantScopeCatalog
                .GetDatabaseName(owner.BusinessDatabaseName)
                .ToUpperInvariant(),
            TenantScopeCatalog
                .NormalizeTenantCodeOrDefault(owner.TenantCode)
                .ToUpperInvariant(),
            OfficeCodeCatalog
                .NormalizeOfficeCodeOrDefault(owner.OfficeCode)
                .ToUpperInvariant(),
            OfficeCodeCatalog
                .NormalizeOfficeCodeOrDefault(
                    owner.BusinessOfficeCode,
                    owner.OfficeCode)
                .ToUpperInvariant());

    private static bool DeferredPurgeRecordBelongsToOwner(
        LocalDeferredRecycleBinPurgeRecord record,
        DeferredPurgeOwnerScope ownerScope)
        => string.Equals(
               record.BusinessDatabaseName,
               ownerScope.BusinessDatabaseName,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               record.TenantCode,
               ownerScope.TenantCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               record.OfficeCode,
               ownerScope.OfficeCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               record.ResponsibleOfficeCode,
               ownerScope.ResponsibleOfficeCode,
               StringComparison.OrdinalIgnoreCase);

    private Task<bool> IsPurgeReceiptSupersededByNewerLocalEntityAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        long purgeRevision,
        CancellationToken ct)
        => kind switch
        {
            RecycleBinEntityKind.Customer => HasLocalEntityNewerThanPurgeAsync(_db.Customers, entityId, purgeRevision, ct),
            RecycleBinEntityKind.CustomerContract => HasLocalEntityNewerThanPurgeAsync(_db.CustomerContracts, entityId, purgeRevision, ct),
            RecycleBinEntityKind.Item => HasLocalEntityNewerThanPurgeAsync(_db.Items, entityId, purgeRevision, ct),
            RecycleBinEntityKind.CompanyProfile => HasLocalEntityNewerThanPurgeAsync(_db.CompanyProfiles, entityId, purgeRevision, ct),
            RecycleBinEntityKind.CustomerCategory => HasLocalEntityNewerThanPurgeAsync(_db.CustomerCategories, entityId, purgeRevision, ct),
            RecycleBinEntityKind.PriceGradeOption => HasLocalEntityNewerThanPurgeAsync(_db.PriceGradeOptions, entityId, purgeRevision, ct),
            RecycleBinEntityKind.TradeTypeOption => HasLocalEntityNewerThanPurgeAsync(_db.TradeTypeOptions, entityId, purgeRevision, ct),
            RecycleBinEntityKind.ItemCategoryOption => HasLocalEntityNewerThanPurgeAsync(_db.ItemCategoryOptions, entityId, purgeRevision, ct),
            RecycleBinEntityKind.Invoice => HasLocalEntityNewerThanPurgeAsync(_db.Invoices, entityId, purgeRevision, ct),
            RecycleBinEntityKind.Payment => HasLocalEntityNewerThanPurgeAsync(_db.Payments, entityId, purgeRevision, ct),
            RecycleBinEntityKind.Transaction => HasLocalEntityNewerThanPurgeAsync(_db.Transactions, entityId, purgeRevision, ct),
            RecycleBinEntityKind.InventoryTransfer => HasLocalEntityNewerThanPurgeAsync(_db.InventoryTransfers, entityId, purgeRevision, ct),
            RecycleBinEntityKind.RentalManagementCompany => HasLocalEntityNewerThanPurgeAsync(_db.RentalManagementCompanies, entityId, purgeRevision, ct),
            RecycleBinEntityKind.RentalBillingProfile => HasLocalEntityNewerThanPurgeAsync(_db.RentalBillingProfiles, entityId, purgeRevision, ct),
            RecycleBinEntityKind.RentalAsset => HasLocalEntityNewerThanPurgeAsync(_db.RentalAssets, entityId, purgeRevision, ct),
            RecycleBinEntityKind.RentalBillingLog => HasLocalEntityNewerThanPurgeAsync(_db.RentalBillingLogs, entityId, purgeRevision, ct),
            _ => Task.FromResult(false)
        };

    private static Task<bool> HasLocalEntityNewerThanPurgeAsync<TEntity>(
        DbSet<TEntity> set,
        Guid entityId,
        long purgeRevision,
        CancellationToken ct)
        where TEntity : class, ILocalSyncEntity
        => set.IgnoreQueryFilters().AnyAsync(entity =>
            entity.Id == entityId &&
            entity.Revision > purgeRevision,
            ct);

    private Task<bool> IsPurgeTargetDirtyAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        CancellationToken ct)
        => kind switch
        {
            RecycleBinEntityKind.Customer => HasDirtyLocalEntityAsync(_db.Customers, entityId, ct),
            RecycleBinEntityKind.CustomerContract => HasDirtyLocalEntityAsync(_db.CustomerContracts, entityId, ct),
            RecycleBinEntityKind.Item => HasDirtyLocalEntityAsync(_db.Items, entityId, ct),
            RecycleBinEntityKind.CompanyProfile => HasDirtyLocalEntityAsync(_db.CompanyProfiles, entityId, ct),
            RecycleBinEntityKind.CustomerCategory => HasDirtyLocalEntityAsync(_db.CustomerCategories, entityId, ct),
            RecycleBinEntityKind.PriceGradeOption => HasDirtyLocalEntityAsync(_db.PriceGradeOptions, entityId, ct),
            RecycleBinEntityKind.TradeTypeOption => HasDirtyLocalEntityAsync(_db.TradeTypeOptions, entityId, ct),
            RecycleBinEntityKind.ItemCategoryOption => HasDirtyLocalEntityAsync(_db.ItemCategoryOptions, entityId, ct),
            RecycleBinEntityKind.Invoice => HasDirtyLocalEntityAsync(_db.Invoices, entityId, ct),
            RecycleBinEntityKind.Payment => HasDirtyLocalEntityAsync(_db.Payments, entityId, ct),
            RecycleBinEntityKind.Transaction => HasDirtyLocalEntityAsync(_db.Transactions, entityId, ct),
            RecycleBinEntityKind.InventoryTransfer => HasDirtyLocalEntityAsync(_db.InventoryTransfers, entityId, ct),
            RecycleBinEntityKind.RentalManagementCompany => HasDirtyLocalEntityAsync(_db.RentalManagementCompanies, entityId, ct),
            RecycleBinEntityKind.RentalBillingProfile => HasDirtyLocalEntityAsync(_db.RentalBillingProfiles, entityId, ct),
            RecycleBinEntityKind.RentalAsset => HasDirtyLocalEntityAsync(_db.RentalAssets, entityId, ct),
            RecycleBinEntityKind.RentalBillingLog => HasDirtyLocalEntityAsync(_db.RentalBillingLogs, entityId, ct),
            _ => Task.FromResult(false)
        };

    private static Task<bool> HasDirtyLocalEntityAsync<TEntity>(
        DbSet<TEntity> set,
        Guid entityId,
        CancellationToken ct)
        where TEntity : class, ILocalSyncEntity
        => set.IgnoreQueryFilters().AnyAsync(entity =>
            entity.Id == entityId &&
            entity.IsDirty,
            ct);

    private async Task<bool> HasPendingPurgeTargetOutboxAsync(
        RecycleBinEntityKind kind,
        Guid entityId,
        DeferredPurgeOwnerScope ownerScope,
        CancellationToken ct)
    {
        var allowedEntityNames = GetPurgeOutboxEntityNames(kind);
        if (allowedEntityNames.Count == 0)
            return false;

        var candidates = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(current =>
                current.EntityId == entityId &&
                current.Status != "Acknowledged")
            .ToListAsync(ct);
        return candidates.Any(current =>
            allowedEntityNames.Contains(
                current.EntityName,
                StringComparer.OrdinalIgnoreCase) &&
            PurgeOutboxBelongsToOwner(current, ownerScope));
    }

    private static bool PurgeOutboxBelongsToOwner(
        LocalSyncOutboxEntry entry,
        DeferredPurgeOwnerScope ownerScope)
    {
        if (!string.IsNullOrWhiteSpace(entry.BusinessDatabaseName))
        {
            return string.Equals(
                TenantScopeCatalog.GetDatabaseName(
                    entry.BusinessDatabaseName),
                ownerScope.BusinessDatabaseName,
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
                   NormalizeOutboxScopeText(entry.TenantCode),
                   ownerScope.TenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   NormalizeOutboxScopeText(entry.OfficeCode),
                   ownerScope.OfficeCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   NormalizeOutboxScopeText(
                       entry.ResponsibleOfficeCode),
                   ownerScope.ResponsibleOfficeCode,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetPurgeOutboxEntityNames(
        RecycleBinEntityKind kind)
        => kind switch
        {
            RecycleBinEntityKind.Customer => [nameof(LocalCustomer), "Customer"],
            RecycleBinEntityKind.CustomerContract => [nameof(LocalCustomerContract), "CustomerContract"],
            RecycleBinEntityKind.Item => [nameof(LocalItem), "Item"],
            RecycleBinEntityKind.CompanyProfile => [nameof(LocalCompanyProfile), "CompanyProfile"],
            RecycleBinEntityKind.CustomerCategory => [nameof(LocalCustomerCategory), "CustomerCategory"],
            RecycleBinEntityKind.PriceGradeOption => [nameof(LocalPriceGradeOption), "PriceGradeOption"],
            RecycleBinEntityKind.TradeTypeOption => [nameof(LocalTradeTypeOption), "TradeTypeOption"],
            RecycleBinEntityKind.ItemCategoryOption => [nameof(LocalItemCategoryOption), "ItemCategoryOption"],
            RecycleBinEntityKind.Invoice => [nameof(LocalInvoice), "Invoice"],
            RecycleBinEntityKind.Payment => [nameof(LocalPayment), "Payment"],
            RecycleBinEntityKind.Transaction => [nameof(LocalTransaction), "TransactionRecord", "Transaction"],
            RecycleBinEntityKind.InventoryTransfer => [nameof(LocalInventoryTransfer), "InventoryTransfer"],
            RecycleBinEntityKind.RentalManagementCompany => [nameof(LocalRentalManagementCompany), "RentalManagementCompany"],
            RecycleBinEntityKind.RentalBillingProfile => [nameof(LocalRentalBillingProfile), "RentalBillingProfile"],
            RecycleBinEntityKind.RentalAsset => [nameof(LocalRentalAsset), "RentalAsset"],
            RecycleBinEntityKind.RentalBillingLog => [nameof(LocalRentalBillingLog), "RentalBillingLog"],
            _ => []
        };

    private static string NormalizePurgeRecordKind(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    internal static bool IsInvoicePurgeRecordKind(string? kind)
        => TryParseRecycleBinEntityKind(
               NormalizePurgeRecordKind(kind),
               out var entityKind) &&
           entityKind == RecycleBinEntityKind.Invoice;

    private static int GetPurgeApplyOrder(string normalizedKind)
        => normalizedKind switch
        {
            "payment" => 0,
            "transaction" => 1,
            "rental-billing-log" => 2,
            "rentalbillinglog" => 2,
            "contract" => 3,
            "invoice" => 4,
            "inventory-transfer" => 4,
            "inventorytransfer" => 4,
            "rental-asset" => 5,
            "rentalasset" => 5,
            "item" => 6,
            "rental-billing-profile" => 7,
            "rentalbillingprofile" => 7,
            "rental-management-company" => 7,
            "rentalmanagementcompany" => 7,
            "customer" => 8,
            "company-profile" => 9,
            "companyprofile" => 9,
            "customer-category" => 10,
            "customercategory" => 10,
            "price-grade-option" => 10,
            "pricegradeoption" => 10,
            "trade-type-option" => 10,
            "tradetypeoption" => 10,
            "item-category-option" => 10,
            "itemcategoryoption" => 10,
            _ => 99
        };

    private static bool TryParseRecycleBinEntityKind(string normalizedKind, out RecycleBinEntityKind kind)
    {
        switch (normalizedKind)
        {
            case "customer":
                kind = RecycleBinEntityKind.Customer;
                return true;
            case "contract":
                kind = RecycleBinEntityKind.CustomerContract;
                return true;
            case "item":
                kind = RecycleBinEntityKind.Item;
                return true;
            case "companyprofile":
            case "company-profile":
                kind = RecycleBinEntityKind.CompanyProfile;
                return true;
            case "customercategory":
            case "customer-category":
                kind = RecycleBinEntityKind.CustomerCategory;
                return true;
            case "pricegradeoption":
            case "price-grade-option":
                kind = RecycleBinEntityKind.PriceGradeOption;
                return true;
            case "tradetypeoption":
            case "trade-type-option":
                kind = RecycleBinEntityKind.TradeTypeOption;
                return true;
            case "itemcategoryoption":
            case "item-category-option":
                kind = RecycleBinEntityKind.ItemCategoryOption;
                return true;
            case "invoice":
                kind = RecycleBinEntityKind.Invoice;
                return true;
            case "payment":
                kind = RecycleBinEntityKind.Payment;
                return true;
            case "transaction":
                kind = RecycleBinEntityKind.Transaction;
                return true;
            case "inventorytransfer":
            case "inventory-transfer":
                kind = RecycleBinEntityKind.InventoryTransfer;
                return true;
            case "rentalmanagementcompany":
            case "rental-management-company":
                kind = RecycleBinEntityKind.RentalManagementCompany;
                return true;
            case "rentalbillingprofile":
            case "rental-billing-profile":
                kind = RecycleBinEntityKind.RentalBillingProfile;
                return true;
            case "rentalasset":
            case "rental-asset":
                kind = RecycleBinEntityKind.RentalAsset;
                return true;
            case "rentalbillinglog":
            case "rental-billing-log":
                kind = RecycleBinEntityKind.RentalBillingLog;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool IsServerSyncDisabled()
    {
        var raw = Environment.GetEnvironmentVariable(DisableServerSyncEnvironmentKey);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public Task StopAndDrainAsync()
    {
        lock (_immediateSyncGate)
        {
            if (_stopAndDrainTask is not null)
                return _stopAndDrainTask;

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopAndDrainTask = completion.Task;
            _stopping = true;
            _resyncRequested = false;
            _flushRequested = false;

            var stopErrors = new List<Exception>();

            if (_compatibilityRuntime is not null)
            {
                _compatibilityRuntime.MutationAvailabilityChanged -=
                    HandleMutationAvailabilityChanged;
            }

            CancelForStop(_immediateSyncCts, stopErrors);
            CancelForStop(_transientFailureRetryCts, stopErrors);
            CancelForStop(_compatibilityBlockCts, stopErrors);
            CancelForStop(_runtimeStopCts, stopErrors);

            if (_dispatcherSubscribed)
            {
                _dispatcher.SyncRequested -= HandleSyncRequested;
                _dispatcherSubscribed = false;
            }

            var timer = _timer;
            _timer = null;
            _ = CompleteStopAndDrainAsync(timer, stopErrors, completion);
            return _stopAndDrainTask;
        }
    }

    private static void CancelForStop(
        CancellationTokenSource? cancellation,
        ICollection<Exception> errors)
    {
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex)
        {
            // Cancel() invokes every registered callback before reporting an
            // aggregate failure. Preserve that failure while continuing the
            // remaining stop steps and background-task drain.
            errors.Add(ex);
        }
    }

    private async Task CompleteStopAndDrainAsync(
        Timer? timer,
        IReadOnlyCollection<Exception> stopErrors,
        TaskCompletionSource completion)
    {
        try
        {
            await StopAndDrainCoreAsync(timer).ConfigureAwait(false);
            if (stopErrors.Count > 0)
            {
                AppLogger.Error(
                    "SYNC",
                    "One or more synchronization stop callbacks failed after cancellation was requested. All registered background work was still drained.",
                    new AggregateException(stopErrors));
            }

            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task StopAndDrainCoreAsync(Timer? timer)
    {
        if (timer is not null)
            await timer.DisposeAsync().ConfigureAwait(false);

        while (true)
        {
            Task[] pendingTasks;
            lock (_immediateSyncGate)
            {
                _observedBackgroundTasks.RemoveWhere(
                    static task => task.IsCompleted);
                if (_observedBackgroundTasks.Count == 0)
                    return;

                pendingTasks = [.. _observedBackgroundTasks];
            }

            try
            {
                await Task.WhenAll(pendingTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Observation tasks own detailed error reporting. A faulted generation is
                // still complete, so keep looping until no later generation remains.
                AppLogger.Warn(
                    "SYNC",
                    $"동기화 백그라운드 작업 오류를 관찰하고 drain을 계속합니다: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Task drainTask;
        lock (_immediateSyncGate)
        {
            if (_disposeRequested)
                return;

            _disposeRequested = true;
            drainTask = StopAndDrainAsync();
        }

        if (drainTask.IsCompletedSuccessfully)
        {
            ReleaseResourcesAfterDrain();
            return;
        }

        _ = drainTask.ContinueWith(
            static (_, state) =>
                ((SyncService)state!).ReleaseResourcesAfterDrain(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void ReleaseResourcesAfterDrain()
    {
        lock (_immediateSyncGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _immediateSyncCts?.Dispose();
            _immediateSyncCts = null;
            _transientFailureRetryCts?.Dispose();
            _transientFailureRetryCts = null;
            _currentSyncTask = null;
            _compatibilityBlockCts.Dispose();
            _runtimeStopCts.Dispose();
            _administrativeBusinessCacheRefreshLock.Dispose();
        }
    }

    private bool CanRunServerMutation()
        => _compatibilityRuntime?.CanMutate != false;

    private CancellationTokenSource?
        CreateCompatibilityLinkedTokenSource(
            CancellationToken callerToken)
    {
        lock (_immediateSyncGate)
        {
            if (_disposed || _stopping)
                return null;

            return CancellationTokenSource
                .CreateLinkedTokenSource(
                    callerToken,
                    _compatibilityBlockCts.Token,
                    _runtimeStopCts.Token);
        }
    }

    private void HandleMutationAvailabilityChanged(
        bool canMutate)
    {
        if (!canMutate)
        {
            CancelForCompatibilityBlock();
            return;
        }

        lock (_immediateSyncGate)
        {
            if (_disposed || _stopping)
                return;

            if (_compatibilityBlockCts
                .IsCancellationRequested)
            {
                _compatibilityBlockCts.Dispose();
                _compatibilityBlockCts =
                    new CancellationTokenSource();
            }
        }
    }

    public void CancelForCompatibilityBlock()
    {
        lock (_immediateSyncGate)
        {
            if (_disposed || _stopping)
                return;

            if (_timer is not null)
            {
                ObserveBackgroundTask(
                    _timer.DisposeAsync().AsTask(),
                    "호환성 차단 타이머 종료");
                _timer = null;
            }

            _compatibilityBlockCts.Cancel();
            _immediateSyncCts?.Cancel();
            _transientFailureRetryCts?.Cancel();
            _resyncRequested = false;
            _flushRequested = false;
        }

        SetStatus(
            "필수 PC 업데이트가 확인되어 동기화가 차단되었습니다. 미동기화 변경은 보존됩니다.");
    }

    private async Task TryRecordDiagnosticAsync(
        string phase,
        string rawMessage,
        Exception? exception = null,
        string? severity = null,
        bool recoveryAttempted = false,
        bool recoverySucceeded = false)
    {
        try
        {
            await _diagnostics.RecordIssueAsync(
                phase,
                rawMessage,
                exception,
                severity,
                recoveryAttempted,
                recoverySucceeded,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"동기화 진단 이벤트 저장 실패 무시: {ex.Message}");
        }
    }
}
