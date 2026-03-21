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
    private const string ExpiresAtUtcKey = "session.expiresAtUtc";
    private static readonly TimeSpan ExpirationSkew = TimeSpan.FromMinutes(1);

    public bool HasCachedSession()
        => Preferences.Default.Get(HasSessionKey, false);

    public async Task<bool> HasUsableSessionAsync()
    {
        if (!HasCachedSession())
            return false;

        var token = await ReadStoredTokenAsync();
        if (!string.IsNullOrWhiteSpace(token) && !IsExpired(ResolveExpirationUtc(token)))
            return true;

        await ClearAsync();
        return false;
    }

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
            OfficeCode = Preferences.Default.Get(OfficeCodeKey, string.Empty),
            ExpiresAtUtc = ReadStoredExpirationUtc()
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
        Preferences.Default.Set(ExpiresAtUtcKey, response.ExpiresAtUtc.ToUniversalTime().ToString("O"));
    }

    public async Task<string?> GetTokenAsync(bool clearStaleSession = true)
    {
        if (!HasCachedSession())
            return null;

        var token = await ReadStoredTokenAsync();
        if (!string.IsNullOrWhiteSpace(token) && !IsExpired(ResolveExpirationUtc(token)))
            return token;

        if (clearStaleSession)
            await ClearAsync();

        return null;
    }

    public async Task<bool> IsTokenExpiredAsync()
    {
        if (!HasCachedSession())
            return true;

        var token = await ReadStoredTokenAsync();
        return string.IsNullOrWhiteSpace(token) || IsExpired(ResolveExpirationUtc(token));
    }

    private static async Task<string?> ReadStoredTokenAsync()
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
        Preferences.Default.Remove(ExpiresAtUtcKey);
        return Task.CompletedTask;
    }

    private DateTime? ResolveExpirationUtc(string token)
    {
        var fromToken = TryReadJwtExpirationUtc(token);
        if (fromToken.HasValue)
        {
            Preferences.Default.Set(ExpiresAtUtcKey, fromToken.Value.ToString("O"));
            return fromToken.Value;
        }

        return ReadStoredExpirationUtc();
    }

    private static DateTime? ReadStoredExpirationUtc()
    {
        var raw = Preferences.Default.Get(ExpiresAtUtcKey, string.Empty);
        return DateTime.TryParse(
            raw,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static bool IsExpired(DateTime? expiresAtUtc)
        => expiresAtUtc.HasValue && expiresAtUtc.Value <= DateTime.UtcNow.Add(ExpirationSkew);

    private static DateTime? TryReadJwtExpirationUtc(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var segments = token.Split('.');
        if (segments.Length < 2)
            return null;

        try
        {
            var payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');

            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("exp", out var expElement))
                return null;

            var expSeconds = expElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when expElement.TryGetInt64(out var numeric) => numeric,
                System.Text.Json.JsonValueKind.String when long.TryParse(expElement.GetString(), out var numeric) => numeric,
                _ => 0L
            };

            return expSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime
                : null;
        }
        catch
        {
            return null;
        }
    }
}
