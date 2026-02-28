using System.Net;
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
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

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
        return await ExecuteWithRetryAsync(
            operationName: "동기화 다운로드(sync/pull)",
            sendAsync: async token =>
            {
                SetAuthHeader();
                return await _http.GetAsync($"sync/pull?sinceRev={sinceRevision}", token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<SyncPullResponse>(token),
            ct);
    }

    public async Task<SyncPushResult?> PushAsync(SyncPushRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "동기화 업로드(sync/push)",
            sendAsync: async token =>
            {
                SetAuthHeader();
                return await _http.PostAsJsonAsync("sync/push", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<SyncPushResult>(token),
            ct);
    }

    private static bool ShouldRetry(HttpStatusCode code)
    {
        return code == HttpStatusCode.RequestTimeout
            || (int)code == 429
            || code == HttpStatusCode.InternalServerError
            || code == HttpStatusCode.BadGateway
            || code == HttpStatusCode.ServiceUnavailable
            || code == HttpStatusCode.GatewayTimeout;
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
            return true;

        if (ex is TimeoutException)
            return true;

        if (ex is HttpRequestException httpEx)
            return httpEx.StatusCode is null || ShouldRetry(httpEx.StatusCode.Value);

        return false;
    }

    private static async Task<string> BuildFailureMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 200)
            body = body[..200] + "...";
        return $"{(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim();
    }

    private async Task<T?> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        Func<HttpResponseMessage, CancellationToken, Task<T?>> readAsync,
        CancellationToken ct)
    {
        Exception? lastException = null;
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await sendAsync(ct);
                if (response.IsSuccessStatusCode)
                    return await readAsync(response, ct);

                var message = await BuildFailureMessageAsync(response, ct);
                var retryable = ShouldRetry(response.StatusCode) && attempt < MaxRetryCount;
                if (!retryable)
                    throw new HttpRequestException($"{operationName} 실패: {message}", null, response.StatusCode);

                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRetryCount)
            {
                lastException = ex;
                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {ex.Message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw new HttpRequestException(
            $"{operationName} 실패 (최대 재시도 {MaxRetryCount}회): {lastException?.Message}",
            lastException);
    }
}
