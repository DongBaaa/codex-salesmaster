using GeoraePlan.Mobile.App.Services;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SessionStore _sessionStore;

    private string _baseUrl = string.Empty;
    private string _statusMessage = "NAS 주소를 확인하세요.";

    public SettingsViewModel(SettingsService settings, SessionStore sessionStore)
    {
        _settings = settings;
        _sessionStore = sessionStore;
        SaveCommand = new AsyncCommand(SaveAsync);
        LogoutCommand = new AsyncCommand(LogoutAsync);
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

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand LogoutCommand { get; }

    public Task LoadAsync()
    {
        BaseUrl = _settings.GetBaseUrl();
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        await _settings.SaveBaseUrlAsync(BaseUrl);
        BaseUrl = _settings.GetBaseUrl();
        StatusMessage = "서버 주소 저장 완료";
    }

    public async Task LogoutAsync()
    {
        await _sessionStore.ClearAsync();
        StatusMessage = "로그아웃 완료";
        LoggedOut?.Invoke();
    }
}
