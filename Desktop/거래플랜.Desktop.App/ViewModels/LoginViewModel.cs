using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    private const string RememberUsernameSettingKey = "Login.RememberUsername";
    private const string RememberPasswordSettingKey = "Login.RememberPassword";
    private const string SavedUsernameSettingKey = "Login.SavedUsername";
    private const string SavedPasswordSettingKey = "Login.SavedPasswordProtected";

    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly LocalStateService _local;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _showOfflineButton;
    [ObservableProperty] private bool _rememberUsername;
    [ObservableProperty] private bool _rememberPassword;

    public event Action? LoginSucceeded;

    public LoginViewModel(ErpApiClient api, SessionState session, LocalStateService local)
    {
        _api = api;
        _session = session;
        _local = local;
    }

    public async Task InitializeAsync()
    {
        RememberUsername = string.Equals(
            await _local.GetSettingAsync(RememberUsernameSettingKey),
            "1",
            StringComparison.Ordinal);

        RememberPassword = string.Equals(
            await _local.GetSettingAsync(RememberPasswordSettingKey),
            "1",
            StringComparison.Ordinal);

        if (RememberUsername)
        {
            Username = await _local.GetSettingAsync(SavedUsernameSettingKey) ?? string.Empty;
        }

        if (RememberPassword)
        {
            Password = DecryptPassword(await _local.GetSettingAsync(SavedPasswordSettingKey));
        }
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (!CanLogin())
            return;

        IsLoading = true;
        ErrorMessage = string.Empty;
        ShowOfflineButton = false;
        try
        {
            var result = await _api.LoginAsync(Username, Password);
            if (result is null || result.Token is null)
            {
                ErrorMessage = "로그인 실패: 아이디 또는 비밀번호를 확인하세요.";
                return;
            }
            // Cache session for offline fallback
            await _local.SaveSessionCacheAsync(
                result.User.Username,
                result.User.Role,
                result.User.Permissions,
                result.User.TenantCode,
                result.User.ScopeType,
                ResolveOfficeCode(result.User),
                Password);
            await _local.SaveOfficeSyncCredentialAsync(result.User, Username, Password);
            await SaveRememberOptionsAsync();
            _session.SetSession(result.Token, result.User, result.ExpiresAtUtc);
            try
            {
                var scopeResult = await _local.RegisterLoginScopeAsync(_session);
                if (scopeResult.ScopeChanged)
                    AppLogger.Info("LOGIN", scopeResult.Message);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LOGIN", $"로그인 계정 범위 등록 중 오류가 발생했지만 로그인은 계속 진행합니다: {ex.Message}");
            }
            LoginSucceeded?.Invoke();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Server unreachable — offer offline mode if cache exists
            var cached = await _local.GetCachedSessionAsync(Username);
            if (cached is not null && await _local.VerifyCachedSessionPasswordAsync(Username, Password))
            {
                ErrorMessage = "서버에 연결할 수 없습니다. 오프라인 모드로 시작할 수 있습니다.";
                ShowOfflineButton = true;
            }
            else if (cached is not null)
            {
                ErrorMessage = "서버에 연결할 수 없고 오프라인 비밀번호 검증에 실패했습니다. 최근에 정상 로그인한 비밀번호를 입력하세요.";
            }
            else
            {
                ErrorMessage = "서버 연결 오류: 서버가 실행 중인지 확인하세요.\n" +
                               "(처음 사용 시 서버에 한 번 이상 로그인해야 오프라인 모드 사용 가능)";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"오류: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOfflineLogin))]
    private async Task OfflineLoginAsync()
    {
        var cached = await _local.GetCachedSessionAsync(Username);
        if (cached is null)
        {
            ErrorMessage = "오프라인 캐시가 없습니다.";
            return;
        }
        if (!await _local.VerifyCachedSessionPasswordAsync(Username, Password))
        {
            ErrorMessage = "오프라인 비밀번호 검증에 실패했습니다. 최근 정상 로그인한 비밀번호를 입력하세요.";
            return;
        }
        _session.SetOfflineSession(cached);
        var cachedOffice = await _local.GetCachedOfficeCodeAsync(Username);
        if (!string.IsNullOrWhiteSpace(cachedOffice))
            _session.SetOfficeCode(cachedOffice);
        await SaveRememberOptionsAsync();
        LoginSucceeded?.Invoke();
    }

    public void SubmitLogin()
    {
        if (LoginCommand.CanExecute(null))
            LoginCommand.Execute(null);
    }

    private bool CanLogin()
        => !IsLoading &&
           !string.IsNullOrWhiteSpace(Username) &&
           !string.IsNullOrWhiteSpace(Password);

    private bool CanOfflineLogin()
        => ShowOfflineButton &&
           !string.IsNullOrWhiteSpace(Username) &&
           !string.IsNullOrWhiteSpace(Password);

    partial void OnUsernameChanged(string value)
    {
        LoginCommand.NotifyCanExecuteChanged();
        ShowOfflineButton = false;
        ErrorMessage = string.Empty;
    }

    partial void OnPasswordChanged(string value)
    {
        LoginCommand.NotifyCanExecuteChanged();
        ShowOfflineButton = false;
        ErrorMessage = string.Empty;
    }

    partial void OnIsLoadingChanged(bool value) => LoginCommand.NotifyCanExecuteChanged();
    partial void OnShowOfflineButtonChanged(bool value) => OfflineLoginCommand.NotifyCanExecuteChanged();

    partial void OnRememberUsernameChanged(bool value)
    {
        if (!value && RememberPassword)
            RememberPassword = false;
    }

    partial void OnRememberPasswordChanged(bool value)
    {
        if (value && !RememberUsername)
            RememberUsername = true;
    }

    private async Task SaveRememberOptionsAsync()
    {
        await _local.SetSettingAsync(RememberUsernameSettingKey, RememberUsername ? "1" : "0");
        await _local.SetSettingAsync(RememberPasswordSettingKey, RememberPassword ? "1" : "0");

        if (RememberUsername && !string.IsNullOrWhiteSpace(Username))
            await _local.SetSettingAsync(SavedUsernameSettingKey, Username);
        else
            await _local.SetSettingAsync(SavedUsernameSettingKey, string.Empty);

        if (RememberPassword && !string.IsNullOrEmpty(Password))
            await _local.SetSettingAsync(SavedPasswordSettingKey, EncryptPassword(Password));
        else
            await _local.SetSettingAsync(SavedPasswordSettingKey, string.Empty);
    }

    private static string EncryptPassword(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DecryptPassword(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
            return string.Empty;

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedText);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsConnectionError(Exception ex)
    {
        if (ex is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(IsConnectionError);

        if (ex is HttpRequestException httpRequestException)
        {
            if (httpRequestException.StatusCode is not null)
                return false;

            return httpRequestException.InnerException is null ||
                   IsConnectionError(httpRequestException.InnerException);
        }

        if (ex is TaskCanceledException) return true;
        if (ex is System.Net.Sockets.SocketException) return true;
        if (ex.InnerException is System.Net.Sockets.SocketException) return true;
        return ex.InnerException is not null && IsConnectionError(ex.InnerException);
    }

    private static string ResolveOfficeCode(UserSessionDto user)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(user.OfficeCode, out var officeCode))
            return officeCode;

        return DomainConstants.IsAdminRole(user.Role)
            ? DomainConstants.OfficeUsenet
            : DomainConstants.OfficeYeonsu;
    }
}
