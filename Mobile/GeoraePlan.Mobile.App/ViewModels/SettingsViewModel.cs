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
    private string _statusMessage = "모바일 앱은 거래플랜 운영 서버에 고정 연결됩니다.";
    private string _connectionModeText = "운영 서버 기본 연결";
    private string _currentVersion = string.Empty;
    private string _latestVersion = "-";
    private string _updateNotes = "새 버전 확인 대기";
    private string _updateStatusMessage = "업데이트 확인 대기";
    private string _integrityAccessText = "운영점검 권한 확인 대기";
    private bool _isUpdateAvailable;
    private bool _isCheckingForUpdate;
    private bool _isConnectionSettingsVisible;
    private bool _canViewIntegrityReport;
    private bool _canManageRecycleBin;

    public SettingsViewModel(SettingsService settings, SessionStore sessionStore, MobileAppUpdateService updateService)
    {
        _settings = settings;
        _sessionStore = sessionStore;
        _updateService = updateService;
        SaveCommand = new AsyncCommand(SaveAsync);
        ResetConnectionCommand = new AsyncCommand(ResetConnectionAsync);
        ToggleConnectionSettingsCommand = new AsyncCommand(ToggleConnectionSettingsAsync);
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

    public string ConnectionModeText
    {
        get => _connectionModeText;
        set => SetProperty(ref _connectionModeText, value);
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

    public bool IsConnectionSettingsVisible
    {
        get => _isConnectionSettingsVisible;
        set => SetProperty(ref _isConnectionSettingsVisible, value);
    }

    public string IntegrityAccessText
    {
        get => _integrityAccessText;
        set => SetProperty(ref _integrityAccessText, value);
    }

    public bool CanViewIntegrityReport
    {
        get => _canViewIntegrityReport;
        set => SetProperty(ref _canViewIntegrityReport, value);
    }

    public bool CanManageRecycleBin
    {
        get => _canManageRecycleBin;
        set => SetProperty(ref _canManageRecycleBin, value);
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ResetConnectionCommand { get; }
    public AsyncCommand ToggleConnectionSettingsCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public AsyncCommand CheckForUpdatesCommand { get; }
    public AsyncCommand InstallUpdateCommand { get; }

    public async Task LoadAsync()
    {
        BaseUrl = _settings.GetBaseUrl();
        RefreshConnectionModeText();
        StatusMessage = _settings.HasCustomBaseUrl()
            ? "고급 연결 URL을 사용 중입니다. 접속 오류가 있으면 운영 서버로 초기화하세요."
            : "기본 운영 서버로 연결 중입니다.";
        CurrentVersion = _updateService.GetCurrentVersion();
        LatestVersion = CurrentVersion;
        UpdateNotes = "새 버전 확인을 눌러 최신 APK를 조회할 수 있습니다.";
        var session = _sessionStore.GetSnapshot();
        CanViewIntegrityReport = session.CanViewIntegrityReport;
        CanManageRecycleBin = session.CanManageRecycleBin;
        IntegrityAccessText = CanViewIntegrityReport
            ? "운영 서버 무결성 결과를 읽기 전용으로 확인할 수 있습니다."
            : "운영점검은 관리자 또는 Settings.Edit 권한 계정만 사용할 수 있습니다.";
        await LoadUpdateInfoAsync(userInitiated: false);
    }

    public async Task SaveAsync()
    {
        try
        {
            await _settings.SaveBaseUrlAsync(BaseUrl);
            BaseUrl = _settings.GetBaseUrl();
            RefreshConnectionModeText();
            StatusMessage = _settings.HasCustomBaseUrl()
                ? "고급 연결 URL을 저장했습니다. 다음 요청부터 해당 서버로 연결합니다."
                : "운영 서버 기본 연결로 저장했습니다.";
        }
        catch (ArgumentException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    public async Task ResetConnectionAsync()
    {
        await _settings.ResetBaseUrlAsync();
        BaseUrl = _settings.GetDefaultBaseUrl();
        RefreshConnectionModeText();
        StatusMessage = "운영 서버 기본 연결로 초기화했습니다.";
    }

    public async Task LogoutAsync()
    {
        await _sessionStore.ClearAsync();
        StatusMessage = "로그아웃 완료";
        LoggedOut?.Invoke();
    }

    public Task CheckForUpdatesAsync()
        => LoadUpdateInfoAsync(userInitiated: true);

    private Task ToggleConnectionSettingsAsync()
    {
        IsConnectionSettingsVisible = !IsConnectionSettingsVisible;
        if (IsConnectionSettingsVisible)
            StatusMessage = "고급 연결 설정은 현장 터널/테스트 서버 확인 때만 사용하세요.";
        return Task.CompletedTask;
    }

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
            UpdateNotes = BuildUpdateNotes(result);
            UpdateStatusMessage = result.Message;
            _pendingAndroidUpdate = result.Package;
            IsUpdateAvailable = result.IsUpdateAvailable || result.RequiresImmediateUpdate;

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

    private static string BuildUpdateNotes(MobileAppUpdateCheckResult result)
    {
        var notes = string.IsNullOrWhiteSpace(result.Package?.Notes)
            ? "배포 메모가 없습니다."
            : result.Package.Notes;

        if (result.Package?.Mandatory == true)
            notes += $"{Environment.NewLine}{Environment.NewLine}필수 업데이트입니다.";

        if (!string.IsNullOrWhiteSpace(result.MinimumSupportedVersion))
            notes += $"{Environment.NewLine}서버 최소 지원 버전: {result.MinimumSupportedVersion}";

        return notes;
    }

    private void RefreshConnectionModeText()
    {
        ConnectionModeText = _settings.HasCustomBaseUrl()
            ? $"고급 연결 사용 중: {_settings.GetBaseUrl()}"
            : $"운영 서버 기본 연결: {_settings.GetDefaultBaseUrl()}";
    }
}
