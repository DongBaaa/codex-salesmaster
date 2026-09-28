using GeoraePlan.Mobile.App.Models;

// Only transport, disk and MAUI command/colour plumbing are replaced. The real
// view model, owner gate, session store and scope rules execute in these tests.
public sealed class Color { }
namespace GeoraePlan.Mobile.App.Theme
{
    public static class GeoraePlanTheme
    {
        public static Color Accent { get; } = new();
        public static Color SecondaryButton { get; } = new();
    }
}
namespace GeoraePlan.Mobile.App.ViewModels
{
    public sealed class AsyncCommand(Func<Task> execute)
    {
        public Task ExecuteAsync() => execute();
    }
}
namespace GeoraePlan.Mobile.App.Services
{
    public sealed class JsonSyncStateStore(MobileSyncState state)
    {
        public Func<Task>? BeforeLoad { get; set; }
        public async Task<MobileSyncState> LoadAsync(MobileSessionOwner owner)
        {
            if (BeforeLoad is not null) await BeforeLoad();
            return state;
        }
    }
    public sealed class SyncCoordinator(MobileSyncState state)
    {
        public Func<Task>? BeforeLoad { get; set; }
        public async Task<MobileSyncState> LoadAsync()
        {
            if (BeforeLoad is not null) await BeforeLoad();
            return state;
        }
        public Task<MobileSyncState> RefreshIfServerChangedAsync(string reason, TimeSpan age)
            => Task.FromResult(state);
        public Task<MobileSyncState> SynchronizeNowAsync() => Task.FromResult(state);
    }
}
