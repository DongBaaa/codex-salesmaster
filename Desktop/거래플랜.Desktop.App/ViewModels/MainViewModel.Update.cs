using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    private DesktopAppUpdateService? _backgroundDesktopUpdateService;
    private AppUpdatePackageDto? _backgroundDesktopUpdatePackage;
    private string? _preparedDesktopUpdatePackagePath;
    private CancellationTokenSource? _backgroundDesktopUpdateCts;
    private bool _backgroundDesktopUpdateCheckStarted;

    [ObservableProperty]
    private bool _isDesktopUpdateBannerVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartPreparedDesktopUpdateCommand))]
    private bool _isDesktopUpdatePreparing;

    [ObservableProperty]
    private bool _isDesktopUpdateReady;

    [ObservableProperty]
    private string _desktopUpdateVersionText = string.Empty;

    [ObservableProperty]
    private string _desktopUpdateStatusText = string.Empty;

    [ObservableProperty]
    private string _desktopUpdateActionText = "업데이트 후 재시작";

    private DesktopAppUpdateService BackgroundDesktopUpdateService
        => _backgroundDesktopUpdateService ??= new DesktopAppUpdateService(_api);

    public void QueueBackgroundDesktopUpdateCheck()
    {
        if (_backgroundDesktopUpdateCheckStarted || _session.IsOfflineMode || !_session.IsLoggedIn || AppRuntimeInfo.IsTestRuntime)
            return;

        _backgroundDesktopUpdateCheckStarted = true;
        _backgroundDesktopUpdateCts?.Dispose();
        _backgroundDesktopUpdateCts = new CancellationTokenSource();
        var token = _backgroundDesktopUpdateCts.Token;

        UiTaskHelper.Forget(
            CheckAndPrepareDesktopUpdateAsync(token),
            "UPDATE",
            "백그라운드 PC 업데이트 확인",
            ex =>
            {
                if (ex is OperationCanceledException)
                    return;

                AppLogger.Warn("UPDATE", $"백그라운드 PC 업데이트 확인 실패: {ex.Message}");
            });
    }

    private async Task CheckAndPrepareDesktopUpdateAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        var result = await BackgroundDesktopUpdateService.CheckForUpdatesAsync(ct: ct);
        if ((!result.IsUpdateAvailable && !result.RequiresImmediateUpdate) || result.Package is null)
            return;

        _backgroundDesktopUpdatePackage = result.Package;
        UpdateDesktopUpdateUi(() =>
        {
            IsDesktopUpdateBannerVisible = true;
            IsDesktopUpdatePreparing = true;
            IsDesktopUpdateReady = false;
            DesktopUpdateVersionText = $"새 PC 버전 {result.LatestVersion}";
            DesktopUpdateStatusText = "업무 중에도 업데이트 파일을 미리 준비하고 있습니다.";
            DesktopUpdateActionText = "준비 중";
        });

        try
        {
            var progress = new Progress<DesktopUpdateDownloadProgress>(progress =>
            {
                UpdateDesktopUpdateUi(() =>
                {
                    DesktopUpdateStatusText = progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0
                        ? $"업데이트 파일 준비 중: {FormatDesktopUpdateBytes(progress.DownloadedBytes)} / {FormatDesktopUpdateBytes(progress.TotalBytes.Value)}"
                        : $"업데이트 파일 준비 중: {FormatDesktopUpdateBytes(progress.DownloadedBytes)}";
                });
            });

            var prepared = await BackgroundDesktopUpdateService.PrepareUpdatePackageAsync(result.Package, progress, ct);
            _preparedDesktopUpdatePackagePath = prepared.PackagePath;
            UpdateDesktopUpdateUi(() =>
            {
                IsDesktopUpdatePreparing = false;
                IsDesktopUpdateReady = true;
                DesktopUpdateActionText = "재시작 후 적용";
                DesktopUpdateStatusText = "다운로드가 끝났습니다. 업무 저장 후 재시작하면 새 버전으로 바로 반영됩니다.";
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UPDATE", $"백그라운드 업데이트 파일 준비 실패: {ex.Message}");
            UpdateDesktopUpdateUi(() =>
            {
                IsDesktopUpdatePreparing = false;
                IsDesktopUpdateReady = false;
                DesktopUpdateActionText = "다운로드 후 재시작";
                DesktopUpdateStatusText = "새 버전이 있습니다. 버튼을 누르면 파일 준비 후 재시작 업데이트를 진행합니다.";
            });
        }
    }

    private bool CanStartPreparedDesktopUpdate()
        => _backgroundDesktopUpdatePackage is not null && !IsDesktopUpdatePreparing;

    [RelayCommand(CanExecute = nameof(CanStartPreparedDesktopUpdate))]
    private async Task StartPreparedDesktopUpdateAsync()
    {
        var package = _backgroundDesktopUpdatePackage;
        if (package is null)
            return;

        var confirm = MessageBox.Show(
            $"PC 버전 {package.Version} 업데이트를 적용하기 위해 거래플랜을 재시작합니다.{Environment.NewLine}{Environment.NewLine}" +
            "현재 작업을 저장한 뒤 진행하세요. 업데이트 전 미동기화 자료를 먼저 확인합니다.",
            "거래플랜 PC 업데이트",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (confirm != MessageBoxResult.Yes)
            return;

        using var readinessCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            UpdateDesktopUpdateUi(() =>
            {
                IsDesktopUpdatePreparing = true;
                DesktopUpdateActionText = "확인 중";
                DesktopUpdateStatusText = "업데이트 전 미동기화 자료와 전송 대기 항목을 확인하고 있습니다.";
            });

            var readiness = await UpdateReadinessService.EnsureReadyForUpdateAsync(_local, _sync, _session, readinessCts.Token);
            if (!readiness.CanProceed)
            {
                MessageBox.Show(
                    readiness.Message,
                    "업데이트 전 동기화 확인 필요",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateDesktopUpdateUi(() =>
                {
                    IsDesktopUpdatePreparing = false;
                    DesktopUpdateActionText = IsDesktopUpdateReady ? "재시작 후 적용" : "다운로드 후 재시작";
                    DesktopUpdateStatusText = readiness.Message;
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(_preparedDesktopUpdatePackagePath) || !File.Exists(_preparedDesktopUpdatePackagePath))
            {
                var progress = new Progress<DesktopUpdateDownloadProgress>(progress =>
                {
                    UpdateDesktopUpdateUi(() =>
                    {
                        DesktopUpdateStatusText = progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0
                            ? $"업데이트 파일 준비 중: {FormatDesktopUpdateBytes(progress.DownloadedBytes)} / {FormatDesktopUpdateBytes(progress.TotalBytes.Value)}"
                            : $"업데이트 파일 준비 중: {FormatDesktopUpdateBytes(progress.DownloadedBytes)}";
                    });
                });

                UpdateDesktopUpdateUi(() =>
                {
                    DesktopUpdateActionText = "다운로드 중";
                    DesktopUpdateStatusText = "새 버전 파일을 준비하고 있습니다.";
                });

                var prepared = await BackgroundDesktopUpdateService.PrepareUpdatePackageAsync(package, progress, readinessCts.Token);
                _preparedDesktopUpdatePackagePath = prepared.PackagePath;
                UpdateDesktopUpdateUi(() => IsDesktopUpdateReady = true);
            }

            UpdateDesktopUpdateUi(() =>
            {
                DesktopUpdateActionText = "재시작 중";
                DesktopUpdateStatusText = "업데이트 도우미를 실행했습니다. 거래플랜이 종료된 뒤 새 버전으로 다시 열립니다.";
            });

            BackgroundDesktopUpdateService.StartUpdate(package, _preparedDesktopUpdatePackagePath);
            Application.Current?.Dispatcher.BeginInvoke(new Action(App.RequestShutdownForUpdate), DispatcherPriority.Send);
        }
        catch (OperationCanceledException)
        {
            UpdateDesktopUpdateUi(() =>
            {
                IsDesktopUpdatePreparing = false;
                DesktopUpdateActionText = IsDesktopUpdateReady ? "재시작 후 적용" : "다운로드 후 재시작";
                DesktopUpdateStatusText = "업데이트 준비 시간이 초과되었습니다. 잠시 후 다시 시도하세요.";
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UPDATE", $"PC 업데이트 시작 실패: {ex.Message}");
            UpdateDesktopUpdateUi(() =>
            {
                IsDesktopUpdatePreparing = false;
                DesktopUpdateActionText = IsDesktopUpdateReady ? "재시작 후 적용" : "다운로드 후 재시작";
                DesktopUpdateStatusText = $"업데이트를 시작하지 못했습니다: {ex.Message}";
            });
            MessageBox.Show(
                $"업데이트를 시작하지 못했습니다.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "거래플랜 PC 업데이트",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void DismissDesktopUpdateBanner()
    {
        if (_backgroundDesktopUpdatePackage?.Mandatory == true)
        {
            MessageBox.Show(
                "필수 업데이트는 숨길 수 없습니다. 업무 저장 후 업데이트를 적용하세요.",
                "거래플랜 PC 업데이트",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _backgroundDesktopUpdateCts?.Cancel();
        UpdateDesktopUpdateUi(() => IsDesktopUpdateBannerVisible = false);
    }

    private static string FormatDesktopUpdateBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static void UpdateDesktopUpdateUi(Action update)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(update);
            return;
        }

        update();
    }
}
