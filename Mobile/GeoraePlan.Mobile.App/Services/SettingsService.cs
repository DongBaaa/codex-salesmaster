using GeoraePlan.Mobile.App.Configuration;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SettingsService
{
    private const string BaseUrlKey = "settings.api.baseurl";
    private const string LastUsernameKey = "settings.last.username";

    public string GetBaseUrl()
        => NormalizeBaseUrl(Preferences.Default.Get(BaseUrlKey, ApiOptions.DefaultBaseUrl));

    public Task SaveBaseUrlAsync(string baseUrl)
    {
        Preferences.Default.Set(BaseUrlKey, NormalizeBaseUrl(baseUrl));
        return Task.CompletedTask;
    }

    public string GetLastUsername()
        => Preferences.Default.Get(LastUsernameKey, string.Empty);

    public Task SaveLastUsernameAsync(string username)
    {
        Preferences.Default.Set(LastUsernameKey, (username ?? string.Empty).Trim());
        return Task.CompletedTask;
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
