using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SessionStore _sessionStore;
    private readonly GeoraePlanApiClient _api;

    private string _baseUrl = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _statusMessage = "아이디와 비밀번호를 입력하세요.";
    private bool _isBusy;
    private bool _rememberUsername = true;
    private bool _rememberPassword;

    public LoginViewModel(SettingsService settings, SessionStore sessionStore, GeoraePlanApiClient api)
    {
        _settings = settings;
        _sessionStore = sessionStore;
        _api = api;
        LoginCommand = new AsyncCommand(LoginAsync);
    }

    public event Action? LoginSucceeded;

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool RememberUsername
    {
        get => _rememberUsername;
        set
        {
            if (!SetProperty(ref _rememberUsername, value))
                return;

            if (!value && RememberPassword)
                RememberPassword = false;
        }
    }

    public bool RememberPassword
    {
        get => _rememberPassword;
        set
        {
            if (!SetProperty(ref _rememberPassword, value))
                return;

            if (value && !RememberUsername)
                RememberUsername = true;
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public AsyncCommand LoginCommand { get; }

    public async Task InitializeAsync()
    {
        BaseUrl = _settings.GetBaseUrl();
        RememberUsername = _settings.GetRememberUsername();
        RememberPassword = _settings.GetRememberPassword();
        Username = RememberUsername ? _settings.GetLastUsername() : string.Empty;
        Password = RememberPassword ? await _settings.GetSavedPasswordAsync() : string.Empty;
        StatusMessage = "거래플랜 NAS에 연결해 로그인합니다.";
    }

    public async Task LoginAsync()
    {
        if (IsBusy)
            return;

        var normalizedUsername = Username.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "아이디와 비밀번호를 모두 입력하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "로그인 중...";

            var response = await _api.LoginAsync(new LoginRequest
            {
                Username = normalizedUsername,
                Password = Password
            });

            if (response is null)
            {
                StatusMessage = "로그인 실패: 아이디 또는 비밀번호를 확인하세요.";
                return;
            }

            await _sessionStore.SaveAsync(response);
            await _settings.SaveLoginPreferencesAsync(normalizedUsername, Password, RememberUsername, RememberPassword);
            StatusMessage = "로그인 성공";
            LoginSucceeded?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"로그인 오류: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (!RememberPassword)
                Password = string.Empty;
        }
    }
}
