using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Views;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    [ObservableProperty] private string _syncModeText = "동기화 상태 확인 대기";
    [ObservableProperty] private string _syncDatabaseText = "-";
    [ObservableProperty] private string _syncPendingChangesText = "미동기화 변경 확인 대기";
    [ObservableProperty] private string _syncSummaryText = "동기화, 동기화 진단, 백업을 한 곳에서 실행할 수 있습니다.";
    [ObservableProperty] private string _syncLastSuccessText = "없음";
    [ObservableProperty] private string _syncLastFailureText = "없음";
    [ObservableProperty] private string _syncLastErrorText = "없음";

    private void InitializeSyncState()
    {
        SyncModeText = _session.IsOfflineMode ? "오프라인 모드" : "온라인 동기화";
        SyncDatabaseText = _session.SelectedBusinessDatabaseLabel;
        SyncPendingChangesText = "미동기화 변경 확인 대기";
        SyncSummaryText = _session.IsOfflineMode
            ? "오프라인 모드에서는 로컬 데이터 확인과 백업 중심으로 작업합니다."
            : "동기화, 동기화 진단, 백업을 이 탭에서 바로 실행할 수 있습니다.";
        SyncLastSuccessText = "없음";
        SyncLastFailureText = "없음";
        SyncLastErrorText = "없음";
    }

    private async Task RefreshSyncStateAsync()
    {
        SyncModeText = _session.IsOfflineMode ? "오프라인 모드" : "온라인 동기화";
        SyncDatabaseText = _session.SelectedBusinessDatabaseLabel;

        var pendingDirtyCount = await _local.CountDirtyAsync(_session);
        SyncPendingChangesText = pendingDirtyCount == 0
            ? "미동기화 변경 없음"
            : $"미동기화 변경 {pendingDirtyCount:N0}건";

        var summary = await _diagnostics.GetSummaryAsync();
        SyncSummaryText = summary.OpenIssueCount == 0
            ? "현재 미해결 동기화 오류가 없습니다."
            : $"현재 미해결 동기화 오류 {summary.OpenIssueCount:N0}건 / 자동 복구 가능 {summary.RecoverableIssueCount:N0}건";
        SyncLastSuccessText = summary.LastSuccessAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음";
        SyncLastFailureText = summary.LastFailureAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음";
        SyncLastErrorText = string.IsNullOrWhiteSpace(summary.LastError) ? "없음" : summary.LastError;
    }

    [RelayCommand]
    private async Task RunSyncAsync()
    {
        if (IsBusy)
            return;

        if (_session.IsOfflineMode)
        {
            StatusMessage = "오프라인 모드에서는 서버 동기화를 실행할 수 없습니다.";
            await RefreshSyncStateAsync();
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "동기화를 실행하는 중...";
            var syncOk = await _sync.TrySyncAsync();
            var dirtyCount = await _local.CountDirtyAsync(_session);
            if (syncOk && dirtyCount == 0)
                await _sync.RefreshSharedMirrorFromServerAsync();

            await RefreshSyncStateAsync();
            StatusMessage = dirtyCount > 0
                ? $"동기화 작업은 완료됐지만 서버 반영 대기 데이터 {dirtyCount:N0}건이 남아 있습니다. 동기화 진단을 확인하세요."
                : syncOk
                    ? "동기화를 완료했습니다."
                    : "동기화가 완료되었지만 일부 오류가 남아 있습니다. 동기화 진단을 확인하세요.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenSyncDiagnosticsAsync()
    {
        if (IsBusy)
            return;

        var diagnosticsViewModel = new SyncDiagnosticsViewModel(_diagnostics, _sync, _local, _rental, _session);
        await diagnosticsViewModel.LoadAsync();

        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;

        var window = new SyncDiagnosticsWindow(diagnosticsViewModel)
        {
            Owner = owner
        };
        window.ShowDialog();
        await RefreshSyncStateAsync();
        StatusMessage = "동기화 진단 창을 열었습니다.";
    }

    [RelayCommand]
    private async Task RunBackupAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var ok = await _backup.BackupNowAsync();
            await RefreshSyncStateAsync();
            StatusMessage = ok ? "백업을 완료했습니다." : "백업 중 오류가 발생했습니다.";
            MessageBox.Show(
                ok ? "백업이 완료되었습니다." : "백업 중 오류가 발생했습니다.",
                "백업",
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
