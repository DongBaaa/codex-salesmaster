using GeoraePlan.Mobile.App.Configuration;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SettingsService
{
    private const string LastUsernameKey = "settings.last.username";
    private const string RememberUsernameKey = "settings.remember.username";
    private const string RememberPasswordKey = "settings.remember.password";
    private const string SavedPasswordKey = "settings.saved.password";

    public string GetBaseUrl()
        => NormalizeBaseUrl(ApiOptions.DefaultBaseUrl);

    public Task SaveBaseUrlAsync(string baseUrl)
        => Task.CompletedTask;

    public string GetLastUsername()
        => Preferences.Default.Get(LastUsernameKey, string.Empty);

    public bool GetRememberUsername()
        => Preferences.Default.Get(RememberUsernameKey, true);

    public bool GetRememberPassword()
        => Preferences.Default.Get(RememberPasswordKey, false);

    public async Task<string> GetSavedPasswordAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(SavedPasswordKey) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task SaveLoginPreferencesAsync(string username, string password, bool rememberUsername, bool rememberPassword)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
        Preferences.Default.Set(RememberUsernameKey, rememberUsername || rememberPassword);
        Preferences.Default.Set(RememberPasswordKey, rememberPassword);

        if (rememberUsername || rememberPassword)
            Preferences.Default.Set(LastUsernameKey, normalizedUsername);
        else
            Preferences.Default.Remove(LastUsernameKey);

        if (rememberPassword)
        {
            try
            {
                await SecureStorage.Default.SetAsync(SavedPasswordKey, password ?? string.Empty);
            }
            catch
            {
                // 비밀번호는 안전한 저장소만 사용합니다.
            }
        }
        else
        {
            ClearSavedPassword();
        }
    }

    public Task SaveLastUsernameAsync(string username)
    {
        Preferences.Default.Set(LastUsernameKey, (username ?? string.Empty).Trim());
        return Task.CompletedTask;
    }

    public void ClearSavedPassword()
    {
        try
        {
            SecureStorage.Default.Remove(SavedPasswordKey);
        }
        catch
        {
            // ignore
        }
    }

    private static string NormalizeBaseUrl(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            value = ApiOptions.DefaultBaseUrl;

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return value.TrimEnd('/');
    }
}
