using System.Text.Json;
using GeoraePlan.Mobile.App.Models;

namespace GeoraePlan.Mobile.App.Services;

public sealed class RecentItemSelectionStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private string FilePath => Path.Combine(FileSystem.AppDataDirectory, "recent-item-selections.json");

    public async Task<IReadOnlyList<RecentItemSelectionRecord>> LoadAsync(string tenantCode, string username, CancellationToken ct = default)
    {
        var state = await LoadStateAsync(ct);
        var key = BuildKey(tenantCode, username);
        return state.TryGetValue(key, out var records)
            ? records
                .OrderByDescending(record => record.SelectedAtUtc)
                .Take(5)
                .ToList()
            : [];
    }

    public async Task SaveAsync(string tenantCode, string username, IReadOnlyList<RecentItemSelectionRecord> records, CancellationToken ct = default)
    {
        var state = await LoadStateAsync(ct);
        var key = BuildKey(tenantCode, username);
        state[key] = records
            .OrderByDescending(record => record.SelectedAtUtc)
            .Take(5)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, ct);
    }

    private async Task<Dictionary<string, List<RecentItemSelectionRecord>>> LoadStateAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
            return new Dictionary<string, List<RecentItemSelectionRecord>>(StringComparer.OrdinalIgnoreCase);

        await using var stream = File.OpenRead(FilePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, List<RecentItemSelectionRecord>>>(stream, _jsonOptions, ct)
               ?? new Dictionary<string, List<RecentItemSelectionRecord>>(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildKey(string tenantCode, string username)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantCode) ? "default" : tenantCode.Trim().ToUpperInvariant();
        var user = string.IsNullOrWhiteSpace(username) ? "anonymous" : username.Trim().ToLowerInvariant();
        return $"{tenant}:{user}";
    }
}
