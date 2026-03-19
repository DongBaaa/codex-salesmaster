using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SessionStore
{
    private const string HasSessionKey = "session.has";
    private const string TokenKey = "session.token";
    private const string UsernameKey = "session.username";
    private const string RoleKey = "session.role";

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
            Role = Preferences.Default.Get(RoleKey, string.Empty)
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
            Preferences.Default.Set(TokenKey, token);
        }

        Preferences.Default.Set(HasSessionKey, true);
        Preferences.Default.Set(UsernameKey, response.User?.Username ?? string.Empty);
        Preferences.Default.Set(RoleKey, response.User?.Role ?? string.Empty);
    }

    public async Task<string?> GetTokenAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(TokenKey)
                   ?? Preferences.Default.Get(TokenKey, string.Empty);
        }
        catch
        {
            return Preferences.Default.Get(TokenKey, string.Empty);
        }
    }

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

        Preferences.Default.Remove(TokenKey);
        Preferences.Default.Remove(HasSessionKey);
        Preferences.Default.Remove(UsernameKey);
        Preferences.Default.Remove(RoleKey);
        return Task.CompletedTask;
    }
}
