using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SessionStore _sessionStore;
    private readonly MobileAppUpdateService _updateService;
    private AppUpdatePackageDto? _pendingAndroidUpdate;

    private string _baseUrl = string.Empty;
    private string _statusMessage = "모바일 앱은 거래플랜 NAS에 고정 연결됩니다.";
    private string _currentVersion = string.Empty;
    private string _latestVersion = "-";
    private string _updateNotes = "새 버전 확인 대기";
    private string _updateStatusMessage = "업데이트 확인 대기";
    private bool _isUpdateAvailable;
    private bool _isCheckingForUpdate;

    public SettingsViewModel(SettingsService settings, SessionStore sessionStore, MobileAppUpdateService updateService)
    {
        _settings = settings;
        _sessionStore = sessionStore;
        _updateService = updateService;
        SaveCommand = new AsyncCommand(SaveAsync);
        LogoutCommand = new AsyncCommand(LogoutAsync);
        CheckForUpdatesCommand = new AsyncCommand(CheckForUpdatesAsync);
        InstallUpdateCommand = new AsyncCommand(InstallUpdateAsync);
    }

    public event Action? LoggedOut;

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        set => SetProperty(ref _currentVersion, value);
    }

    public string LatestVersion
    {
        get => _latestVersion;
        set => SetProperty(ref _latestVersion, value);
    }

    public string UpdateNotes
    {
        get => _updateNotes;
        set => SetProperty(ref _updateNotes, value);
    }

    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        set => SetProperty(ref _updateStatusMessage, value);
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set => SetProperty(ref _isUpdateAvailable, value);
    }

    public bool IsCheckingForUpdate
    {
        get => _isCheckingForUpdate;
        set => SetProperty(ref _isCheckingForUpdate, value);
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public AsyncCommand CheckForUpdatesCommand { get; }
    public AsyncCommand InstallUpdateCommand { get; }

    public async Task LoadAsync()
    {
        BaseUrl = _settings.GetBaseUrl();
        StatusMessage = "앱 연결 정보는 관리자 설정으로 고정되어 있습니다.";
        CurrentVersion = _updateService.GetCurrentVersion();
        LatestVersion = CurrentVersion;
        UpdateNotes = "새 버전 확인을 눌러 최신 APK를 조회할 수 있습니다.";
        await LoadUpdateInfoAsync(userInitiated: false);
    }

    public async Task SaveAsync()
    {
        await _settings.SaveBaseUrlAsync(BaseUrl);
        BaseUrl = _settings.GetBaseUrl();
        StatusMessage = "연결 정보는 숨김 상태로 고정되어 있습니다.";
    }

    public async Task LogoutAsync()
    {
        await _sessionStore.ClearAsync();
        StatusMessage = "로그아웃 완료";
        LoggedOut?.Invoke();
    }

    public Task CheckForUpdatesAsync()
        => LoadUpdateInfoAsync(userInitiated: true);

    public async Task InstallUpdateAsync()
    {
        if (IsCheckingForUpdate)
            return;

        if (_pendingAndroidUpdate is null || !IsUpdateAvailable)
            await LoadUpdateInfoAsync(userInitiated: true);

        if (_pendingAndroidUpdate is null || !IsUpdateAvailable)
            return;

        try
        {
            IsCheckingForUpdate = true;
            UpdateStatusMessage = $"APK {_pendingAndroidUpdate.Version} 다운로드 중...";
            var savedPath = await _updateService.DownloadAndLaunchInstallerAsync(_pendingAndroidUpdate);
            UpdateStatusMessage = "APK 다운로드 완료. 안드로이드 설치 화면을 확인하세요.";
            StatusMessage = $"설치 파일 저장 위치: {savedPath}";
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"업데이트 설치 실패: {ex.Message}";
            StatusMessage = UpdateStatusMessage;
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    private async Task LoadUpdateInfoAsync(bool userInitiated)
    {
        if (IsCheckingForUpdate)
            return;

        try
        {
            IsCheckingForUpdate = true;
            var result = await _updateService.CheckForUpdatesAsync();
            CurrentVersion = result.CurrentVersion;
            LatestVersion = result.LatestVersion;
            UpdateNotes = string.IsNullOrWhiteSpace(result.Package?.Notes)
                ? "배포 메모가 없습니다."
                : result.Package.Notes;
            UpdateStatusMessage = result.Message;
            _pendingAndroidUpdate = result.Package;
            IsUpdateAvailable = result.IsUpdateAvailable;

            if (userInitiated)
                StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _pendingAndroidUpdate = null;
            IsUpdateAvailable = false;
            UpdateStatusMessage = $"업데이트 확인 실패: {ex.Message}";
            if (userInitiated)
                StatusMessage = UpdateStatusMessage;
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }
}
