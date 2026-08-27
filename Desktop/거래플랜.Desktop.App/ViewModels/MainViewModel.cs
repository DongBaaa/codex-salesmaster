using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

internal readonly record struct InvoiceLedgerCacheKey(Guid? CustomerId, DateOnly? From, DateOnly? To)
{
    public string ToOperationDetail()
    {
        var customerText = CustomerId.HasValue && CustomerId.Value != Guid.Empty
            ? CustomerId.Value.ToString("D")
            : "all";
        var fromText = From?.ToString("yyyy-MM-dd") ?? "min";
        var toText = To?.ToString("yyyy-MM-dd") ?? "max";
        return $"customer={customerText}, from={fromText}, to={toText}";
    }
}

internal static class InvoiceLedgerCacheStore
{
    internal const int MaxEntries = 32;

    internal static void Set<TKey, TValue>(Dictionary<TKey, TValue> cache, TKey key, TValue value)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(cache);

        if (!cache.ContainsKey(key) && cache.Count >= MaxEntries)
            cache.Clear();

        cache[key] = value;
    }
}

internal sealed class InvoiceLedgerScreenCache
{
    private readonly Dictionary<InvoiceLedgerCacheKey, IReadOnlyList<LocalInvoiceListSummary>> _invoiceSummaryCache = new();
    private readonly Dictionary<InvoiceLedgerCacheKey, IReadOnlyList<LocalTransaction>> _standaloneTransactionCache = new();
    private readonly Dictionary<Guid, CustomerFinancialSummary> _financialSummaryCache = new();

    public void Clear()
    {
        _invoiceSummaryCache.Clear();
        _standaloneTransactionCache.Clear();
        _financialSummaryCache.Clear();
    }

    public async Task<(IReadOnlyList<LocalInvoiceListSummary> Value, bool CacheHit)> GetInvoiceSummariesAsync(
        InvoiceLedgerCacheKey key,
        bool forceReload,
        Func<Task<List<LocalInvoiceListSummary>>> loader)
        => await GetOrLoadAsync(_invoiceSummaryCache, key, forceReload, async () => (IReadOnlyList<LocalInvoiceListSummary>)await loader());

    public async Task<(IReadOnlyList<LocalTransaction> Value, bool CacheHit)> GetStandaloneTransactionsAsync(
        InvoiceLedgerCacheKey key,
        bool forceReload,
        Func<Task<List<LocalTransaction>>> loader)
        => await GetOrLoadAsync(_standaloneTransactionCache, key, forceReload, async () => (IReadOnlyList<LocalTransaction>)await loader());

    public async Task<(CustomerFinancialSummary Value, bool CacheHit)> GetCustomerFinancialSummaryAsync(
        Guid customerId,
        bool forceReload,
        Func<Task<CustomerFinancialSummary>> loader)
        => await GetOrLoadAsync(_financialSummaryCache, customerId, forceReload, loader);

    private static async Task<(TValue Value, bool CacheHit)> GetOrLoadAsync<TKey, TValue>(
        Dictionary<TKey, TValue> cache,
        TKey key,
        bool forceReload,
        Func<Task<TValue>> loader)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(loader);

        if (!forceReload && cache.TryGetValue(key, out var cached))
            return (cached, true);

        var value = await loader();
        InvoiceLedgerCacheStore.Set(cache, key, value);
        return (value, false);
    }
}

internal readonly record struct SyncStatusCompositionIdentity(
    Guid SessionId,
    long SyncScopeEpoch,
    string TenantCode,
    string OfficeCode,
    string BusinessDatabaseName);

internal sealed class SyncStatusCompositionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;

    internal long BeginRequest()
        => Interlocked.Increment(ref _generation);

    internal void Invalidate()
        => Interlocked.Increment(ref _generation);

    internal void ApplyAuthoritative(Action assignment)
    {
        Invalidate();
        assignment();
    }

    internal bool IsCurrent(
        long generation,
        SyncStatusCompositionIdentity expectedIdentity,
        SyncStatusCompositionIdentity currentIdentity)
        => generation == Volatile.Read(ref _generation) &&
           expectedIdentity == currentIdentity;

    internal async Task RunAsync(Func<Task> operation)
    {
        await _gate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SyncService _sync;
    private readonly BackupService _backup;
    private readonly RentalStateService _rental;
    private readonly SyncDiagnosticsService _diagnostics;
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly SyncStatusCompositionCoordinator _syncStatusCompositionCoordinator = new();
    private bool _applyingComposedSyncStatus;
    private bool _applyingAuthoritativeSyncStatus;
    private readonly IPrintService _invoicePrintService = new WpfInvoicePrintService();
    private static readonly JsonSerializerOptions PrintModelJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RecentPostLoginSyncSkipWindow = TimeSpan.FromMinutes(2);
    private readonly LegacyDataMigrationService _legacyMigrationService;
    private readonly object _customerAutoSaveGate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _customerAutoSaveCtsByCustomer = [];
    private readonly HashSet<Task> _activeCustomerAutoSaveTasks = [];
    private readonly SemaphoreSlim _customerInlineDataGate;
    private readonly Dictionary<Guid, CustomerInlineEditPatch> _pendingCustomerInlineEdits = [];
    private readonly CustomerInlineSaveStateTracker _customerInlineSaveState = new();
    private bool _customerInlineBusinessTransitionInProgress;
    private int _customerFinancialPreviewVersion;
    private readonly BackgroundTaskTracker _ownerScopeBackgroundWork = new();
    private readonly object _customerFinancialPreviewTaskGate = new();
    private CancellationTokenSource? _customerFinancialPreviewCts;
    private Task _customerFinancialPreviewTask = Task.CompletedTask;
    private bool _shutdownBackgroundWorkCancellationRequested;
    private int _invoicePreviewVersion;
    private CancellationTokenSource? _invoicePreviewCts;
    private int _invoiceFilterApplyVersion;
    private readonly UiDebouncer _invoiceFilterDebouncer = new();
    private readonly UiDebouncer _customerFilterDebouncer = new();
    private readonly SemaphoreSlim _invoiceListLoadGate = new(1, 1);
    private readonly InvoiceLedgerScreenCache _invoiceLedgerCache = new();
    private readonly Dictionary<InvoiceRowCacheKey, IReadOnlyList<InvoiceListRow>> _invoiceRowCache = new();
    private bool _dashboardMetricsLoaded;
    private CancellationTokenSource? _invoiceFilterApplyCts;
    private Task _invoiceFilterApplyTask = Task.CompletedTask;
    private CancellationTokenSource? _invoiceListLoadCts;
    private const string LegacySourceDbPathSettingKey = "LegacyMigration.SourceDbPath";
    private const string LegacyCustomerExcelPathSettingKey = "LegacyMigration.CustomerExcelPath";
    private const string LegacyItemExcelPathSettingKey = "LegacyMigration.ItemExcelPath";
    private static readonly TimeSpan DetailedInvoiceTimingInfoThreshold = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan DetailedInvoiceTimingWarningThreshold = TimeSpan.FromMilliseconds(700);

    private readonly record struct InvoiceRowCacheKey(
        Guid? CustomerId,
        DateOnly? From,
        DateOnly? To,
        string OfficeFilterCode,
        string VoucherTypeFilter,
        string CustomerNameFilter,
        string MinAmountFilter,
        string MaxAmountFilter);


    // Status bar
    [ObservableProperty] private string _syncStatus = "동기화 대기";
    [ObservableProperty] private string _currentUserDisplay = string.Empty;

    // Tabs
    [ObservableProperty] private int _selectedTabIndex;

    // Dashboard card metrics
    [ObservableProperty] private decimal _dashboardMonthlySales;
    [ObservableProperty] private decimal _dashboardReceivable;
    [ObservableProperty] private decimal _dashboardPayable;
    [ObservableProperty] private int _dashboardCustomerCount;
    [ObservableProperty] private int _dashboardSafetyStockAlerts;
    [ObservableProperty] private int _dashboardMonthlyInvoiceCount;
    [ObservableProperty] private decimal _dashboardMonthlyAverageSales;
    [ObservableProperty] private int _dashboardRentalDueTodayCount;
    [ObservableProperty] private int _dashboardRentalUpcomingCount;
    [ObservableProperty] private int _dashboardRentalOverdueCount;
    [ObservableProperty] private string _rentalAlertPopupMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DashboardSalesMetricToggleText))]
    [NotifyPropertyChangedFor(nameof(DashboardSummaryColumnCount))]
    [NotifyPropertyChangedFor(nameof(ShowDashboardExpandedSalesCards))]
    private bool _dashboardSalesMetricsExpanded = true;
    public string DashboardSalesMetricToggleText => DashboardSalesMetricsExpanded ? "매출/평균 접기" : "매출/평균 펼치기";
    public bool CanViewDashboardSalesCards => _session.HasAdministrativePrivileges;
    public bool ShowDashboardSalesMetricToggle => CanViewDashboardSalesCards;
    public bool ShowDashboardExpandedSalesCards => CanViewDashboardSalesCards && DashboardSalesMetricsExpanded;
    public int DashboardSummaryColumnCount => CanViewDashboardSalesCards
        ? (DashboardSalesMetricsExpanded ? 8 : 6)
        : 5;

    // 전표 목록 - Left panel (거래처 필터)
    private List<LocalCustomer> _allCustomers = new();
    private Dictionary<Guid, string> _customerNameById = new();
    public ObservableCollection<LocalCustomer> FilteredCustomers { get; } = new ResettableObservableCollection<LocalCustomer>();
    [ObservableProperty] private string _customerFilterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCustomer))]
    [NotifyPropertyChangedFor(nameof(IsPreviewCustomerInfoReadOnly))]
    [NotifyPropertyChangedFor(nameof(InvoicePrimaryColumnHeader))]
    private LocalCustomer? _selectedCustomerFilter;
    public bool HasSelectedCustomer => SelectedCustomerFilter is not null;
    public bool IsPreviewCustomerInfoReadOnly => !HasSelectedCustomer;
    public string InvoicePrimaryColumnHeader => HasSelectedCustomer ? "거래내역" : "거래처";

    // 거래처 인라인 편집 (우측 패널)
    private bool _suppressCustomerSave;
    [ObservableProperty] private string _editCustBizNumber = string.Empty;
    [ObservableProperty] private string _editCustPhone = string.Empty;
    [ObservableProperty] private string _editCustDept = string.Empty;
    [ObservableProperty] private string _editCustContactPerson = string.Empty;
    [ObservableProperty] private string _editCustAddress = string.Empty;
    [ObservableProperty] private string _editCustNotes = string.Empty;
    [ObservableProperty] private string _customerInlineSaveStatus = "거래처를 선택하면 빠른 수정 상태가 표시됩니다.";

    partial void OnEditCustBizNumberChanged(string value)
        => TriggerCustomerAutoSave(CustomerInlineFieldMask.BusinessNumber);
    partial void OnEditCustPhoneChanged(string value)
        => TriggerCustomerAutoSave(CustomerInlineFieldMask.Phone);
    partial void OnEditCustDeptChanged(string value)
        => TriggerCustomerAutoSave(CustomerInlineFieldMask.Department);
    partial void OnEditCustContactPersonChanged(string value)
        => TriggerCustomerAutoSave(CustomerInlineFieldMask.ContactPerson);
    partial void OnEditCustAddressChanged(string value)
        => TriggerCustomerAutoSave(CustomerInlineFieldMask.Address);
    partial void OnEditCustNotesChanged(string value)
        => TriggerCustomerAutoSave(CustomerInlineFieldMask.Notes);

    private void TriggerCustomerAutoSave(CustomerInlineFieldMask changedField)
    {
        if (_suppressCustomerSave)
            return;

        lock (_customerFinancialPreviewTaskGate)
        {
            if (_shutdownBackgroundWorkCancellationRequested)
                return;
        }

        var customer = SelectedCustomerFilter;
        if (customer is null)
            return;

        var scope = CaptureCustomerInlineEditScopeIdentity();
        CustomerInlineEditableFields baseline;
        long baseRevision;
        CustomerInlineFieldMask changedFields;
        lock (_customerAutoSaveGate)
        {
            if (_customerInlineBusinessTransitionInProgress)
            {
                CustomerInlineSaveStatus =
                    "업체 DB 전환 중에는 거래처 정보를 수정할 수 없습니다. 전환 완료 후 다시 입력해 주세요.";
                return;
            }

            if (_pendingCustomerInlineEdits.TryGetValue(customer.Id, out var pending) &&
                pending.Scope == scope)
            {
                baseline = pending.Baseline;
                baseRevision = pending.BaseRevision;
                changedFields = pending.ChangedFields | changedField;
            }
            else
            {
                baseline = CustomerInlineEditableFields.Capture(customer);
                baseRevision = customer.Revision;
                changedFields = changedField;
            }
        }

        var patch = new CustomerInlineEditPatch(
            customer.Id,
            customer.NameOriginal,
            baseRevision,
            scope,
            baseline,
            new CustomerInlineEditableFields(
                EditCustBizNumber,
                EditCustPhone,
                EditCustDept,
                EditCustContactPerson,
                EditCustAddress,
                EditCustNotes),
            changedFields);
        QueueCustomerAutoSave(patch, TimeSpan.FromMilliseconds(350));
    }

    private void QueueCustomerAutoSave(
        CustomerInlineEditPatch patch,
        TimeSpan delay)
    {
        var customerId = patch.CustomerId;
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation;
        int generation;

        lock (_customerAutoSaveGate)
        {
            if (_customerInlineBusinessTransitionInProgress)
            {
                cancellation.Dispose();
                return;
            }

            generation = _customerInlineSaveState.Begin(
                customerId,
                patch.Label);
            _customerAutoSaveCtsByCustomer.TryGetValue(
                customerId,
                out previousCancellation);
            _customerAutoSaveCtsByCustomer[customerId] = cancellation;
            _pendingCustomerInlineEdits[customerId] = patch;
        }

        if (previousCancellation is not null)
        {
            try
            {
                previousCancellation.Cancel();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "AUTOSAVE",
                    $"Customer inline auto-save cancellation failed for '{patch.Label}'.",
                    ex);
            }
        }

        SetCustomerInlineSaveStatus(
            customerId,
            "거래처 정보 변경 감지 - 잠시 후 자동저장합니다.");

        Task? task;
        try
        {
            task = _ownerScopeBackgroundWork.TryStart(
                () => AutoSaveCustomerAsync(
                    patch,
                    generation,
                    cancellation,
                    delay));
        }
        catch (Exception ex)
        {
            _customerInlineSaveState.MarkFailure(customerId, generation);
            CompleteCustomerAutoSaveAttempt(customerId, cancellation);
            SetCustomerInlineSaveStatus(
                customerId,
                $"거래처 정보 자동저장 시작 실패: {ex.Message}");
            AppLogger.Error(
                "AUTOSAVE",
                $"Customer inline auto-save could not start for '{patch.Label}'.",
                ex);
            return;
        }

        if (task is null)
        {
            _customerInlineSaveState.MarkFailure(customerId, generation);
            CompleteCustomerAutoSaveAttempt(customerId, cancellation);
            SetCustomerInlineSaveStatus(
                customerId,
                "거래처 정보 자동저장을 시작하지 못했습니다. 다시 시도해 주세요.");
            AppLogger.Warn(
                "AUTOSAVE",
                $"Customer inline auto-save tracking was closed for '{patch.Label}'.");
            return;
        }

        TrackCustomerAutoSaveTask(task);
        UiTaskHelper.Forget(
            task,
            "MAIN",
            "거래처 인라인 자동저장",
            ex => AppLogger.Warn(
                "AUTOSAVE",
                $"Customer inline auto-save failed for '{patch.Label}': {ex.Message}"));
    }

    private async Task AutoSaveCustomerAsync(
        CustomerInlineEditPatch patch,
        int generation,
        CancellationTokenSource cancellation,
        TimeSpan delay)
    {
        var customerId = patch.CustomerId;
        var cancellationToken = cancellation.Token;
        var gateEntered = false;

        try
        {
            await Task.Delay(delay, cancellationToken);

            if (!_customerInlineSaveState.IsLatest(customerId, generation))
                return;

            await _customerInlineDataGate.WaitAsync(cancellationToken);
            gateEntered = true;

            if (!_customerInlineSaveState.IsLatest(customerId, generation))
                return;

            CustomerInlineEditPatch? effectivePatch;
            lock (_customerAutoSaveGate)
            {
                if (!_customerInlineSaveState.IsLatest(customerId, generation) ||
                    !_pendingCustomerInlineEdits.TryGetValue(customerId, out effectivePatch) ||
                    effectivePatch is null)
                {
                    return;
                }
            }

            SetCustomerInlineSaveStatus(customerId, "거래처 정보 저장 중...");
            var savedCustomer = await CommitCustomerInlineEditPatchAsync(
                effectivePatch,
                CancellationToken.None);

            if (_customerInlineSaveState.MarkSuccess(customerId, generation))
            {
                RemovePendingCustomerInlineEdit(customerId, effectivePatch);
                ApplyCustomerInlineSaveResult(savedCustomer);
                SetCustomerInlineSaveStatus(
                    customerId,
                    $"거래처 정보 저장됨 · {DateTime.Now:HH:mm:ss}");
            }
            else
            {
                RebasePendingCustomerInlineEdit(
                    customerId,
                    effectivePatch.Scope,
                    savedCustomer);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested &&
                  !_customerInlineSaveState.IsLatest(customerId, generation))
        {
            // A newer edit for this customer superseded this attempt.
        }
        catch (Exception ex)
        {
            if (_customerInlineSaveState.MarkFailure(customerId, generation))
            {
                SetCustomerInlineSaveStatus(
                    customerId,
                    $"거래처 정보 자동저장 실패: {ex.Message}");
            }

            throw;
        }
        finally
        {
            if (gateEntered)
                _customerInlineDataGate.Release();

            CompleteCustomerAutoSaveAttempt(customerId, cancellation);
        }
    }

    private async Task<LocalCustomer> CommitCustomerInlineEditPatchAsync(
        CustomerInlineEditPatch patch,
        CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            using var commitLease = await _session
                .AcquireSyncScopeCommitLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
            if (patch.Scope != CaptureCustomerInlineEditScopeIdentityWithLeaseHeld())
            {
                throw new InvalidOperationException(
                    "거래처 편집을 시작한 뒤 로그인 또는 업체 DB 범위가 변경되어 자동저장을 중단했습니다.");
            }

            using var serviceScope = _serviceScopeFactory?.CreateScope();
            var local = serviceScope?.ServiceProvider.GetRequiredService<LocalStateService>() ?? _local;
            var current = await local
                .GetCustomerAsync(patch.CustomerId, _session, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"자동저장할 거래처 '{patch.Label}'을(를) 현재 업체 DB에서 찾을 수 없습니다.");

            var merge = CustomerInlineEditPatchMerge.TryMerge(current, patch);
            if (!merge.Succeeded)
            {
                var fields = string.Join(
                    ", ",
                    merge.ConflictingFields.Select(GetCustomerInlineFieldDisplayName));
                throw new InvalidOperationException(
                    $"다른 PC에서도 같은 거래처 항목이 변경되었습니다: {fields}. " +
                    "최신 값을 확인한 뒤 원하는 값을 다시 입력해 주세요.");
            }

            current.NameMatchKey = current.NameOriginal.ToUpperInvariant();
            var result = await local
                .UpsertCustomerAsync(current, _session, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Message)
                        ? "Customer inline auto-save was rejected."
                        : result.Message);
            }

            return current;
        }, cancellationToken);
    }

    private static string GetCustomerInlineFieldDisplayName(string fieldName)
        => fieldName switch
        {
            nameof(LocalCustomer.BusinessNumber) => "사업자번호",
            nameof(LocalCustomer.Phone) => "전화번호",
            nameof(LocalCustomer.Department) => "부서",
            nameof(LocalCustomer.ContactPerson) => "담당자",
            nameof(LocalCustomer.Address) => "주소",
            nameof(LocalCustomer.Notes) => "메모",
            _ => fieldName
        };

    private CustomerInlineEditScopeIdentity CaptureCustomerInlineEditScopeIdentity()
    {
        using var scopeLease = _session.AcquireSyncScopeSnapshotLease();
        return CaptureCustomerInlineEditScopeIdentityWithLeaseHeld();
    }

    private CustomerInlineEditScopeIdentity CaptureCustomerInlineEditScopeIdentityWithLeaseHeld()
        => new(
            _session.SessionId,
            _session.SyncScopeEpoch,
            _session.TenantCode,
            _session.OfficeCode,
            _session.BusinessOfficeCode,
            _session.ScopeType,
            _session.SelectedBusinessDatabaseName);

    private void ApplyCustomerInlineSaveResult(LocalCustomer savedCustomer)
    {
        var savedFields = CustomerInlineEditableFields.Capture(savedCustomer);
        foreach (var target in _allCustomers.Where(customer => customer.Id == savedCustomer.Id))
            ApplyCustomerInlineSaveResult(target, savedCustomer, savedFields);

        if (SelectedCustomerFilter is { } selected &&
            selected.Id == savedCustomer.Id &&
            !_allCustomers.Any(customer => ReferenceEquals(customer, selected)))
        {
            ApplyCustomerInlineSaveResult(selected, savedCustomer, savedFields);
        }
    }

    private static void ApplyCustomerInlineSaveResult(
        LocalCustomer target,
        LocalCustomer savedCustomer,
        CustomerInlineEditableFields savedFields)
    {
        CustomerInlineEditPatchMerge.Overlay(target, savedFields);
        target.Revision = savedCustomer.Revision;
        target.CreatedAtUtc = savedCustomer.CreatedAtUtc;
        target.UpdatedAtUtc = savedCustomer.UpdatedAtUtc;
        target.IsDirty = savedCustomer.IsDirty;
        target.IsDeleted = savedCustomer.IsDeleted;
        target.NameMatchKey = savedCustomer.NameMatchKey;
    }

    private void TrackCustomerAutoSaveTask(Task task)
    {
        lock (_customerAutoSaveGate)
            _activeCustomerAutoSaveTasks.Add(task);

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_customerAutoSaveGate)
                    _activeCustomerAutoSaveTasks.Remove(completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainCustomerAutoSaveTasksAsync()
    {
        while (true)
        {
            Task[] activeTasks;
            lock (_customerAutoSaveGate)
            {
                _activeCustomerAutoSaveTasks.RemoveWhere(task => task.IsCompleted);
                activeTasks = _activeCustomerAutoSaveTasks.ToArray();
            }

            if (activeTasks.Length == 0)
                return;

            try
            {
                await Task.WhenAll(activeTasks);
            }
            catch
            {
                // Per-customer failure state retains the actionable error.
            }
        }
    }

    private void CompleteCustomerAutoSaveAttempt(
        Guid customerId,
        CancellationTokenSource cancellation)
    {
        lock (_customerAutoSaveGate)
        {
            if (_customerAutoSaveCtsByCustomer.TryGetValue(customerId, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _customerAutoSaveCtsByCustomer.Remove(customerId);
            }
        }

        cancellation.Dispose();
    }

    private void RemovePendingCustomerInlineEdit(
        Guid customerId,
        CustomerInlineEditPatch patch)
    {
        lock (_customerAutoSaveGate)
        {
            if (_pendingCustomerInlineEdits.TryGetValue(customerId, out var current) &&
                ReferenceEquals(current, patch))
            {
                _pendingCustomerInlineEdits.Remove(customerId);
            }
        }
    }

    private void RebasePendingCustomerInlineEdit(
        Guid customerId,
        CustomerInlineEditScopeIdentity savedScope,
        LocalCustomer savedCustomer)
    {
        lock (_customerAutoSaveGate)
        {
            if (!_pendingCustomerInlineEdits.TryGetValue(customerId, out var pending) ||
                pending.Scope != savedScope)
            {
                return;
            }

            _pendingCustomerInlineEdits[customerId] =
                CustomerInlineEditPatchMerge.RebaseAfterSupersededSave(
                    pending,
                    savedCustomer);
        }
    }

    private CustomerInlineEditPatch[] SnapshotPendingCustomerInlineEdits()
    {
        lock (_customerAutoSaveGate)
            return _pendingCustomerInlineEdits.Values.ToArray();
    }

    private bool HasPendingCustomerInlineEdits
    {
        get
        {
            lock (_customerAutoSaveGate)
                return _pendingCustomerInlineEdits.Count > 0;
        }
    }

    private void SetCustomerInlineSaveStatus(Guid customerId, string status)
    {
        if (SelectedCustomerFilter?.Id == customerId)
            CustomerInlineSaveStatus = status;
    }

    private void RetryUnresolvedCustomerInlineSave(Guid customerId)
    {
        if (!_customerInlineSaveState.SnapshotUnresolvedFailures()
                .Any(failure => failure.CustomerId == customerId))
        {
            return;
        }

        lock (_customerFinancialPreviewTaskGate)
        {
            if (_shutdownBackgroundWorkCancellationRequested)
                return;
        }

        CustomerInlineEditPatch? patch;
        lock (_customerAutoSaveGate)
        {
            if (_customerAutoSaveCtsByCustomer.ContainsKey(customerId) ||
                _customerInlineBusinessTransitionInProgress ||
                !_pendingCustomerInlineEdits.TryGetValue(customerId, out patch))
            {
                return;
            }
        }

        QueueCustomerAutoSave(patch, TimeSpan.Zero);
    }

    // 전표 목록 - Bottom panel (선택한 전표 라인 미리보기)
    public ObservableCollection<InvoiceLineEditModel> PreviewLines { get; } = new();
    [ObservableProperty] private decimal _previewSupplyAmount;
    [ObservableProperty] private decimal _previewVatAmount;
    [ObservableProperty] private decimal _previewTotalAmount;

    // 전표 목록 - Right panel (거래처 정보 미리보기)
    [ObservableProperty] private string _previewCustomerName = string.Empty;
    [ObservableProperty] private string _previewCustomerBizNumber = string.Empty;
    [ObservableProperty] private string _previewCustomerPhone = string.Empty;
    [ObservableProperty] private string _previewCustomerAddress = string.Empty;
    [ObservableProperty] private string _previewCustomerNotes = string.Empty;
    [ObservableProperty] private string _previewCustomerDepartment = string.Empty;
    [ObservableProperty] private string _previewCustomerContactPerson = string.Empty;

    // Invoice List (전표 목록)
    public ObservableCollection<InvoiceListRow> InvoiceRows { get; } = new ResettableObservableCollection<InvoiceListRow>();
    public ObservableCollection<FavoriteInvoiceQuickItem> FavoriteInvoices { get; } = new ResettableObservableCollection<FavoriteInvoiceQuickItem>();
    [ObservableProperty] private InvoiceListRow? _selectedInvoiceRow;
    [ObservableProperty] private FavoriteInvoiceQuickItem? _selectedFavoriteInvoice;
    [ObservableProperty] private DateOnly _filterFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateOnly _filterTo = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _filterCustomerName = string.Empty;
    [ObservableProperty] private string _selectedVoucherTypeFilter = "전체";
    [ObservableProperty] private string _filterMinAmountText = string.Empty;
    [ObservableProperty] private string _filterMaxAmountText = string.Empty;
    public IReadOnlyList<string> VoucherTypeFilterOptions { get; } = ["전체", "매출", "매입", "발주", "경비", "수금"];
    private bool _suppressFilterAutoSave;
    private DateOnly _invoiceDefaultFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateOnly _invoiceDefaultTo = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _invoiceLegacyMonthDefaultTo = DateOnly.FromDateTime(DateTime.Today);
    private const string InvoiceFilterCustomerSettingKey = "InvoiceFilter.CustomerName";
    private const string InvoiceFilterVoucherTypeSettingKey = "InvoiceFilter.VoucherType";
    private const string InvoiceFilterOfficeCodeSettingKey = "InvoiceFilter.OfficeCode";
    private const string InvoiceFilterMinAmountSettingKey = "InvoiceFilter.MinAmount";
    private const string InvoiceFilterMaxAmountSettingKey = "InvoiceFilter.MaxAmount";
    private const string FavoriteInvoiceIdsSettingKey = "InvoiceFavorites.Ids";

    // Invoice Editor (전표 작성)
    [ObservableProperty] private Guid _editInvoiceId = Guid.NewGuid();
    [ObservableProperty] private LocalCustomer? _editCustomer;
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private DateOnly _editInvoiceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private VoucherType _editVoucherType = VoucherType.Sales;
    [ObservableProperty] private string _editMemo = string.Empty;
    [ObservableProperty] private decimal _editTotalAmount;
    [ObservableProperty] private decimal _editSupplyAmount;
    [ObservableProperty] private decimal _editVatAmount;
    [ObservableProperty] private string _editVatMode = InvoiceVatModes.Included;
    private string _editConcurrencyStamp = string.Empty;
    public ObservableCollection<InvoiceLineEditModel> EditLines { get; } = new();
    public Array VoucherTypes => Enum.GetValues<VoucherType>();

    // Payment Tab (수금 입력)
    [ObservableProperty] private InvoiceListRow? _paymentInvoice;
    public ObservableCollection<PaymentRowModel> PaymentRows { get; } = new();
    [ObservableProperty] private decimal _paymentTotalPaid;
    [ObservableProperty] private decimal _paymentBalance;

    // Statement tab (거래명세서)
    [ObservableProperty] private InvoiceListRow? _statementInvoice;

    // Company settings (회사 설정)
    [ObservableProperty] private string _companyTradeName = string.Empty;
    [ObservableProperty] private string _companyRepresentative = string.Empty;
    [ObservableProperty] private string _companyBusinessNumber = string.Empty;
    [ObservableProperty] private string _companyBusinessType = string.Empty;
    [ObservableProperty] private string _companyBusinessItem = string.Empty;
    [ObservableProperty] private string _companyAddress = string.Empty;
    [ObservableProperty] private string _companyContactNumber = string.Empty;
    [ObservableProperty] private string _companyEmail = string.Empty;
    [ObservableProperty] private string _companyBankAccountText = string.Empty;
    [ObservableProperty] private byte[]? _companyStampImage;
    [ObservableProperty] private string _companyStampImagePath = "(없음)";
    [ObservableProperty] private string _legacySourceDbPath = string.Empty;
    [ObservableProperty] private string _legacyCustomerExcelPath = string.Empty;
    [ObservableProperty] private string _legacyItemExcelPath = string.Empty;
    [ObservableProperty] private string _legacyMigrationStatus = "원본 데이터 추출/가져오기 대기";
    private Guid _companyProfileId = Guid.NewGuid();
    private LocalCompanyProfile? _loadedCompanyProfile;

    public MainViewModel(
        LocalStateService local,
        SyncService sync,
        BackupService backup,
        RentalStateService rental,
        SyncDiagnosticsService diagnostics,
        ErpApiClient api,
        SessionState session,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        _local = local;
        _customerInlineDataGate = local.OwnerScopeDataGate;
        _sync = sync;
        _backup = backup;
        _rental = rental;
        _diagnostics = diagnostics;
        _api = api;
        _session = session;
        _serviceScopeFactory = serviceScopeFactory;
        _legacyMigrationService = new LegacyDataMigrationService(local);

        _sync.SyncStatusChanged += HandleSyncStatusChanged;
        _session.BusinessDatabaseChanged += HandleBusinessDatabaseChanged;
        RefreshCurrentUserDisplay();
    }

    public void CancelPendingBackgroundWorkForShutdown()
    {
        lock (_customerFinancialPreviewTaskGate)
            _shutdownBackgroundWorkCancellationRequested = true;

        _ownerScopeBackgroundWork.BeginShutdown();
        Interlocked.Increment(ref _customerFinancialPreviewVersion);
        Interlocked.Increment(ref _invoicePreviewVersion);
        Interlocked.Increment(ref _previewCustomerContractVersion);
        Interlocked.Increment(ref _invoiceFilterApplyVersion);
        _syncStatusCompositionCoordinator.Invalidate();

        TryRunShutdownCancellation(
            _customerFilterDebouncer.Cancel,
            "customer filter debounce");
        TryRunShutdownCancellation(
            _invoiceFilterDebouncer.Cancel,
            "invoice filter debounce");

        lock (_customerFinancialPreviewTaskGate)
        {
            TryCancelShutdownToken(
                _customerFinancialPreviewCts,
                "customer financial preview");
        }

        TryCancelShutdownToken(_invoicePreviewCts, "invoice preview");
        TryCancelShutdownToken(
            _previewCustomerContractCts,
            "customer contract preview");

        TryCancelShutdownToken(_invoiceFilterApplyCts, "invoice filter apply");

        TryCancelShutdownToken(_invoiceListLoadCts, "invoice list load");
        _invoiceListLoadCts = null;

        TryCancelShutdownToken(
            _backgroundDesktopUpdateCts,
            "background desktop update");
    }

    private static void TryCancelShutdownToken(
        CancellationTokenSource? cancellation,
        string operation)
    {
        if (cancellation is null)
            return;

        TryRunShutdownCancellation(cancellation.Cancel, operation);
    }

    private static void TryRunShutdownCancellation(
        Action cancellation,
        string operation)
    {
        try
        {
            cancellation();
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "MAIN",
                $"Shutdown cancellation callback failure: {operation}",
                ex);
        }
    }

    public async Task DrainPendingBackgroundWorkForShutdownAsync()
    {
        CancelPendingBackgroundWorkForShutdown();
        Task previewTask;
        lock (_customerFinancialPreviewTaskGate)
            previewTask = _customerFinancialPreviewTask;

        try
        {
            await Task.WhenAll(
                previewTask,
                _customerFilterDebouncer.CancelAndDrainAsync(),
                _invoiceFilterDebouncer.CancelAndDrainAsync(),
                _ownerScopeBackgroundWork.DrainAsync());
        }
        catch (OperationCanceledException)
        {
            // 종료 취소에 응답한 미리보기 조회는 정상적인 drain으로 처리합니다.
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "MAIN",
                $"종료 전 거래처 재무 미리보기 작업 확인 중 오류를 기록하고 종료 절차를 계속합니다. {ex.Message}");
        }

        finally
        {
            _invoicePreviewCts?.Dispose();
            _invoicePreviewCts = null;
            _previewCustomerContractCts?.Dispose();
            _previewCustomerContractCts = null;
            _invoiceFilterApplyCts?.Dispose();
            _invoiceFilterApplyCts = null;
            _backgroundDesktopUpdateCts?.Dispose();
            _backgroundDesktopUpdateCts = null;
        }

        // Inline customer edits are user data, not disposable UI refresh work.
        // A completed fault is retained per customer until that same customer's
        // latest captured edit is saved successfully.
        var unresolvedFailures = _customerInlineSaveState.SnapshotUnresolvedFailures();
        var pendingEdits = SnapshotPendingCustomerInlineEdits();
        if (unresolvedFailures.Count > 0 || pendingEdits.Length > 0)
        {
            var customerLabels = unresolvedFailures
                .Select(failure => failure.Label)
                .Concat(pendingEdits.Select(patch => patch.Label))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray();
            var targetText = customerLabels.Length == 0
                ? "알 수 없는 거래처"
                : string.Join(", ", customerLabels);

            throw new InvalidOperationException(
                $"거래처 정보 자동저장이 완료되지 않아 종료를 취소했습니다. 대상: {targetText}. " +
                "권한과 동기화 상태를 확인한 뒤 해당 거래처를 다시 선택하거나 저장을 다시 시도해 주세요.");
        }
    }

    public bool IsShutdownBackgroundWorkCompleted
    {
        get
        {
            lock (_customerFinancialPreviewTaskGate)
                return _customerFinancialPreviewTask.IsCompleted &&
                       _ownerScopeBackgroundWork.IsCompleted &&
                       _customerFilterDebouncer.IsIdle &&
                       _invoiceFilterDebouncer.IsIdle &&
                       !HasPendingCustomerInlineEdits &&
                       _customerInlineSaveState.SnapshotUnresolvedFailures().Count == 0;
        }
    }

    public void ResumePendingBackgroundWorkAfterShutdownCanceled()
    {
        lock (_customerFinancialPreviewTaskGate)
            _shutdownBackgroundWorkCancellationRequested = false;
        _ownerScopeBackgroundWork.Resume();

        foreach (var patch in SnapshotPendingCustomerInlineEdits())
            QueueCustomerAutoSave(patch, TimeSpan.Zero);
    }

    public async Task RunBusinessDatabaseTransitionAsync(Func<Task> transitionAsync)
    {
        ArgumentNullException.ThrowIfNull(transitionAsync);

        lock (_customerAutoSaveGate)
        {
            if (_customerInlineBusinessTransitionInProgress)
            {
                throw new InvalidOperationException(
                    "업체 DB 전환이 이미 진행 중입니다. 잠시 후 다시 시도해 주세요.");
            }

            _customerInlineBusinessTransitionInProgress = true;
        }

        var dataGateEntered = false;
        try
        {
            await QuiesceInvoiceWorkForBusinessDatabaseTransitionAsync();
            await DrainCustomerAutoSaveTasksAsync();
            ThrowIfCustomerInlineEditsIncomplete("업체 DB 전환");

            await _customerInlineDataGate.WaitAsync();
            dataGateEntered = true;
            await transitionAsync();
        }
        finally
        {
            if (dataGateEntered)
                _customerInlineDataGate.Release();

            lock (_customerAutoSaveGate)
                _customerInlineBusinessTransitionInProgress = false;
        }
    }

    private async Task QuiesceInvoiceWorkForBusinessDatabaseTransitionAsync()
    {
        Interlocked.Increment(ref _invoiceFilterApplyVersion);
        await _invoiceFilterDebouncer.CancelAndDrainAsync();

        var filterCancellation = _invoiceFilterApplyCts;
        TryCancelShutdownToken(filterCancellation, "business database transition invoice filter apply");
        TryCancelShutdownToken(_invoiceListLoadCts, "business database transition invoice list load");

        try
        {
            await _invoiceFilterApplyTask;
        }
        catch (OperationCanceledException)
        {
            // The transition intentionally cancels stale filter persistence/list work.
        }
        finally
        {
            if (ReferenceEquals(_invoiceFilterApplyCts, filterCancellation))
                _invoiceFilterApplyCts = null;
            filterCancellation?.Dispose();
            _invoiceFilterApplyTask = Task.CompletedTask;
        }
    }

    private void ThrowIfCustomerInlineEditsIncomplete(string operation)
    {
        var unresolvedFailures = _customerInlineSaveState.SnapshotUnresolvedFailures();
        var pendingEdits = SnapshotPendingCustomerInlineEdits();
        if (unresolvedFailures.Count == 0 && pendingEdits.Length == 0)
            return;

        var customerLabels = unresolvedFailures
            .Select(failure => failure.Label)
            .Concat(pendingEdits.Select(patch => patch.Label))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();
        var targetText = customerLabels.Length == 0
            ? "알 수 없는 거래처"
            : string.Join(", ", customerLabels);

        throw new InvalidOperationException(
            $"거래처 정보 자동저장이 완료되지 않아 {operation}을(를) 중단했습니다. 대상: {targetText}. " +
            "권한과 동기화 상태를 확인한 뒤 해당 거래처를 다시 선택하거나 저장을 다시 시도해 주세요.");
    }

    private bool IsBusinessDatabaseTransitionInProgress()
    {
        lock (_customerAutoSaveGate)
            return _customerInlineBusinessTransitionInProgress;
    }

    private void HandleSyncStatusChanged(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            _ = dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() => HandleSyncStatusChanged(status)));
            return;
        }

        var generation = _syncStatusCompositionCoordinator.BeginRequest();
        var identity = CaptureSyncStatusCompositionIdentity();
        var task = _ownerScopeBackgroundWork.TryStart(
            () => ApplySyncStatusAsync(status, generation, identity));
        if (task is null)
            return;
        UiTaskHelper.Forget(
            task,
            "SYNC-UI",
            "동기화 상태 표시 갱신",
            ex => AppLogger.Warn("SYNC-UI", $"동기화 상태 표시 갱신 실패: {ex.Message}"));
        AppLogger.Info("SYNC-UI", status);
    }

    public void ApplyExternalSyncStatus(string status) => HandleSyncStatusChanged(status);

    private async Task<T> RunIsolatedSyncAsync<T>(
        Func<SyncService, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_serviceScopeFactory is null)
            return await Task.Run(() => operation(_sync, ct), ct);

        return await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var scope = _serviceScopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
            sync.SyncStatusChanged += HandleSyncStatusChanged;
            try
            {
                return await operation(sync, ct).ConfigureAwait(false);
            }
            finally
            {
                sync.SyncStatusChanged -= HandleSyncStatusChanged;
                await sync.StopAndDrainAsync().ConfigureAwait(false);
            }
        }, ct);
    }

    private async Task<T> RunIsolatedLocalStateAsync<T>(Func<LocalStateService, Task<T>> operation)
    {
        if (_serviceScopeFactory is null)
            throw new InvalidOperationException("A service scope is required for isolated sync-status queries.");

        return await Task.Run(async () =>
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var local = scope.ServiceProvider.GetRequiredService<LocalStateService>();
            return await operation(local).ConfigureAwait(false);
        });
    }

    private async Task ApplySyncStatusAsync(
        string status,
        long generation,
        SyncStatusCompositionIdentity identity)
    {
        await _syncStatusCompositionCoordinator.RunAsync(async () =>
        {
            if (!IsSyncStatusCompositionCurrent(generation, identity))
                return;

            var resolvedStatus = await ComposeSyncStatusAsync(status);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(() => ApplyComposedSyncStatusIfCurrent(resolvedStatus, generation, identity));
            else
                ApplyComposedSyncStatusIfCurrent(resolvedStatus, generation, identity);
        });
    }

    private SyncStatusCompositionIdentity CaptureSyncStatusCompositionIdentity()
        => new(
            _session.SessionId,
            _session.SyncScopeEpoch,
            _session.TenantCode,
            _session.OfficeCode,
            _session.SelectedBusinessDatabaseName);

    private bool IsSyncStatusCompositionCurrent(
        long generation,
        SyncStatusCompositionIdentity identity)
        => _syncStatusCompositionCoordinator.IsCurrent(
            generation,
            identity,
            CaptureSyncStatusCompositionIdentity());

    private void ApplyComposedSyncStatusIfCurrent(
        string status,
        long generation,
        SyncStatusCompositionIdentity identity)
    {
        if (!IsSyncStatusCompositionCurrent(generation, identity))
            return;

        _applyingComposedSyncStatus = true;
        try
        {
            SyncStatus = status;
        }
        finally
        {
            _applyingComposedSyncStatus = false;
        }
    }

    private void InvalidatePendingSyncStatusComposition()
        => _syncStatusCompositionCoordinator.Invalidate();

    private void SetAuthoritativeSyncStatus(string status)
    {
        _applyingAuthoritativeSyncStatus = true;
        try
        {
            _syncStatusCompositionCoordinator.ApplyAuthoritative(() => SyncStatus = status);
        }
        finally
        {
            _applyingAuthoritativeSyncStatus = false;
        }
    }

    partial void OnSyncStatusChanging(string value)
    {
        if (!_applyingComposedSyncStatus && !_applyingAuthoritativeSyncStatus)
            InvalidatePendingSyncStatusComposition();
    }

    private async Task<string> ComposeSyncStatusAsync(string status)
    {
        if (_session.IsOfflineMode)
            return status;

        if (!status.StartsWith("동기화 완료", StringComparison.Ordinal)
            && !status.StartsWith("중앙 서버 기준 캐시 재구성 완료", StringComparison.Ordinal)
            && !IsSyncAttentionStatus(status))
        {
            return status;
        }

        if (_serviceScopeFactory is null)
            return status;

        return await RunIsolatedLocalStateAsync(async local =>
        {
            var dirtyCount = await local.CountDirtyAsync(_session);
            if (dirtyCount <= 0)
                return status;

            if (IsSyncAttentionStatus(status))
                return await local.GetPendingSyncWaitingMessageAsync(_session, $"{status} /", CancellationToken.None)
                       ?? $"{status} / 서버 반영 대기 데이터 {dirtyCount:N0}건";

            return await local.GetPendingSyncWaitingMessageAsync(_session, "동기화 작업은 완료됐지만", CancellationToken.None)
                   ?? $"동기화 작업은 완료됐지만 서버 반영 대기 데이터 {dirtyCount:N0}건이 남아 있습니다.";
        });
    }

    private static bool IsSyncAttentionStatus(string status)
        => status.StartsWith("동기화 확인 필요", StringComparison.Ordinal)
           || status.StartsWith("서버 응답 지연", StringComparison.Ordinal);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await LoadCustomersAsync(ct);
        await RefreshInvoiceDefaultDateRangeFromDataAsync(ct);
        await LoadInvoiceFilterSettingsAsync(ct);
        await LoadInvoiceListAsync(ct);
        await LoadCompanyProfileAsync(ct);
        await LoadLegacyMigrationSettingsAsync(ct);
        if (_session.IsOfflineMode)
            SetAuthoritativeSyncStatus("오프라인 모드에서는 자동 동기화를 진행하지 않습니다.");
    }

    public void SetInvoiceDefaultDateRange(DateOnly serverToday)
    {
        var invoiceLegacyMonthDefaultFrom = new DateOnly(serverToday.Year, serverToday.Month, 1);
        _invoiceLegacyMonthDefaultTo = serverToday;
        _invoiceDefaultFrom = invoiceLegacyMonthDefaultFrom;
        _invoiceDefaultTo = _invoiceLegacyMonthDefaultTo;
        FilterFrom = _invoiceDefaultFrom;
        FilterTo = _invoiceDefaultTo;
    }

    public async Task<bool> ShouldShowPostLoginSyncPopupAsync(CancellationToken ct = default)
        => await IsInitialServerDataLoadRequiredAsync(ct);

    public async Task<bool> IsInitialServerDataLoadRequiredAsync(CancellationToken ct = default)
    {
        if (_session.IsOfflineMode)
            return false;

        if (await _local.IsServerMirrorRefreshRequiredAsync(ct))
            return true;

        if (await _local.HasLikelyCorruptedPrimaryWorkCacheAsync(_session, ct))
        {
            await _local.MarkServerMirrorRefreshRequiredAsync(ct);
            return true;
        }

        if (!await _local.HasVisiblePrimaryWorkCacheAsync(_session, ct))
            return true;

        return !await HasPersistedSyncRevisionAsync(ct);
    }

    public async Task RunPostLoginSyncAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_session.IsOfflineMode)
        {
            SetAuthoritativeSyncStatus("로그인 후 서버 동기화를 진행하지 못했습니다.");
            return;
        }

        try
        {
            if (await ShouldSkipImmediatePostLoginSyncAsync(ct))
            {
                var lastSuccess = await GetLastSuccessfulSyncAtAsync(ct);
                SetAuthoritativeSyncStatus(lastSuccess.HasValue
                    ? $"최근 동기화 기록({lastSuccess.Value.ToLocalTime():HH:mm:ss})이 있어 시작 동기화는 생략했습니다."
                    : "최근 동기화 기록이 있어 시작 동기화는 생략했습니다.");
                return;
            }

            var initialDataLoadRequired = await IsInitialServerDataLoadRequiredAsync(ct);
            var shouldRefreshCurrentBusinessScope = await ShouldRefreshCurrentBusinessScopeAfterPostLoginAsync(ct);
            var dirtyBefore = await _local.CountDirtyAsync(_session, ct);
            SetAuthoritativeSyncStatus(initialDataLoadRequired
                ? "초기 데이터 동기화 중입니다. 거래처/거래내역을 서버에서 받는 동안 잠시만 기다려 주세요."
                : "로그인 후 서버 동기화 중...");

            var syncOk = await RunIsolatedSyncAsync(
                (sync, token) => sync.TrySyncAsync(token),
                ct);
            ct.ThrowIfCancellationRequested();
            var dirtyAfter = await _local.CountDirtyAsync(_session, ct);

            // 업데이트 직후 전체 캐시 재구성은 동기화 내부 복구 경로에서 완료될 수 있다.
            // 이 경우 syncOk가 false여도 DB에는 거래처/거래내역이 다시 채워질 수 있으므로
            // 반드시 메인 목록을 한 번 재조회해 빈 화면이 그대로 남지 않게 한다.
            await ReloadAfterPassiveSyncAsync(ct);
            var hasVisiblePrimaryWorkCache = await _local.HasVisiblePrimaryWorkCacheAsync(_session, ct);

            if (syncOk && dirtyAfter == 0)
            {
                var refreshOk = true;
                var currentBusinessScopeRefreshAttempted = false;
                if (shouldRefreshCurrentBusinessScope && await _local.IsServerMirrorRefreshRequiredAsync(ct))
                {
                    currentBusinessScopeRefreshAttempted = true;
                    refreshOk = await RunIsolatedSyncAsync(
                        (sync, token) => sync.RefreshCurrentBusinessScopeFromServerAsync(token),
                        ct);
                    ct.ThrowIfCancellationRequested();
                }

                if (currentBusinessScopeRefreshAttempted)
                    await ReloadAfterPassiveSyncAsync(ct);
                hasVisiblePrimaryWorkCache = await _local.HasVisiblePrimaryWorkCacheAsync(_session, ct);

                if (initialDataLoadRequired && !hasVisiblePrimaryWorkCache)
                {
                    SetAuthoritativeSyncStatus("초기 데이터 표시 확인 중입니다. 서버 기준으로 한 번 더 받습니다...");
                    var mirrorRefreshOk = await RunIsolatedSyncAsync(
                        (sync, token) => sync.RefreshSharedMirrorFromServerAsync(token),
                        ct);
                    ct.ThrowIfCancellationRequested();
                    await ReloadAfterPassiveSyncAsync(ct);
                    hasVisiblePrimaryWorkCache = await _local.HasVisiblePrimaryWorkCacheAsync(_session, ct);
                    if (mirrorRefreshOk && hasVisiblePrimaryWorkCache)
                    {
                        SetAuthoritativeSyncStatus($"초기 데이터 동기화 완료 {DateTime.Now:HH:mm:ss}");
                        return;
                    }
                }

                SetAuthoritativeSyncStatus(shouldRefreshCurrentBusinessScope && !refreshOk
                    ? "로그인 후 현재 업체 DB 캐시 재구성은 일부 실패했지만 앱은 계속 사용할 수 있습니다."
                    : $"로그인 후 서버 동기화 완료 {DateTime.Now:HH:mm:ss}");
                return;
            }

            if (dirtyAfter == 0 && hasVisiblePrimaryWorkCache)
            {
                SetAuthoritativeSyncStatus($"서버 기준 데이터 복구 완료 {DateTime.Now:HH:mm:ss}");
                return;
            }

            if (dirtyBefore > 0 || dirtyAfter > 0)
            {
                var backupOk = await _backup.BackupNowAsync(ct);
                ct.ThrowIfCancellationRequested();
                AppLogger.Warn(
                    "APP",
                    $"Post-login auto sync failed with {dirtyAfter} dirty rows. Auto-backup {(backupOk ? "succeeded" : "failed")}.");
                await _diagnostics.RecordIssueAsync(
                    phase: "post-login-sync",
                    rawMessage: $"로그인 후 자동 동기화 확인 필요. dirty={dirtyAfter}, backup={(backupOk ? "ok" : "failed")}.",
                    severity: "Warning",
                    recoveryAttempted: true,
                    recoverySucceeded: false,
                    ct: ct);
            }
            else
            {
                await _diagnostics.RecordIssueAsync(
                    phase: "post-login-sync",
                    rawMessage: "로그인 후 자동 동기화 확인 필요. dirty row는 없지만 서버 캐시 재구성 또는 네트워크 상태를 확인해야 합니다.",
                    severity: "Warning",
                    recoveryAttempted: false,
                    recoverySucceeded: false,
                    ct: ct);
            }

            if (dirtyAfter > 0)
            {
                var pendingMessage = await _local.GetPendingSyncWaitingMessageAsync(_session, ct: ct);
                SetAuthoritativeSyncStatus(string.IsNullOrWhiteSpace(pendingMessage)
                    ? $"서버 반영 대기 데이터 {dirtyAfter:N0}건이 남아 있습니다. 환경설정 > 동기화에서 확인해 주세요."
                    : $"{pendingMessage} 환경설정 > 동기화에서 확인해 주세요.");
            }
            else
            {
                SetAuthoritativeSyncStatus("동기화 확인이 지연되어 백그라운드에서 다시 확인합니다. 앱은 계속 사용할 수 있습니다.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("APP", "로그인 후 자동 동기화 확인 필요", ex);
            await _diagnostics.RecordIssueAsync(
                phase: "post-login-sync",
                rawMessage: ex.InnerException?.Message ?? ex.Message,
                exception: ex,
                severity: "Warning");
            SetAuthoritativeSyncStatus("로그인 후 서버 확인이 지연되었습니다. 백그라운드에서 다시 확인하며 앱은 계속 사용할 수 있습니다.");
        }
    }

    private async Task<bool> ShouldSkipImmediatePostLoginSyncAsync(CancellationToken ct = default)
    {
        if (await _local.IsServerMirrorRefreshRequiredAsync(ct))
            return false;

        if (await _local.HasLikelyCorruptedPrimaryWorkCacheAsync(_session, ct))
        {
            await _local.MarkServerMirrorRefreshRequiredAsync(ct);
            return false;
        }

        if (!await HasPersistedSyncRevisionAsync(ct))
            return false;

        if (!await _local.HasVisiblePrimaryWorkCacheAsync(_session, ct))
            return false;

        if (await HasServerRevisionAdvancedSinceLastSyncAsync(ct))
            return false;

        var lastSuccess = await GetLastSuccessfulSyncAtAsync(ct);
        if (!lastSuccess.HasValue || DateTimeOffset.Now - lastSuccess.Value.ToLocalTime() > RecentPostLoginSyncSkipWindow)
            return false;

        var dirtyCount = await _local.CountDirtyAsync(_session, ct);
        return dirtyCount == 0;
    }

    private async Task<bool> HasServerRevisionAdvancedSinceLastSyncAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await _api.GetSyncStatusAsync(ct);
            if (status is null || status.CurrentServerRevision <= 0)
                return false;

            var revisionRaw = await _local.GetSettingAsync("LastSyncRevision", ct);
            _ = long.TryParse(revisionRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastSyncRevision);
            return status.CurrentServerRevision > lastSyncRevision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"Post-login revision check failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ShouldRefreshCurrentBusinessScopeAfterPostLoginAsync(CancellationToken ct = default)
        => await _local.IsServerMirrorRefreshRequiredAsync(ct);

    private async Task<bool> HasPersistedSyncRevisionAsync(CancellationToken ct = default)
    {
        var revisionRaw = await _local.GetSettingAsync("LastSyncRevision", ct);
        return long.TryParse(revisionRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) && revision > 0;
    }

    private async Task<DateTimeOffset?> GetLastSuccessfulSyncAtAsync(CancellationToken ct = default)
    {
        var raw = await _local.GetSettingAsync("Sync.LastSuccessAt", ct);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;
    }

    // Customer Filter (Left Panel)
    private async Task LoadCustomersAsync(
        CancellationToken ct = default,
        bool dataGateAlreadyHeld = false)
    {
        var ownsDataGate = !dataGateAlreadyHeld;
        if (ownsDataGate)
            await _customerInlineDataGate.WaitAsync(ct);

        try
        {
            var selectedCustomerId = SelectedCustomerFilter?.Id;
            var customers = await _local.GetCustomersAsync(_session, ct);
            ApplyPendingCustomerInlineEditOverlays(customers);

            _allCustomers = customers;
            _customerNameById = _allCustomers
                .Where(customer => customer.Id != Guid.Empty)
                .GroupBy(customer => customer.Id)
                .ToDictionary(group => group.Key, group => group.First().NameOriginal);
            DashboardCustomerCount = _allCustomers.Count;
            ApplyCustomerFilter();

            if (selectedCustomerId.HasValue)
            {
                var refreshedSelection = _allCustomers.FirstOrDefault(customer => customer.Id == selectedCustomerId.Value);
                SelectedCustomerFilter = refreshedSelection;
            }
        }
        finally
        {
            if (ownsDataGate)
                _customerInlineDataGate.Release();
        }
    }

    private void ApplyPendingCustomerInlineEditOverlays(
        IReadOnlyCollection<LocalCustomer> customers)
    {
        var currentScope = CaptureCustomerInlineEditScopeIdentity();
        var pendingEdits = SnapshotPendingCustomerInlineEdits()
            .Where(patch => patch.Scope == currentScope)
            .GroupBy(patch => patch.CustomerId)
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var customer in customers)
        {
            if (pendingEdits.TryGetValue(customer.Id, out var patch))
            {
                CustomerInlineEditPatchMerge.OverlayChangedFields(
                    customer,
                    patch.Desired,
                    patch.ChangedFields);
            }
        }
    }

    private void ApplyCustomerFilter()
    {
        var text = CustomerFilterText.Trim();
        var filtered = string.IsNullOrEmpty(text)
            ? _allCustomers
            : _allCustomers.Where(c => MatchesCustomerQuickFilter(c, text));
        FilteredCustomers.ReplaceWith(filtered);
    }

    private static bool MatchesCustomerQuickFilter(LocalCustomer customer, string rawText)
    {
        var tokens = rawText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        return tokens.All(token => ContainsAnyCustomerField(customer, token));
    }

    private static bool ContainsAnyCustomerField(LocalCustomer customer, string token)
        => ContainsText(customer.NameOriginal, token)
           || ContainsText(customer.BusinessNumber, token)
           || ContainsText(customer.Phone, token)
           || ContainsText(customer.MobilePhone, token)
           || ContainsText(customer.ContactPerson, token)
           || ContainsText(customer.Department, token)
           || ContainsText(customer.TradeType, token)
           || ContainsText(customer.PriceGrade, token)
           || ContainsText(customer.ResponsibleOfficeCode, token)
           || ContainsText(customer.Address, token)
           || ContainsText(customer.DetailAddress, token)
           || ContainsText(customer.Notes, token);

    private static bool ContainsText(string? value, string token)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    partial void OnCustomerFilterTextChanged(string value)
        => _customerFilterDebouncer.Debounce(TimeSpan.FromMilliseconds(150), ApplyCustomerFilter);
    partial void OnSelectedCustomerFilterChanged(LocalCustomer? value)
    {
        _suppressCustomerSave = true;
        try
        {
            PreviewCustomerName = value?.NameOriginal ?? string.Empty;
            EditCustBizNumber = value?.BusinessNumber ?? string.Empty;
            EditCustPhone = value?.Phone ?? string.Empty;
            EditCustDept = value?.Department ?? string.Empty;
            EditCustContactPerson = value?.ContactPerson ?? string.Empty;
            EditCustAddress = value?.Address ?? string.Empty;
            EditCustNotes = value?.Notes ?? string.Empty;
            CustomerInlineSaveStatus = value is null
                ? "거래처를 선택하면 빠른 수정 상태가 표시됩니다."
                : "거래처 정보 빠른 수정 가능 - 입력칸을 벗어나면 자동저장됩니다.";
        }
        finally { _suppressCustomerSave = false; }

        if (value is not null)
            RetryUnresolvedCustomerInlineSave(value.Id);

        RequestRefreshCustomerFinancialPreview(value);
        RequestRefreshPreviewCustomerContract(value);
        HandleInvoiceFilterChanged();
    }

    partial void OnFilterFromChanged(DateOnly value) => HandleInvoiceFilterChanged();
    partial void OnFilterToChanged(DateOnly value) => HandleInvoiceFilterChanged();
    partial void OnFilterCustomerNameChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnSelectedVoucherTypeFilterChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnFilterMinAmountTextChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnFilterMaxAmountTextChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnEditVatModeChanged(string value) => RecalcTotals();

    [RelayCommand]
    private async Task ResetInvoiceFiltersAsync()
    {
        _suppressFilterAutoSave = true;
        FilterFrom = _invoiceDefaultFrom;
        FilterTo = _invoiceDefaultTo;
        FilterCustomerName = string.Empty;
        SelectedVoucherTypeFilter = "전체";
        SelectedInvoiceOfficeFilterCode = GetDefaultInvoiceOfficeFilterCode();
        FilterMinAmountText = string.Empty;
        FilterMaxAmountText = string.Empty;
        SelectedCustomerFilter = null;
        _suppressFilterAutoSave = false;

        await PersistInvoiceFiltersAsync();
        await LoadInvoiceListAsync();
    }

    [RelayCommand]
    private void ClearCustomerFilter()
    {
        CustomerFilterText = string.Empty;
        _customerFilterDebouncer.Cancel();
        SelectedCustomerFilter = null;
        ApplyCustomerFilter();
    }

    [RelayCommand]
    private void SelectRecentInvoice()
    {
        if (InvoiceRows.Count == 0)
            return;

        SelectedInvoiceRow = InvoiceRows[0];
    }

    [RelayCommand]
    private async Task ToggleInvoiceFavoriteAsync()
    {
        if (SelectedInvoiceRow is null)
        {
            System.Windows.MessageBox.Show("즐겨찾기에 등록할 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        await _customerInlineDataGate.WaitAsync();
        try
        {
            var ids = await GetFavoriteInvoiceIdsAsync();
            if (ids.Contains(SelectedInvoiceRow.Id))
                ids.Remove(SelectedInvoiceRow.Id);
            else
                ids.Insert(0, SelectedInvoiceRow.Id);

            await SaveFavoriteInvoiceIdsAsync(ids);
            await LoadInvoiceFavoritesAsync(dataGateAlreadyHeld: true);
        }
        finally
        {
            _customerInlineDataGate.Release();
        }
    }

    [RelayCommand]
    private async Task OpenFavoriteInvoiceAsync()
    {
        if (SelectedFavoriteInvoice is null)
        {
            System.Windows.MessageBox.Show("이동할 즐겨찾기 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var targetId = SelectedFavoriteInvoice.InvoiceId;
        var targetRow = InvoiceRows.FirstOrDefault(r => r.Id == targetId);

        if (targetRow is null)
        {
            LocalInvoice? invoice;
            await _customerInlineDataGate.WaitAsync();
            try
            {
                invoice = await _local.GetInvoiceAsync(targetId, _session);
            }
            finally
            {
                _customerInlineDataGate.Release();
            }

            if (invoice is null)
            {
                System.Windows.MessageBox.Show("선택한 즐겨찾기 전표를 찾을 수 없습니다.", "알림", System.Windows.MessageBoxButton.OK);
                return;
            }

            _suppressFilterAutoSave = true;
            SelectedCustomerFilter = _allCustomers.FirstOrDefault(c => c.Id == invoice.CustomerId);
            FilterCustomerName = string.Empty;
            SelectedVoucherTypeFilter = "전체";
            FilterMinAmountText = string.Empty;
            FilterMaxAmountText = string.Empty;
            FilterFrom = _invoiceDefaultFrom;
            FilterTo = _invoiceDefaultTo;
            _suppressFilterAutoSave = false;

            await PersistInvoiceFiltersAsync();
            await LoadInvoiceListAsync();
            targetRow = InvoiceRows.FirstOrDefault(r => r.Id == targetId);
        }

        if (targetRow is null)
        {
            System.Windows.MessageBox.Show("즐겨찾기 전표를 현재 목록에서 찾지 못했습니다.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        SelectedTabIndex = 0;
        SelectedInvoiceRow = targetRow;
    }

    // Invoice Preview (on selection)
    partial void OnSelectedInvoiceRowChanged(InvoiceListRow? value)
        => RequestLoadPreview(value);

    private void RequestLoadPreview(InvoiceListRow? row)
    {
        _invoicePreviewCts?.Cancel();
        _invoicePreviewCts?.Dispose();
        _invoicePreviewCts = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _invoicePreviewVersion);
        var token = _invoicePreviewCts.Token;
        var task = _ownerScopeBackgroundWork.TryStart(
            () => LoadPreviewAsync(row, version, token));
        if (task is null)
        {
            _invoicePreviewCts.Cancel();
            _invoicePreviewCts.Dispose();
            _invoicePreviewCts = null;
            return;
        }
        UiTaskHelper.Forget(
            task,
            "MAIN",
            "전표 미리보기 로드",
            ex =>
            {
                if (IsCurrentInvoicePreview(version))
                    AppLogger.Warn("MAIN", $"전표 미리보기 로드 실패: {ex.Message}");
            });
    }

    private async Task LoadPreviewAsync(
        InvoiceListRow? row,
        int version,
        CancellationToken ct)
    {
        await _customerInlineDataGate.WaitAsync(ct);
        try
        {
            await LoadPreviewCoreAsync(row, version, ct);
        }
        finally
        {
            _customerInlineDataGate.Release();
        }
    }

    private async Task LoadPreviewCoreAsync(
        InvoiceListRow? row,
        int version,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCurrentInvoicePreview(version))
            return;

        PreviewLines.Clear();
        PreviewTotalAmount = 0;
        PreviewSupplyAmount = 0;
        PreviewVatAmount = 0;

        if (row is null)
        {
            if (SelectedCustomerFilter is null)
            {
                ClearPreviewCustomerInfo();
                await RefreshCustomerFinancialPreviewAsync(
                    null,
                    ct,
                    dataGateAlreadyHeld: true);
                RequestRefreshPreviewCustomerContract(null);
            }
            return;
        }

        if (row.IsTransactionRow)
        {
            if (SelectedCustomerFilter is null)
            {
                var transactionCustomer = _allCustomers.FirstOrDefault(c => c.Id == row.CustomerId)
                    ?? await _local.GetCustomerAsync(row.CustomerId, ct);
                ct.ThrowIfCancellationRequested();
                if (!IsCurrentInvoicePreview(version))
                    return;

                if (transactionCustomer is not null)
                {
                    PreviewCustomerName = transactionCustomer.NameOriginal;
                    _suppressCustomerSave = true;
                    try
                    {
                        EditCustBizNumber = transactionCustomer.BusinessNumber;
                        EditCustPhone = transactionCustomer.Phone;
                        EditCustDept = transactionCustomer.Department;
                        EditCustContactPerson = transactionCustomer.ContactPerson;
                        EditCustAddress = transactionCustomer.Address;
                        EditCustNotes = transactionCustomer.Notes;
                    }
                    finally
                    {
                        _suppressCustomerSave = false;
                    }

                    CustomerInlineSaveStatus = "수금/지급 내역 선택 상태입니다. 거래처 정보는 복사할 수 있으며, 수정은 왼쪽 거래처를 선택한 뒤 가능합니다.";
                    await RefreshCustomerFinancialPreviewAsync(
                        transactionCustomer,
                        ct,
                        dataGateAlreadyHeld: true);
                    RequestRefreshPreviewCustomerContract(transactionCustomer);
                }
            }

            return;
        }

        var inv = await _local.GetLatestInvoiceVersionAsync(row.Id, _session, ct);
        ct.ThrowIfCancellationRequested();
        if (!IsCurrentInvoicePreview(version))
            return;

        if (inv is null)
        {
            if (SelectedCustomerFilter is null)
            {
                ClearPreviewCustomerInfo();
                await RefreshCustomerFinancialPreviewAsync(
                    null,
                    ct,
                    dataGateAlreadyHeld: true);
                RequestRefreshPreviewCustomerContract(null);
            }
            return;
        }

        foreach (var line in inv.Lines
                     .Where(l => !l.IsDeleted)
                     .OrderBy(l => l.OrderIndex > 0 ? l.OrderIndex : int.MaxValue)
                     .ThenBy(l => l.Id))
            PreviewLines.Add(InvoiceLineEditModel.FromLocal(line));

        PreviewTotalAmount = inv.TotalAmount;
        PreviewSupplyAmount = inv.SupplyAmount;
        PreviewVatAmount = inv.VatAmount;

        // 좌측 거래처가 선택되지 않은 경우에만 우측 하단 고객 정보 업데이트
        if (SelectedCustomerFilter is null)
        {
            var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId)
                ?? await _local.GetCustomerAsync(inv.CustomerId, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentInvoicePreview(version))
                return;

            if (customer is not null)
            {
                PreviewCustomerName = customer.NameOriginal;
                _suppressCustomerSave = true;
                try
                {
                    EditCustBizNumber = customer.BusinessNumber;
                    EditCustPhone = customer.Phone;
                    EditCustDept = customer.Department;
                    EditCustContactPerson = customer.ContactPerson;
                    EditCustAddress = customer.Address;
                    EditCustNotes = customer.Notes;
                }
                finally { _suppressCustomerSave = false; }
                CustomerInlineSaveStatus = "전표 선택 상태입니다. 거래처 정보는 복사 가능하며, 수정은 왼쪽 거래처를 선택한 뒤 가능합니다.";

                await RefreshCustomerFinancialPreviewAsync(
                    customer,
                    ct,
                    dataGateAlreadyHeld: true);
                RequestRefreshPreviewCustomerContract(customer);
            }
            else
            {
                ClearPreviewCustomerInfo();
                await RefreshCustomerFinancialPreviewAsync(
                    null,
                    ct,
                    dataGateAlreadyHeld: true);
                RequestRefreshPreviewCustomerContract(null);
            }
        }
    }

    private void ClearPreviewCustomerInfo()
    {
        PreviewCustomerName = string.Empty;
        PreviewCustomerBizNumber = string.Empty;
        PreviewCustomerPhone = string.Empty;
        PreviewCustomerAddress = string.Empty;
        PreviewCustomerNotes = string.Empty;
        PreviewCustomerDepartment = string.Empty;
        PreviewCustomerContactPerson = string.Empty;

        _suppressCustomerSave = true;
        try
        {
            EditCustBizNumber = string.Empty;
            EditCustPhone = string.Empty;
            EditCustDept = string.Empty;
            EditCustContactPerson = string.Empty;
            EditCustAddress = string.Empty;
            EditCustNotes = string.Empty;
        }
        finally
        {
            _suppressCustomerSave = false;
        }

        CustomerInlineSaveStatus = "거래처 또는 전표를 선택하면 거래처 정보가 표시됩니다.";
    }

    private bool IsCurrentInvoicePreview(int version)
        => version == Volatile.Read(ref _invoicePreviewVersion);

    // Invoice List
    [RelayCommand]
    private async Task LoadInvoiceListAsync()
        => await ReloadInvoiceListAsync();

    private async Task LoadInvoiceListAsync(CancellationToken ct)
        => await ReloadInvoiceListAsync(ct);

    private async Task ReloadInvoiceListAsync(CancellationToken ct = default)
    {
        await RunTrackedInvoiceListLoadAsync(forceReload: true, ct);
    }

    private Task RunTrackedInvoiceListLoadAsync(bool forceReload, CancellationToken ct)
        => _ownerScopeBackgroundWork.TryStart(
               () => LoadInvoiceListCoreAsync(
                   forceReload: forceReload,
                   cancellationToken: ct))
           ?? Task.CompletedTask;

    private async Task LoadInvoiceListCoreAsync(
        bool forceReload = false,
        CancellationToken cancellationToken = default,
        bool dataGateAlreadyHeld = false)
    {
        CancellationTokenSource? loadCts = null;
        var ct = cancellationToken;
        var dataGateEntered = false;
        var invoiceGateEntered = false;
        var previouslySelectedInvoiceId = SelectedInvoiceRow?.Id;
        var previouslySelectedVersionGroupId = SelectedInvoiceRow?.EffectiveVersionGroupId;

        try
        {
            if (!dataGateAlreadyHeld)
            {
                await _customerInlineDataGate.WaitAsync(ct);
                dataGateEntered = true;
            }

            _invoiceListLoadCts?.Cancel();
            loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _invoiceListLoadCts = loadCts;
            ct = loadCts.Token;

            await _invoiceListLoadGate.WaitAsync(ct);
            invoiceGateEntered = true;
            if (!ReferenceEquals(_invoiceListLoadCts, loadCts))
                return;

            if (forceReload)
                InvalidateInvoiceLedgerCaches();

            var overallStopwatch = Stopwatch.StartNew();
            Guid? customerId = SelectedCustomerFilter?.Id;
            var queryDateRange = ResolveMainInvoiceQueryDateRange(FilterFrom, FilterTo);
            var queryKey = new InvoiceLedgerCacheKey(customerId, queryDateRange.From, queryDateRange.To);

            var dataLoadStopwatch = Stopwatch.StartNew();
            var (invoiceList, invoiceSummaryCacheHit) = await _invoiceLedgerCache.GetInvoiceSummariesAsync(
                queryKey,
                forceReload,
                () => _local.GetInvoiceListSummariesAsync(queryDateRange.From, queryDateRange.To, customerId, _session, ct));
            var (standaloneTransactions, standaloneTransactionCacheHit) = await _invoiceLedgerCache.GetStandaloneTransactionsAsync(
                queryKey,
                forceReload,
                () => _local.GetStandaloneTransactionsForLedgerAsync(queryDateRange.From, queryDateRange.To, customerId, _session, ct));
            dataLoadStopwatch.Stop();
            OperationTiming.LogIfSlow(
                "MAIN",
                "Invoice list source load",
                dataLoadStopwatch.Elapsed,
                $"{queryKey.ToOperationDetail()}, force={forceReload}, invoiceCache={FormatCacheState(invoiceSummaryCacheHit)}, transactionCache={FormatCacheState(standaloneTransactionCacheHit)}, invoices={invoiceList.Count:N0}, transactions={standaloneTransactions.Count:N0}",
                infoThreshold: DetailedInvoiceTimingInfoThreshold,
                warningThreshold: DetailedInvoiceTimingWarningThreshold);
            if (!IsCurrentInvoiceListLoad(loadCts))
                return;

            var hiddenTextFilters = NormalizeHiddenInvoiceTextFilters(
                FilterCustomerName,
                FilterMinAmountText,
                FilterMaxAmountText);
            var rowCacheKey = BuildInvoiceRowCacheKey(customerId, queryDateRange.From, queryDateRange.To, hiddenTextFilters);
            var canReuseAsAllInvoiceSet = customerId is null && queryDateRange.From is null && queryDateRange.To is null;
            var rowMaterializationStopwatch = Stopwatch.StartNew();
            IReadOnlyList<InvoiceListRow>? cachedRows = null;
            var rowCacheHit = !forceReload && _invoiceRowCache.TryGetValue(rowCacheKey, out cachedRows);
            IReadOnlyList<InvoiceListRow> rows;
            int finalInvoiceCount;
            int finalTransactionCount;

            if (rowCacheHit && cachedRows is not null)
            {
                rows = cachedRows;
                finalInvoiceCount = rows.Count(row => !row.IsTransactionRow);
                finalTransactionCount = rows.Count(row => row.IsTransactionRow);
            }
            else
            {
                var customerMap = await BuildInvoiceCustomerNameMapAsync(
                    invoiceList,
                    standaloneTransactions.Select(transaction => transaction.CustomerId),
                    ct);
                if (!IsCurrentInvoiceListLoad(loadCts))
                    return;

                var showCustomerName = customerId is null;
                IEnumerable<LocalInvoiceListSummary> filteredInvoices = invoiceList;
                filteredInvoices = filteredInvoices.Where(MatchesSelectedInvoiceOffice);

                if (!string.IsNullOrWhiteSpace(hiddenTextFilters.CustomerName))
                {
                    var needle = hiddenTextFilters.CustomerName.Trim();
                    filteredInvoices = filteredInvoices.Where(inv =>
                    {
                        var name = customerMap.TryGetValue(inv.CustomerId, out var n) ? n : string.Empty;
                        return name.Contains(needle, StringComparison.OrdinalIgnoreCase);
                    });
                }

                VoucherType? selectedVoucherType = null;
                if (!string.Equals(SelectedVoucherTypeFilter, "전체", StringComparison.OrdinalIgnoreCase))
                {
                    selectedVoucherType = SelectedVoucherTypeFilter switch
                    {
                        "매출" => VoucherType.Sales,
                        "매입" => VoucherType.Purchase,
                        "발주" => VoucherType.Procurement,
                        "경비" => VoucherType.Expense,
                        "수금" => VoucherType.Collection,
                        _ => (VoucherType?)null
                    };

                    if (selectedVoucherType is { } type)
                        filteredInvoices = filteredInvoices.Where(inv => inv.VoucherType == type);
                }

                var minAmount = ParseAmountFilter(hiddenTextFilters.MinAmountText);
                var maxAmount = ParseAmountFilter(hiddenTextFilters.MaxAmountText);
                if (minAmount.HasValue)
                    filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount >= minAmount.Value);
                if (maxAmount.HasValue)
                    filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount <= maxAmount.Value);

                IEnumerable<LocalTransaction> filteredTransactions = standaloneTransactions
                    .Where(transaction => MatchesSelectedInvoiceOfficeCode(transaction.ResponsibleOfficeCode));
                if (!string.IsNullOrWhiteSpace(hiddenTextFilters.CustomerName))
                {
                    var needle = hiddenTextFilters.CustomerName.Trim();
                    filteredTransactions = filteredTransactions.Where(transaction =>
                    {
                        var name = customerMap.TryGetValue(transaction.CustomerId, out var n) ? n : string.Empty;
                        return name.Contains(needle, StringComparison.OrdinalIgnoreCase);
                    });
                }

                if (!string.Equals(SelectedVoucherTypeFilter, "전체", StringComparison.OrdinalIgnoreCase))
                {
                    filteredTransactions = selectedVoucherType == VoucherType.Collection
                        ? filteredTransactions
                        : Enumerable.Empty<LocalTransaction>();
                }

                if (minAmount.HasValue)
                    filteredTransactions = filteredTransactions.Where(transaction => GetStandaloneTransactionLedgerAmount(transaction) >= minAmount.Value);
                if (maxAmount.HasValue)
                    filteredTransactions = filteredTransactions.Where(transaction => GetStandaloneTransactionLedgerAmount(transaction) <= maxAmount.Value);

                var finalInvoices = filteredInvoices
                    .OrderByDescending(i => i.InvoiceDate)
                    .ThenByDescending(i => i.InvoiceNumber)
                    .ToList();
                var finalTransactions = filteredTransactions.ToList();
                finalInvoiceCount = finalInvoices.Count;
                finalTransactionCount = finalTransactions.Count;

                var invoiceRows = finalInvoices.Select(inv =>
                {
                    var custName = customerMap.TryGetValue(inv.CustomerId, out var n) ? n : "(미지정)";
                    return InvoiceListRow.From(inv, custName, showCustomerName);
                }).ToList();
                var transactionRows = finalTransactions.Select(transaction =>
                {
                    var custName = customerMap.TryGetValue(transaction.CustomerId, out var n) ? n : "(미지정)";
                    return InvoiceListRow.From(transaction, custName, showCustomerName);
                });
                rows = invoiceRows
                    .Concat(transactionRows)
                    .OrderByDescending(row => row.InvoiceDate)
                    .ThenByDescending(row => row.UpdatedAtUtc)
                    .ThenByDescending(row => row.DisplayNumber)
                    .ToList();
                InvoiceLedgerCacheStore.Set(_invoiceRowCache, rowCacheKey, rows);
            }

            rowMaterializationStopwatch.Stop();
            OperationTiming.LogIfSlow(
                "MAIN",
                "Invoice row materialization",
                rowMaterializationStopwatch.Elapsed,
                $"{queryKey.ToOperationDetail()}, force={forceReload}, rowCache={FormatCacheState(rowCacheHit)}, invoices={finalInvoiceCount:N0}, transactions={finalTransactionCount:N0}, rows={rows.Count:N0}",
                infoThreshold: DetailedInvoiceTimingInfoThreshold,
                warningThreshold: DetailedInvoiceTimingWarningThreshold);
            if (!IsCurrentInvoiceListLoad(loadCts))
                return;

            InvoiceRows.ReplaceWith(rows);
            RestoreSelectedInvoiceAfterListReload(previouslySelectedInvoiceId, previouslySelectedVersionGroupId);

            await RefreshDashboardMetricsAsync(canReuseAsAllInvoiceSet ? invoiceList : null, ct, forceReload);
            if (!IsCurrentInvoiceListLoad(loadCts))
                return;

            await LoadInvoiceFavoritesAsync(canReuseAsAllInvoiceSet ? invoiceList : null, ct, forceReload, dataGateAlreadyHeld: true);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentInvoiceListLoad(loadCts))
                return;

            await RefreshSelectedCustomerFinancialPreviewAsync(ct, dataGateAlreadyHeld: true);
            ct.ThrowIfCancellationRequested();
            overallStopwatch.Stop();
            OperationTiming.LogIfSlow(
                "MAIN",
                "Invoice list load",
                overallStopwatch.Elapsed,
                $"{queryKey.ToOperationDetail()}, force={forceReload}, invoiceCache={FormatCacheState(invoiceSummaryCacheHit)}, transactionCache={FormatCacheState(standaloneTransactionCacheHit)}, rowCache={FormatCacheState(rowCacheHit)}, rows={rows.Count:N0}",
                infoThreshold: DetailedInvoiceTimingInfoThreshold,
                warningThreshold: DetailedInvoiceTimingWarningThreshold);
        }
        catch (OperationCanceledException)
            when (loadCts is not null &&
                  ct.IsCancellationRequested &&
                  !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (invoiceGateEntered)
                _invoiceListLoadGate.Release();
            if (dataGateEntered)
                _customerInlineDataGate.Release();
            if (loadCts is not null && ReferenceEquals(_invoiceListLoadCts, loadCts))
                _invoiceListLoadCts = null;
            loadCts?.Dispose();
        }
    }

    private bool IsCurrentInvoiceListLoad(CancellationTokenSource loadCts)
        => ReferenceEquals(_invoiceListLoadCts, loadCts) && !loadCts.IsCancellationRequested;

    private void InvalidateInvoiceLedgerCaches()
    {
        _invoiceLedgerCache.Clear();
        _invoiceRowCache.Clear();
        _dashboardMetricsLoaded = false;
    }

    private async Task ReloadCustomerAndInvoiceDataAsync(CancellationToken ct = default)
    {
        await _customerInlineDataGate.WaitAsync(ct);

        try
        {
            InvalidateInvoiceLedgerCaches();
            await LoadCustomersAsync(ct, dataGateAlreadyHeld: true);
            await LoadInvoiceListCoreAsync(
                forceReload: true,
                cancellationToken: ct,
                dataGateAlreadyHeld: true);
        }
        finally
        {
            _customerInlineDataGate.Release();
        }
    }

    private InvoiceRowCacheKey BuildInvoiceRowCacheKey(
        Guid? customerId,
        DateOnly? from,
        DateOnly? to,
        (string CustomerName, string MinAmountText, string MaxAmountText) hiddenTextFilters)
        => new(
            customerId,
            from,
            to,
            NormalizeInvoiceCacheText(SelectedInvoiceOfficeFilterCode),
            NormalizeInvoiceCacheText(SelectedVoucherTypeFilter),
            NormalizeInvoiceCacheText(hiddenTextFilters.CustomerName),
            NormalizeInvoiceCacheText(hiddenTextFilters.MinAmountText),
            NormalizeInvoiceCacheText(hiddenTextFilters.MaxAmountText));

    private static string NormalizeInvoiceCacheText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string FormatCacheState(bool cacheHit)
        => cacheHit ? "hit" : "miss";

    private void RestoreSelectedInvoiceAfterListReload(Guid? previouslySelectedInvoiceId, Guid? previouslySelectedVersionGroupId)
    {
        if (!previouslySelectedInvoiceId.HasValue)
        {
            if (SelectedInvoiceRow is not null && !InvoiceRows.Any(row => row.Id == SelectedInvoiceRow.Id))
                SelectedInvoiceRow = null;
            return;
        }

        var refreshedSelection = InvoiceRows.FirstOrDefault(row => row.Id == previouslySelectedInvoiceId.Value);
        if (refreshedSelection is not null)
        {
            if (!ReferenceEquals(SelectedInvoiceRow, refreshedSelection))
                SelectedInvoiceRow = refreshedSelection;
            return;
        }

        if (previouslySelectedVersionGroupId.HasValue && previouslySelectedVersionGroupId.Value != Guid.Empty)
        {
            var latestVersionSelection = InvoiceRows.FirstOrDefault(row =>
                row.EffectiveVersionGroupId == previouslySelectedVersionGroupId.Value);
            if (latestVersionSelection is not null)
            {
                if (!ReferenceEquals(SelectedInvoiceRow, latestVersionSelection))
                    SelectedInvoiceRow = latestVersionSelection;
                return;
            }
        }

        if (SelectedInvoiceRow is not null)
            SelectedInvoiceRow = null;
    }

    public async Task<LocalInvoice?> GetLatestSelectedInvoiceAsync(CancellationToken ct = default)
    {
        var selected = SelectedInvoiceRow;
        if (selected is null)
            return null;
        if (selected.IsTransactionRow)
            return null;

        LocalInvoice? latest;
        await _customerInlineDataGate.WaitAsync(ct);
        try
        {
            latest = await _local.GetLatestInvoiceVersionAsync(selected.Id, _session, ct);
        }
        finally
        {
            _customerInlineDataGate.Release();
        }

        if (latest is null)
            return null;

        if (latest.Id != selected.Id)
        {
            await ReloadInvoiceListAsync(ct);
            var latestRow = InvoiceRows.FirstOrDefault(row => row.Id == latest.Id)
                ?? InvoiceRows.FirstOrDefault(row => row.EffectiveVersionGroupId == ResolveEffectiveVersionGroupId(latest));
            if (latestRow is not null)
                SelectedInvoiceRow = latestRow;
        }

        return latest;
    }

    private static Guid ResolveEffectiveVersionGroupId(LocalInvoice invoice)
        => invoice.VersionGroupId == Guid.Empty ? invoice.Id : invoice.VersionGroupId;

    private Task<Dictionary<Guid, string>> BuildInvoiceCustomerNameMapAsync(IEnumerable<LocalInvoiceListSummary> invoices, CancellationToken ct)
        => BuildInvoiceCustomerNameMapAsync(invoices, Enumerable.Empty<Guid>(), ct);

    private async Task<Dictionary<Guid, string>> BuildInvoiceCustomerNameMapAsync(
        IEnumerable<LocalInvoiceListSummary> invoices,
        IEnumerable<Guid> extraCustomerIds,
        CancellationToken ct)
    {
        var customerIds = invoices
            .Select(invoice => invoice.CustomerId)
            .Concat(extraCustomerIds ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var customerMap = new Dictionary<Guid, string>(customerIds.Count);
        foreach (var customerId in customerIds)
        {
            if (_customerNameById.TryGetValue(customerId, out var customerName))
                customerMap[customerId] = customerName;
        }

        var missingCustomerIds = customerIds
            .Where(id => !customerMap.ContainsKey(id))
            .ToList();
        if (missingCustomerIds.Count == 0)
            return customerMap;

        var missingCustomerMap = await _local.GetCustomerNameMapAsync(missingCustomerIds, ct);
        foreach (var pair in missingCustomerMap)
        {
            customerMap[pair.Key] = pair.Value;
            _customerNameById[pair.Key] = pair.Value;
        }
        return customerMap;
    }

    private static decimal GetStandaloneTransactionLedgerAmount(LocalTransaction transaction)
        => transaction.PaymentTotal > 0m && transaction.ReceiptTotal <= 0m
            ? transaction.PaymentTotal
            : transaction.ReceiptTotal;

    private async Task RefreshDashboardMetricsAsync(
        IReadOnlyList<LocalInvoiceListSummary>? invoices = null,
        CancellationToken ct = default,
        bool forceReload = false)
    {
        if (_dashboardMetricsLoaded && !forceReload)
            return;

        var sourceInvoices = invoices;
        if (sourceInvoices is null)
        {
            var dashboardKey = new InvoiceLedgerCacheKey(CustomerId: null, From: null, To: null);
            var dashboardLoadStopwatch = Stopwatch.StartNew();
            var (cachedInvoices, invoiceCacheHit) = await _invoiceLedgerCache.GetInvoiceSummariesAsync(
                dashboardKey,
                forceReload,
                () => _local.GetInvoiceListSummariesAsync(from: null, to: null, customerId: null, session: _session, ct));
            dashboardLoadStopwatch.Stop();
            OperationTiming.LogIfSlow(
                "MAIN",
                "Dashboard invoice summary load",
                dashboardLoadStopwatch.Elapsed,
                $"{dashboardKey.ToOperationDetail()}, force={forceReload}, invoiceCache={FormatCacheState(invoiceCacheHit)}, invoices={cachedInvoices.Count:N0}",
                infoThreshold: DetailedInvoiceTimingInfoThreshold,
                warningThreshold: DetailedInvoiceTimingWarningThreshold);
            sourceInvoices = cachedInvoices;
        }

        var now = DateOnly.FromDateTime(DateTime.Today);

        if (CanViewDashboardSalesCards)
        {
            var monthlySales = sourceInvoices
                .Where(i => i.VoucherType == VoucherType.Sales
                         && i.InvoiceDate.Year == now.Year
                         && i.InvoiceDate.Month == now.Month)
                .Sum(i => i.TotalAmount);

            var monthlyInvoiceCount = sourceInvoices.Count(i =>
                i.InvoiceDate.Year == now.Year && i.InvoiceDate.Month == now.Month);

            DashboardMonthlySales = monthlySales;
            DashboardMonthlyInvoiceCount = monthlyInvoiceCount;
            DashboardMonthlyAverageSales = monthlyInvoiceCount == 0
                ? 0
                : Math.Round(monthlySales / monthlyInvoiceCount, 0, MidpointRounding.AwayFromZero);
        }
        else
        {
            DashboardMonthlySales = 0m;
            DashboardMonthlyInvoiceCount = 0;
            DashboardMonthlyAverageSales = 0m;
        }
        DashboardReceivable = sourceInvoices
            .Where(invoice => invoice.VoucherType == VoucherType.Sales)
            .Sum(invoice => Math.Max(0m, invoice.TotalAmount - invoice.SettledAmount));
        DashboardPayable = sourceInvoices
            .Where(invoice => invoice.VoucherType == VoucherType.Purchase)
            .Sum(invoice => Math.Max(0m, invoice.TotalAmount - invoice.SettledAmount));

        var items = await _local.GetItemsAsync(_session, ct);
        DashboardSafetyStockAlerts = items.Count(i =>
            i.SafetyStock > 0 && i.CurrentStock <= i.SafetyStock);
        DashboardCustomerCount = _allCustomers.Count;

        var rentalSummary = await _rental.GetDashboardSummaryAsync(_session, now, ct);
        DashboardRentalDueTodayCount = rentalSummary.DueTodayCount;
        DashboardRentalUpcomingCount = rentalSummary.UpcomingCount;
        DashboardRentalOverdueCount = rentalSummary.OverdueCount;
        RentalAlertPopupMessage = rentalSummary.AlertPopupMessage;

        await RefreshContractDashboardAsync();
        await RefreshRecycleBinDashboardAsync();
        _dashboardMetricsLoaded = true;
    }

    [RelayCommand]
    private void ToggleDashboardSalesMetrics()
        => DashboardSalesMetricsExpanded = !DashboardSalesMetricsExpanded;

    [RelayCommand]
    private async Task OpenDashboardReceivableDetailsAsync()
        => await OpenDashboardBalanceDetailsAsync(VoucherType.Sales, "미수 잔액 상세", "미수 잔액", "#FFCC80");

    [RelayCommand]
    private async Task OpenDashboardPayableDetailsAsync()
        => await OpenDashboardBalanceDetailsAsync(VoucherType.Purchase, "미지급 잔액 상세", "미지급 잔액", "#CE93D8");

    private async Task OpenDashboardBalanceDetailsAsync(
        VoucherType voucherType,
        string title,
        string balanceKindText,
        string accentBrush)
    {
        try
        {
            var detailViewModel = new DashboardBalanceDetailViewModel(
                _local,
                _session,
                voucherType,
                title,
                $"{balanceKindText}이 남은 거래처와 전표내역을 현재 계정/담당지점 조회 권한 범위로 표시합니다.",
                balanceKindText,
                accentBrush,
                afterPaymentSavedAsync: () => LoadInvoiceListAsync());
            await detailViewModel.RefreshAsync();
            var window = new DashboardBalanceDetailsWindow(detailViewModel);
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow;
            if (owner is not null && !ReferenceEquals(owner, window))
                window.Owner = owner;

            WindowShowHelper.ShowModeless(window);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MAIN", $"{title} 조회 실패: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"{title}을 불러오지 못했습니다.{Environment.NewLine}{ex.Message}",
                title,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void HandleInvoiceFilterChanged()
    {
        if (_suppressFilterAutoSave || IsBusinessDatabaseTransitionInProgress())
            return;

        RequestApplyInvoiceFilters();
    }

    private void RequestApplyInvoiceFilters()
    {
        _invoiceFilterDebouncer.Debounce(TimeSpan.FromMilliseconds(180), () =>
        {
            var version = Interlocked.Increment(ref _invoiceFilterApplyVersion);
            _invoiceFilterApplyCts?.Cancel();
            _invoiceFilterApplyCts?.Dispose();
            _invoiceFilterApplyCts = new CancellationTokenSource();
            var token = _invoiceFilterApplyCts.Token;
            var task = _ownerScopeBackgroundWork.TryStart(
                () => ApplyInvoiceFiltersAsync(version, token));
            if (task is null)
            {
                _invoiceFilterApplyCts.Cancel();
                _invoiceFilterApplyCts.Dispose();
                _invoiceFilterApplyCts = null;
                return;
            }
            _invoiceFilterApplyTask = task;
            UiTaskHelper.Forget(
                task,
                "MAIN",
                "전표 필터 적용",
                ex =>
                {
                    if (IsCurrentInvoiceFilterApply(version))
                        AppLogger.Warn("MAIN", $"전표 필터 적용 실패: {ex.Message}");
                });
        });
    }

    private async Task ApplyInvoiceFiltersAsync(int version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCurrentInvoiceFilterApply(version))
            return;

        await _customerInlineDataGate.WaitAsync(ct);
        try
        {
            await PersistInvoiceFiltersCoreAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentInvoiceFilterApply(version))
                return;

            await LoadInvoiceListCoreAsync(
                forceReload: false,
                cancellationToken: ct,
                dataGateAlreadyHeld: true);
        }
        finally
        {
            _customerInlineDataGate.Release();
        }
    }

    private bool IsCurrentInvoiceFilterApply(int version)
        => version == Volatile.Read(ref _invoiceFilterApplyVersion);

    private Task PersistInvoiceFiltersAsync()
        => PersistInvoiceFiltersAsync(CancellationToken.None);

    private async Task PersistInvoiceFiltersAsync(CancellationToken ct)
    {
        await _customerInlineDataGate.WaitAsync(ct);
        try
        {
            await PersistInvoiceFiltersCoreAsync(ct);
        }
        finally
        {
            _customerInlineDataGate.Release();
        }
    }

    private async Task PersistInvoiceFiltersCoreAsync(CancellationToken ct)
    {
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterCustomerSettingKey), FilterCustomerName ?? string.Empty, ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterVoucherTypeSettingKey), SelectedVoucherTypeFilter ?? "전체", ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterOfficeCodeSettingKey), SelectedInvoiceOfficeFilterCode ?? GetDefaultInvoiceOfficeFilterCode(), ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMinAmountSettingKey), FilterMinAmountText ?? string.Empty, ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMaxAmountSettingKey), FilterMaxAmountText ?? string.Empty, ct);
    }

    private async Task LoadInvoiceFilterSettingsAsync(
        CancellationToken ct = default,
        bool dataGateAlreadyHeld = false)
    {
        var dataGateEntered = false;
        if (!dataGateAlreadyHeld)
        {
            await _customerInlineDataGate.WaitAsync(ct);
            dataGateEntered = true;
        }

        try
        {
            await LoadInvoiceFilterSettingsCoreAsync(ct);
        }
        finally
        {
            if (dataGateEntered)
                _customerInlineDataGate.Release();
        }
    }

    private async Task LoadInvoiceFilterSettingsCoreAsync(CancellationToken ct)
    {
        _suppressFilterAutoSave = true;
        var hadPersistedHiddenTextFilter = false;
        try
        {
            InitializeInvoiceOfficeFilterOptions();
            var customerNameValue = await _local.GetSettingAsync(
                BuildAccountScopedInvoiceFilterKey(InvoiceFilterCustomerSettingKey),
                ct);
            var voucherTypeValue = await _local.GetSettingAsync(
                BuildAccountScopedInvoiceFilterKey(InvoiceFilterVoucherTypeSettingKey),
                ct);
            var minAmountValue = await _local.GetSettingAsync(
                BuildAccountScopedInvoiceFilterKey(InvoiceFilterMinAmountSettingKey),
                ct);
            var maxAmountValue = await _local.GetSettingAsync(
                BuildAccountScopedInvoiceFilterKey(InvoiceFilterMaxAmountSettingKey),
                ct);
            hadPersistedHiddenTextFilter = HasHiddenInvoiceTextFilter(
                customerNameValue,
                minAmountValue,
                maxAmountValue);
            var hiddenTextFilters = NormalizeHiddenInvoiceTextFilters(
                customerNameValue,
                minAmountValue,
                maxAmountValue);
            FilterFrom = _invoiceDefaultFrom;
            FilterTo = _invoiceDefaultTo;

            FilterCustomerName = hiddenTextFilters.CustomerName;
            SelectedVoucherTypeFilter = VoucherTypeFilterOptions.Contains(voucherTypeValue ?? string.Empty)
                ? voucherTypeValue!
                : "전체";
            var defaultOfficeFilterCode = GetDefaultInvoiceOfficeFilterCode();
            var normalizedOfficeCode = defaultOfficeFilterCode;
            SelectedInvoiceOfficeFilterCode = InvoiceOfficeFilterOptions.Any(option =>
                string.Equals(option.Code, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase))
                ? normalizedOfficeCode
                : defaultOfficeFilterCode;
            FilterMinAmountText = hiddenTextFilters.MinAmountText;
            FilterMaxAmountText = hiddenTextFilters.MaxAmountText;
        }
        finally
        {
            _suppressFilterAutoSave = false;
        }

        if (hadPersistedHiddenTextFilter)
            await PersistInvoiceFiltersCoreAsync(ct);
    }

    private static (DateOnly? From, DateOnly? To) ResolveMainInvoiceQueryDateRange(DateOnly filterFrom, DateOnly filterTo)
    {
        // 메인화면에서 기간 조회 UI를 제거했으므로 보이지 않는 내부 날짜값이 거래내역을 숨기면 안 된다.
        return (null, null);
    }

    private static (string CustomerName, string MinAmountText, string MaxAmountText) NormalizeHiddenInvoiceTextFilters(
        string? customerName,
        string? minAmountText,
        string? maxAmountText)
    {
        // 현재 메인화면에는 거래처명/금액 필터 입력 UI가 없으므로 이전 버전 설정값을 조회 조건에 적용하지 않는다.
        return (string.Empty, string.Empty, string.Empty);
    }

    private static bool HasHiddenInvoiceTextFilter(string? customerName, string? minAmountText, string? maxAmountText)
        => !string.IsNullOrWhiteSpace(customerName)
           || !string.IsNullOrWhiteSpace(minAmountText)
           || !string.IsNullOrWhiteSpace(maxAmountText);

    private async Task RefreshInvoiceDefaultDateRangeFromDataAsync(CancellationToken ct = default)
    {
        var (firstDate, lastDate) = await _local.GetInvoiceDateRangeAsync(_session, ct);
        if (!firstDate.HasValue || !lastDate.HasValue)
            return;

        var defaultTo = lastDate.Value > _invoiceLegacyMonthDefaultTo
            ? lastDate.Value
            : _invoiceLegacyMonthDefaultTo;

        _invoiceDefaultFrom = firstDate.Value;
        _invoiceDefaultTo = defaultTo;
    }

    private static decimal? ParseAmountFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.CurrentCulture, out var value))
            return value;
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return value;

        return null;
    }

    private async Task<List<Guid>> GetFavoriteInvoiceIdsAsync(CancellationToken ct = default)
    {
        var raw = await _local.GetSettingAsync(
            BuildAccountScopedInvoiceFilterKey(FavoriteInvoiceIdsSettingKey),
            ct);
        if (string.IsNullOrWhiteSpace(raw))
            return new List<Guid>();

        var ids = new List<Guid>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(token, out var id))
                continue;
            if (!ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    private Task SaveFavoriteInvoiceIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken ct = default)
    {
        var payload = string.Join(',', ids.Select(id => id.ToString("D")));
        return _local.SetSettingAsync(
            BuildAccountScopedInvoiceFilterKey(FavoriteInvoiceIdsSettingKey),
            payload,
            ct);
    }

    private async Task LoadInvoiceFavoritesAsync(
        IReadOnlyList<LocalInvoiceListSummary>? sourceInvoices = null,
        CancellationToken ct = default,
        bool forceReload = false,
        bool dataGateAlreadyHeld = false)
    {
        var dataGateEntered = false;
        if (!dataGateAlreadyHeld)
        {
            await _customerInlineDataGate.WaitAsync(ct);
            dataGateEntered = true;
        }

        try
        {
            var selectedId = SelectedFavoriteInvoice?.InvoiceId;
            var ids = await GetFavoriteInvoiceIdsAsync(ct);
            var allInvoices = sourceInvoices;
            if (allInvoices is null)
            {
                var favoritesKey = new InvoiceLedgerCacheKey(CustomerId: null, From: null, To: null);
                var favoritesLoadStopwatch = Stopwatch.StartNew();
                var (cachedInvoices, invoiceCacheHit) = await _invoiceLedgerCache.GetInvoiceSummariesAsync(
                    favoritesKey,
                    forceReload,
                    () => _local.GetInvoiceListSummariesAsync(from: null, to: null, customerId: null, session: _session, ct));
                favoritesLoadStopwatch.Stop();
                OperationTiming.LogIfSlow(
                    "MAIN",
                    "Favorite invoice summary load",
                    favoritesLoadStopwatch.Elapsed,
                    $"{favoritesKey.ToOperationDetail()}, force={forceReload}, invoiceCache={FormatCacheState(invoiceCacheHit)}, invoices={cachedInvoices.Count:N0}",
                    infoThreshold: DetailedInvoiceTimingInfoThreshold,
                    warningThreshold: DetailedInvoiceTimingWarningThreshold);
                allInvoices = cachedInvoices;
            }

            var invoiceMap = allInvoices.ToDictionary(i => i.Id);
            var customerMap = await BuildInvoiceCustomerNameMapAsync(allInvoices, ct);

            var favoriteItems = new List<FavoriteInvoiceQuickItem>();
            foreach (var id in ids)
            {
                if (!invoiceMap.TryGetValue(id, out var invoice))
                    continue;

                var customerName = customerMap.TryGetValue(invoice.CustomerId, out var n) ? n : "(미지정)";
                var display = $"{invoice.InvoiceDate:yyyy/MM/dd}  {customerName}  {invoice.TotalAmount:N0}원";

                favoriteItems.Add(new FavoriteInvoiceQuickItem
                {
                    InvoiceId = id,
                    DisplayText = display
                });
            }

            FavoriteInvoices.ReplaceWith(favoriteItems);

            if (FavoriteInvoices.Count != ids.Count)
                await SaveFavoriteInvoiceIdsAsync(FavoriteInvoices.Select(f => f.InvoiceId), ct);

            SelectedFavoriteInvoice = selectedId.HasValue
                ? FavoriteInvoices.FirstOrDefault(f => f.InvoiceId == selectedId.Value)
                : FavoriteInvoices.FirstOrDefault();
        }
        finally
        {
            if (dataGateEntered)
                _customerInlineDataGate.Release();
        }
    }

    [RelayCommand]
    private void NewInvoice()
    {
        EditInvoiceId = Guid.NewGuid();
        _editConcurrencyStamp = string.Empty;
        EditCustomer = null;
        EditCustomerName = string.Empty;
        EditInvoiceDate = DateOnly.FromDateTime(DateTime.Today);
        EditVoucherType = VoucherType.Sales;
        EditMemo = string.Empty;
        EditTotalAmount = 0;
        EditSupplyAmount = 0;
        EditVatAmount = 0;
        EditVatMode = InvoiceVatModes.Included;
        EditLines.Clear();
        AddNewLine();
    }

    [RelayCommand]
    private async Task EditInvoiceAsync()
    {
        if (SelectedInvoiceRow is null) return;
        var inv = await GetLatestSelectedInvoiceAsync();
        if (inv is null) return;

        EditInvoiceId = inv.Id;
        _editConcurrencyStamp = inv.ConcurrencyStamp;
        EditInvoiceDate = inv.InvoiceDate;
        EditVoucherType = inv.VoucherType;
        EditMemo = inv.Memo;
        EditTotalAmount = inv.TotalAmount;
        EditSupplyAmount = inv.SupplyAmount;
        EditVatAmount = inv.VatAmount;
        EditVatMode = InvoiceVatModes.Normalize(inv.VatMode);

        EditCustomer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId)
            ?? await _local.GetCustomerAsync(inv.CustomerId);
        EditCustomerName = EditCustomer?.NameOriginal ?? string.Empty;

        EditLines.Clear();
        foreach (var line in inv.Lines.Where(l => !l.IsDeleted))
            EditLines.Add(InvoiceLineEditModel.FromLocal(line));
    }

    [RelayCommand]
    private async Task SaveInvoiceAsync()
    {
        if (EditCustomer is null)
        {
            System.Windows.MessageBox.Show("거래처를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var lines = EditLines.Where(l => !string.IsNullOrWhiteSpace(l.ItemName)).ToList();
        var existingInvoice = await _local.GetInvoiceAsync(EditInvoiceId, _session);
        var responsibleOfficeCode = string.IsNullOrWhiteSpace(existingInvoice?.ResponsibleOfficeCode)
            ? OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(EditCustomer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet)
            : existingInvoice.ResponsibleOfficeCode;
        var sourceWarehouseCode = string.IsNullOrWhiteSpace(existingInvoice?.SourceWarehouseCode)
            ? OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(responsibleOfficeCode, DomainConstants.OfficeUsenet))
            : existingInvoice.SourceWarehouseCode;
        var inv = new LocalInvoice
        {
            Id = EditInvoiceId,
            CustomerId = EditCustomer.Id,
            InvoiceDate = EditInvoiceDate,
            VoucherType = EditVoucherType,
            Memo = EditMemo,
            VatMode = InvoiceVatModes.Normalize(EditVatMode),
            TaxInvoiceIssued = existingInvoice?.TaxInvoiceIssued ?? false,
            ResponsibleOfficeCode = responsibleOfficeCode,
            SourceWarehouseCode = sourceWarehouseCode,
            LinkedRentalBillingProfileId = existingInvoice?.LinkedRentalBillingProfileId,
            LinkedRentalBillingRunId = existingInvoice?.LinkedRentalBillingRunId,
            ConcurrencyStamp = _editConcurrencyStamp,
            Lines = lines.Select(l => l.ToLocal(EditInvoiceId)).ToList()
        };

        var saveContext = new InvoiceSaveContext
        {
            Username = _session.User?.Username ?? "local-user",
            Role = _session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = _session.OfficeCode,
            ForceOverride = false,
            AutoRebaseWhenLatestSavedBySameUser = true,
            ExpectedConcurrencyStamp = string.IsNullOrWhiteSpace(_editConcurrencyStamp)
                ? null
                : _editConcurrencyStamp
        };

        var saveResult = await _local.SaveInvoiceAsync(inv, saveContext, _session);
        if (!saveResult.Success)
        {
            System.Windows.MessageBox.Show(
                saveResult.Message,
                saveResult.ConcurrencyConflict
                    ? "동시 수정 충돌"
                    : saveResult.PermissionDenied ? "권한 없음" : "저장 실패",
                System.Windows.MessageBoxButton.OK,
                saveResult.ConcurrencyConflict || saveResult.PermissionDenied
                    ? System.Windows.MessageBoxImage.Warning
                    : System.Windows.MessageBoxImage.Error);
            return;
        }

        var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
        _editConcurrencyStamp = saveResult.SavedConcurrencyStamp;
        await ReloadInvoiceListAsync();
        System.Windows.MessageBox.Show(
            LocalStateService.ComposeServerWriteStatusMessage("저장되었습니다.", serverWriteResult),
            "알림",
            System.Windows.MessageBoxButton.OK);
    }

    [RelayCommand]
    private async Task DeleteInvoiceAsync()
    {
        if (SelectedInvoiceRow is null)
            return;

        await DeleteInvoiceRowsAsync(new[] { SelectedInvoiceRow });
    }

    public async Task DeleteInvoiceRowsAsync(IEnumerable<InvoiceListRow> invoiceRows)
    {
        var rows = invoiceRows
            .Where(row => row is not null)
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .ToList();

        if (rows.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "삭제할 내역을 선택하세요.",
                "알림",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var targetText = rows.Count == 1
            ? "선택한 내역 1건"
            : $"선택한 내역 {rows.Count:N0}건";

        var confirm = System.Windows.MessageBox.Show(
            $"{targetText}을 삭제하시겠습니까?{Environment.NewLine}삭제된 전표/수금·지급 내역은 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "거래내역 삭제 확인",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.OK)
            return;

        var deletedCount = 0;
        foreach (var row in rows)
        {
            var result = row.IsTransactionRow
                ? await _local.DeleteTransactionAsync(row.TransactionId ?? row.Id, _session, row.Revision)
                : await _local.DeleteInvoiceAsync(row.Id, _session, row.Revision);
            if (!result.Success)
            {
                await ReloadInvoiceListAsync();
                System.Windows.MessageBox.Show(
                    result.Message,
                    result.ConcurrencyConflict ? "동시 수정 충돌" : result.PermissionDenied ? "권한 없음" : "삭제 실패",
                    System.Windows.MessageBoxButton.OK,
                    result.ConcurrencyConflict || result.PermissionDenied
                        ? System.Windows.MessageBoxImage.Warning
                        : System.Windows.MessageBoxImage.Error);
                return;
            }

            deletedCount++;
        }

        var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
        await ReloadInvoiceListAsync();
        var completedMessage = deletedCount == 1
            ? "거래내역을 삭제했습니다."
            : $"거래내역 {deletedCount:N0}건을 삭제했습니다.";

        System.Windows.MessageBox.Show(
            LocalStateService.ComposeServerWriteStatusMessage(completedMessage, serverWriteResult),
            "알림",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    // Lines
    [RelayCommand]
    private void AddNewLine()
    {
        EditLines.Add(new InvoiceLineEditModel());
        RecalcTotals();
    }

    [RelayCommand]
    private void RemoveLine(InvoiceLineEditModel? line)
    {
        if (line is null) return;
        EditLines.Remove(line);
        RecalcTotals();
    }

    public void RecalcTotals()
    {
        var totals = InvoiceVatModes.CalculateTotals(EditLines.Select(l => l.LineAmount), EditVatMode);
        EditTotalAmount = totals.TotalAmount;
        EditSupplyAmount = totals.SupplyAmount;
        EditVatAmount = totals.VatAmount;
    }

    // Payments
    [RelayCommand]
    private async Task LoadPaymentsAsync()
    {
        if (SelectedInvoiceRow is null) return;

        var inv = await GetLatestSelectedInvoiceAsync();
        if (inv is null) return;
        PaymentInvoice = ResolveInvoiceListRowForInvoice(inv);

        PaymentRows.Clear();
        foreach (var p in inv.Payments.Where(p => !p.IsDeleted))
            PaymentRows.Add(PaymentRowModel.FromLocal(p));

        RecalcPaymentTotals(inv);
        SelectedTabIndex = 1; // 수금 입력 탭으로 이동(전표작성 탭 제거 후)
    }

    [RelayCommand]
    private void AddPaymentRow()
    {
        if (PaymentInvoice is null) return;
        PaymentRows.Add(new PaymentRowModel { InvoiceId = PaymentInvoice.Id });
    }

    [RelayCommand]
    private async Task SavePaymentsAsync()
    {
        if (PaymentInvoice is null) return;

        if (PaymentRows.Any(row => row.Amount < 0))
        {
            System.Windows.MessageBox.Show("수금 금액은 0 이상으로 입력하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var targetInvoice = await _local.GetLatestInvoiceVersionAsync(PaymentInvoice.Id, _session);
        if (targetInvoice is null)
            return;
        if (targetInvoice.Id != PaymentInvoice.Id)
        {
            PaymentInvoice = ResolveInvoiceListRowForInvoice(targetInvoice);
            PaymentRows.Clear();
            foreach (var payment in targetInvoice.Payments.Where(payment => !payment.IsDeleted))
                PaymentRows.Add(PaymentRowModel.FromLocal(payment));
            RecalcPaymentTotals(targetInvoice);
            System.Windows.MessageBox.Show(
                "최신 전표 버전이 이미 저장되어 수금/지급 내역을 다시 불러왔습니다. 확인 후 다시 저장하세요.",
                "최신 전표 재조회",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var inputTotal = PaymentRows.Sum(row => row.Amount);
        if (inputTotal > targetInvoice.TotalAmount)
        {
            var proceed = System.Windows.MessageBox.Show(
                "입력한 수금 합계가 전표 합계를 초과합니다. 계속 저장할까요?",
                "수금 검증",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (proceed != System.Windows.MessageBoxResult.Yes)
                return;
        }

        var savedRowCount = 0;
        foreach (var row in PaymentRows)
        {
            if (row.Amount == 0) continue;
            row.InvoiceId = PaymentInvoice.Id;
            var result = await _local.SavePaymentAsync(row.ToLocal(), _session);
            if (!result.Success)
            {
                if (result.ConcurrencyConflict)
                {
                    await LoadPaymentsAsync();
                    var conflictDetail = savedRowCount > 0
                        ? "\n일부 수금 행은 이미 저장되었을 수 있으니 최신 목록을 다시 확인하세요."
                        : "\n최신 수금 내역을 다시 불러왔습니다. 확인 후 다시 저장하세요.";
                    System.Windows.MessageBox.Show(
                        result.Message + conflictDetail,
                        "동시 수정 충돌",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                System.Windows.MessageBox.Show(
                    result.Message,
                    result.PermissionDenied ? "권한 없음" : "저장 실패",
                    System.Windows.MessageBoxButton.OK,
                    result.PermissionDenied
                        ? System.Windows.MessageBoxImage.Warning
                        : System.Windows.MessageBoxImage.Error);
                return;
            }

            savedRowCount++;
        }

        var inv = await _local.GetLatestInvoiceVersionAsync(PaymentInvoice.Id, _session);
        if (inv is not null) RecalcPaymentTotals(inv);
        await ReloadInvoiceListAsync();
        var paymentServerWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
        System.Windows.MessageBox.Show(
            LocalStateService.ComposeServerWriteStatusMessage("수금이 저장되었습니다.", paymentServerWriteResult),
            "알림",
            System.Windows.MessageBoxButton.OK);
    }

    private void RecalcPaymentTotals(LocalInvoice inv)
    {
        PaymentTotalPaid = PaymentRows.Sum(p => p.Amount);
        PaymentBalance = inv.TotalAmount - PaymentTotalPaid;
    }

    private InvoiceListRow ResolveInvoiceListRowForInvoice(LocalInvoice invoice)
    {
        var existingRow = InvoiceRows.FirstOrDefault(row => row.Id == invoice.Id)
            ?? InvoiceRows.FirstOrDefault(row => row.EffectiveVersionGroupId == ResolveEffectiveVersionGroupId(invoice));
        if (existingRow is not null)
            return existingRow;

        var customerName = _customerNameById.TryGetValue(invoice.CustomerId, out var name)
            ? name
            : "(미지정)";
        return InvoiceListRow.From(invoice, customerName, SelectedCustomerFilter is null);
    }

    // Statement Print (F9)
    [RelayCommand]
    private async Task PrintStatementAsync()
    {
        try
        {
            var target = StatementInvoice ?? SelectedInvoiceRow;
            if (target is null)
            {
                System.Windows.MessageBox.Show("출력할 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
                return;
            }

            var inv = await _local.GetLatestInvoiceVersionAsync(target.Id, _session);
            var company = await _local.GetCompanyProfileAsync(_session);

            if (inv is null || company is null)
            {
                System.Windows.MessageBox.Show("전표 또는 회사 정보가 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
                return;
            }

            var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId)
                ?? await _local.GetCustomerAsync(inv.CustomerId);
            if (customer is null)
            {
                System.Windows.MessageBox.Show("거래처 정보를 찾을 수 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
                return;
            }

            var printModel = await LoadOrCreateInvoicePrintModelAsync(
                inv,
                customer,
                company,
                printWithDate: true,
                printWithPrice: true);
            var previewDocument = _invoicePrintService.BuildFixedDocument(printModel);
            var printDocumentName = inv.VoucherType switch
            {
                VoucherType.Purchase => "매입 명세서",
                VoucherType.Procurement => string.IsNullOrWhiteSpace(printModel.DocumentTitle) ? "발주서" : printModel.DocumentTitle,
                _ => "거래명세서"
            };
            var previewViewModel = new PrintPreviewViewModel(
                previewDocument,
                _invoicePrintService,
                $"{printDocumentName}_{inv.InvoiceDate:yyyyMMdd}_{customer.NameOriginal}");
            var previewWindow = new PrintPreviewWindow(previewViewModel)
            {
                Owner = GetActiveWindow()
            };

            WindowShowHelper.ShowModeless(previewWindow);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"전표 인쇄 중 오류가 발생했습니다.\n{ex.Message}",
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task<InvoicePrintModel> LoadOrCreateInvoicePrintModelAsync(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice)
    {
        var defaultModel = _invoicePrintService.CreateDefaultModel(invoice, customer, company, printWithDate, printWithPrice);
        var payload = await _local.GetInvoicePrintPayloadAsync(invoice.Id);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<InvoicePrintModel>(payload, PrintModelJsonOptions);
                if (saved is not null)
                {
                    saved.InvoiceId = invoice.Id;
                    saved.PrintWithDate = printWithDate;
                    saved.PrintWithPrice = printWithPrice;
                    if (invoice.VoucherType == VoucherType.Procurement)
                        saved.DocumentTitle = saved.DocumentTitle is "납품서" or "의뢰서" ? saved.DocumentTitle : "발주서";

                    InvoicePrintModelCurrentInfoSynchronizer.RefreshLinkedBusinessPartyFields(saved, defaultModel);
                    InvoicePrintLineSynchronizer.AlignToInvoiceLineOrder(saved, defaultModel);

                    return saved;
                }
            }
            catch
            {
                // Corrupted payload falls back to default model.
            }
        }

        var model = defaultModel;
        if (invoice.VoucherType == VoucherType.Procurement)
            model.DocumentTitle = model.DocumentTitle is "납품서" or "의뢰서" ? model.DocumentTitle : "발주서";
        return model;
    }

    private static Window? GetActiveWindow()
    {
        return Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive);
    }
// Company Settings
    private async Task LoadCompanyProfileAsync(CancellationToken ct = default)
    {
        var profile = await _local.GetCompanyProfileAsync(_session, ct);
        if (profile is null) return;

        _loadedCompanyProfile = profile;
        _companyProfileId = profile.Id;
        CompanyTradeName = profile.TradeName;
        CompanyRepresentative = profile.Representative;
        CompanyBusinessNumber = profile.BusinessNumber;
        CompanyBusinessType = profile.BusinessType;
        CompanyBusinessItem = profile.BusinessItem;
        CompanyAddress = profile.Address;
        CompanyContactNumber = profile.ContactNumber;
        CompanyEmail = profile.Email;
        CompanyBankAccountText = profile.BankAccountText;
        CompanyStampImage = profile.StampImage;
        CompanyStampImagePath = profile.StampImage is { Length: > 0 } ? "(이미지 있음)" : "(없음)";
    }

    [RelayCommand]
    private async Task SaveCompanyProfileAsync()
    {
        if (!_session.HasPermission("CompanyProfile.Edit")
            && _session.User?.Role != "Admin")
        {
            System.Windows.MessageBox.Show("회사 정보는 관리자 권한이 있는 계정만 저장할 수 있습니다.", "권한 제한", System.Windows.MessageBoxButton.OK);
            return;
        }

        var source = _loadedCompanyProfile;
        var profile = new LocalCompanyProfile
        {
            Id = _companyProfileId,
            ProfileName = source?.ProfileName ?? string.Empty,
            OfficeCode = source?.OfficeCode ?? _session.BusinessOfficeCode,
            IsDefaultForOffice = source?.IsDefaultForOffice ?? true,
            IsActive = source?.IsActive ?? true,
            CreatedAtUtc = source?.CreatedAtUtc ?? default,
            UpdatedAtUtc = source?.UpdatedAtUtc ?? default,
            Revision = source?.Revision ?? 0,
            IsDeleted = source?.IsDeleted ?? false,
            TradeName = CompanyTradeName,
            Representative = CompanyRepresentative,
            BusinessNumber = CompanyBusinessNumber,
            BusinessType = CompanyBusinessType,
            BusinessItem = CompanyBusinessItem,
            Address = CompanyAddress,
            ContactNumber = CompanyContactNumber,
            FaxNumber = source?.FaxNumber ?? string.Empty,
            Email = CompanyEmail,
            BankAccountText = CompanyBankAccountText,
            StampImage = CompanyStampImage
        };

        try
        {
            await _local.SaveCompanyProfileAsync(profile);
            await LoadCompanyProfileAsync();
            System.Windows.MessageBox.Show("회사 정보가 저장되었습니다.", "알림", System.Windows.MessageBoxButton.OK);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                ex.Message.Contains("최신", StringComparison.CurrentCultureIgnoreCase) ||
                ex.Message.Contains("동시", StringComparison.CurrentCultureIgnoreCase)
                    ? "동시 수정 충돌"
                    : "저장 실패",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void SelectStampImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "직인 이미지 선택",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (DialogWindowCloseHelper.ShowDialog(dlg) != true) return;
        CompanyStampImage = File.ReadAllBytes(dlg.FileName);
        CompanyStampImagePath = "(이미지 있음)";
    }

    [RelayCommand]
    private void ClearStampImage()
    {
        CompanyStampImage = null;
        CompanyStampImagePath = "(없음)";
    }

    private async Task LoadLegacyMigrationSettingsAsync(CancellationToken ct = default)
    {
        var defaultDb = GetDefaultLegacySourceDbPath();
        var defaultCustomerExcel = Path.Combine(AppPaths.UserDownloadsDir, "거래처 목록.xlsx");
        var defaultItemExcel = Path.Combine(AppPaths.UserDownloadsDir, "제품 목록.xlsx");

        LegacySourceDbPath = await _local.GetSettingAsync(LegacySourceDbPathSettingKey, ct) ?? defaultDb;
        LegacyCustomerExcelPath = await _local.GetSettingAsync(LegacyCustomerExcelPathSettingKey, ct) ?? defaultCustomerExcel;
        LegacyItemExcelPath = await _local.GetSettingAsync(LegacyItemExcelPathSettingKey, ct) ?? defaultItemExcel;

        if (string.IsNullOrWhiteSpace(LegacySourceDbPath))
            LegacySourceDbPath = defaultDb;
        if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath))
            LegacyCustomerExcelPath = defaultCustomerExcel;
        if (string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            LegacyItemExcelPath = defaultItemExcel;
    }

    private async Task PersistLegacyMigrationSettingsAsync()
    {
        await _local.SetSettingAsync(LegacySourceDbPathSettingKey, LegacySourceDbPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyCustomerExcelPathSettingKey, LegacyCustomerExcelPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyItemExcelPathSettingKey, LegacyItemExcelPath ?? string.Empty);
    }

    private static string GetDefaultLegacySourceDbPath()
    {
        var candidate = @"C:\LegacySalesApp\DATA\LEGACY_DATA.FDB";
        if (File.Exists(candidate))
            return candidate;
        return string.Empty;
    }

    [RelayCommand]
    private async Task SelectLegacySourceDbPathAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "외부 레거시 DB(FDB) 선택",
            Filter = "Firebird DB|*.fdb|모든 파일|*.*",
            CheckFileExists = true
        };

        if (DialogWindowCloseHelper.ShowDialog(dialog) != true)
            return;

        LegacySourceDbPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyCustomerExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyCustomerExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppPaths.UserDownloadsDir;

        var dialog = new SaveFileDialog
        {
            Title = "거래처 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "거래처 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (DialogWindowCloseHelper.ShowDialog(dialog) != true)
            return;

        LegacyCustomerExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyItemExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyItemExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppPaths.UserDownloadsDir;

        var dialog = new SaveFileDialog
        {
            Title = "제품 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "제품 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (DialogWindowCloseHelper.ShowDialog(dialog) != true)
            return;

        LegacyItemExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task ExportLegacyDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacySourceDbPath) || !File.Exists(LegacySourceDbPath))
            {
                MessageBox.Show("외부 레거시 DB 경로를 먼저 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            {
                MessageBox.Show("거래처/제품 엑셀 경로를 먼저 지정하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LegacyMigrationStatus = "외부 레거시 데이터를 엑셀로 추출 중...";
            var result = await _legacyMigrationService.ExportFromOriginalAsync(
                LegacySourceDbPath,
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();

            LegacyMigrationStatus = $"추출 완료: 거래처 {result.CustomerCount:N0}건, 제품 {result.ItemCount:N0}건";
            MessageBox.Show(
                $"추출 완료\n거래처: {result.CustomerCount:N0}건\n제품: {result.ItemCount:N0}건\n\n{result.CustomerExcelPath}\n{result.ItemExcelPath}",
                "데이터 추출",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"추출 실패: {ex.Message}";
            MessageBox.Show($"데이터 추출 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ImportLegacyExcelDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || !File.Exists(LegacyCustomerExcelPath))
            {
                MessageBox.Show("거래처 엑셀 파일 경로를 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyItemExcelPath) || !File.Exists(LegacyItemExcelPath))
            {
                MessageBox.Show("제품 엑셀 파일 경로를 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LegacyMigrationStatus = "엑셀 가져오기 미리보기 생성 중...";
            var preview = await _legacyMigrationService.PreviewExcelImportAsync(
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);
            var confirm = MessageBox.Show(
                "엑셀 가져오기 미리보기" + Environment.NewLine + Environment.NewLine +
                preview.ToDisplayText() + Environment.NewLine + Environment.NewLine +
                "현재 DB 백업을 만든 뒤 위 내용대로 반영합니다. 계속하시겠습니까?",
                "데이터 가져오기",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                LegacyMigrationStatus = "엑셀 데이터 가져오기를 취소했습니다.";
                return;
            }

            var backupPath = await _backup.BackupNowWithPathAsync();
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                LegacyMigrationStatus = "엑셀 가져오기 전에 현재 DB 백업을 생성하지 못했습니다. 백업 상태를 확인한 뒤 다시 시도하세요.";
                MessageBox.Show(LegacyMigrationStatus, "데이터 가져오기", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LegacyMigrationStatus = $"현재 DB 백업 완료: {Path.GetFileName(backupPath)}. 엑셀 데이터를 거래플랜으로 가져오는 중...";
            var result = await _legacyMigrationService.ImportFromExcelAsync(
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();
            await ReloadCustomerAndInvoiceDataAsync();

            LegacyMigrationStatus =
                $"가져오기 완료: 거래처 +{result.CreatedCustomers:N0}/수정 {result.UpdatedCustomers:N0}, " +
                $"제품 +{result.CreatedItems:N0}/수정 {result.UpdatedItems:N0}";

            MessageBox.Show(
                $"가져오기 완료\n" +
                $"거래처: 신규 {result.CreatedCustomers:N0}, 수정 {result.UpdatedCustomers:N0}, 건너뜀 {result.SkippedCustomers:N0}\n" +
                $"제품: 신규 {result.CreatedItems:N0}, 수정 {result.UpdatedItems:N0}, 건너뜀 {result.SkippedItems:N0}",
                "데이터 가져오기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"가져오기 실패: {ex.Message}";
            MessageBox.Show($"데이터 가져오기 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExportAndImportLegacyDataAsync()
    {
        await ExportLegacyDataAsync();
        if (!LegacyMigrationStatus.StartsWith("추출 완료", StringComparison.Ordinal))
            return;
        await ImportLegacyExcelDataAsync();
    }

    // Refresh Customers (거래처 등록/수정 후 갱신)
    [RelayCommand]
    public async Task RefreshCustomersAsync()
    {
        await ReloadCustomerAndInvoiceDataAsync();
    }

    // Sync
    public async Task ReloadAfterPassiveSyncAsync(CancellationToken ct = default)
    {
        await ReloadCustomerAndInvoiceDataAsync(ct);
    }

    [RelayCommand]
    private async Task ForceSyncAsync()
    {
        SetAuthoritativeSyncStatus("수동 동기화 중...");
        var syncOk = await RunIsolatedSyncAsync((sync, ct) => sync.TrySyncAsync(ct));
        var dirtyCount = await _local.CountDirtyAsync(_session);
        await ReloadCustomerAndInvoiceDataAsync();

        SetAuthoritativeSyncStatus(dirtyCount > 0
            ? await _local.GetPendingSyncWaitingMessageAsync(_session, "동기화 작업은 완료됐지만", CancellationToken.None)
                ?? $"동기화 작업은 완료됐지만 서버 반영 대기 데이터 {dirtyCount:N0}건이 남아 있습니다."
            : syncOk
                ? $"동기화 완료 {DateTime.Now:HH:mm:ss}"
                : "동기화가 완료되었지만 확인이 필요한 항목이 남아 있습니다. 동기화 진단을 확인하세요.");
    }

    // Backup
    [RelayCommand]
    private async Task BackupNowAsync()
    {
        var ok = await _backup.BackupNowAsync();
        System.Windows.MessageBox.Show(
            ok ? "백업이 완료되었습니다." : "백업 중 오류가 발생했습니다.",
            "백업", System.Windows.MessageBoxButton.OK);
    }
}
