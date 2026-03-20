using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    private AppUpdatePackageDto? _pendingDesktopUpdate;

    [ObservableProperty] private string _currentAppVersion = string.Empty;
    [ObservableProperty] private string _latestDesktopVersion = "-";
    [ObservableProperty] private string _updateNotes = "새 버전 확인 대기";
    [ObservableProperty] private string _updateStatusText = "업데이트 확인 대기";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isCheckingForUpdate;
    [ObservableProperty] private DateTime? _updateReleasedAtUtc;

    public string UpdateReleasedAtText => UpdateReleasedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    partial void OnUpdateReleasedAtUtcChanged(DateTime? value)
        => OnPropertyChanged(nameof(UpdateReleasedAtText));

    private void InitializeUpdateState()
    {
        CurrentAppVersion = _updateService.GetCurrentVersion();
        LatestDesktopVersion = CurrentAppVersion;
        UpdateNotes = "새 버전 확인을 눌러 최신 배포본을 조회할 수 있습니다.";
        UpdateStatusText = "현재 설치 버전: " + CurrentAppVersion;
    }

    private async Task LoadUpdateInfoAsync(bool userInitiated)
    {
        if (IsCheckingForUpdate)
            return;

        try
        {
            IsCheckingForUpdate = true;
            var result = await _updateService.CheckForUpdatesAsync();
            CurrentAppVersion = result.CurrentVersion;
            LatestDesktopVersion = result.LatestVersion;
            IsUpdateAvailable = result.IsUpdateAvailable;
            _pendingDesktopUpdate = result.Package;
            UpdateReleasedAtUtc = result.Package?.ReleasedAtUtc;
            UpdateNotes = string.IsNullOrWhiteSpace(result.Package?.Notes)
                ? "배포 메모가 없습니다."
                : result.Package.Notes;
            UpdateStatusText = result.Message;

            if (userInitiated)
                StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _pendingDesktopUpdate = null;
            IsUpdateAvailable = false;
            UpdateStatusText = $"업데이트 확인 실패: {ex.Message}";
            if (userInitiated)
                StatusMessage = UpdateStatusText;
            거래플랜.Desktop.App.Services.AppLogger.Warn("UPDATE", $"Desktop update check failed. {ex.Message}");
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync()
        => LoadUpdateInfoAsync(userInitiated: true);

    [RelayCommand]
    private async Task StartDesktopUpdateAsync()
    {
        if (IsCheckingForUpdate)
            return;

        if (_pendingDesktopUpdate is null || !IsUpdateAvailable)
            await LoadUpdateInfoAsync(userInitiated: true);

        if (_pendingDesktopUpdate is null || !IsUpdateAvailable)
            return;

        var confirm = MessageBox.Show(
            $"새 PC 버전 {_pendingDesktopUpdate.Version}을 설치하시겠습니까?{Environment.NewLine}{Environment.NewLine}" +
            "현재 앱은 저장/동기화 후 자동으로 종료되고, 업데이트가 끝나면 다시 실행됩니다.",
            "업데이트 시작",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (confirm != MessageBoxResult.OK)
            return;

        try
        {
            _updateService.StartUpdate(_pendingDesktopUpdate);
            UpdateStatusText = $"업데이트 {_pendingDesktopUpdate.Version} 설치를 시작했습니다.";
            StatusMessage = "업데이트 도우미를 실행했습니다. 저장 후 앱을 다시 시작합니다.";
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"업데이트 시작 실패: {ex.Message}";
            StatusMessage = UpdateStatusText;
            거래플랜.Desktop.App.Services.AppLogger.Error("UPDATE", "Desktop update start failed", ex);
        }
    }
}
