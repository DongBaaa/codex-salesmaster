using GeoraePlan.Mobile.App.Models;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileRealtimeSyncService : IDisposable
{
    private readonly SessionStore _sessionStore;
    private readonly GeoraePlanApiClient _api;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _worker;

    public MobileRealtimeSyncService(
        SessionStore sessionStore,
        GeoraePlanApiClient api,
        SyncCoordinator syncCoordinator,
        MobileRefreshCoordinator refreshCoordinator)
    {
        _sessionStore = sessionStore;
        _api = api;
        _syncCoordinator = syncCoordinator;
        _refreshCoordinator = refreshCoordinator;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
                return;

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _worker = null;
        }

        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch
        {
            // ignore shutdown race
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    continue;
                }

                var state = await _syncCoordinator.LoadAsync(ct).ConfigureAwait(false);
                state.Normalize();

                var status = await _api.WaitForSyncChangeAsync(
                    state.LastRevision,
                    TimeSpan.FromSeconds(25),
                    ct).ConfigureAwait(false);

                if (status is null || status.CurrentServerRevision <= state.LastRevision)
                    continue;

                var synced = await _syncCoordinator
                    .RefreshIfServerChangedAsync("mobile-realtime", TimeSpan.Zero, ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(synced.LastError))
                {
                    _refreshCoordinator.MarkAllChanged();
                    MobileAppLogger.Info("SYNC", $"실시간 변경 감지 후 모바일 동기화 완료 rev={synced.LastRevision:N0}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MobileAppLogger.Warn("SYNC", $"실시간 변경 감지 실패: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
