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
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly SyncRequestDispatcher _dispatcher;
    private readonly object _immediateSyncGate = new();
    private Timer? _timer;
    private CancellationTokenSource? _immediateSyncCts;
    private Task<bool>? _currentSyncTask;
    private bool _resyncRequested;
    private bool _flushRequested;

    public event Action<string>? SyncStatusChanged;

    public SyncService(
        LocalDbContext db,
        LocalStateService local,
        ErpApiClient api,
        SessionState session,
        SyncRequestDispatcher dispatcher)
    {
        _db = db;
        _local = local;
        _api = api;
        _session = session;
        _dispatcher = dispatcher;
        _dispatcher.SyncRequested += HandleSyncRequested;
    }

    public void Start(TimeSpan interval, bool runImmediately = false)
    {
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
        => StartSyncAsync(waitForRunningSync: false, ct);

    public async Task<bool> FlushPendingChangesAsync(CancellationToken ct = default)
    {
        if (!_session.IsLoggedIn)
            return false;

        CancelPendingImmediateSync();

        var attempts = 0;
        while (attempts < 3)
        {
            attempts++;
            var synced = await StartSyncAsync(waitForRunningSync: true, ct);
            var dirtyCount = await _local.CountDirtyAsync(ct);
            if (dirtyCount == 0)
                return synced;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return await _local.CountDirtyAsync(ct) == 0;
    }

    public async Task<bool> RefreshSharedMirrorFromServerAsync(CancellationToken ct = default)
    {
        if (!_session.IsLoggedIn || _session.IsOfflineMode)
            return false;

        if (await _local.CountDirtyAsync(ct) > 0)
            return false;

        SetStatus("중앙 서버 기준 캐시를 다시 불러오는 중...");

        var pull = await _api.PullAsync(0, ct);
        if (pull is null)
            return false;

        await _local.ResetSharedMirrorCacheAsync(ct);
        await ApplyPullAsync(pull, 0L, ct);
        await _local.SetSettingAsync("Sync.LastSuccessAt", DateTime.Now.ToString("O"), CancellationToken.None);
        SetStatus($"중앙 서버 기준 캐시 재구성 완료 {DateTime.Now:HH:mm:ss}");
        return true;
    }

    private Task<bool> StartSyncAsync(bool waitForRunningSync, CancellationToken ct)
    {
        if (!_session.IsLoggedIn)
            return Task.FromResult(false);

        lock (_immediateSyncGate)
        {
            if (_currentSyncTask is not null && !_currentSyncTask.IsCompleted)
                return waitForRunningSync ? _currentSyncTask : Task.FromResult(false);

            var syncTask = RunSyncCoreAsync(ct);
            _currentSyncTask = syncTask;
            _ = syncTask.ContinueWith(
                completedTask =>
                {
                    CancellationTokenSource? rerunCts = null;
                    var rerunImmediately = false;
                    lock (_immediateSyncGate)
                    {
                        if (ReferenceEquals(_currentSyncTask, completedTask))
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
                    }
                    else if (rerunImmediately)
                    {
                        _ = StartSyncAsync(waitForRunningSync: true, CancellationToken.None);
                    }
                    else
                    {
                        var succeeded = completedTask.Status == TaskStatus.RanToCompletion && completedTask.Result;
                        _dispatcher.CompleteSync(succeeded);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return syncTask;
        }
    }

    private async Task<bool> RunSyncCoreAsync(CancellationToken ct)
    {
        try
        {
            SetStatus("동기화 중...");
            AppLogger.Info("SYNC", "동기화 시작");

            await ExecuteWithRetryAsync(PushDirtyAsync, "업로드", ct);
            await ExecuteWithRetryAsync(PullNewAsync, "다운로드", ct);

            await _local.SetSettingAsync("Sync.LastSuccessAt", DateTime.Now.ToString("O"), CancellationToken.None);
            SetStatus($"동기화 완료 {DateTime.Now:HH:mm:ss}");
            AppLogger.Info("SYNC", "동기화 완료");
            return true;
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            if (detail.Length > 220)
                detail = detail[..220] + "...";

            await _local.SetSettingAsync(
                "Sync.LastError",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {detail}",
                CancellationToken.None);

            SetStatus($"동기화 오류: {detail}");
            AppLogger.Error("SYNC", "동기화 실패", ex);
            return false;
        }
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
        if (!_session.IsLoggedIn || _session.IsOfflineMode)
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
        try
        {
            await Task.Delay(DebouncedSyncDelay, ct);
            await StartSyncAsync(waitForRunningSync: true, ct);
        }
        catch (OperationCanceledException)
        {
            // newer local change arrived; debounce in progress
        }
        catch (Exception ex)
        {
            AppLogger.Error("SYNC", "즉시 동기화 실패", ex);
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
        var req = new SyncPushRequest
        {
            CompanyProfiles = await _db.CompanyProfiles.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Units = await _db.Units.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            CustomerCategories = await _db.CustomerCategories.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            PriceGradeOptions = await _db.PriceGradeOptions.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            TradeTypeOptions = await _db.TradeTypeOptions.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            ItemCategoryOptions = await _db.ItemCategoryOptions.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            CustomerMasters = await _db.CustomerMasters.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Customers = await _db.Customers.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            CustomerContracts = await _db.CustomerContracts.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Items = await _db.Items.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            ItemWarehouseStocks = await _db.ItemWarehouseStocks
                .AsNoTracking()
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Transactions = await _db.Transactions.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            TransactionAttachments = await _db.TransactionAttachments.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(e => LocalMappings.ToDto(e, ReadTransactionAttachmentContent(e))).ToList(), ct),

            InventoryTransfers = await _db.InventoryTransfers.IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            RentalManagementCompanies = await _db.RentalManagementCompanies.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            RentalBillingProfiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            RentalAssets = await _db.RentalAssets.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            RentalBillingLogs = await _db.RentalBillingLogs.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Invoices = await _db.Invoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Payments = await _db.Payments.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct)
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

        await MarkCleanAsync<LocalCompanyProfile>(ct);
        await MarkCleanAsync<LocalUnit>(ct);
        await MarkCleanAsync<LocalCustomerCategory>(ct);
        await MarkCleanAsync<LocalPriceGradeOption>(ct);
        await MarkCleanAsync<LocalTradeTypeOption>(ct);
        await MarkCleanAsync<LocalItemCategoryOption>(ct);
        await MarkCleanAsync<LocalCustomerMaster>(ct);
        await MarkCleanAsync<LocalCustomer>(ct);
        await MarkCleanAsync<LocalCustomerContract>(ct);
        await MarkCleanAsync<LocalItem>(ct);
        await MarkCleanAsync<LocalTransaction>(ct);
        await MarkCleanAsync<LocalTransactionAttachment>(ct);
        await MarkCleanInventoryTransfersAsync(ct);
        await MarkCleanAsync<LocalRentalManagementCompany>(ct);
        await MarkCleanAsync<LocalRentalBillingProfile>(ct);
        await MarkCleanAsync<LocalRentalAsset>(ct);
        await MarkCleanAsync<LocalRentalBillingLog>(ct);
        await MarkCleanInvoicesAsync(ct);
        await MarkCleanAsync<LocalPayment>(ct);

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

    private async Task MarkCleanAsync<T>(CancellationToken ct) where T : class, ILocalSyncEntity
    {
        await _db.Set<T>().IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    private async Task MarkServerNewerConflictsCleanAsync<T>(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where T : class, ILocalSyncEntity
    {
        await _db.Set<T>().IgnoreQueryFilters()
            .Where(entity => ids.Contains(entity.Id) && entity.IsDirty)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsDirty, false), ct);
    }

    private async Task MarkCleanInvoicesAsync(CancellationToken ct)
    {
        await _db.Invoices.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    private async Task MarkCleanInventoryTransfersAsync(CancellationToken ct)
    {
        await _db.InventoryTransfers.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    private async Task PullNewAsync(CancellationToken ct)
    {
        var revStr = await _local.GetSettingAsync("LastSyncRevision", ct) ?? "0";
        var sinceRev = long.TryParse(revStr, out var r) ? r : 0L;

        var pull = await _api.PullAsync(sinceRev, ct);
        if (pull is null)
            return;

        await ApplyPullAsync(pull, sinceRev, ct);
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
            await _local.SetSettingAsync("LastSyncRevision", pull.LatestRevision.ToString(), ct);
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
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.InventoryTransfers.IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .FirstOrDefaultAsync(transfer => transfer.Id == local.Id, ct);

            if (existing is null)
            {
                _db.InventoryTransfers.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);

                foreach (var line in local.Lines)
                {
                    var existingLine = existing.Lines.FirstOrDefault(current => current.Id == line.Id);
                    if (existingLine is null)
                        existing.Lines.Add(line);
                    else
                        _db.Entry(existingLine).CurrentValues.SetValues(line);
                }

                foreach (var existingLine in existing.Lines.Where(line => !local.Lines.Any(current => current.Id == line.Id)))
                    existingLine.IsDeleted = true;
            }
        }

        await _db.SaveChangesAsync(ct);
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
}
