using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
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
    private readonly IPrintService _invoicePrintService = new WpfInvoicePrintService();
    private static readonly JsonSerializerOptions PrintModelJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RecentPostLoginSyncSkipWindow = TimeSpan.FromMinutes(2);
    private readonly LegacyDataMigrationService _legacyMigrationService;
    private CancellationTokenSource? _customerAutoSaveCts;
    private int _customerAutoSaveVersion;
    private int _customerFinancialPreviewVersion;
    private int _invoicePreviewVersion;
    private int _invoiceFilterApplyVersion;
    private readonly UiDebouncer _invoiceFilterDebouncer = new();
    private readonly UiDebouncer _customerFilterDebouncer = new();
    private readonly SemaphoreSlim _invoiceListLoadGate = new(1, 1);
    private CancellationTokenSource? _invoiceFilterApplyCts;
    private CancellationTokenSource? _invoiceListLoadCts;
    private const string LegacySourceDbPathSettingKey = "LegacyMigration.SourceDbPath";
    private const string LegacyCustomerExcelPathSettingKey = "LegacyMigration.CustomerExcelPath";
    private const string LegacyItemExcelPathSettingKey = "LegacyMigration.ItemExcelPath";

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
    [ObservableProperty] private decimal _dashboardSalesTrendPercent;
    [ObservableProperty] private int _dashboardRentalDueTodayCount;
    [ObservableProperty] private int _dashboardRentalUpcomingCount;
    [ObservableProperty] private int _dashboardRentalOverdueCount;
    [ObservableProperty] private string _rentalAlertPopupMessage = string.Empty;

    // 전표 목록 - Left panel (거래처 필터)
    private List<LocalCustomer> _allCustomers = new();
    private Dictionary<Guid, string> _customerNameById = new();
    public ObservableCollection<LocalCustomer> FilteredCustomers { get; } = new ResettableObservableCollection<LocalCustomer>();
    [ObservableProperty] private string _customerFilterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCustomer))]
    [NotifyPropertyChangedFor(nameof(InvoicePrimaryColumnHeader))]
    private LocalCustomer? _selectedCustomerFilter;
    public bool HasSelectedCustomer => SelectedCustomerFilter is not null;
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

    partial void OnEditCustBizNumberChanged(string value) => TriggerCustomerAutoSave();
    partial void OnEditCustPhoneChanged(string value) => TriggerCustomerAutoSave();
    partial void OnEditCustDeptChanged(string value) => TriggerCustomerAutoSave();
    partial void OnEditCustContactPersonChanged(string value) => TriggerCustomerAutoSave();
    partial void OnEditCustAddressChanged(string value) => TriggerCustomerAutoSave();
    partial void OnEditCustNotesChanged(string value) => TriggerCustomerAutoSave();

    private void TriggerCustomerAutoSave()
    {
        if (_suppressCustomerSave)
            return;

        _customerAutoSaveCts?.Cancel();
        _customerAutoSaveCts?.Dispose();
        _customerAutoSaveCts = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _customerAutoSaveVersion);
        var token = _customerAutoSaveCts.Token;
        CustomerInlineSaveStatus = "거래처 정보 변경 감지 - 잠시 후 자동저장합니다.";
        UiTaskHelper.Forget(
            AutoSaveCustomerAsync(token, version),
            "MAIN",
            "거래처 인라인 자동저장",
            ex =>
            {
                CustomerInlineSaveStatus = $"거래처 정보 자동저장 실패: {ex.Message}";
                AppLogger.Warn("AUTOSAVE", $"Customer inline auto-save failed: {ex.Message}");
            });
    }

    private async Task AutoSaveCustomerAsync(CancellationToken cancellationToken, int version)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_suppressCustomerSave || version != Volatile.Read(ref _customerAutoSaveVersion))
            return;

        var customer = SelectedCustomerFilter;
        if (customer is null)
            return;

        CustomerInlineSaveStatus = "거래처 정보 저장 중...";
        customer.BusinessNumber = EditCustBizNumber;
        customer.Phone = EditCustPhone;
        customer.Department = EditCustDept;
        customer.ContactPerson = EditCustContactPerson;
        customer.Address = EditCustAddress;
        customer.Notes = EditCustNotes;
        customer.NameMatchKey = customer.NameOriginal.ToUpperInvariant();
        var result = await _local.UpsertCustomerAsync(customer, _session);
        if (!result.Success)
        {
            CustomerInlineSaveStatus = string.IsNullOrWhiteSpace(result.Message)
                ? "거래처 정보 저장 실패 - 권한 또는 동기화 상태를 확인하세요."
                : $"거래처 정보 저장 실패: {result.Message}";
            AppLogger.Warn("AUTOSAVE", $"Customer inline auto-save failed for '{customer.NameOriginal}'. {result.Message}");
            return;
        }

        CustomerInlineSaveStatus = $"거래처 정보 저장됨 · {DateTime.Now:HH:mm:ss}";
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
        Interlocked.Increment(ref _customerAutoSaveVersion);
        Interlocked.Increment(ref _customerFinancialPreviewVersion);
        Interlocked.Increment(ref _invoicePreviewVersion);
        Interlocked.Increment(ref _invoiceFilterApplyVersion);

        _customerFilterDebouncer.Dispose();
        _invoiceFilterDebouncer.Dispose();

        _customerAutoSaveCts?.Cancel();
        _customerAutoSaveCts?.Dispose();
        _customerAutoSaveCts = null;

        _invoiceFilterApplyCts?.Cancel();
        _invoiceFilterApplyCts?.Dispose();
        _invoiceFilterApplyCts = null;

        _invoiceListLoadCts?.Cancel();
        _invoiceListLoadCts = null;

        _backgroundDesktopUpdateCts?.Cancel();
    }

    private void HandleSyncStatusChanged(string status)
    {
        UiTaskHelper.Forget(
            ApplySyncStatusAsync(status),
            "SYNC-UI",
            "동기화 상태 표시 갱신",
            ex => AppLogger.Warn("SYNC-UI", $"동기화 상태 표시 갱신 실패: {ex.Message}"));
        AppLogger.Info("SYNC-UI", status);
    }

    public void ApplyExternalSyncStatus(string status) => HandleSyncStatusChanged(status);

    private async Task<T> RunIsolatedSyncAsync<T>(Func<SyncService, Task<T>> operation)
    {
        if (_serviceScopeFactory is null)
            return await operation(_sync);

        using var scope = _serviceScopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
        sync.SyncStatusChanged += HandleSyncStatusChanged;
        try
        {
            return await operation(sync);
        }
        finally
        {
            sync.SyncStatusChanged -= HandleSyncStatusChanged;
        }
    }

    private async Task ApplySyncStatusAsync(string status)
    {
        var resolvedStatus = await ComposeSyncStatusAsync(status);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(() => SyncStatus = resolvedStatus);
        else
            SyncStatus = resolvedStatus;
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

        var dirtyCount = await _local.CountDirtyAsync(_session);
        if (dirtyCount <= 0)
            return status;

        if (IsSyncAttentionStatus(status))
            return await _local.GetPendingSyncWaitingMessageAsync(_session, $"{status} /", CancellationToken.None)
                   ?? $"{status} / 서버 반영 대기 데이터 {dirtyCount:N0}건";

        return await _local.GetPendingSyncWaitingMessageAsync(_session, "동기화 작업은 완료됐지만", CancellationToken.None)
               ?? $"동기화 작업은 완료됐지만 서버 반영 대기 데이터 {dirtyCount:N0}건이 남아 있습니다.";
    }

    private static bool IsSyncAttentionStatus(string status)
        => status.StartsWith("동기화 확인 필요", StringComparison.Ordinal)
           || status.StartsWith("서버 응답 지연", StringComparison.Ordinal);

    public async Task LoadAsync()
    {
        await LoadCustomersAsync();
        await RefreshInvoiceDefaultDateRangeFromDataAsync();
        await LoadInvoiceFilterSettingsAsync();
        await LoadInvoiceListAsync();
        await LoadCompanyProfileAsync();
        await LoadLegacyMigrationSettingsAsync();
        if (_session.IsOfflineMode)
            SyncStatus = "오프라인 모드에서는 자동 동기화를 진행하지 않습니다.";
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

    public async Task<bool> ShouldShowPostLoginSyncPopupAsync()
        => await IsInitialServerDataLoadRequiredAsync();

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

    public async Task RunPostLoginSyncAsync()
    {
        if (_session.IsOfflineMode)
        {
            SyncStatus = "로그인 후 서버 동기화를 진행하지 못했습니다.";
            return;
        }

        try
        {
            if (await ShouldSkipImmediatePostLoginSyncAsync())
            {
                var lastSuccess = await GetLastSuccessfulSyncAtAsync();
                SyncStatus = lastSuccess.HasValue
                    ? $"최근 동기화 기록({lastSuccess.Value.ToLocalTime():HH:mm:ss})이 있어 시작 동기화는 생략했습니다."
                    : "최근 동기화 기록이 있어 시작 동기화는 생략했습니다.";
                return;
            }

            var initialDataLoadRequired = await IsInitialServerDataLoadRequiredAsync();
            var shouldRefreshCurrentBusinessScope = await ShouldRefreshCurrentBusinessScopeAfterPostLoginAsync();
            var dirtyBefore = await _local.CountDirtyAsync(_session);
            SyncStatus = initialDataLoadRequired
                ? "초기 데이터 동기화 중입니다. 거래처/거래내역을 서버에서 받는 동안 잠시만 기다려 주세요."
                : "로그인 후 서버 동기화 중...";

            var syncOk = await RunIsolatedSyncAsync(sync => sync.TrySyncAsync());
            var dirtyAfter = await _local.CountDirtyAsync(_session);

            // 업데이트 직후 전체 캐시 재구성은 동기화 내부 복구 경로에서 완료될 수 있다.
            // 이 경우 syncOk가 false여도 DB에는 거래처/거래내역이 다시 채워질 수 있으므로
            // 반드시 메인 목록을 한 번 재조회해 빈 화면이 그대로 남지 않게 한다.
            await ReloadAfterPassiveSyncAsync();
            var hasVisiblePrimaryWorkCache = await _local.HasVisiblePrimaryWorkCacheAsync(_session);

            if (syncOk && dirtyAfter == 0)
            {
                var refreshOk = true;
                if (shouldRefreshCurrentBusinessScope && await _local.IsServerMirrorRefreshRequiredAsync())
                    refreshOk = await RunIsolatedSyncAsync(sync => sync.RefreshCurrentBusinessScopeFromServerAsync());

                await ReloadAfterPassiveSyncAsync();
                hasVisiblePrimaryWorkCache = await _local.HasVisiblePrimaryWorkCacheAsync(_session);

                if (initialDataLoadRequired && !hasVisiblePrimaryWorkCache)
                {
                    SyncStatus = "초기 데이터 표시 확인 중입니다. 서버 기준으로 한 번 더 받습니다...";
                    var mirrorRefreshOk = await RunIsolatedSyncAsync(sync => sync.RefreshSharedMirrorFromServerAsync());
                    await ReloadAfterPassiveSyncAsync();
                    hasVisiblePrimaryWorkCache = await _local.HasVisiblePrimaryWorkCacheAsync(_session);
                    if (mirrorRefreshOk && hasVisiblePrimaryWorkCache)
                    {
                        SyncStatus = $"초기 데이터 동기화 완료 {DateTime.Now:HH:mm:ss}";
                        return;
                    }
                }

                SyncStatus = shouldRefreshCurrentBusinessScope && !refreshOk
                    ? "로그인 후 현재 업체 DB 캐시 재구성은 일부 실패했지만 앱은 계속 사용할 수 있습니다."
                    : $"로그인 후 서버 동기화 완료 {DateTime.Now:HH:mm:ss}";
                return;
            }

            if (dirtyAfter == 0 && hasVisiblePrimaryWorkCache)
            {
                SyncStatus = $"서버 기준 데이터 복구 완료 {DateTime.Now:HH:mm:ss}";
                return;
            }

            if (dirtyBefore > 0 || dirtyAfter > 0)
            {
                var backupOk = await _backup.BackupNowAsync();
                AppLogger.Warn(
                    "APP",
                    $"Post-login auto sync failed with {dirtyAfter} dirty rows. Auto-backup {(backupOk ? "succeeded" : "failed")}.");
                await _diagnostics.RecordIssueAsync(
                    phase: "post-login-sync",
                    rawMessage: $"로그인 후 자동 동기화 확인 필요. dirty={dirtyAfter}, backup={(backupOk ? "ok" : "failed")}.",
                    severity: "Warning",
                    recoveryAttempted: true,
                    recoverySucceeded: false);
            }
            else
            {
                await _diagnostics.RecordIssueAsync(
                    phase: "post-login-sync",
                    rawMessage: "로그인 후 자동 동기화 확인 필요. dirty row는 없지만 서버 캐시 재구성 또는 네트워크 상태를 확인해야 합니다.",
                    severity: "Warning",
                    recoveryAttempted: false,
                    recoverySucceeded: false);
            }

            if (dirtyAfter > 0)
            {
                var pendingMessage = await _local.GetPendingSyncWaitingMessageAsync(_session, ct: CancellationToken.None);
                SyncStatus = string.IsNullOrWhiteSpace(pendingMessage)
                    ? $"서버 반영 대기 데이터 {dirtyAfter:N0}건이 남아 있습니다. 환경설정 > 동기화에서 확인해 주세요."
                    : $"{pendingMessage} 환경설정 > 동기화에서 확인해 주세요.";
            }
            else
            {
                SyncStatus = "동기화 확인이 지연되어 백그라운드에서 다시 확인합니다. 앱은 계속 사용할 수 있습니다.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("APP", "로그인 후 자동 동기화 확인 필요", ex);
            await _diagnostics.RecordIssueAsync(
                phase: "post-login-sync",
                rawMessage: ex.InnerException?.Message ?? ex.Message,
                exception: ex,
                severity: "Warning");
            SyncStatus = "로그인 후 서버 확인이 지연되었습니다. 백그라운드에서 다시 확인하며 앱은 계속 사용할 수 있습니다.";
        }
    }

    private async Task<bool> ShouldSkipImmediatePostLoginSyncAsync()
    {
        if (await _local.IsServerMirrorRefreshRequiredAsync())
            return false;

        if (await _local.HasLikelyCorruptedPrimaryWorkCacheAsync(_session))
        {
            await _local.MarkServerMirrorRefreshRequiredAsync();
            return false;
        }

        if (!await HasPersistedSyncRevisionAsync())
            return false;

        if (!await _local.HasVisiblePrimaryWorkCacheAsync(_session))
            return false;

        if (await HasServerRevisionAdvancedSinceLastSyncAsync())
            return false;

        var lastSuccess = await GetLastSuccessfulSyncAtAsync();
        if (!lastSuccess.HasValue || DateTimeOffset.Now - lastSuccess.Value.ToLocalTime() > RecentPostLoginSyncSkipWindow)
            return false;

        var dirtyCount = await _local.CountDirtyAsync(_session);
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
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"Post-login revision check failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ShouldRefreshCurrentBusinessScopeAfterPostLoginAsync()
        => await _local.IsServerMirrorRefreshRequiredAsync();

    private async Task<bool> HasPersistedSyncRevisionAsync(CancellationToken ct = default)
    {
        var revisionRaw = await _local.GetSettingAsync("LastSyncRevision", ct);
        return long.TryParse(revisionRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) && revision > 0;
    }

    private async Task<DateTimeOffset?> GetLastSuccessfulSyncAtAsync()
    {
        var raw = await _local.GetSettingAsync("Sync.LastSuccessAt");
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;
    }

    // Customer Filter (Left Panel)
    private async Task LoadCustomersAsync()
    {
        var selectedCustomerId = SelectedCustomerFilter?.Id;
        _allCustomers = await _local.GetCustomersAsync(_session);
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
        SelectedCustomerFilter = null;
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

        var ids = await GetFavoriteInvoiceIdsAsync();
        if (ids.Contains(SelectedInvoiceRow.Id))
            ids.Remove(SelectedInvoiceRow.Id);
        else
            ids.Insert(0, SelectedInvoiceRow.Id);

        await SaveFavoriteInvoiceIdsAsync(ids);
        await LoadInvoiceFavoritesAsync();
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
            var invoice = await _local.GetInvoiceAsync(targetId, _session);
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
        var version = Interlocked.Increment(ref _invoicePreviewVersion);
        UiTaskHelper.Forget(
            LoadPreviewAsync(row, version),
            "MAIN",
            "전표 미리보기 로드",
            ex =>
            {
                if (IsCurrentInvoicePreview(version))
                    AppLogger.Warn("MAIN", $"전표 미리보기 로드 실패: {ex.Message}");
            });
    }

    private async Task LoadPreviewAsync(InvoiceListRow? row, int version)
    {
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
                await RefreshCustomerFinancialPreviewAsync(null);
                RequestRefreshPreviewCustomerContract(null);
            }
            return;
        }

        var inv = await _local.GetInvoiceAsync(row.Id, _session);
        if (!IsCurrentInvoicePreview(version))
            return;

        if (inv is null)
        {
            if (SelectedCustomerFilter is null)
                await RefreshCustomerFinancialPreviewAsync(null);
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
                ?? await _local.GetCustomerAsync(inv.CustomerId);
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

                await RefreshCustomerFinancialPreviewAsync(customer);
                RequestRefreshPreviewCustomerContract(customer);
            }
            else
            {
                await RefreshCustomerFinancialPreviewAsync(null);
                RequestRefreshPreviewCustomerContract(null);
            }
        }
    }

    private bool IsCurrentInvoicePreview(int version)
        => version == Volatile.Read(ref _invoicePreviewVersion);

    // Invoice List
    [RelayCommand]
    private async Task LoadInvoiceListAsync()
    {
        _invoiceListLoadCts?.Cancel();
        var loadCts = new CancellationTokenSource();
        _invoiceListLoadCts = loadCts;
        var ct = loadCts.Token;
        var gateEntered = false;
        var previouslySelectedInvoiceId = SelectedInvoiceRow?.Id;

        try
        {
            await _invoiceListLoadGate.WaitAsync(ct);
            gateEntered = true;
            if (!ReferenceEquals(_invoiceListLoadCts, loadCts))
                return;

            Guid? customerId = SelectedCustomerFilter?.Id;
            var queryDateRange = ResolveMainInvoiceQueryDateRange(FilterFrom, FilterTo);
            var invoiceList = await _local.GetInvoiceListSummariesAsync(queryDateRange.From, queryDateRange.To, customerId, _session, ct);
            var canReuseAsAllInvoiceSet = customerId is null && queryDateRange.From is null && queryDateRange.To is null;
            var customerMap = await BuildInvoiceCustomerNameMapAsync(invoiceList, ct);
            var showCustomerName = customerId is null;
            IEnumerable<LocalInvoiceListSummary> filteredInvoices = invoiceList;
            var hiddenTextFilters = NormalizeHiddenInvoiceTextFilters(
                FilterCustomerName,
                FilterMinAmountText,
                FilterMaxAmountText);

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

            if (!string.Equals(SelectedVoucherTypeFilter, "전체", StringComparison.OrdinalIgnoreCase))
            {
                var selectedType = SelectedVoucherTypeFilter switch
                {
                    "매출" => VoucherType.Sales,
                    "매입" => VoucherType.Purchase,
                    "발주" => VoucherType.Procurement,
                    "경비" => VoucherType.Expense,
                    "수금" => VoucherType.Collection,
                    _ => (VoucherType?)null
                };

                if (selectedType is { } type)
                    filteredInvoices = filteredInvoices.Where(inv => inv.VoucherType == type);
            }

            var minAmount = ParseAmountFilter(hiddenTextFilters.MinAmountText);
            var maxAmount = ParseAmountFilter(hiddenTextFilters.MaxAmountText);
            if (minAmount.HasValue)
                filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount >= minAmount.Value);
            if (maxAmount.HasValue)
                filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount <= maxAmount.Value);

            var finalInvoices = filteredInvoices
                .OrderByDescending(i => i.InvoiceDate)
                .ThenByDescending(i => i.InvoiceNumber)
                .ToList();

            var rows = finalInvoices.Select(inv =>
            {
                var custName = customerMap.TryGetValue(inv.CustomerId, out var n) ? n : "(미지정)";
                return InvoiceListRow.From(inv, custName, showCustomerName);
            }).ToList();
            InvoiceRows.ReplaceWith(rows);
            RestoreSelectedInvoiceAfterListReload(previouslySelectedInvoiceId);

            await RefreshDashboardMetricsAsync(canReuseAsAllInvoiceSet ? invoiceList : null, ct);
            await LoadInvoiceFavoritesAsync(canReuseAsAllInvoiceSet ? invoiceList : null, ct);
            ct.ThrowIfCancellationRequested();
            await RefreshSelectedCustomerFinancialPreviewAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (gateEntered)
                _invoiceListLoadGate.Release();
            if (ReferenceEquals(_invoiceListLoadCts, loadCts))
                _invoiceListLoadCts = null;
            loadCts.Dispose();
        }
    }

    private void RestoreSelectedInvoiceAfterListReload(Guid? previouslySelectedInvoiceId)
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

        if (SelectedInvoiceRow is not null)
            SelectedInvoiceRow = null;
    }

    private async Task<Dictionary<Guid, string>> BuildInvoiceCustomerNameMapAsync(IEnumerable<LocalInvoiceListSummary> invoices, CancellationToken ct)
    {
        var customerIds = invoices
            .Select(invoice => invoice.CustomerId)
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

    private async Task RefreshDashboardMetricsAsync(IEnumerable<LocalInvoiceListSummary>? invoices = null, CancellationToken ct = default)
    {
        var sourceInvoices = invoices?.ToList()
            ?? await _local.GetInvoiceListSummariesAsync(from: null, to: null, customerId: null, session: _session, ct);
        var now = DateOnly.FromDateTime(DateTime.Today);
        var prevMonthDate = now.AddMonths(-1);

        var monthlySales = sourceInvoices
            .Where(i => i.VoucherType == VoucherType.Sales
                     && i.InvoiceDate.Year == now.Year
                     && i.InvoiceDate.Month == now.Month)
            .Sum(i => i.TotalAmount);

        var previousMonthlySales = sourceInvoices
            .Where(i => i.VoucherType == VoucherType.Sales
                     && i.InvoiceDate.Year == prevMonthDate.Year
                     && i.InvoiceDate.Month == prevMonthDate.Month)
            .Sum(i => i.TotalAmount);

        var monthlyInvoiceCount = sourceInvoices.Count(i =>
            i.InvoiceDate.Year == now.Year && i.InvoiceDate.Month == now.Month);

        DashboardMonthlySales = monthlySales;
        DashboardMonthlyInvoiceCount = monthlyInvoiceCount;
        DashboardMonthlyAverageSales = monthlyInvoiceCount == 0
            ? 0
            : Math.Round(monthlySales / monthlyInvoiceCount, 0, MidpointRounding.AwayFromZero);
        DashboardSalesTrendPercent = previousMonthlySales == 0
            ? (monthlySales > 0 ? 100m : 0m)
            : Math.Round(((monthlySales - previousMonthlySales) / previousMonthlySales) * 100m, 1, MidpointRounding.AwayFromZero);

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
    }

    private void HandleInvoiceFilterChanged()
    {
        if (_suppressFilterAutoSave)
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
            UiTaskHelper.Forget(
                ApplyInvoiceFiltersAsync(version, token),
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

        await PersistInvoiceFiltersAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!IsCurrentInvoiceFilterApply(version))
            return;

        await LoadInvoiceListAsync();
    }

    private bool IsCurrentInvoiceFilterApply(int version)
        => version == Volatile.Read(ref _invoiceFilterApplyVersion);

    private Task PersistInvoiceFiltersAsync()
        => PersistInvoiceFiltersAsync(CancellationToken.None);

    private async Task PersistInvoiceFiltersAsync(CancellationToken ct)
    {
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterCustomerSettingKey), FilterCustomerName ?? string.Empty, ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterVoucherTypeSettingKey), SelectedVoucherTypeFilter ?? "전체", ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterOfficeCodeSettingKey), SelectedInvoiceOfficeFilterCode ?? GetDefaultInvoiceOfficeFilterCode(), ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMinAmountSettingKey), FilterMinAmountText ?? string.Empty, ct);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMaxAmountSettingKey), FilterMaxAmountText ?? string.Empty, ct);
    }

    private async Task LoadInvoiceFilterSettingsAsync()
    {
        _suppressFilterAutoSave = true;

        InitializeInvoiceOfficeFilterOptions();
        var customerNameValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterCustomerSettingKey));
        var voucherTypeValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterVoucherTypeSettingKey));
        var minAmountValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMinAmountSettingKey));
        var maxAmountValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMaxAmountSettingKey));
        var hadPersistedHiddenTextFilter = HasHiddenInvoiceTextFilter(customerNameValue, minAmountValue, maxAmountValue);
        var hiddenTextFilters = NormalizeHiddenInvoiceTextFilters(customerNameValue, minAmountValue, maxAmountValue);
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

        _suppressFilterAutoSave = false;

        if (hadPersistedHiddenTextFilter)
            await PersistInvoiceFiltersAsync();
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

    private async Task RefreshInvoiceDefaultDateRangeFromDataAsync()
    {
        var (firstDate, lastDate) = await _local.GetInvoiceDateRangeAsync(_session);
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

    private async Task<List<Guid>> GetFavoriteInvoiceIdsAsync()
    {
        var raw = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(FavoriteInvoiceIdsSettingKey));
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

    private Task SaveFavoriteInvoiceIdsAsync(IEnumerable<Guid> ids)
    {
        var payload = string.Join(',', ids.Select(id => id.ToString("D")));
        return _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(FavoriteInvoiceIdsSettingKey), payload);
    }

    private async Task LoadInvoiceFavoritesAsync(IEnumerable<LocalInvoiceListSummary>? sourceInvoices = null, CancellationToken ct = default)
    {
        var selectedId = SelectedFavoriteInvoice?.InvoiceId;
        var ids = await GetFavoriteInvoiceIdsAsync();
        var allInvoices = sourceInvoices?.ToList()
            ?? await _local.GetInvoiceListSummariesAsync(from: null, to: null, customerId: null, session: _session, ct);
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
            await SaveFavoriteInvoiceIdsAsync(FavoriteInvoices.Select(f => f.InvoiceId));

        SelectedFavoriteInvoice = selectedId.HasValue
            ? FavoriteInvoices.FirstOrDefault(f => f.InvoiceId == selectedId.Value)
            : FavoriteInvoices.FirstOrDefault();
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
        var inv = await _local.GetInvoiceAsync(SelectedInvoiceRow.Id, _session);
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
        await LoadInvoiceListAsync();
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
                "삭제할 전표를 선택하세요.",
                "알림",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var targetText = rows.Count == 1
            ? "선택한 전표 1건"
            : $"선택한 전표 {rows.Count:N0}건";

        var confirm = System.Windows.MessageBox.Show(
            $"{targetText}을 삭제하시겠습니까?{Environment.NewLine}삭제된 전표는 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "전표 삭제 확인",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.OK)
            return;

        var deletedCount = 0;
        foreach (var row in rows)
        {
            var result = await _local.DeleteInvoiceAsync(row.Id, _session, row.Revision);
            if (!result.Success)
            {
                await LoadInvoiceListAsync();
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
        await LoadInvoiceListAsync();
        var completedMessage = deletedCount == 1
            ? "전표를 삭제했습니다."
            : $"전표 {deletedCount:N0}건을 삭제했습니다.";

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
        PaymentInvoice = SelectedInvoiceRow;

        var inv = await _local.GetInvoiceAsync(SelectedInvoiceRow.Id, _session);
        if (inv is null) return;

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

        var targetInvoice = await _local.GetInvoiceAsync(PaymentInvoice.Id, _session);
        if (targetInvoice is null)
            return;

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

        var inv = await _local.GetInvoiceAsync(PaymentInvoice.Id, _session);
        if (inv is not null) RecalcPaymentTotals(inv);
        await LoadInvoiceListAsync();
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

            var inv = await _local.GetInvoiceAsync(target.Id, _session);
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

            previewWindow.ShowDialog();
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
    private async Task LoadCompanyProfileAsync()
    {
        var profile = await _local.GetCompanyProfileAsync(_session);
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
            Email = CompanyEmail,
            BankAccountText = CompanyBankAccountText,
            StampImage = CompanyStampImage
        };

        await _local.SaveCompanyProfileAsync(profile);
        await LoadCompanyProfileAsync();
        System.Windows.MessageBox.Show("회사 정보가 저장되었습니다.", "알림", System.Windows.MessageBoxButton.OK);
    }

    [RelayCommand]
    private void SelectStampImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "직인 이미지 선택",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dlg.ShowDialog() != true) return;
        CompanyStampImage = File.ReadAllBytes(dlg.FileName);
        CompanyStampImagePath = "(이미지 있음)";
    }

    [RelayCommand]
    private void ClearStampImage()
    {
        CompanyStampImage = null;
        CompanyStampImagePath = "(없음)";
    }

    private async Task LoadLegacyMigrationSettingsAsync()
    {
        var defaultDb = GetDefaultLegacySourceDbPath();
        var defaultCustomerExcel = Path.Combine(AppPaths.UserDownloadsDir, "거래처 목록.xlsx");
        var defaultItemExcel = Path.Combine(AppPaths.UserDownloadsDir, "제품 목록.xlsx");

        LegacySourceDbPath = await _local.GetSettingAsync(LegacySourceDbPathSettingKey) ?? defaultDb;
        LegacyCustomerExcelPath = await _local.GetSettingAsync(LegacyCustomerExcelPathSettingKey) ?? defaultCustomerExcel;
        LegacyItemExcelPath = await _local.GetSettingAsync(LegacyItemExcelPathSettingKey) ?? defaultItemExcel;

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

        if (dialog.ShowDialog() != true)
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

        if (dialog.ShowDialog() != true)
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

        if (dialog.ShowDialog() != true)
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
            await LoadCustomersAsync();
            await LoadInvoiceListAsync();

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
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();
    }

    // Sync
    public async Task ReloadAfterPassiveSyncAsync()
    {
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();
    }

    [RelayCommand]
    private async Task ForceSyncAsync()
    {
        SyncStatus = "수동 동기화 중...";
        var syncOk = await RunIsolatedSyncAsync(sync => sync.TrySyncAsync());
        var dirtyCount = await _local.CountDirtyAsync(_session);
        if (syncOk && dirtyCount == 0)
            await RunIsolatedSyncAsync(sync => sync.RefreshSharedMirrorFromServerAsync());
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();

        SyncStatus = dirtyCount > 0
            ? await _local.GetPendingSyncWaitingMessageAsync(_session, "동기화 작업은 완료됐지만", CancellationToken.None)
                ?? $"동기화 작업은 완료됐지만 서버 반영 대기 데이터 {dirtyCount:N0}건이 남아 있습니다."
            : syncOk
                ? $"동기화 완료 {DateTime.Now:HH:mm:ss}"
                : "동기화가 완료되었지만 확인이 필요한 항목이 남아 있습니다. 동기화 진단을 확인하세요.";
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
