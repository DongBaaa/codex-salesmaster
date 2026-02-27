using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Services;

/// <summary>
/// Thin wrapper around the SalesMaster server REST API.
/// </summary>
public sealed class ErpApiClient
{
    private readonly HttpClient _http;
    private readonly SessionState _session;

    public ErpApiClient(HttpClient http, SessionState session)
    {
        _http = http;
        _session = session;
    }

    private void SetAuthHeader()
    {
        if (_session.Token is not null)
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _session.Token);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────
    public async Task<LoginResponse?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("auth/login",
            new LoginRequest { Username = username, Password = password }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<LoginResponse>(ct);
    }

    // ── Sync ──────────────────────────────────────────────────────────────────
    public async Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
    {
        SetAuthHeader();
        var resp = await _http.GetAsync($"sync/pull?sinceRev={sinceRevision}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SyncPullResponse>(ct);
    }

    public async Task<SyncPushResult?> PushAsync(SyncPushRequest request, CancellationToken ct = default)
    {
        SetAuthHeader();
        var resp = await _http.PostAsJsonAsync("sync/push", request, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SyncPushResult>(ct);
    }
}
