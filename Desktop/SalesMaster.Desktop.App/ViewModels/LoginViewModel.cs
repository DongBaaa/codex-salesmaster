using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly LocalStateService _local;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _showOfflineButton;

    public event Action? LoginSucceeded;

    public LoginViewModel(ErpApiClient api, SessionState session, LocalStateService local)
    {
        _api = api;
        _session = session;
        _local = local;
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
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
                result.User.Username, result.User.Role, result.User.Permissions);
            _session.SetSession(result.Token, result.User);
            LoginSucceeded?.Invoke();
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Server unreachable — offer offline mode if cache exists
            var cached = await _local.GetCachedSessionAsync(Username);
            if (cached is not null)
            {
                ErrorMessage = "서버에 연결할 수 없습니다. 오프라인 모드로 시작할 수 있습니다.";
                ShowOfflineButton = true;
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
        _session.SetOfflineSession(cached);
        LoginSucceeded?.Invoke();
    }

    private bool CanLogin() => !string.IsNullOrWhiteSpace(Username) && !IsLoading;
    private bool CanOfflineLogin() => ShowOfflineButton && !string.IsNullOrWhiteSpace(Username);

    partial void OnUsernameChanged(string value)
    {
        LoginCommand.NotifyCanExecuteChanged();
        ShowOfflineButton = false;
        ErrorMessage = string.Empty;
    }
    partial void OnIsLoadingChanged(bool value) => LoginCommand.NotifyCanExecuteChanged();
    partial void OnShowOfflineButtonChanged(bool value) => OfflineLoginCommand.NotifyCanExecuteChanged();

    private static bool IsConnectionError(Exception ex)
    {
        if (ex is HttpRequestException) return true;
        if (ex is TaskCanceledException) return true;
        if (ex.InnerException is System.Net.Sockets.SocketException) return true;
        return false;
    }
}
