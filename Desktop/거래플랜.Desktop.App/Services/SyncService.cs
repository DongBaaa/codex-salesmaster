using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
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
    private const int MaxRetryCount = 3;
    private const string DisableServerSyncEnvironmentKey = "GEORAEPLAN_DISABLE_SERVER_SYNC";
    private const string DeviceIdSettingKey = "Sync.DeviceId";
    private const string LastConflictSummarySettingKey = "Sync.LastConflictSummary";
    private static readonly TimeSpan AdministrativeBusinessCacheRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DebouncedSyncDelay = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> EquivalentConflictIgnoredPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreatedAtUtc",
        "UpdatedAtUtc",
        "Revision",
        "ExpectedRevision",
        "MutationId",
        "MutationCreatedAtUtc",
        "PreparedAtUtc",
        "SentAtUtc",
        "AcknowledgedAtUtc"
    };
    private static readonly HashSet<string> ItemCanonicalRepairIgnoredPropertyNames = new(
        EquivalentConflictIgnoredPropertyNames,
        StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "OfficeCode",
        "TenantCode"
    };

    private readonly LocalDbContext _db;
    private readonly LocalStateService _local;
    private readonly RentalStateService _rental;
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly SyncRequestDispatcher _dispatcher;
    private readonly SyncDiagnosticsService _diagnostics;
    private readonly SemaphoreSlim _administrativeBusinessCacheRefreshLock = new(1, 1);
    private readonly object _immediateSyncGate = new();
    private Timer? _timer;
    private CancellationTokenSource? _immediateSyncCts;
    private Task<bool>? _currentSyncTask;
    private bool _resyncRequested;
    private bool _flushRequested;
    private bool _disposed;
    private DateTime _lastSyncStartedUtc = DateTime.MinValue;
    private DateTime _lastSyncCompletedUtc = DateTime.MinValue;
    private DateTime _lastAdministrativeBusinessCacheRefreshUtc = DateTime.MinValue;
    private Guid _lastAdministrativeBusinessCacheSessionId = Guid.Empty;

    public event Action<string>? SyncStatusChanged;

    public bool HasRecentSuccessfulSync(TimeSpan window)
        => !_disposed
           && _lastSyncCompletedUtc != DateTime.MinValue
           && DateTime.UtcNow - _lastSyncCompletedUtc < window;

    public bool HasActiveOrQueuedSync
    {
        get
        {
            lock (_immediateSyncGate)
            {
                return (_currentSyncTask is not null && !_currentSyncTask.IsCompleted)
                       || _resyncRequested
                       || (_immediateSyncCts is not null && !_immediateSyncCts.IsCancellationRequested);
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
        SyncDiagnosticsService diagnostics)
    {
        _db = db;
        _local = local;
        _rental = rental;
        _api = api;
        _session = session;
        _dispatcher = dispatcher;
        _diagnostics = diagnostics;
        _dispatcher.SyncRequested += HandleSyncRequested;
    }

    public void Start(TimeSpan interval, bool runImmediately = false)
    {
        if (_disposed || IsServerSyncDisabled())
            return;

        _timer?.Dispose();
        var normalizedInterval = interval <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : interval;
        var due = runImmediately ? TimeSpan.Zero : normalizedInterval;
        _timer = new Timer(_ => ObserveBackgroundTask(TrySyncAsync(), "타이머 자동 동기화"), null,
            due, normalizedInterval);
    }

    public void Start(int intervalMinutes = 5, bool runImmediately = false)
    {
        Start(TimeSpan.FromMinutes(intervalMinutes), runImmediately);
    }

    public Task<bool> TrySyncAsync(CancellationToken ct = default)
        => _disposed
            ? Task.FromResult(false)
            : IsServerSyncDisabled()
                ? Task.FromResult(true)
                : StartSyncAsync(waitForRunningSync: false, ct);

    public async Task<bool> FlushPendingChangesAsync(CancellationToken ct = default)
    {
        if (_disposed || !_session.IsLoggedIn)
            return false;
        if (IsServerSyncDisabled())
            return true;

        CancelPendingImmediateSync();

        var attempts = 0;
        while (attempts < 3)
        {
            attempts++;
            var synced = await StartSyncAsync(waitForRunningSync: true, ct);
            var hasPendingChanges = await _local.HasPendingSyncChangesAsync(ct);
            if (!hasPendingChanges)
                return synced;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return !await _local.HasPendingSyncChangesAsync(ct);
    }

    public async Task<SyncScopeExecutionResult> TrySyncScopeAsync(string scopeKey, CancellationToken ct = default)
    {
        if (_disposed)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, false, false, false, "동기화 서비스를 사용할 수 없습니다.");

        if (!_session.IsLoggedIn)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, false, false, false, "로그인 후 다시 시도하세요.");

        if (_session.IsOfflineMode)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, false, false, false, "오프라인 모드에서는 선택 범위 동기화를 실행할 수 없습니다.");

        if (IsServerSyncDisabled())
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, true, false, false, "서버 동기화가 비활성화되어 있어 선택 범위 동기화를 건너뜁니다.");

        var blockingReason = await _local.GetPendingSyncBlockingReasonAsync(_session, scopeKey, ct);
        if (blockingReason is null)
            return new SyncScopeExecutionResult(scopeKey, scopeKey, 0, 0, false, true, false, false, "선택한 범위에는 남은 변경이 없습니다.");

        var usedCurrentSession = blockingReason.IsCurrentScope;
        var usedStoredCredential = false;

        try
        {
            if (string.Equals(scopeKey, "SHARED", StringComparison.OrdinalIgnoreCase))
            {
                if (!_session.HasAdministrativePrivileges)
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
                    await TryRecordPendingScopeDiagnosticAsync(scopeKey, blockingReason.PendingCount, "missing_sync_credential");
                    return new SyncScopeExecutionResult(scopeKey, blockingReason.ScopeDisplayName, blockingReason.PendingCount, blockingReason.PendingCount, false, false, false, false, blockingReason.Message);
                }

                var login = await _api.LoginAsync(credential.Username, credential.Password, ct);
                if (login is null || string.IsNullOrWhiteSpace(login.Token))
                {
                    await InvalidateStoredOfficeCredentialAsync(credential, ct);
                    var refreshedReason = await _local.GetPendingSyncBlockingReasonAsync(_session, scopeKey, ct);
                    var failureMessage = refreshedReason?.Message ?? blockingReason.Message;
                    return new SyncScopeExecutionResult(scopeKey, blockingReason.ScopeDisplayName, blockingReason.PendingCount, blockingReason.PendingCount, false, false, false, false, failureMessage);
                }

                var officeSession = new SessionState();
                officeSession.SetSession(login.Token, login.User);
                using var officeHttpClient = new HttpClient
                {
                    BaseAddress = _api.GetBaseUri(),
                    Timeout = TimeSpan.FromSeconds(100)
                };
                var officeApi = new ErpApiClient(officeHttpClient, officeSession);
                usedStoredCredential = true;
                SetStatus($"{blockingReason.ScopeDisplayName} 범위를 저장된 계정으로 동기화하는 중...");
                await ExecuteWithRetryAsync(token => PushDirtyAsync(officeApi, officeSession, includeSharedDirty: false, token), $"{blockingReason.ScopeDisplayName} 추가 업로드", ct);
                await ClearStaleDirtyAsync(officeApi, officeSession, includeSharedDirty: false, ct);
            }

            var remainingReason = await _local.GetPendingSyncBlockingReasonAsync(_session, scopeKey, ct);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            await TryRecordDiagnosticAsync(
                phase: "scope-sync",
                rawMessage: $"{blockingReason.ScopeDisplayName} 범위 동기화 실패: {detail}",
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

    public async Task<bool> RefreshSharedMirrorFromServerAsync(CancellationToken ct = default)
    {
        if (_disposed || !_session.IsLoggedIn || _session.IsOfflineMode)
            return false;
        if (IsServerSyncDisabled())
            return true;

        if (await _local.HasPendingSyncChangesAsync(ct))
        {
            var pendingMessage = await _local.GetPendingSyncWaitingMessageAsync("로컬 미동기화 변경이 남아 있어 중앙 서버 기준 캐시를 다시 불러올 수 없습니다.");
            SetStatus(pendingMessage ?? "로컬 미동기화 변경이 남아 있어 중앙 서버 기준 캐시를 다시 불러올 수 없습니다.");
            return false;
        }

        SetStatus("중앙 서버 기준 캐시를 다시 불러오는 중...");

        try
        {
            return await TryRefreshSharedMirrorCoreAsync(ct);
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
    }

    public async Task<bool> RefreshCurrentBusinessScopeFromServerAsync(CancellationToken ct = default)
    {
        if (_disposed || !_session.IsLoggedIn || _session.IsOfflineMode)
            return false;
        if (IsServerSyncDisabled())
            return true;

        var currentScopeDirtyCount = await _local.CountDirtyAsync(_session, ct);
        if (currentScopeDirtyCount > 0)
        {
            SetStatus($"현재 업체 DB에 미동기화 변경 {currentScopeDirtyCount:N0}건이 남아 있어 범위 재구성을 건너뜁니다.");
            return false;
        }

        SetStatus("현재 업체 DB 기준 캐시를 다시 불러오는 중...");

        try
        {
            return await TryRefreshCurrentBusinessScopeCoreAsync(ct);
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
    }

    public async Task<bool> EnsureAdministrativeBusinessCachesAsync(CancellationToken ct = default)
    {
        if (_disposed || !_session.IsLoggedIn || _session.IsOfflineMode || !_session.HasAdministrativePrivileges)
            return false;
        if (IsServerSyncDisabled())
            return false;

        var runningSyncTask = GetCurrentRunningSyncTask();
        if (runningSyncTask is not null)
        {
            try
            {
                await runningSyncTask;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLogger.Warn("SYNC", $"관리자 전체 업체 캐시 병합 전 실행 중인 동기화 대기 실패 무시: {ex.Message}");
            }
        }

        var now = DateTime.UtcNow;
        if (_lastAdministrativeBusinessCacheSessionId == _session.SessionId &&
            now - _lastAdministrativeBusinessCacheRefreshUtc < AdministrativeBusinessCacheRefreshInterval)
        {
            return false;
        }

        await _administrativeBusinessCacheRefreshLock.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (_lastAdministrativeBusinessCacheSessionId == _session.SessionId &&
                now - _lastAdministrativeBusinessCacheRefreshUtc < AdministrativeBusinessCacheRefreshInterval)
            {
                return false;
            }

            var mergedBusinessDatabaseCount = 0;
            foreach (var businessDatabaseName in TenantScopeCatalog.AllTenants
                         .Select(TenantScopeCatalog.GetDatabaseName)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var pull = await _api.PullAsync(0, businessDatabaseName, ct);
                    if (pull is null)
                        continue;

                    await ApplyPullAsync(pull, 0L, ct, updateSyncRevision: false);
                    _db.ChangeTracker.Clear();
                    mergedBusinessDatabaseCount++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    AppLogger.Warn(
                        "SYNC",
                        $"관리자 전체 업체 캐시 병합 실패: db={businessDatabaseName}, detail={ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (mergedBusinessDatabaseCount == 0)
                return false;

            _lastAdministrativeBusinessCacheRefreshUtc = DateTime.UtcNow;
            _lastAdministrativeBusinessCacheSessionId = _session.SessionId;
            return true;
        }
        finally
        {
            _administrativeBusinessCacheRefreshLock.Release();
        }
    }

    private Task<bool>? GetCurrentRunningSyncTask()
    {
        lock (_immediateSyncGate)
        {
            return _currentSyncTask is not null && !_currentSyncTask.IsCompleted
                ? _currentSyncTask
                : null;
        }
    }

    private Task<bool> StartSyncAsync(bool waitForRunningSync, CancellationToken ct)
    {
        if (_disposed || !_session.IsLoggedIn)
            return Task.FromResult(false);
        if (IsServerSyncDisabled())
            return Task.FromResult(true);

        lock (_immediateSyncGate)
        {
            if (_currentSyncTask is not null && !_currentSyncTask.IsCompleted)
                return waitForRunningSync ? _currentSyncTask : Task.FromResult(false);

            var syncTask = RunSyncCoreAsync(ct);
            _currentSyncTask = syncTask;
            ObserveBackgroundTask(FinalizeSyncAsync(syncTask), "동기화 후처리");
            return syncTask;
        }
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

            if (_resyncRequested && _session.IsLoggedIn && !_session.IsOfflineMode)
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
            ObserveBackgroundTask(RunDeferredImmediateSyncAsync(rerunCts.Token), "예약된 즉시 동기화");
            return;
        }

        if (rerunImmediately)
        {
            ObserveBackgroundTask(StartSyncAsync(waitForRunningSync: true, CancellationToken.None), "즉시 재동기화");
            return;
        }

        _dispatcher.CompleteSync(succeeded);
    }

    private async Task<bool> RunSyncCoreAsync(CancellationToken ct)
    {
        try
        {
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
            await ExecuteWithRetryAsync(PullNewAsync, "다운로드", ct);

            var remainingDirtyCount = await _local.CountDirtyAsync(ct);

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
        catch (Exception ex) when (IsDisposedContextException(ex))
        {
            AppLogger.Warn("SYNC", $"동기화 종료 중 안전 무시: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            if (detail.Length > 220)
                detail = detail[..220] + "...";

            await TrySetSettingSafeAsync(
                "Sync.LastError",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {detail}",
                CancellationToken.None);

            await TryRecordDiagnosticAsync(
                phase: "sync",
                rawMessage: detail,
                exception: ex,
                severity: "Error");

            SetStatus($"동기화 오류: {detail}");
            AppLogger.Error("SYNC", "동기화 실패", ex);
            return false;
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
        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
            return true;

        if (ex is TimeoutException)
            return true;

        if (ex is HttpRequestException httpEx)
            return httpEx.StatusCode is null;

        return false;
    }

    private void HandleSyncRequested(SyncRequestMode mode)
    {
        if (_disposed || !_session.IsLoggedIn || _session.IsOfflineMode)
            return;

        CancellationTokenSource? cts = null;
        var runImmediately = false;
        lock (_immediateSyncGate)
        {
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
                runImmediately = true;
            }
            else
            {
                _immediateSyncCts = new CancellationTokenSource();
                cts = _immediateSyncCts;
            }
        }

        if (runImmediately)
            ObserveBackgroundTask(StartSyncAsync(waitForRunningSync: true, CancellationToken.None), "수동 즉시 동기화");
        else if (cts is not null)
            ObserveBackgroundTask(RunDeferredImmediateSyncAsync(cts.Token), "지연 즉시 동기화");
    }

    private async Task RunDeferredImmediateSyncAsync(CancellationToken ct)
    {
        if (_disposed)
            return;

        try
        {
            await Task.Delay(DebouncedSyncDelay, ct);

            if (_disposed || !_session.IsLoggedIn || _session.IsOfflineMode)
                return;

            await StartSyncAsync(waitForRunningSync: true, ct);
        }
        catch (OperationCanceledException)
        {
            // newer local change arrived; debounce in progress
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", "즉시 동기화 실패", ex);
            await TryRecordDiagnosticAsync(
                phase: "debounced-sync",
                rawMessage: ex.InnerException?.Message ?? ex.Message,
                exception: ex,
                severity: "Warning");
        }
    }

    private void ObserveBackgroundTask(Task task, string operationName)
    {
        UiTaskHelper.Forget(task, "SYNC", operationName, ex =>
        {
            AppLogger.Error("SYNC", $"{operationName} 실패", ex);
            UiTaskHelper.Forget(
                ObserveBackgroundTaskFailureAsync(operationName, ex),
                "SYNC",
                $"{operationName} 진단 기록");
        });
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

    private void SetStatus(string message) => SyncStatusChanged?.Invoke(message);

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
                var login = await _api.LoginAsync(credential.Username, credential.Password, ct);
                if (login is null || string.IsNullOrWhiteSpace(login.Token))
                {
                    await InvalidateStoredOfficeCredentialAsync(credential, ct);
                    continue;
                }

                var officeSession = new SessionState();
                officeSession.SetSession(login.Token, login.User);

                var officeDirtyCount = await _local.CountDirtyAsync(officeSession, ct);
                if (officeDirtyCount == 0)
                    continue;

                using var officeHttpClient = new HttpClient
                {
                    BaseAddress = _api.GetBaseUri(),
                    Timeout = TimeSpan.FromSeconds(100)
                };
                var officeApi = new ErpApiClient(officeHttpClient, officeSession);
                SetStatus($"{normalizedOfficeCode} 지점 변경분을 추가 동기화하는 중...");
                await ExecuteWithRetryAsync(
                    token => PushDirtyAsync(officeApi, officeSession, includeSharedDirty: false, token),
                    $"{normalizedOfficeCode} 지점 업로드",
                    ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLogger.Warn("SYNC", $"지점별 추가 동기화 실패: office={normalizedOfficeCode}, detail={ex.Message}");
                await TryRecordDiagnosticAsync(
                    phase: "office-sync",
                    rawMessage: $"지점별 추가 동기화 실패({normalizedOfficeCode}): {ex.InnerException?.Message ?? ex.Message}",
                    exception: ex,
                    severity: "Warning");
            }
        }

        await ReportRemainingDirtyOfficesAsync(null, "remaining_dirty", ct);
    }

    private async Task ClearStaleDirtyWithStoredOfficeSessionsAsync(CancellationToken ct)
    {
        if (await _local.CountDirtyAsync(ct) == 0)
            return;

        await ClearStaleDirtyAsync(_api, _session, includeSharedDirty: true, ct);
        if (await _local.CountDirtyAsync(ct) == 0)
            return;

        var pendingOfficeSummaries = await GetPendingDirtyOfficeSummariesOutsideCurrentSessionAsync(ct);
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
                var login = await _api.LoginAsync(credential.Username, credential.Password, ct);
                if (login is null || string.IsNullOrWhiteSpace(login.Token))
                {
                    await InvalidateStoredOfficeCredentialAsync(credential, ct);
                    continue;
                }

                var officeSession = new SessionState();
                officeSession.SetSession(login.Token, login.User);
                if (await _local.CountDirtyAsync(officeSession, ct) == 0)
                    continue;

                using var officeHttpClient = new HttpClient
                {
                    BaseAddress = _api.GetBaseUri(),
                    Timeout = TimeSpan.FromSeconds(100)
                };
                var officeApi = new ErpApiClient(officeHttpClient, officeSession);
                await ClearStaleDirtyAsync(officeApi, officeSession, includeSharedDirty: false, ct);
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
    {
        var sessionDirtyCount = await _local.CountDirtyAsync(session, ct);
        if (sessionDirtyCount == 0 && (!includeSharedDirty || !session.HasAdministrativePrivileges))
            return;

        var pull = await apiClient.PullAsync(0, ct);
        if (pull is null)
            return;

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

            if (clearedCount > 0)
            {
                AppLogger.Info("SYNC", $"stale dirty 자동정리: office={session.OfficeCode}, cleaned={clearedCount}");
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

        var trackedEntities = await _db.Set<TLocal>()
            .IgnoreQueryFilters()
            .Where(entity => dirtyIds.Contains(entity.Id))
            .ToListAsync(ct);

        var changed = 0;
        foreach (var entity in trackedEntities)
        {
            if (!entity.IsDirty || !serverMap.TryGetValue(entity.Id, out var serverEntity))
                continue;

            if (!IsStaleDirtyMatch(entity, serverEntity))
                continue;

            entity.Revision = serverEntity.Revision;
            entity.UpdatedAtUtc = serverEntity.UpdatedAtUtc;
            entity.IsDeleted = serverEntity.IsDeleted;
            entity.IsDirty = false;
            changed++;
        }

        if (changed > 0)
            await _db.SaveChangesAsync(ct);

        return changed;
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
        List<RentalManagementCompanyDto> ManagementCompanies,
        List<RentalBillingProfileDto> BillingProfiles,
        List<RentalAssetDto> Assets,
        List<RentalBillingLogDto> BillingLogs);

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

        var scopedDirtyRentalAssetIds = (await _local.GetDirtyRentalAssetsForSyncAsync(session, ct))
            .Where(asset => !asset.IsDeleted)
            .Select(asset => asset.Id)
            .Distinct()
            .ToList();

        if (scopedDirtyRentalAssetIds.Count > 0)
        {
            var rentalRepair = await _rental.RepairRentalCatalogLinksAsync(scopedDirtyRentalAssetIds, ct);
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

        var dirtyCompanyProfiles = includeSharedDirty
            ? await _db.CompanyProfiles.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyUnits = includeSharedDirty
            ? await _db.Units.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyCustomerCategories = includeSharedDirty
            ? await _db.CustomerCategories.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyPriceGradeOptions = includeSharedDirty
            ? await _db.PriceGradeOptions.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyTradeTypeOptions = includeSharedDirty
            ? await _db.TradeTypeOptions.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyItemCategoryOptions = includeSharedDirty
            ? await _db.ItemCategoryOptions.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyCustomerMasters = await _local.GetDirtyCustomerMastersForSyncAsync(session, ct);
        var dirtyCustomers = await _local.GetDirtyCustomersForSyncAsync(session, ct);
        var dirtyCustomerContracts = await _local.GetDirtyCustomerContractsForSyncAsync(session, ct);
        var dirtyItems = await _local.GetDirtyItemsForSyncAsync(session, ct);
        var dirtyItemWarehouseStocks = includeSharedDirty
            ? await _db.ItemWarehouseStocks
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyTransactions = await _local.GetDirtyTransactionsForSyncAsync(session, ct);
        var dirtyTransactionAttachments = await _local.GetDirtyTransactionAttachmentsForSyncAsync(session, ct);
        var dirtyInventoryTransfers = await _local.GetDirtyInventoryTransfersForSyncAsync(session, ct);
        var dirtyRentalManagementCompanies = includeSharedDirty
            ? await _db.RentalManagementCompanies.IgnoreQueryFilters()
                .Where(entity => entity.IsDirty)
                .AsNoTracking()
                .ToListAsync(ct)
            : [];
        var dirtyRentalBillingProfiles = await _local.GetDirtyRentalBillingProfilesForSyncAsync(session, ct);
        var dirtyRentalAssets = await _local.GetDirtyRentalAssetsForSyncAsync(session, ct);
        var dirtyRentalBillingLogs = await _local.GetDirtyRentalBillingLogsForSyncAsync(session, ct);
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
        var items = dirtyItems.Select(LocalMappings.ToDto).ToList();
        var itemWarehouseStocks = dirtyItemWarehouseStocks.Select(LocalMappings.ToDto).ToList();
        var transactions = dirtyTransactions.Select(LocalMappings.ToDto).ToList();
        var transactionAttachments = dirtyTransactionAttachments
            .Select(entity => LocalMappings.ToDto(entity, ReadTransactionAttachmentContent(entity)))
            .ToList();
        var inventoryTransfers = dirtyInventoryTransfers.Select(LocalMappings.ToDto).ToList();
        var referencedRentalBillingProfiles = await LoadReferencedRentalBillingProfilesForPushAsync(
            dirtyRentalAssets,
            dirtyRentalBillingProfiles,
            ct);
        var referencedRentalManagementCompanies = await LoadReferencedRentalManagementCompaniesForPushAsync(
            dirtyRentalAssets,
            dirtyRentalBillingProfiles.Concat(referencedRentalBillingProfiles).ToList(),
            dirtyRentalManagementCompanies,
            ct);
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

        var rentalManagementCompanies = dirtyRentalManagementCompanies
            .Concat(referencedRentalManagementCompanies)
            .GroupBy(company => company.Id)
            .Select(group => LocalMappings.ToDto(group.First()))
            .ToList();
        var rentalBillingProfiles = dirtyRentalBillingProfiles
            .Concat(referencedRentalBillingProfiles)
            .GroupBy(profile => profile.Id)
            .Select(group => LocalMappings.ToDto(group.First()))
            .ToList();
        var rentalAssets = dirtyRentalAssets.Select(LocalMappings.ToDto).ToList();
        var rentalBillingLogs = dirtyRentalBillingLogs.Select(LocalMappings.ToDto).ToList();
        var invoices = dirtyInvoices.Select(LocalMappings.ToDto).ToList();
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
            ItemWarehouseStocks = itemWarehouseStocks,
            Transactions = transactions,
            TransactionAttachments = transactionAttachments,
            InventoryTransfers = inventoryTransfers,
            RentalManagementCompanies = rentalManagementCompanies,
            RentalBillingProfiles = rentalBillingProfiles,
            RentalAssets = rentalAssets,
            RentalBillingLogs = rentalBillingLogs,
            Invoices = invoices,
            Payments = payments
        };

        req.DeviceId = await GetOrCreateDeviceIdAsync(ct);
        var additionalRentalRequests = new List<RentalTenantSyncPayload>();
        if (session.HasAdministrativePrivileges)
        {
            var currentBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(session.SelectedBusinessDatabaseName);
            var rentalTenantPayloads = BuildAdministrativeRentalTenantPayloads(
                rentalManagementCompanies,
                rentalBillingProfiles,
                rentalAssets,
                rentalBillingLogs);
            if (rentalTenantPayloads.Count > 0)
            {
                if (rentalTenantPayloads.TryGetValue(currentBusinessDatabaseName, out var currentPayload))
                {
                    req.RentalManagementCompanies = currentPayload.ManagementCompanies;
                    req.RentalBillingProfiles = currentPayload.BillingProfiles;
                    req.RentalAssets = currentPayload.Assets;
                    req.RentalBillingLogs = currentPayload.BillingLogs;
                    rentalTenantPayloads.Remove(currentBusinessDatabaseName);
                }
                else
                {
                    req.RentalManagementCompanies = [];
                    req.RentalBillingProfiles = [];
                    req.RentalAssets = [];
                    req.RentalBillingLogs = [];
                }

                additionalRentalRequests.AddRange(rentalTenantPayloads.Values);
            }
        }

        StampOutgoingMutations(req, req.DeviceId);
        await PushPreparedRequestAsync(apiClient, session, req, businessDatabaseNameOverride: null, ct);

        foreach (var additionalRequest in additionalRentalRequests)
        {
            ct.ThrowIfCancellationRequested();

            var supplementalRequest = new SyncPushRequest
            {
                DeviceId = req.DeviceId,
                RentalManagementCompanies = additionalRequest.ManagementCompanies,
                RentalBillingProfiles = additionalRequest.BillingProfiles,
                RentalAssets = additionalRequest.Assets,
                RentalBillingLogs = additionalRequest.BillingLogs
            };
            StampOutgoingMutations(supplementalRequest, supplementalRequest.DeviceId);
            await PushPreparedRequestAsync(
                apiClient,
                session,
                supplementalRequest,
                additionalRequest.BusinessDatabaseName,
                ct);
        }
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
           req.Transactions.Count +
           req.TransactionAttachments.Count +
           req.InventoryTransfers.Count +
           req.RentalManagementCompanies.Count +
           req.RentalBillingProfiles.Count +
           req.RentalAssets.Count +
           req.RentalBillingLogs.Count +
           req.Invoices.Count +
           req.Payments.Count == 0;

    private async Task PushPreparedRequestAsync(
        ErpApiClient apiClient,
        SessionState session,
        SyncPushRequest req,
        string? businessDatabaseNameOverride,
        CancellationToken ct)
    {
        if (IsPushRequestEmpty(req))
            return;

        await RecordPreparedMutationsAsync(req, session, ct);

        try
        {
            var result = await apiClient.PushAsync(req, businessDatabaseNameOverride, ct);
            if (result is null)
            {
                await TryMarkOutboxFailedAsync(req, "서버 응답이 비어 있어 동기화를 완료하지 못했습니다.", ct);
                return;
            }

            await MarkOutboxSentAsync(req, ct);

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

            if (result.ConflictCount > 0)
            {
                var serverNewerConflicts = result.Conflicts
                    .Where(conflict => string.Equals(conflict.Reason, "Server version is newer.", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (serverNewerConflicts.Count > 0)
                {
                    await ResolveServerNewerConflictsAsync(serverNewerConflicts, ct);
                    AppLogger.Warn("SYNC", $"서버 최신 버전 우선으로 충돌 {serverNewerConflicts.Count}건을 정리했습니다.");
                    await AppendConflictSummaryAsync($"서버 최신값 우선으로 동기화 충돌 {serverNewerConflicts.Count}건을 자동 정리했습니다.");
                }

                var repairedItemRevisionConflicts = await ResolveCanonicalItemRevisionConflictsAsync(
                    result.Conflicts
                        .Except(serverNewerConflicts)
                        .ToList(),
                    ct);

                if (repairedItemRevisionConflicts.Count > 0)
                {
                    AppLogger.Warn("SYNC", $"중복 품목 자연키/리비전 충돌 {repairedItemRevisionConflicts.Count}건을 서버 기준 품목으로 자동 복구했습니다.");
                    await AppendConflictSummaryAsync($"중복 품목 자연키/리비전 충돌 {repairedItemRevisionConflicts.Count}건을 서버 기준 품목으로 자동 복구했습니다.");
                }

                var equivalentRevisionConflicts = result.Conflicts
                    .Except(serverNewerConflicts)
                    .Except(repairedItemRevisionConflicts)
                    .Where(IsEquivalentRevisionConflict)
                    .ToList();

                if (equivalentRevisionConflicts.Count > 0)
                {
                    await ResolveServerNewerConflictsAsync(equivalentRevisionConflicts, ct);
                    AppLogger.Warn("SYNC", $"재시도 중 서버에 이미 반영된 동일 내용 충돌 {equivalentRevisionConflicts.Count}건을 자동 정리했습니다.");
                    await AppendConflictSummaryAsync($"재시도 중 서버에 이미 반영된 동일 내용 충돌 {equivalentRevisionConflicts.Count}건을 자동 정리했습니다.");
                }

                var remainingConflicts = result.Conflicts
                    .Except(serverNewerConflicts)
                    .Except(repairedItemRevisionConflicts)
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
                    throw new InvalidOperationException(detail);
                }
            }

            foreach (var assigned in result.AssignedInvoiceNumbers)
            {
                await _db.Invoices.IgnoreQueryFilters()
                    .Where(invoice => invoice.Id == assigned.Key)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(invoice => invoice.InvoiceNumber, assigned.Value)
                            .SetProperty(invoice => invoice.IsDirty, false),
                        ct);

                SynchronizeTrackedInvoiceAssignment(assigned.Key, assigned.Value);
            }

            await MarkCleanAsync<LocalCompanyProfile>(req.CompanyProfiles.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalUnit>(req.Units.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalCustomerCategory>(req.CustomerCategories.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalPriceGradeOption>(req.PriceGradeOptions.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalTradeTypeOption>(req.TradeTypeOptions.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalItemCategoryOption>(req.ItemCategoryOptions.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalCustomerMaster>(req.CustomerMasters.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalCustomer>(req.Customers.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalCustomerContract>(req.CustomerContracts.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalItem>(req.Items.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalTransaction>(req.Transactions.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalTransactionAttachment>(req.TransactionAttachments.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanInventoryTransfersAsync(req.InventoryTransfers.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalRentalManagementCompany>(req.RentalManagementCompanies.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalRentalBillingProfile>(req.RentalBillingProfiles.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalRentalAsset>(req.RentalAssets.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalRentalBillingLog>(req.RentalBillingLogs.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanInvoicesAsync(req.Invoices.Select(entity => entity.Id).ToList(), ct);
            await MarkCleanAsync<LocalPayment>(req.Payments.Select(entity => entity.Id).ToList(), ct);
            await MarkOutboxAcknowledgedAsync(req, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await TryMarkOutboxFailedAsync(req, ex.InnerException?.Message ?? ex.Message, ct);
            throw;
        }
    }

    private static Dictionary<string, RentalTenantSyncPayload> BuildAdministrativeRentalTenantPayloads(
        IReadOnlyCollection<RentalManagementCompanyDto> managementCompanies,
        IReadOnlyCollection<RentalBillingProfileDto> billingProfiles,
        IReadOnlyCollection<RentalAssetDto> assets,
        IReadOnlyCollection<RentalBillingLogDto> billingLogs)
    {
        var payloads = new Dictionary<string, RentalTenantSyncPayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in managementCompanies)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalManagementCompanyBusinessDatabaseName(dto));
            payload.ManagementCompanies.Add(dto);
        }

        foreach (var dto in billingProfiles)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode));
            payload.BillingProfiles.Add(dto);
        }

        foreach (var dto in assets)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode));
            payload.Assets.Add(dto);
        }

        foreach (var dto in billingLogs)
        {
            var payload = GetOrCreateRentalTenantPayload(payloads, ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode));
            payload.BillingLogs.Add(dto);
        }

        return payloads;
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
            []);
        payloads[businessDatabaseName] = payload;
        return payload;
    }

    private static string ResolveRentalManagementCompanyBusinessDatabaseName(RentalManagementCompanyDto dto)
        => ResolveRentalBusinessDatabaseName(dto.TenantCode, dto.Code, dto.Code);

    private static string ResolveRentalBusinessDatabaseName(string? tenantCode, string? officeCode, string? fallbackOfficeCode)
        => TenantScopeCatalog.GetDatabaseName(
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                tenantCode,
                officeCode,
                fallbackOfficeCode: fallbackOfficeCode));

    private async Task<List<LocalRentalBillingProfile>> LoadReferencedRentalBillingProfilesForPushAsync(
        IReadOnlyCollection<LocalRentalAsset> dirtyRentalAssets,
        IReadOnlyCollection<LocalRentalBillingProfile> dirtyRentalBillingProfiles,
        CancellationToken ct)
    {
        if (dirtyRentalAssets.Count == 0)
            return [];

        var existingProfileIds = dirtyRentalBillingProfiles
            .Select(profile => profile.Id)
            .ToHashSet();

        var referencedProfileIds = dirtyRentalAssets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .Select(asset => asset.BillingProfileId!.Value)
            .Distinct()
            .Where(profileId => !existingProfileIds.Contains(profileId))
            .ToList();

        if (referencedProfileIds.Count == 0)
            return [];

        var referencedProfiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => referencedProfileIds.Contains(profile.Id) && !profile.IsDeleted)
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

    private async Task<List<LocalRentalManagementCompany>> LoadReferencedRentalManagementCompaniesForPushAsync(
        IReadOnlyCollection<LocalRentalAsset> dirtyRentalAssets,
        IReadOnlyCollection<LocalRentalBillingProfile> referencedRentalBillingProfiles,
        IReadOnlyCollection<LocalRentalManagementCompany> dirtyRentalManagementCompanies,
        CancellationToken ct)
    {
        var existingCodes = dirtyRentalManagementCompanies
            .Select(company => (company.Code ?? string.Empty).Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referencedCodes = dirtyRentalAssets
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
            .Where(company => referencedCodes.Contains(company.Code) && !company.IsDeleted)
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

    private async Task<bool> TryApplyServerItemConflictSnapshotAsync(ConflictLogDto conflict, CancellationToken ct)
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

    private void SynchronizeTrackedInvoiceAssignment(Guid invoiceId, string invoiceNumber)
    {
        foreach (var entry in _db.ChangeTracker.Entries<LocalInvoice>())
        {
            if (entry.Entity.Id != invoiceId)
                continue;

            entry.Entity.InvoiceNumber = invoiceNumber;
            entry.Entity.IsDirty = false;
            entry.State = EntityState.Unchanged;
        }
    }

    private async Task PullNewAsync(CancellationToken ct)
    {
        var revStr = await _local.GetSettingAsync("LastSyncRevision", ct) ?? "0";
        var sinceRev = long.TryParse(revStr, out var r) ? r : 0L;
        var hasPendingDirty = await _local.CountDirtyAsync(ct) > 0;
        var requiresMirrorRefresh = await _local.IsServerMirrorRefreshRequiredAsync(ct);

        if (requiresMirrorRefresh && !hasPendingDirty)
        {
            AppLogger.Info("SYNC", "버전 정비 후 범위 불일치 데이터를 정리하기 위해 중앙 서버 기준 전체 캐시 재구성을 수행합니다.");
            if (!await TryRefreshSharedMirrorCoreAsync(ct))
                throw new InvalidOperationException("중앙 서버 기준 캐시 재구성에 실패했습니다.");

            await _local.ClearServerMirrorRefreshRequiredAsync(ct);
            return;
        }

        if (requiresMirrorRefresh && hasPendingDirty)
            AppLogger.Warn("SYNC", "전체 캐시 재구성 예약이 남아 있지만 미동기화 변경이 있어 이번 동기화에서는 유지합니다.");

        if (sinceRev <= 0 && !hasPendingDirty)
        {
            AppLogger.Info("SYNC", "마지막 동기화 리비전이 없어 중앙 서버 기준 전체 캐시 재구성을 사용합니다.");
            if (!await TryRefreshSharedMirrorCoreAsync(ct))
                throw new InvalidOperationException("중앙 서버 기준 캐시 재구성에 실패했습니다.");

            return;
        }

        var pull = await _api.PullAsync(sinceRev, ct);
        if (pull is null)
            return;

        try
        {
            _db.ChangeTracker.Clear();
            using (_local.SuppressSyncDispatch())
            {
                await ApplyPullAsync(pull, sinceRev, ct);
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.ChangeTracker.Clear();

            if (hasPendingDirty)
            {
                AppLogger.Warn("SYNC", $"증분 pull 반영 중 동시성 충돌이 발생했지만 미동기화 변경이 남아 있어 전체 캐시 재구성은 건너뜁니다: {ex.Message}");
                await TryRecordDiagnosticAsync(
                    phase: "pull",
                    rawMessage: $"증분 pull 반영 중 동시성 충돌(미동기화 변경 보존): {ex.Message}",
                    exception: ex,
                    severity: "Warning",
                    recoveryAttempted: false,
                    recoverySucceeded: false);
                throw;
            }

            AppLogger.Info("SYNC", $"증분 pull 반영 중 동시성 충돌이 발생해 전체 캐시 재구성을 수행합니다: {ex.Message}");
            var recovered = await TryRefreshSharedMirrorCoreAsync(ct);
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
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task ApplyPullAsync(
        SyncPullResponse pull,
        long sinceRev,
        CancellationToken ct,
        bool updateSyncRevision = true)
    {
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
        await UpsertPulledAsync(pull.CustomerContracts, _db.CustomerContracts, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledItemsAsync(pull.Items, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledItemWarehouseStocksAsync(pull.ItemWarehouseStocks, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.Transactions, _db.Transactions, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledTransactionAttachmentsAsync(pull.TransactionAttachments, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledInventoryTransfersAsync(pull.InventoryTransfers, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledRentalManagementCompaniesAsync(pull.RentalManagementCompanies, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledRentalBillingProfilesAsync(pull.RentalBillingProfiles, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledRentalAssetsAsync(pull.RentalAssets, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.RentalBillingLogs, _db.RentalBillingLogs, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledInvoicesAsync(pull.Invoices, ct);
        _db.ChangeTracker.Clear();
        await UpsertPulledAsync(pull.Payments, _db.Payments, LocalMappings.ToLocal, ct);
        _db.ChangeTracker.Clear();
        await ApplyPulledPurgeRecordsAsync(pull.PurgeRecords, ct);
        _db.ChangeTracker.Clear();

        if (updateSyncRevision && pull.LatestRevision > sinceRev)
            await TrySetSettingSafeAsync("LastSyncRevision", pull.LatestRevision.ToString(), ct);
    }

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

    private async Task UpsertPulledItemsAsync(
        IReadOnlyList<ItemDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var skippedIncomingIds = await RemoveStalePulledItemConflictsAsync(dtos, ct);

        foreach (var dto in dtos)
        {
            if (skippedIncomingIds.Contains(dto.Id))
                continue;

            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.Items.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == local.Id, ct);
            if (existing is null)
            {
                _db.Items.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
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
        var skippedIncomingIds = await RemoveStalePulledRentalAssetConflictsAsync(dtos, ct);

        foreach (var dto in dtos)
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

    private async Task<HashSet<Guid>> RemoveStalePulledItemConflictsAsync(
        IReadOnlyList<ItemDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return [];

        var incomingItems = dtos
            .GroupBy(dto => dto.Id)
            .Select(group => group
                .OrderByDescending(dto => dto.Revision)
                .ThenByDescending(dto => dto.UpdatedAtUtc)
                .ThenByDescending(dto => dto.CreatedAtUtc)
                .ThenBy(dto => dto.Id)
                .First())
            .ToList();
        var candidates = await _db.Items.IgnoreQueryFilters().ToListAsync(ct);
        if (candidates.Count == 0)
            return [];

        var duplicateToCanonicalIdMap = new Dictionary<Guid, Guid>();
        var skippedIncomingIds = new HashSet<Guid>();
        var dirtyConflictDetails = new List<string>();
        var ambiguousConflictDetails = new List<string>();

        foreach (var candidate in candidates)
        {
            var matchingIncomingIds = incomingItems
                .Where(dto => dto.Id != candidate.Id)
                .Where(dto => ItemsSharePullNaturalKey(candidate, dto))
                .Select(dto => dto.Id)
                .Distinct()
                .ToList();

            if (matchingIncomingIds.Count == 0)
                continue;

            if (matchingIncomingIds.Count > 1)
            {
                ambiguousConflictDetails.Add($"{candidate.MaterialNumber}/{candidate.SerialNumber} -> {candidate.Id}");
                continue;
            }

            var incomingId = matchingIncomingIds[0];
            if (candidate.IsDirty)
            {
                skippedIncomingIds.Add(incomingId);
                dirtyConflictDetails.Add($"{candidate.MaterialNumber}/{candidate.SerialNumber} -> {candidate.Id}");
                continue;
            }

            duplicateToCanonicalIdMap[candidate.Id] = incomingId;
        }

        if (duplicateToCanonicalIdMap.Count > 0)
        {
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

        return skippedIncomingIds;
    }

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

        var invoiceLines = await _db.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(ct);
        foreach (var line in invoiceLines)
        {
            if (line.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(line.ItemId.Value, out var canonicalId))
                line.ItemId = canonicalId;
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
        foreach (var line in inventoryTransferLines)
        {
            if (line.ItemId.HasValue && duplicateToCanonicalIdMap.TryGetValue(line.ItemId.Value, out var canonicalId))
                line.ItemId = canonicalId;
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
                UpdatedAtUtc = stock.UpdatedAtUtc
            };
            canonicalStockLookup[stockKey] = migratedStock;
            _db.ItemWarehouseStocks.Add(migratedStock);
            _db.ItemWarehouseStocks.Remove(stock);
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<HashSet<Guid>> RemoveStalePulledRentalAssetConflictsAsync(
        IReadOnlyList<RentalAssetDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return [];

        var incomingByManagementNumber = BuildIncomingRentalAssetLookup(
            dtos,
            dto => dto.ManagementNumber);
        var incomingByManagementId = BuildIncomingRentalAssetLookup(
            dtos,
            dto => dto.ManagementId);
        var incomingByAssetKey = BuildIncomingRentalAssetLookup(
            dtos,
            dto => dto.AssetKey);

        if (incomingByManagementNumber.Count == 0 &&
            incomingByManagementId.Count == 0 &&
            incomingByAssetKey.Count == 0)
        {
            return [];
        }

        var managementNumbers = incomingByManagementNumber.Keys.ToList();
        var managementIds = incomingByManagementId.Keys.ToList();
        var assetKeys = incomingByAssetKey.Keys.ToList();

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

        foreach (var candidate in candidates)
        {
            var matchingIncomingIds = GetMatchingIncomingRentalAssetIds(
                candidate,
                incomingByManagementNumber,
                incomingByManagementId,
                incomingByAssetKey);

            if (matchingIncomingIds.Count == 0 || matchingIncomingIds.Contains(candidate.Id))
                continue;

            if (candidate.IsDirty)
            {
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
            _db.ChangeTracker.Clear();
            await _db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => staleConflictIds.Contains(asset.Id))
                .ExecuteDeleteAsync(ct);
            _db.ChangeTracker.Clear();

            AppLogger.Warn(
                "SYNC",
                $"렌탈 자산 pull 충돌 복구: 관리번호/관리ID가 같은 로컬 자산 {staleConflictIds.Count}건을 서버 기준으로 정리했습니다.");
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

    private static HashSet<Guid> GetMatchingIncomingRentalAssetIds(
        LocalRentalAsset candidate,
        IReadOnlyDictionary<string, HashSet<Guid>> incomingByManagementNumber,
        IReadOnlyDictionary<string, HashSet<Guid>> incomingByManagementId,
        IReadOnlyDictionary<string, HashSet<Guid>> incomingByAssetKey)
    {
        var matchingIds = new HashSet<Guid>();

        AddIncomingRentalAssetIds(matchingIds, incomingByManagementNumber, candidate.ManagementNumber);
        AddIncomingRentalAssetIds(matchingIds, incomingByManagementId, candidate.ManagementId);
        AddIncomingRentalAssetIds(matchingIds, incomingByAssetKey, candidate.AssetKey);

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
        => $"{itemId:D}|{(warehouseCode ?? string.Empty).Trim().ToUpperInvariant()}";

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

                    _db.ChangeTracker.Clear();
                    await _db.RentalManagementCompanies.IgnoreQueryFilters()
                        .Where(company => company.Id == local.Id)
                        .ExecuteDeleteAsync(ct);
                    _db.ChangeTracker.Clear();
                    existingCompanies = await _db.RentalManagementCompanies.IgnoreQueryFilters().ToListAsync(ct);
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
        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            var existing = await _db.ItemWarehouseStocks.FindAsync([local.ItemId, local.WarehouseCode], ct);
            if (existing is null)
            {
                _db.ItemWarehouseStocks.Add(local);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertPulledTransactionAttachmentsAsync(
        IReadOnlyList<TransactionAttachmentDto> dtos,
        CancellationToken ct)
    {
        foreach (var dto in dtos)
        {
            var existing = await _db.TransactionAttachments.IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == dto.Id, ct);
            if (dto.IsDeleted && existing is not null && !string.IsNullOrWhiteSpace(existing.StoredPath) && File.Exists(existing.StoredPath))
            {
                try { File.Delete(existing.StoredPath); } catch { /* ignore local cleanup failure */ }
            }

            var attachmentPath = PersistTransactionAttachment(dto, existing?.StoredPath);
            var local = LocalMappings.ToLocal(dto, storedFileName: Path.GetFileName(attachmentPath), storedPath: attachmentPath);
            local.IsDirty = false;

            if (existing is null)
            {
                _db.TransactionAttachments.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
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

    private async Task<bool> TryRefreshSharedMirrorCoreAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pull = await _api.PullAsync(0, ct);
            if (pull is null)
                return false;

            try
            {
                _db.ChangeTracker.Clear();
                using (_local.SuppressSyncDispatch())
                {
                    await _local.ResetSharedMirrorCacheAsync(ct);
                    await ApplyPullAsync(pull, 0L, ct);
                }

                await _local.ClearServerMirrorRefreshRequiredAsync(CancellationToken.None);
                await TrySetSettingSafeAsync("Sync.LastSuccessAt", DateTime.Now.ToString("O"), CancellationToken.None);
                await TrySetSettingSafeAsync("Sync.LastError", string.Empty, CancellationToken.None);
                await _diagnostics.ResolveOpenIssuesAsync(ct: CancellationToken.None);
                _lastSyncCompletedUtc = DateTime.UtcNow;
                SetStatus($"중앙 서버 기준 캐시 재구성 완료 {DateTime.Now:HH:mm:ss}");
                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt == 0)
            {
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
            catch
            {
                _db.ChangeTracker.Clear();
                throw;
            }
        }

        return false;
    }

    private async Task<bool> TryRefreshCurrentBusinessScopeCoreAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pull = await _api.PullAsync(0, ct);
            if (pull is null)
                return false;

            try
            {
                _db.ChangeTracker.Clear();
                using (_local.SuppressSyncDispatch())
                {
                    await ApplyPullAsync(pull, 0L, ct);
                }

                await TrySetSettingSafeAsync("Sync.LastSuccessAt", DateTime.Now.ToString("O"), CancellationToken.None);
                await TrySetSettingSafeAsync("Sync.LastError", string.Empty, CancellationToken.None);
                await _diagnostics.ResolveOpenIssuesAsync(ct: CancellationToken.None);
                _lastSyncCompletedUtc = DateTime.UtcNow;
                SetStatus($"현재 업체 DB 기준 캐시 재구성 완료 {DateTime.Now:HH:mm:ss}");
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
            await _local.SetSettingAsync(key, value, ct);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"설정값 저장 실패 무시 ({key}): {ex.Message}");
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

    private static void StampOutgoingMutations(SyncPushRequest request, string deviceId)
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
        StampOutgoingMutations(request.Transactions, nameof(LocalTransaction), deviceId);
        StampOutgoingMutations(request.TransactionAttachments, nameof(LocalTransactionAttachment), deviceId);
        StampOutgoingMutations(request.InventoryTransfers, nameof(LocalInventoryTransfer), deviceId);
        StampOutgoingMutations(request.RentalManagementCompanies, nameof(LocalRentalManagementCompany), deviceId);
        StampOutgoingMutations(request.RentalBillingProfiles, nameof(LocalRentalBillingProfile), deviceId);
        StampOutgoingMutations(request.RentalAssets, nameof(LocalRentalAsset), deviceId);
        StampOutgoingMutations(request.RentalBillingLogs, nameof(LocalRentalBillingLog), deviceId);
        StampOutgoingMutations(request.Invoices, nameof(LocalInvoice), deviceId);
        StampOutgoingMutations(request.Payments, nameof(LocalPayment), deviceId);
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

    private IEnumerable<(string EntityName, SyncEntityDto Entity)> EnumerateOutgoingMutations(SyncPushRequest request)
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
        foreach (var entity in request.RentalBillingLogs)
            yield return (nameof(LocalRentalBillingLog), entity);
        foreach (var entity in request.Invoices)
            yield return (nameof(LocalInvoice), entity);
        foreach (var entity in request.Payments)
            yield return (nameof(LocalPayment), entity);
    }

    private async Task RecordPreparedMutationsAsync(SyncPushRequest request, SessionState session, CancellationToken ct)
    {
        var outgoing = EnumerateOutgoingMutations(request)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Entity.MutationId))
            .ToList();
        if (outgoing.Count == 0)
            return;

        var scopeLookup = await BuildPreparedMutationScopeLookupAsync(request, session, ct);
        var mutationIds = outgoing.Select(entry => entry.Entity.MutationId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingIds = await _db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => mutationIds.Contains(entry.MutationId))
            .Select(entry => entry.MutationId)
            .ToListAsync(ct);
        var existingIdSet = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (entityName, entity) in outgoing)
        {
            if (existingIdSet.Contains(entity.MutationId))
                continue;

            var scope = ResolvePreparedMutationScope(entity, session, scopeLookup);
            _db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = entity.MutationId,
                DeviceId = request.DeviceId,
                EntityName = entityName,
                EntityId = entity.Id,
                ExpectedRevision = entity.ExpectedRevision,
                TenantCode = scope.TenantCode,
                OfficeCode = scope.OfficeCode,
                ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                Status = "Prepared",
                PreparedAtUtc = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private sealed record PreparedMutationScope(string TenantCode, string OfficeCode, string ResponsibleOfficeCode);

    private sealed class PreparedMutationScopeLookup
    {
        public Dictionary<Guid, PreparedMutationScope> CustomerScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> InvoiceScopeById { get; } = new();
        public Dictionary<Guid, PreparedMutationScope> TransactionScopeById { get; } = new();
    }

    private async Task<PreparedMutationScopeLookup> BuildPreparedMutationScopeLookupAsync(
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
            var customers = await _db.Customers.IgnoreQueryFilters()
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
                lookup.CustomerScopeById[customer.Id] = NormalizePreparedMutationScope(
                    customer.TenantCode,
                    customer.OfficeCode,
                    customer.ResponsibleOfficeCode,
                    session,
                    customer.OfficeCode);
        }

        var invoiceIds = request.Payments
            .Where(payment => payment.InvoiceId != Guid.Empty)
            .Select(payment => payment.InvoiceId)
            .Distinct()
            .ToList();
        if (invoiceIds.Count > 0)
        {
            var invoices = await _db.Invoices.IgnoreQueryFilters()
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
                lookup.InvoiceScopeById[invoice.Id] = NormalizePreparedMutationScope(
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode,
                    session,
                    invoice.OfficeCode);
        }

        var transactionIds = request.TransactionAttachments
            .Where(attachment => attachment.TransactionId != Guid.Empty)
            .Select(attachment => attachment.TransactionId)
            .Distinct()
            .ToList();
        if (transactionIds.Count > 0)
        {
            var transactions = await _db.Transactions.IgnoreQueryFilters()
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
                lookup.TransactionScopeById[transaction.Id] = NormalizePreparedMutationScope(
                    transaction.TenantCode,
                    transaction.OfficeCode,
                    transaction.ResponsibleOfficeCode,
                    session,
                    transaction.OfficeCode);
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
            ItemDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.OfficeCode, session, dto.OfficeCode),
            TransactionDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            TransactionAttachmentDto dto when lookup.TransactionScopeById.TryGetValue(dto.TransactionId, out var transactionScope) => transactionScope,
            TransactionAttachmentDto => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode),
            InventoryTransferDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.SourceOfficeCode, dto.TargetOfficeCode, session, dto.SourceOfficeCode),
            RentalManagementCompanyDto dto => NormalizePreparedMutationScope(dto.TenantCode, OfficeCodeCatalog.Shared, session.OfficeCode, session, OfficeCodeCatalog.Shared),
            RentalBillingProfileDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            RentalAssetDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            RentalBillingLogDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            InvoiceDto dto => NormalizePreparedMutationScope(dto.TenantCode, dto.OfficeCode, dto.ResponsibleOfficeCode, session, dto.OfficeCode),
            PaymentDto dto when lookup.InvoiceScopeById.TryGetValue(dto.InvoiceId, out var invoiceScope) => invoiceScope,
            PaymentDto => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode),
            _ => NormalizePreparedMutationScope(session.TenantCode, session.OfficeCode, session.OfficeCode, session, session.OfficeCode)
        };
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

    private async Task MarkOutboxSentAsync(SyncPushRequest request, CancellationToken ct)
    {
        var mutationIds = EnumerateOutgoingMutations(request)
            .Select(entry => entry.Entity.MutationId)
            .Where(mutationId => !string.IsNullOrWhiteSpace(mutationId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mutationIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var rows = await _db.SyncOutboxEntries
            .Where(entry => mutationIds.Contains(entry.MutationId))
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = "Sent";
            row.SentAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkOutboxAcknowledgedAsync(SyncPushRequest request, CancellationToken ct)
    {
        var mutationIds = EnumerateOutgoingMutations(request)
            .Select(entry => entry.Entity.MutationId)
            .Where(mutationId => !string.IsNullOrWhiteSpace(mutationId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mutationIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var rows = await _db.SyncOutboxEntries
            .Where(entry => mutationIds.Contains(entry.MutationId))
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = "Acknowledged";
            row.AcknowledgedAtUtc = now;
            row.ErrorMessage = string.Empty;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task TryMarkOutboxFailedAsync(SyncPushRequest request, string? errorMessage, CancellationToken ct)
    {
        try
        {
            var mutationIds = EnumerateOutgoingMutations(request)
                .Select(entry => entry.Entity.MutationId)
                .Where(mutationId => !string.IsNullOrWhiteSpace(mutationId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (mutationIds.Count == 0)
                return;

            await _local.MarkSyncOutboxFailedAsync(mutationIds!, errorMessage, ct);
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

            await _local.SetSettingAsync(LastConflictSummarySettingKey, merged, CancellationToken.None);
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
        try
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.InventoryTransfers.IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(transfer => transfer.Id == local.Id, ct);

            if (existing is not null)
            {
                var incomingIsNewer = local.Revision > existing.Revision ||
                                      (local.Revision == existing.Revision && local.UpdatedAtUtc >= existing.UpdatedAtUtc);
                if (existing.IsDirty || !incomingIsNewer)
                    return;
            }

            _db.ChangeTracker.Clear();

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await _db.InventoryTransferLines
                .Where(line => line.TransferId == local.Id)
                .ExecuteDeleteAsync(ct);
            await _db.InventoryTransfers.IgnoreQueryFilters()
                .Where(transfer => transfer.Id == local.Id)
                .ExecuteDeleteAsync(ct);

            _db.InventoryTransfers.Add(local);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (allowRetry)
        {
            _db.ChangeTracker.Clear();
            await UpsertPulledInventoryTransferAsync(dto, ct, allowRetry: false);
        }
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

    private static string PersistTransactionAttachment(TransactionAttachmentDto dto, string? existingPath = null)
    {
        if (dto.IsDeleted)
            return string.Empty;

        var attachmentDir = Path.Combine(AppPaths.TransactionAttachmentsDir, dto.TransactionId.ToString("N"));
        Directory.CreateDirectory(attachmentDir);

        var fileName = SanitizeAttachmentFileName(dto.FileName, dto.Id);
        var storedPath = Path.Combine(attachmentDir, fileName);
        var content = dto.FileContent ?? [];
        File.WriteAllBytes(storedPath, content);

        if (!string.IsNullOrWhiteSpace(existingPath) &&
            !string.Equals(existingPath, storedPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(existingPath))
        {
            try { File.Delete(existingPath); } catch { /* ignore rename cleanup failure */ }
        }

        return storedPath;
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

    private static string NormalizeRentalAssetNaturalKey(string? value)
        => (value ?? string.Empty).Trim();

    private async Task UpsertPulledInvoicesAsync(IReadOnlyList<InvoiceDto> dtos, CancellationToken ct)
    {
        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.Invoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == local.Id, ct);

            if (existing is null)
            {
                _db.Invoices.Add(local);
            }
            else if (!existing.IsDirty)
            {
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
                    else
                        _db.Entry(exPay).CurrentValues.SetValues(pay);
                }
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyPulledPurgeRecordsAsync(
        IReadOnlyList<RecycleBinPurgeRecordDto> dtos,
        CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        foreach (var dto in dtos
                     .Where(current => current.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(current.Kind))
                     .GroupBy(current => (NormalizePurgeRecordKind(current.Kind), current.EntityId))
                     .Select(group => group
                         .OrderByDescending(current => current.PurgedAtUtc)
                         .ThenByDescending(current => current.Revision)
                         .First())
                     .OrderBy(current => GetPurgeApplyOrder(NormalizePurgeRecordKind(current.Kind))))
        {
            var kind = NormalizePurgeRecordKind(dto.Kind);
            if (!TryParseRecycleBinEntityKind(kind, out var entityKind))
                continue;

            var result = await _local.ApplyServerPurgeRecycleBinEntryAsync(entityKind, dto.EntityId, ct);
            if (!result.Success && !result.NotFound)
                AppLogger.Warn("SYNC", $"서버 영구삭제 반영 실패: {kind} / {dto.EntityId:D} / {result.Message}");
        }
    }

    private static string NormalizePurgeRecordKind(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static int GetPurgeApplyOrder(string normalizedKind)
        => normalizedKind switch
        {
            "payment" => 0,
            "transaction" => 1,
            "rental-billing-log" => 2,
            "rentalbillinglog" => 2,
            "contract" => 3,
            "invoice" => 4,
            "rental-asset" => 5,
            "rentalasset" => 5,
            "item" => 6,
            "rental-billing-profile" => 7,
            "rentalbillingprofile" => 7,
            "customer" => 8,
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
            case "invoice":
                kind = RecycleBinEntityKind.Invoice;
                return true;
            case "payment":
                kind = RecycleBinEntityKind.Payment;
                return true;
            case "transaction":
                kind = RecycleBinEntityKind.Transaction;
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _administrativeBusinessCacheRefreshLock.Dispose();
        _dispatcher.SyncRequested -= HandleSyncRequested;
        _timer?.Dispose();

        lock (_immediateSyncGate)
        {
            _immediateSyncCts?.Cancel();
            _immediateSyncCts?.Dispose();
            _immediateSyncCts = null;
            _currentSyncTask = null;
            _resyncRequested = false;
            _flushRequested = false;
        }
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
