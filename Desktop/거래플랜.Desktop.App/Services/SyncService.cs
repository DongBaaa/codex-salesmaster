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
        if (_disposed)
            return;

        _timer?.Dispose();
        var normalizedInterval = interval <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : interval;
        var due = runImmediately ? TimeSpan.Zero : normalizedInterval;
        _timer = new Timer(_ => _ = TrySyncAsync(), null,
            due, normalizedInterval);
    }

    public void Start(int intervalMinutes = 5, bool runImmediately = false)
    {
        Start(TimeSpan.FromMinutes(intervalMinutes), runImmediately);
    }

    public Task<bool> TrySyncAsync(CancellationToken ct = default)
        => _disposed ? Task.FromResult(false) : StartSyncAsync(waitForRunningSync: false, ct);

    public async Task<bool> FlushPendingChangesAsync(CancellationToken ct = default)
    {
        if (_disposed || !_session.IsLoggedIn)
            return false;

        CancelPendingImmediateSync();

        var attempts = 0;
        while (attempts < 3)
        {
            attempts++;
            var synced = await StartSyncAsync(waitForRunningSync: true, ct);
            var dirtyCount = await _local.CountDirtyAsync(_session, ct);
            if (dirtyCount == 0)
                return synced;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return await _local.CountDirtyAsync(_session, ct) == 0;
    }

    public async Task<bool> RefreshSharedMirrorFromServerAsync(CancellationToken ct = default)
    {
        if (_disposed || !_session.IsLoggedIn || _session.IsOfflineMode)
            return false;

        if (await _local.CountDirtyAsync(_session, ct) > 0)
            return false;

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

        lock (_immediateSyncGate)
        {
            if (_currentSyncTask is not null && !_currentSyncTask.IsCompleted)
                return waitForRunningSync ? _currentSyncTask : Task.FromResult(false);

            var syncTask = RunSyncCoreAsync(ct);
            _currentSyncTask = syncTask;
            _ = FinalizeSyncAsync(syncTask);
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
            _ = RunDeferredImmediateSyncAsync(rerunCts.Token);
            return;
        }

        if (rerunImmediately)
        {
            _ = StartSyncAsync(waitForRunningSync: true, CancellationToken.None);
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
            _ = StartSyncAsync(waitForRunningSync: true, CancellationToken.None);
        else if (cts is not null)
            _ = RunDeferredImmediateSyncAsync(cts.Token);
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
        if (customerMasterRepair.MarkedCleanOutOfScopeCount > 0 ||
            customerMasterRepair.ClearedMissingCategoryCount > 0 ||
            customerMasterRepair.NormalizedScopeCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 거래처 기준정보 보정: scanned={customerMasterRepair.ScannedCount}, " +
                $"normalizedScope={customerMasterRepair.NormalizedScopeCount}, " +
                $"clearedMissingCategory={customerMasterRepair.ClearedMissingCategoryCount}, " +
                $"clearedOutOfScopeDirty={customerMasterRepair.MarkedCleanOutOfScopeCount}");
        }

        var customerRepair = await _local.RepairDirtyCustomersForSyncAsync(_session, ct);
        if (customerRepair.MarkedCleanOutOfScopeCount > 0 ||
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
                $"clearedOutOfScopeDirty={customerRepair.MarkedCleanOutOfScopeCount}");
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
            invoiceRepair.MarkedCleanOutOfScopeCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 전표 참조 보정: scanned={invoiceRepair.ScannedCount}, " +
                $"resolvedCustomers={invoiceRepair.ResolvedMissingCustomerCount}, " +
                $"clearedOutOfScopeDirty={invoiceRepair.MarkedCleanOutOfScopeCount}");
        }

        var transactionAttachmentRepair = await _local.RepairDirtyTransactionAttachmentsForSyncAsync(_session, ct);
        if (transactionAttachmentRepair.MarkedDeletedMissingTransactionCount > 0 ||
            transactionAttachmentRepair.MarkedCleanStaleDeletedCount > 0 ||
            transactionAttachmentRepair.MarkedCleanOutOfScopeCount > 0)
        {
            AppLogger.Warn(
                "SYNC",
                $"동기화 전 증빙 참조 보정: scanned={transactionAttachmentRepair.ScannedCount}, " +
                $"markedDeletedMissingTransaction={transactionAttachmentRepair.MarkedDeletedMissingTransactionCount}, " +
                $"cleanedStaleDeleted={transactionAttachmentRepair.MarkedCleanStaleDeletedCount}, " +
                $"clearedOutOfScopeDirty={transactionAttachmentRepair.MarkedCleanOutOfScopeCount}");
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
        var rentalManagementCompanies = (await rentalManagementCompaniesTask).Select(LocalMappings.ToDto).ToList();
        var rentalBillingProfiles = (await rentalBillingProfilesTask).Select(LocalMappings.ToDto).ToList();
        var rentalAssets = (await rentalAssetsTask).Select(LocalMappings.ToDto).ToList();
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
    }

    private async Task ApplyPullAsync(SyncPullResponse pull, long sinceRev, CancellationToken ct)
    {
        await UpsertPulledAsync(pull.CompanyProfiles, _db.CompanyProfiles, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.Units, _db.Units, LocalMappings.ToLocal, ct);
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
        await UpsertPulledAsync(pull.RentalManagementCompanies, _db.RentalManagementCompanies, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.RentalBillingProfiles, _db.RentalBillingProfiles, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.RentalAssets, _db.RentalAssets, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.RentalBillingLogs, _db.RentalBillingLogs, LocalMappings.ToLocal, ct);
        await UpsertPulledInvoicesAsync(pull.Invoices, ct);
        await UpsertPulledAsync(pull.Payments, _db.Payments, LocalMappings.ToLocal, ct);

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

    private async Task UpsertPulledSelectionOptionsAsync<TLocal, TDto>(
        IReadOnlyList<TDto> dtos,
        DbSet<TLocal> set,
        Func<TDto, TLocal> toLocal,
        Func<TLocal, string> nameSelector,
        CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
        where TDto : class
    {
        var existingEntities = await set.IgnoreQueryFilters().ToListAsync(ct);

        foreach (var dto in dtos)
        {
            var local = toLocal(dto);
            local.IsDirty = false;

            var existing = existingEntities.FirstOrDefault(entity => entity.Id == local.Id);
            if (existing is null)
            {
                var normalizedName = NormalizeOptionName(nameSelector(local));
                var existingByName = existingEntities.FirstOrDefault(entity =>
                    string.Equals(NormalizeOptionName(nameSelector(entity)), normalizedName, StringComparison.CurrentCultureIgnoreCase));

                if (existingByName is not null)
                {
                    if (existingByName.IsDirty)
                        continue;

                    existingByName.IsDeleted = true;
                    existingByName.IsDirty = false;
                    _db.Entry(existingByName).CurrentValues[nameof(ILocalSyncEntity.UpdatedAtUtc)] = local.UpdatedAtUtc;
                    _db.Entry(existingByName).CurrentValues[nameof(ILocalSyncEntity.Revision)] = local.Revision;

                    set.Add(local);
                    existingEntities.Add(local);
                    continue;
                }

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
