using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SessionStore
{
    private const string HasSessionKey = "session.has";
    private const string TokenKey = "session.token";
    private const string UsernameKey = "session.username";
    private const string RoleKey = "session.role";
    private const string TenantCodeKey = "session.tenant";
    private const string OfficeCodeKey = "session.office";

    public bool HasCachedSession()
        => Preferences.Default.Get(HasSessionKey, false);

    public SessionSnapshot GetSnapshot()
    {
        if (!HasCachedSession())
            return SessionSnapshot.Empty;

        return new SessionSnapshot
        {
            IsAuthenticated = true,
            Username = Preferences.Default.Get(UsernameKey, string.Empty),
            Role = Preferences.Default.Get(RoleKey, string.Empty),
            TenantCode = Preferences.Default.Get(TenantCodeKey, string.Empty),
            OfficeCode = Preferences.Default.Get(OfficeCodeKey, string.Empty)
        };
    }

    public async Task SaveAsync(LoginResponse response)
    {
        var token = response.Token ?? string.Empty;
        try
        {
            await SecureStorage.Default.SetAsync(TokenKey, token);
        }
        catch
        {
            await ClearAsync();
            throw new InvalidOperationException("보안 저장소를 사용할 수 없어 로그인 정보를 안전하게 저장하지 못했습니다.");
        }

        Preferences.Default.Set(HasSessionKey, true);
        Preferences.Default.Set(UsernameKey, response.User?.Username ?? string.Empty);
        Preferences.Default.Set(RoleKey, response.User?.Role ?? string.Empty);
        Preferences.Default.Set(TenantCodeKey, response.User?.TenantCode ?? string.Empty);
        Preferences.Default.Set(OfficeCodeKey, response.User?.OfficeCode ?? string.Empty);
    }

    public async Task<string?> GetTokenAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(TokenKey);
        }
        catch
        {
            return null;
        }
    }

#if DEBUG
    public async Task SaveDebugSnapshotAsync(string token, string username, string role, string officeCode = "", string tenantCode = "")
    {
        await SecureStorage.Default.SetAsync(TokenKey, token ?? string.Empty);
        Preferences.Default.Set(HasSessionKey, true);
        Preferences.Default.Set(UsernameKey, username ?? string.Empty);
        Preferences.Default.Set(RoleKey, role ?? string.Empty);
        Preferences.Default.Set(TenantCodeKey, tenantCode ?? string.Empty);
        Preferences.Default.Set(OfficeCodeKey, officeCode ?? string.Empty);
    }
#endif

    public Task ClearAsync()
    {
        try
        {
            SecureStorage.Default.Remove(TokenKey);
        }
        catch
        {
            // ignore
        }

        Preferences.Default.Remove(HasSessionKey);
        Preferences.Default.Remove(UsernameKey);
        Preferences.Default.Remove(RoleKey);
        Preferences.Default.Remove(TenantCodeKey);
        Preferences.Default.Remove(OfficeCodeKey);
        return Task.CompletedTask;
    }
}
