using System.IO;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Background sync service: push local dirty rows, then pull latest rows.
/// </summary>
public sealed class SyncService : IDisposable
{
    private const int MaxRetryCount = 3;
    private const string DisableServerSyncEnvironmentKey = "GEORAEPLAN_DISABLE_SERVER_SYNC";
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DebouncedSyncDelay = TimeSpan.FromSeconds(15);

    private readonly LocalDbContext _db;
    private readonly LocalStateService _local;
    private readonly RentalStateService _rental;
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly SyncRequestDispatcher _dispatcher;
    private readonly SyncDiagnosticsService _diagnostics;
    private readonly object _immediateSyncGate = new();
    private Timer? _timer;
    private CancellationTokenSource? _immediateSyncCts;
    private Task<bool>? _currentSyncTask;
    private bool _resyncRequested;
    private bool _flushRequested;
    private bool _disposed;
    private DateTime _lastSyncStartedUtc = DateTime.MinValue;
    private DateTime _lastSyncCompletedUtc = DateTime.MinValue;

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

            await ExecuteWithRetryAsync(PushDirtyAsync, "업로드", ct);
            await ExecuteWithRetryAsync(PullNewAsync, "다운로드", ct);

            await TrySetSettingSafeAsync("Sync.LastSuccessAt", DateTime.Now.ToString("O"), CancellationToken.None);
            await TrySetSettingSafeAsync("Sync.LastError", string.Empty, CancellationToken.None);
            await _diagnostics.ResolveOpenIssuesAsync(ct: CancellationToken.None);
            _lastSyncCompletedUtc = DateTime.UtcNow;
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

            if (await _local.CountDirtyAsync(_session, ct) == 0 && HasRecentSuccessfulSync(TimeSpan.FromSeconds(30)))
            {
                AppLogger.Info("SYNC", "로컬 변경 없는 중복 즉시 동기화 요청을 건너뜁니다.");
                return;
            }

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

    private async Task PushDirtyAsync(CancellationToken ct)
    {
        var customerMasterRepair = await _local.RepairDirtyCustomerMastersForSyncAsync(_session, ct);
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

        var customerRepair = await _local.RepairDirtyCustomersForSyncAsync(_session, ct);
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

        var scopedDirtyRentalAssetIds = (await _local.GetDirtyRentalAssetsForSyncAsync(_session, ct))
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

        var transactionRepair = await _local.RepairDirtyTransactionsForSyncAsync(_session, ct);
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

        var invoiceRepair = await _local.RepairDirtyInvoicesForSyncAsync(_session, ct);
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

        var transactionAttachmentRepair = await _local.RepairDirtyTransactionAttachmentsForSyncAsync(_session, ct);
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

        var paymentRepair = await _local.RepairDirtyPaymentsForSyncAsync(_session, ct);
        if (paymentRepair.MarkedDeletedMissingInvoiceCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 결제 참조 보정: scanned={paymentRepair.ScannedCount}, " +
                $"deletedMissingInvoicePayments={paymentRepair.MarkedDeletedMissingInvoiceCount}");
        }

        var companyProfilesTask = _db.CompanyProfiles.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .AsNoTracking()
            .ToListAsync(ct);
        var unitsTask = _db.Units.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .AsNoTracking()
            .ToListAsync(ct);
        var customerCategoriesTask = _db.CustomerCategories.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .AsNoTracking()
            .ToListAsync(ct);
        var priceGradeOptionsTask = _db.PriceGradeOptions.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .AsNoTracking()
            .ToListAsync(ct);
        var tradeTypeOptionsTask = _db.TradeTypeOptions.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .AsNoTracking()
            .ToListAsync(ct);
        var itemCategoryOptionsTask = _db.ItemCategoryOptions.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .AsNoTracking()
            .ToListAsync(ct);
        var customerMastersTask = _local.GetDirtyCustomerMastersForSyncAsync(_session, ct);
        var customersTask = _local.GetDirtyCustomersForSyncAsync(_session, ct);
        var customerContractsTask = _local.GetDirtyCustomerContractsForSyncAsync(_session, ct);
        var itemsTask = _local.GetDirtyItemsForSyncAsync(_session, ct);
        var itemWarehouseStocksTask = _db.ItemWarehouseStocks
            .AsNoTracking()
            .ToListAsync(ct);
        var transactionsTask = _local.GetDirtyTransactionsForSyncAsync(_session, ct);
        var transactionAttachmentsTask = _local.GetDirtyTransactionAttachmentsForSyncAsync(_session, ct);
        var inventoryTransfersTask = _local.GetDirtyInventoryTransfersForSyncAsync(_session, ct);
        var rentalManagementCompaniesTask = _db.RentalManagementCompanies.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .AsNoTracking()
            .ToListAsync(ct);
        var rentalBillingProfilesTask = _local.GetDirtyRentalBillingProfilesForSyncAsync(_session, ct);
        var rentalAssetsTask = _local.GetDirtyRentalAssetsForSyncAsync(_session, ct);
        var rentalBillingLogsTask = _local.GetDirtyRentalBillingLogsForSyncAsync(_session, ct);
        var invoicesTask = _local.GetDirtyInvoicesForSyncAsync(_session, ct);
        var paymentsTask = _local.GetDirtyPaymentsForSyncAsync(_session, ct);

        await Task.WhenAll(
            companyProfilesTask,
            unitsTask,
            customerCategoriesTask,
            priceGradeOptionsTask,
            tradeTypeOptionsTask,
            itemCategoryOptionsTask,
            customerMastersTask,
            customersTask,
            customerContractsTask,
            itemsTask,
            itemWarehouseStocksTask,
            transactionsTask,
            transactionAttachmentsTask,
            inventoryTransfersTask,
            rentalManagementCompaniesTask,
            rentalBillingProfilesTask,
            rentalAssetsTask,
            rentalBillingLogsTask,
            invoicesTask,
            paymentsTask);

        var companyProfiles = (await companyProfilesTask).Select(LocalMappings.ToDto).ToList();
        var units = (await unitsTask).Select(LocalMappings.ToDto).ToList();
        var customerCategories = (await customerCategoriesTask).Select(LocalMappings.ToDto).ToList();
        var priceGradeOptions = (await priceGradeOptionsTask).Select(LocalMappings.ToDto).ToList();
        var tradeTypeOptions = (await tradeTypeOptionsTask).Select(LocalMappings.ToDto).ToList();
        var itemCategoryOptions = (await itemCategoryOptionsTask).Select(LocalMappings.ToDto).ToList();
        var customerMasters = (await customerMastersTask).Select(LocalMappings.ToDto).ToList();
        var customers = (await customersTask).Select(LocalMappings.ToDto).ToList();
        var customerContracts = (await customerContractsTask).Select(LocalMappings.ToDto).ToList();
        var items = (await itemsTask).Select(LocalMappings.ToDto).ToList();
        var itemWarehouseStocks = (await itemWarehouseStocksTask).Select(LocalMappings.ToDto).ToList();
        var transactions = (await transactionsTask).Select(LocalMappings.ToDto).ToList();
        var transactionAttachments = (await transactionAttachmentsTask)
            .Select(entity => LocalMappings.ToDto(entity, ReadTransactionAttachmentContent(entity)))
            .ToList();
        var inventoryTransfers = (await inventoryTransfersTask).Select(LocalMappings.ToDto).ToList();
        var dirtyRentalManagementCompanies = await rentalManagementCompaniesTask;
        var dirtyRentalBillingProfiles = await rentalBillingProfilesTask;
        var dirtyRentalAssets = await rentalAssetsTask;
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
        var rentalBillingLogs = (await rentalBillingLogsTask).Select(LocalMappings.ToDto).ToList();
        var invoices = (await invoicesTask).Select(LocalMappings.ToDto).ToList();
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
        var payments = (await paymentsTask).Select(LocalMappings.ToDto).ToList();

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

        var hasDirty = req.CompanyProfiles.Count +
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
                       req.Payments.Count > 0;
        if (!hasDirty)
            return;

        var result = await _api.PushAsync(req, ct);
        if (result is null)
            return;

        if (result.ConflictCount > 0)
        {
            var serverNewerConflicts = result.Conflicts
                .Where(conflict => string.Equals(conflict.Reason, "Server version is newer.", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (serverNewerConflicts.Count > 0)
            {
                await ResolveServerNewerConflictsAsync(serverNewerConflicts, ct);
                AppLogger.Warn("SYNC", $"서버 최신 버전 우선으로 충돌 {serverNewerConflicts.Count}건을 정리했습니다.");
            }

            var unresolvedConflicts = result.Conflicts
                .Except(serverNewerConflicts)
                .ToList();

            if (unresolvedConflicts.Count > 0)
            {
                var first = unresolvedConflicts.FirstOrDefault();
                var detail = first is null
                    ? $"동기화 충돌 {unresolvedConflicts.Count}건"
                    : $"동기화 충돌 {unresolvedConflicts.Count}건: {first.EntityName} {first.EntityId} - {first.Reason}";

                AppLogger.Warn("SYNC", detail);
                await TryRecordDiagnosticAsync("push", detail, severity: "Error");
                throw new InvalidOperationException(detail);
            }
        }

        foreach (var assigned in result.AssignedInvoiceNumbers)
        {
            var inv = await _db.Invoices.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == assigned.Key, ct);
            if (inv is not null)
            {
                inv.InvoiceNumber = assigned.Value;
                inv.IsDirty = false;
            }
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

        await _db.SaveChangesAsync(ct);
    }

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

        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkCleanAsync<T>(IReadOnlyCollection<Guid> ids, CancellationToken ct) where T : class, ILocalSyncEntity
    {
        if (ids.Count == 0)
            return;

        await _db.Set<T>().IgnoreQueryFilters()
            .Where(e => ids.Contains(e.Id) && e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    private async Task MarkServerNewerConflictsCleanAsync<T>(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where T : class, ILocalSyncEntity
    {
        await _db.Set<T>().IgnoreQueryFilters()
            .Where(entity => ids.Contains(entity.Id) && entity.IsDirty)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsDirty, false), ct);
    }

    private async Task MarkCleanInvoicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;

        await _db.Invoices.IgnoreQueryFilters()
            .Where(e => ids.Contains(e.Id) && e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    private async Task MarkCleanInventoryTransfersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;

        await _db.InventoryTransfers.IgnoreQueryFilters()
            .Where(e => ids.Contains(e.Id) && e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    private async Task PullNewAsync(CancellationToken ct)
    {
        var revStr = await _local.GetSettingAsync("LastSyncRevision", ct) ?? "0";
        var sinceRev = long.TryParse(revStr, out var r) ? r : 0L;

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
            AppLogger.Warn("SYNC", $"증분 pull 반영 중 동시성 충돌이 발생해 전체 캐시 재구성을 시도합니다: {ex.Message}");
            await TryRecordDiagnosticAsync(
                phase: "pull",
                rawMessage: $"증분 pull 반영 중 동시성 충돌: {ex.Message}",
                exception: ex,
                severity: "Warning",
                recoveryAttempted: true,
                recoverySucceeded: false);
            _db.ChangeTracker.Clear();

            if (!await TryRefreshSharedMirrorCoreAsync(ct))
                throw;
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task ApplyPullAsync(SyncPullResponse pull, long sinceRev, CancellationToken ct)
    {
        await UpsertPulledAsync(pull.CompanyProfiles, _db.CompanyProfiles, LocalMappings.ToLocal, ct);
        await UpsertPulledUnitsAsync(pull.Units, ct);
        await UpsertPulledAsync(pull.CustomerCategories, _db.CustomerCategories, LocalMappings.ToLocal, ct);
        await UpsertPulledSelectionOptionsAsync(pull.PriceGradeOptions, _db.PriceGradeOptions, LocalMappings.ToLocal, option => option.Name, ct);
        await UpsertPulledSelectionOptionsAsync(pull.TradeTypeOptions, _db.TradeTypeOptions, LocalMappings.ToLocal, option => option.Name, ct);
        await UpsertPulledSelectionOptionsAsync(pull.ItemCategoryOptions, _db.ItemCategoryOptions, LocalMappings.ToLocal, option => option.Name, ct);
        await UpsertPulledAsync(pull.CustomerMasters, _db.CustomerMasters, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.Customers, _db.Customers, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.CustomerContracts, _db.CustomerContracts, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.Items, _db.Items, LocalMappings.ToLocal, ct);
        await UpsertPulledItemWarehouseStocksAsync(pull.ItemWarehouseStocks, ct);
        await UpsertPulledAsync(pull.Transactions, _db.Transactions, LocalMappings.ToLocal, ct);
        await UpsertPulledTransactionAttachmentsAsync(pull.TransactionAttachments, ct);
        await UpsertPulledInventoryTransfersAsync(pull.InventoryTransfers, ct);
        await UpsertPulledRentalManagementCompaniesAsync(pull.RentalManagementCompanies, ct);
        await UpsertPulledRentalBillingProfilesAsync(pull.RentalBillingProfiles, ct);
        await UpsertPulledRentalAssetsAsync(pull.RentalAssets, ct);
        await UpsertPulledAsync(pull.RentalBillingLogs, _db.RentalBillingLogs, LocalMappings.ToLocal, ct);
        await UpsertPulledInvoicesAsync(pull.Invoices, ct);
        await UpsertPulledAsync(pull.Payments, _db.Payments, LocalMappings.ToLocal, ct);
        await ApplyPulledPurgeRecordsAsync(pull.PurgeRecords, ct);

        if (pull.LatestRevision > sinceRev)
            await TrySetSettingSafeAsync("LastSyncRevision", pull.LatestRevision.ToString(), ct);
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

    private async Task UpsertPulledUnitsAsync(
        IReadOnlyList<UnitDto> dtos,
        CancellationToken ct)
    {
        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

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

        var now = DateTime.UtcNow;
        var activeGroups = await _db.Units.IgnoreQueryFilters()
            .Where(unit => !unit.IsDeleted && unit.IsActive)
            .OrderBy(unit => unit.CreatedAtUtc)
            .ThenBy(unit => unit.Name)
            .ToListAsync(ct);

        foreach (var group in activeGroups
                     .GroupBy(unit => UnitCatalogNormalizer.Normalize(unit.Name), StringComparer.Ordinal)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key)))
        {
            var canonicalName = group.Key;
            var canonical = group
                .OrderByDescending(unit => string.Equals(unit.Name, canonicalName, StringComparison.Ordinal))
                .ThenByDescending(unit => unit.Revision)
                .ThenBy(unit => unit.CreatedAtUtc)
                .ThenBy(unit => unit.Id)
                .First();

            if (!string.Equals(canonical.Name, canonicalName, StringComparison.Ordinal))
            {
                canonical.Name = canonicalName;
                canonical.UpdatedAtUtc = now;
            }

            foreach (var duplicate in group.Where(unit => unit.Id != canonical.Id))
                _db.Units.Remove(duplicate);
        }

        await _db.SaveChangesAsync(ct);
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

    private async Task UpsertPulledRentalBillingProfilesAsync(
        IReadOnlyList<RentalBillingProfileDto> dtos,
        CancellationToken ct)
    {
        var skippedIncomingIds = await RemoveStalePulledRentalBillingProfileConflictsAsync(dtos, ct);

        foreach (var dto in dtos)
        {
            if (skippedIncomingIds.Contains(dto.Id))
                continue;

            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(profile => profile.Id == local.Id, ct);
            if (existing is null)
            {
                _db.RentalBillingProfiles.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
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

        var profileKeys = incomingByProfileKey.Keys.ToList();
        var candidates = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(profile => profileKeys.Contains(profile.ProfileKey))
            .ToListAsync(ct);

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

    private static string NormalizeRentalBillingProfileNaturalKey(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private async Task UpsertPulledSelectionOptionsAsync<TLocal, TDto>(
        IReadOnlyList<TDto> dtos,
        DbSet<TLocal> set,
        Func<TDto, TLocal> toLocal,
        Func<TLocal, string> nameSelector,
        CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
        where TDto : class
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

        var existingEntities = await set.IgnoreQueryFilters().ToListAsync(ct);

        foreach (var local in incomingOptions)
        {
            var normalizedName = NormalizeOptionName(nameSelector(local));
            if (string.IsNullOrWhiteSpace(normalizedName))
                continue;

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
                foreach (var conflict in conflictingEntities)
                {
                    conflict.IsDeleted = true;
                    conflict.IsDirty = false;
                    conflict.UpdatedAtUtc = local.UpdatedAtUtc;
                    conflict.Revision = local.Revision;
                    SetOptionalBoolProperty(conflict, "IsActive", false);
                }

                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
                existingEntities = await set.IgnoreQueryFilters().ToListAsync(ct);

                AppLogger.Warn(
                    "SYNC",
                    $"선택옵션 pull 충돌 복구: {typeof(TLocal).Name} '{nameSelector(local)}' 이름 충돌 {conflictingEntities.Count}건을 정리했습니다.");
            }

            var existing = existingEntities.FirstOrDefault(entity => entity.Id == local.Id);
            if (existing is null)
            {
                set.Add(local);
                existingEntities.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }

        await _db.SaveChangesAsync(ct);
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
            "contract" => 2,
            "invoice" => 3,
            "item" => 4,
            "customer" => 5,
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
