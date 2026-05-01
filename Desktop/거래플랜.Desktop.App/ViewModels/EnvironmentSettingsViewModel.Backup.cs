using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed class BackupSnapshotRow
{
    public string FilePath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string CreatedAtText { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
}

public sealed partial class EnvironmentSettingsViewModel
{
    [ObservableProperty] private BackupSnapshotRow? _selectedBackupSnapshot;
    [ObservableProperty] private string _backupDataStatus = "로컬 백업 목록을 불러오는 중...";

    public ObservableCollection<BackupSnapshotRow> BackupSnapshots { get; } = new();

    public bool CanManageBackupData => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.DataBackupRestore);

    private void InitializeBackupState()
    {
        BackupDataStatus = CanManageBackupData
            ? "현재 로컬 DB 백업과 이전 데이터 가져오기 경로를 함께 관리합니다."
            : "백업/복원은 관리자 또는 Data.BackupRestore 권한이 있는 계정만 사용할 수 있습니다.";
    }

    [RelayCommand]
    private Task ReloadBackupSnapshotsAsync()
    {
        BackupSnapshots.Clear();
        foreach (var snapshot in _backup.GetBackupSnapshots())
        {
            BackupSnapshots.Add(new BackupSnapshotRow
            {
                FilePath = snapshot.FilePath,
                DisplayName = snapshot.DisplayName,
                FileName = snapshot.FileName,
                CreatedAtText = snapshot.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SizeText = snapshot.SizeText
            });
        }

        BackupDataStatus = BackupSnapshots.Count == 0
            ? "저장된 로컬 DB 백업이 없습니다."
            : $"로컬 DB 백업 {BackupSnapshots.Count:N0}건을 불러왔습니다. 오늘 백업은 모두 보관하고 지난 날짜는 일별 최신 1개만 보관하며 30일 초과 백업은 자동 정리됩니다.";

        SelectedBackupSnapshot ??= BackupSnapshots.FirstOrDefault();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreateBackupSnapshotAsync()
    {
        if (!CanManageBackupData)
        {
            BackupDataStatus = "백업은 관리자 또는 Data.BackupRestore 권한이 있는 계정만 실행할 수 있습니다.";
            StatusMessage = BackupDataStatus;
            return;
        }

        IsBusy = true;
        try
        {
            var backupPath = await _backup.BackupNowWithPathAsync();
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                BackupDataStatus = "로컬 DB 백업을 생성하지 못했습니다.";
                StatusMessage = BackupDataStatus;
                return;
            }

            await ReloadBackupSnapshotsAsync();
            BackupDataStatus = $"로컬 DB 백업을 생성했습니다: {System.IO.Path.GetFileName(backupPath)}";
            StatusMessage = BackupDataStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (!CanManageBackupData)
        {
            BackupDataStatus = "백업 폴더 열기는 관리자 또는 Data.BackupRestore 권한이 있는 계정만 사용할 수 있습니다.";
            StatusMessage = BackupDataStatus;
            return;
        }

        _backup.OpenBackupFolder();
        BackupDataStatus = "백업 폴더를 열었습니다.";
        StatusMessage = BackupDataStatus;
    }

    [RelayCommand]
    private Task ScheduleSelectedBackupRestoreAsync()
    {
        if (!CanManageBackupData)
        {
            BackupDataStatus = "백업 복원 예약은 관리자 또는 Data.BackupRestore 권한이 있는 계정만 사용할 수 있습니다.";
            StatusMessage = BackupDataStatus;
            return Task.CompletedTask;
        }

        if (SelectedBackupSnapshot is null)
        {
            BackupDataStatus = "복원 예약할 백업을 선택하세요.";
            StatusMessage = BackupDataStatus;
            return Task.CompletedTask;
        }

        var confirm = MessageBox.Show(
            $"선택한 백업({SelectedBackupSnapshot.FileName})을 다음 실행 시 복원하도록 예약하시겠습니까?{Environment.NewLine}{Environment.NewLine}" +
            "현재 앱을 완전히 종료한 뒤 다시 실행해야 복원이 적용됩니다.",
            "백업 복원 예약",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
            return Task.CompletedTask;

        if (_backup.ScheduleRestoreOnNextStartup(SelectedBackupSnapshot.FilePath, out var message))
        {
            BackupDataStatus = message;
            StatusMessage = message;
            MessageBox.Show(message, "백업 복원 예약", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        BackupDataStatus = message;
        StatusMessage = message;
        MessageBox.Show(message, "백업 복원 예약", MessageBoxButton.OK, MessageBoxImage.Warning);
        return Task.CompletedTask;
    }
}
