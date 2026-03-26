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
        }
        finally
        {
            IsBusy = false;
        }
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
            if (syncOk && await _local.CountDirtyAsync(_session) == 0)
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
    private async Task RunAutoRepairAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            SummaryStatusText = "문제 데이터 자동 복구를 수행하는 중...";

            var customerMasterRepair = await _local.RepairDirtyCustomerMastersForSyncAsync(_session);
            var customerRepair = await _local.RepairDirtyCustomersForSyncAsync(_session);
            var transactionRepair = await _local.RepairDirtyTransactionsForSyncAsync(_session);
            var invoiceRepair = await _local.RepairDirtyInvoicesForSyncAsync(_session);
            var attachmentRepair = await _local.RepairDirtyTransactionAttachmentsForSyncAsync(_session);
            var paymentRepair = await _local.RepairDirtyPaymentsForSyncAsync(_session);
            var rentalAssetIds = (await _local.GetDirtyRentalAssetsForSyncAsync(_session))
                .Where(item => !item.IsDeleted)
                .Select(item => item.Id)
                .Distinct()
                .ToList();
            var rentalRepair = rentalAssetIds.Count > 0
                ? await _rental.RepairRentalCatalogLinksAsync(rentalAssetIds)
                : new RentalCatalogRepairResult();

            RepairSummaryText =
                $"거래처기준 {customerMasterRepair.ScannedCount:N0}건 / 거래처 {customerRepair.ScannedCount:N0}건 / " +
                $"거래내역 {transactionRepair.ScannedCount:N0}건 / 전표 {invoiceRepair.ScannedCount:N0}건 / " +
                $"증빙 {attachmentRepair.ScannedCount:N0}건 / 결제 {paymentRepair.ScannedCount:N0}건 / " +
                $"렌탈자산 {rentalRepair.ScannedAssetCount:N0}건 점검";

            await _diagnostics.RecordIssueAsync(
                phase: "manual-repair",
                rawMessage: $"자동 복구 실행 완료. {RepairSummaryText}",
                severity: "Warning",
                recoveryAttempted: true,
                recoverySucceeded: true);

            await ReloadAsync();
            SummaryStatusText = "자동 복구를 완료했습니다. 이후 동기화를 다시 시도해 주세요.";
        }
        finally
        {
            IsBusy = false;
        }
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