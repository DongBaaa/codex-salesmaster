using GeoraePlan.Mobile.App.Configuration;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SettingsService
{
    private const string BaseUrlKey = "settings.api.baseUrl";
    private const string LastUsernameKey = "settings.last.username";
    private const string RememberUsernameKey = "settings.remember.username";
    private const string RememberPasswordKey = "settings.remember.password";
    private const string SavedPasswordKey = "settings.saved.password";

    public string GetBaseUrl()
    {
        var saved = Preferences.Default.Get(BaseUrlKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            if (TryNormalizeBaseUrl(saved, out var normalizedSaved))
                return normalizedSaved;

            Preferences.Default.Remove(BaseUrlKey);
        }

        return NormalizeBaseUrl(ApiOptions.DefaultBaseUrl);
    }

    public Task SaveBaseUrlAsync(string baseUrl)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        var defaultBaseUrl = NormalizeBaseUrl(ApiOptions.DefaultBaseUrl);
        if (string.Equals(normalized, defaultBaseUrl, StringComparison.OrdinalIgnoreCase))
            Preferences.Default.Remove(BaseUrlKey);
        else
            Preferences.Default.Set(BaseUrlKey, normalized);

        return Task.CompletedTask;
    }

    public Task ResetBaseUrlAsync()
    {
        Preferences.Default.Remove(BaseUrlKey);
        return Task.CompletedTask;
    }

    public bool HasCustomBaseUrl()
        => !string.IsNullOrWhiteSpace(Preferences.Default.Get(BaseUrlKey, string.Empty));

    public string GetDefaultBaseUrl()
        => NormalizeBaseUrl(ApiOptions.DefaultBaseUrl);

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
        if (TryNormalizeBaseUrl(raw, out var normalized))
            return normalized;

        throw new ArgumentException("서버 주소는 http:// 또는 https:// 로 시작하는 올바른 URL이어야 합니다.", nameof(raw));
    }

    private static bool TryNormalizeBaseUrl(string? raw, out string normalized)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            value = ApiOptions.DefaultBaseUrl;

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        value = value.TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }
}
