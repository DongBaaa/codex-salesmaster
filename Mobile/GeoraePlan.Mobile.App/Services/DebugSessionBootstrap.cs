#if DEBUG
using System.Text.Json;

namespace GeoraePlan.Mobile.App.Services;

internal static class DebugSessionBootstrap
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<bool> TryApplyAsync(SessionStore sessionStore)
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, "debug-session.json");
        if (!File.Exists(path))
            return false;

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<DebugSessionPayload>(json, JsonOptions);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.Token) ||
                string.IsNullOrWhiteSpace(payload.Username))
            {
                return false;
            }

            await sessionStore.SaveDebugSnapshotAsync(payload.Token, payload.Username, payload.Role ?? string.Empty)
                .ConfigureAwait(false);
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore debug cleanup failures.
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class DebugSessionPayload
    {
        public string? Token { get; set; }
        public string? Username { get; set; }
        public string? Role { get; set; }
    }
}
#endif
