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
    private string _statusMessage = "NAS 서버 주소와 계정을 입력하세요.";
    private bool _isBusy;

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

    public Task InitializeAsync()
    {
        BaseUrl = _settings.GetBaseUrl();
        Username = _settings.GetLastUsername();
        Password = string.Empty;
        return Task.CompletedTask;
    }

    public async Task LoginAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "로그인 중...";

            await _settings.SaveBaseUrlAsync(BaseUrl);
            var response = await _api.LoginAsync(new LoginRequest
            {
                Username = Username.Trim(),
                Password = Password
            });

            if (response is null)
            {
                StatusMessage = "로그인 실패: 아이디 또는 비밀번호를 확인하세요.";
                return;
            }

            await _sessionStore.SaveAsync(response);
            await _settings.SaveLastUsernameAsync(Username.Trim());
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
            Password = string.Empty;
        }
    }
}
