using System.Text.Json;
using GeoraePlan.Mobile.App.Models;

namespace GeoraePlan.Mobile.App.Services;

public sealed class JsonSyncStateStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private string FilePath => Path.Combine(FileSystem.AppDataDirectory, "mobile-sync-state.json");

    public async Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            var fresh = new MobileSyncState();
            fresh.Normalize();
            return fresh;
        }

        await using var stream = File.OpenRead(FilePath);
        var state = await JsonSerializer.DeserializeAsync<MobileSyncState>(stream, _jsonOptions, ct)
                    ?? new MobileSyncState();

        state.Normalize();
        return state;
    }

    public async Task SaveAsync(MobileSyncState state, CancellationToken ct = default)
    {
        state.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, ct);
    }
}
