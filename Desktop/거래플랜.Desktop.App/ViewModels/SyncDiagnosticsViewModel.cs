using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class SyncDiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly SyncDiagnosticsService _diagnostics;
    private readonly SyncService _sync;
    private readonly LocalStateService _local;
    private readonly RentalStateService _rental;
    private readonly SessionState _session;
    private int _reloadVersion;

    public ObservableCollection<SyncDiagnosticListItem> Events { get; } = new();
    public IReadOnlyList<string> CategoryOptions { get; } = ["전체", "권한/범위 오류", "참조 누락 오류", "동시성 충돌", "통신 오류", "서버 처리 오류", "시작 복구 오류", "저장/동기화 오류"];
    public IReadOnlyList<string> StatusOptions { get; } = ["전체", "Open", "Resolved", "Recovered"];
    public IReadOnlyList<string> SeverityOptions { get; } = ["전체", "Error", "Warning"];

    [ObservableProperty] private SyncDiagnosticListItem? _selectedEvent;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCategory = "전체";
    [ObservableProperty] private string _selectedStatus = "전체";
    [ObservableProperty] private string _selectedSeverity = "전체";
    [ObservableProperty] private bool _onlyRecoverable;
    [ObservableProperty] private string _summaryStatusText = "동기화 진단을 불러오는 중...";
    [ObservableProperty] private int _openIssueCount;
    [ObservableProperty] private int _recoverableIssueCount;
    [ObservableProperty] private int _totalIssueCount;
    [ObservableProperty] private string _lastSuccessText = "없음";
    [ObservableProperty] private string _lastFailureText = "없음";
    [ObservableProperty] private string _lastErrorText = "없음";
    [ObservableProperty] private string _lastRevisionText = "0";
    [ObservableProperty] private string _repairSummaryText = "자동 복구 전";
    [ObservableProperty] private string _selectedRecoveryActionTitle = "선택 오류 복구";
    [ObservableProperty] private string _selectedRecoveryActionDetail = "선택한 오류 유형에 맞는 복구 경로를 제안합니다.";

    public SyncDiagnosticsViewModel(
        SyncDiagnosticsService diagnostics,
        SyncService sync,
        LocalStateService local,
        RentalStateService rental,
        SessionState session)
    {
        _diagnostics = diagnostics;
        _sync = sync;
        _local = local;
        _rental = rental;
        _session = session;
        _diagnostics.DiagnosticsChanged += HandleDiagnosticsChanged;
        UpdateSelectedRecoveryDescription();
    }

    public async Task LoadAsync()
        => await ReloadAsync();

    public void Dispose()
        => _diagnostics.DiagnosticsChanged -= HandleDiagnosticsChanged;

    partial void OnSearchTextChanged(string value) => RequestReload();
    partial void OnSelectedCategoryChanged(string value) => RequestReload();
    partial void OnSelectedStatusChanged(string value) => RequestReload();
    partial void OnSelectedSeverityChanged(string value) => RequestReload();
    partial void OnOnlyRecoverableChanged(bool value) => RequestReload();
    partial void OnSelectedEventChanged(SyncDiagnosticListItem? value) => UpdateSelectedRecoveryDescription();

    private void HandleDiagnosticsChanged()
        => RequestReload();

    private void RequestReload()
    {
        var version = Interlocked.Increment(ref _reloadVersion);
        UiTaskHelper.Forget(ReloadAsync(version), "SYNC-DIAG", "동기화 진단 새로고침", ex =>
        {
            SummaryStatusText = $"진단 새로고침 실패: {ex.Message}";
        });
    }

    private async Task ReloadAsync(int? version = null)
    {
        var currentVersion = version ?? Interlocked.Increment(ref _reloadVersion);
        IsBusy = true;
        SummaryStatusText = "동기화 진단 정보를 불러오는 중...";

        try
        {
            var summaryTask = _diagnostics.GetSummaryAsync();
            var eventsTask = _diagnostics.GetEventsAsync(new SyncDiagnosticFilter(
                SearchText,
                SelectedCategory,
                SelectedStatus,
                SelectedSeverity,
                OnlyRecoverable));
            await Task.WhenAll(summaryTask, eventsTask);

            if (currentVersion != Volatile.Read(ref _reloadVersion))
                return;

            var summary = await summaryTask;
            var events = await eventsTask;

            OpenIssueCount = summary.OpenIssueCount;
            RecoverableIssueCount = summary.RecoverableIssueCount;
            TotalIssueCount = summary.TotalIssueCount;
            LastSuccessText = summary.LastSuccessAtUtc.HasValue
                ? summary.LastSuccessAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "없음";
            LastFailureText = summary.LastFailureAtUtc.HasValue
                ? summary.LastFailureAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "없음";
            LastErrorText = string.IsNullOrWhiteSpace(summary.LastError) ? "없음" : summary.LastError;
            LastRevisionText = summary.LastKnownSyncRevision.ToString("N0");
            SummaryStatusText = summary.OpenIssueCount == 0
                ? "현재 미해결 동기화 오류가 없습니다."
                : $"현재 미해결 동기화 오류 {summary.OpenIssueCount:N0}건, 자동 복구 가능 {summary.RecoverableIssueCount:N0}건";

            var selectedId = SelectedEvent?.Id;
            Events.Clear();
            foreach (var item in events)
                Events.Add(item);

            SelectedEvent = selectedId.HasValue
                ? Events.FirstOrDefault(item => item.Id == selectedId.Value) ?? Events.FirstOrDefault()
                : Events.FirstOrDefault();
            UpdateSelectedRecoveryDescription();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateSelectedRecoveryDescription()
    {
        var plan = BuildRecoveryPlan(SelectedEvent is null ? [] : [SelectedEvent]);
        SelectedRecoveryActionTitle = SelectedEvent is null
            ? "선택 오류 복구"
            : $"선택 오류 복구 - {plan.Title}";
        SelectedRecoveryActionDetail = SelectedEvent is null
            ? "오류를 선택하면 해당 유형에 맞는 복구 방식이 표시됩니다."
            : plan.Description;
    }

    [RelayCommand]
    private async Task RefreshAsync()
        => await ReloadAsync();

    [RelayCommand]
    private async Task RetrySyncAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            SummaryStatusText = "동기화를 다시 시도하는 중...";
            var syncOk = await _sync.TrySyncAsync();
            if (syncOk && await _local.CountDirtyAsync() == 0)
                await _sync.RefreshSharedMirrorFromServerAsync();

            await ReloadAsync();
            SummaryStatusText = syncOk ? "동기화 재시도를 완료했습니다." : "동기화 재시도는 완료되었지만 일부 오류가 남아 있습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebuildSharedCacheAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            SummaryStatusText = "공유 캐시를 재구성하는 중...";
            var ok = await _sync.RefreshSharedMirrorFromServerAsync();
            await ReloadAsync();
            SummaryStatusText = ok ? "공유 캐시 재구성을 완료했습니다." : "공유 캐시 재구성에 실패했습니다. 진단 리포트를 저장해 주세요.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunSelectedRepairAsync()
    {
        if (IsBusy || SelectedEvent is null)
            return;

        await ExecuteRepairPlanAsync([SelectedEvent], selectedOnly: true);
    }

    [RelayCommand]
    private async Task RunAutoRepairAsync()
    {
        if (IsBusy)
            return;

        var openEvents = await _diagnostics.GetEventsAsync(new SyncDiagnosticFilter(
            SearchText: string.Empty,
            Category: "전체",
            Status: "Open",
            Severity: "전체",
            OnlyRecoverable: false));

        await ExecuteRepairPlanAsync(openEvents, selectedOnly: false);
    }

    private async Task ExecuteRepairPlanAsync(IReadOnlyCollection<SyncDiagnosticListItem> events, bool selectedOnly)
    {
        if (events.Count == 0)
        {
            SummaryStatusText = selectedOnly ? "선택된 오류가 없습니다." : "복구할 미해결 오류가 없습니다.";
            return;
        }

        var plan = BuildRecoveryPlan(events);
        IsBusy = true;
        try
        {
            SummaryStatusText = selectedOnly
                ? $"{plan.Title} 복구를 수행하는 중..."
                : "미해결 동기화 오류를 유형별로 자동 복구하는 중...";

            var summaryParts = new List<string>();

            if (plan.RepairCustomerMasters)
            {
                var result = await _local.RepairDirtyCustomerMastersForSyncAsync(_session);
                summaryParts.Add($"거래처기준 {result.ScannedCount:N0}건");
            }

            if (plan.RepairCustomers)
            {
                var result = await _local.RepairDirtyCustomersForSyncAsync(_session);
                summaryParts.Add($"거래처 {result.ScannedCount:N0}건");
            }

            if (plan.RepairTransactions)
            {
                var result = await _local.RepairDirtyTransactionsForSyncAsync(_session);
                summaryParts.Add($"거래내역 {result.ScannedCount:N0}건");
            }

            if (plan.RepairInvoices)
            {
                var result = await _local.RepairDirtyInvoicesForSyncAsync(_session);
                summaryParts.Add($"전표 {result.ScannedCount:N0}건");
            }

            if (plan.RepairAttachments)
            {
                var result = await _local.RepairDirtyTransactionAttachmentsForSyncAsync(_session);
                summaryParts.Add($"증빙 {result.ScannedCount:N0}건");
            }

            if (plan.RepairPayments)
            {
                var result = await _local.RepairDirtyPaymentsForSyncAsync(_session);
                summaryParts.Add($"결제 {result.ScannedCount:N0}건");
            }

            if (plan.RepairRentalAssets)
            {
                var rentalAssetIds = (await _local.GetDirtyRentalAssetsForSyncAsync(_session))
                    .Where(item => !item.IsDeleted)
                    .Select(item => item.Id)
                    .Distinct()
                    .ToList();
                var result = rentalAssetIds.Count > 0
                    ? await _rental.RepairRentalCatalogLinksAsync(rentalAssetIds)
                    : new RentalCatalogRepairResult();
                summaryParts.Add($"렌탈자산 {result.ScannedAssetCount:N0}건");
            }

            if (plan.RefreshSharedCache)
            {
                var refreshOk = await _sync.RefreshSharedMirrorFromServerAsync();
                summaryParts.Add(refreshOk ? "공유 캐시 재구성 완료" : "공유 캐시 재구성 실패");
            }

            if (plan.RetrySync)
            {
                var syncOk = await _sync.TrySyncAsync();
                summaryParts.Add(syncOk ? "동기화 재시도 완료" : "동기화 재시도 실패");
            }

            if (plan.ExportDiagnosticReport)
            {
                var path = await _diagnostics.ExportReportAsync(events.Select(item => item.Id).ToArray());
                summaryParts.Add($"리포트 저장 {path}");
            }

            RepairSummaryText = summaryParts.Count == 0
                ? "수행할 자동 복구 작업이 없었습니다."
                : string.Join(" / ", summaryParts);

            await _diagnostics.RecordIssueAsync(
                phase: selectedOnly ? "selected-repair" : "manual-repair",
                rawMessage: $"자동 복구 실행 완료. {RepairSummaryText}",
                severity: "Warning",
                recoveryAttempted: true,
                recoverySucceeded: true);

            await ReloadAsync();
            SummaryStatusText = selectedOnly
                ? $"{plan.Title} 복구를 완료했습니다. 필요 시 동기화를 다시 시도해 주세요."
                : "미해결 오류 유형별 자동 복구를 완료했습니다. 필요 시 동기화를 다시 시도해 주세요.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static SyncRecoveryPlan BuildRecoveryPlan(IReadOnlyCollection<SyncDiagnosticListItem> events)
    {
        var plan = new SyncRecoveryPlan();
        if (events.Count == 0)
            return plan with { Title = "복구 대상 없음", Description = "복구할 진단 이벤트가 없습니다." };

        foreach (var item in events)
        {
            var entity = NormalizeToken(item.EntityName);
            var reference = NormalizeToken(item.ReferenceEntityName);
            switch (item.Category)
            {
                case "권한/범위 오류":
                    plan.RepairCustomerMasters |= entity is "customermaster" or "customercategory" or "customermastersync";
                    plan.RepairCustomers |= entity is "customer" or "invoice" or "transaction" or "payment";
                    plan.RepairInvoices |= entity == "invoice";
                    plan.RepairTransactions |= entity == "transaction";
                    plan.RepairAttachments |= entity == "transactionattachment";
                    plan.RepairPayments |= entity == "payment";
                    plan.RetrySync = true;
                    break;

                case "참조 누락 오류":
                    switch (reference)
                    {
                        case "customer":
                            plan.RepairCustomers = true;
                            if (entity == "invoice")
                                plan.RepairInvoices = true;
                            if (entity == "transaction")
                                plan.RepairTransactions = true;
                            break;
                        case "invoice":
                            plan.RepairTransactions = true;
                            plan.RepairPayments = true;
                            break;
                        case "transaction":
                            plan.RepairAttachments = true;
                            break;
                        case "item":
                            plan.RepairRentalAssets = true;
                            break;
                        default:
                            plan.RepairCustomers |= entity == "customer";
                            plan.RepairInvoices |= entity == "invoice";
                            plan.RepairTransactions |= entity == "transaction";
                            plan.RepairAttachments |= entity == "transactionattachment";
                            plan.RepairPayments |= entity == "payment";
                            plan.RepairRentalAssets |= entity == "rentalasset";
                            break;
                    }
                    plan.RetrySync = true;
                    break;

                case "동시성 충돌":
                case "시작 복구 오류":
                    plan.RefreshSharedCache = true;
                    plan.RetrySync = true;
                    break;

                case "통신 오류":
                    plan.RetrySync = true;
                    break;

                case "서버 처리 오류":
                    plan.ExportDiagnosticReport = true;
                    break;

                default:
                    plan.RepairCustomerMasters |= entity == "customermaster";
                    plan.RepairCustomers |= entity == "customer";
                    plan.RepairInvoices |= entity == "invoice";
                    plan.RepairTransactions |= entity == "transaction";
                    plan.RepairAttachments |= entity == "transactionattachment";
                    plan.RepairPayments |= entity == "payment";
                    plan.RepairRentalAssets |= entity == "rentalasset";
                    plan.RetrySync = true;
                    break;
            }

            if (entity == "inventorytransfer")
                plan.RefreshSharedCache = true;
        }

        var stepLabels = new List<string>();
        if (plan.RepairCustomerMasters) stepLabels.Add("거래처 기준 정리");
        if (plan.RepairCustomers) stepLabels.Add("거래처 범위 복구");
        if (plan.RepairInvoices) stepLabels.Add("전표 참조 복구");
        if (plan.RepairTransactions) stepLabels.Add("거래내역 참조 복구");
        if (plan.RepairAttachments) stepLabels.Add("증빙 참조 복구");
        if (plan.RepairPayments) stepLabels.Add("결제 참조 복구");
        if (plan.RepairRentalAssets) stepLabels.Add("렌탈 자산 품목 링크 복구");
        if (plan.RefreshSharedCache) stepLabels.Add("공유 캐시 재구성");
        if (plan.RetrySync) stepLabels.Add("동기화 재시도");
        if (plan.ExportDiagnosticReport) stepLabels.Add("진단 리포트 저장");

        return plan with
        {
            Title = stepLabels.Count == 0 ? "기본 복구" : stepLabels[0],
            Description = stepLabels.Count == 0
                ? "현재 선택한 오류는 자동 복구 대상이 명확하지 않아 기본 재시도만 권장됩니다."
                : $"권장 복구 순서: {string.Join(" → ", stepLabels)}"
        };
    }

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace(" ", string.Empty).ToLowerInvariant();

    private sealed record SyncRecoveryPlan
    {
        public string Title { get; init; } = "기본 복구";
        public string Description { get; init; } = "현재 선택한 오류에 대한 기본 복구를 수행합니다.";
        public bool RepairCustomerMasters { get; set; }
        public bool RepairCustomers { get; set; }
        public bool RepairInvoices { get; set; }
        public bool RepairTransactions { get; set; }
        public bool RepairAttachments { get; set; }
        public bool RepairPayments { get; set; }
        public bool RepairRentalAssets { get; set; }
        public bool RefreshSharedCache { get; set; }
        public bool RetrySync { get; set; }
        public bool ExportDiagnosticReport { get; set; }
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (IsBusy)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "동기화 진단 리포트 저장",
            Filter = "진단 리포트(JSON)|*.json",
            AddExtension = true,
            DefaultExt = ".json",
            FileName = $"sync-diagnostics-{DateTime.Now:yyyyMMdd_HHmmss}.json",
            InitialDirectory = AppPaths.DiagnosticsDir
        };

        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        try
        {
            var jsonPath = await _diagnostics.ExportReportAsync(SelectedEvent is null ? null : [SelectedEvent.Id]);
            File.Copy(jsonPath, dialog.FileName, overwrite: true);
            var generatedMarkdownPath = Path.ChangeExtension(jsonPath, ".md");
            var targetMarkdownPath = Path.ChangeExtension(dialog.FileName, ".md");
            if (File.Exists(generatedMarkdownPath))
                File.Copy(generatedMarkdownPath, targetMarkdownPath, overwrite: true);

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(dialog.FileName) ?? AppPaths.DiagnosticsDir,
                UseShellExecute = true
            });
            SummaryStatusText = $"진단 리포트를 저장했습니다: {dialog.FileName}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        _diagnostics.OpenLogFolder();
        SummaryStatusText = "로그 폴더를 열었습니다.";
    }

    [RelayCommand]
    private async Task ClearResolvedAsync()
    {
        if (IsBusy)
            return;

        if (MessageBox.Show(
                "해결 완료/복구 완료된 진단 이력을 정리하시겠습니까?",
                "동기화 진단",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _diagnostics.ClearResolvedEventsAsync();
            await ReloadAsync();
            SummaryStatusText = "해결된 진단 이력을 정리했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
