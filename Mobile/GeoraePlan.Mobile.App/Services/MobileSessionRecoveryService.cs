using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileSessionRecoveryService
{
    private static readonly TimeSpan SessionRecoveryRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly SettingsService _settings;
    private readonly SessionStore _sessionStore;
    private readonly HttpClient _httpClient = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _restoreLock = new(1, 1);

    public MobileSessionRecoveryService(SettingsService settings, SessionStore sessionStore)
    {
        _settings = settings;
        _sessionStore = sessionStore;
    }

    public async Task<SessionRecoveryResult> TryRestoreSessionAsync(string reason, CancellationToken ct = default)
        => await TryRestoreSessionAsync(reason, forceRefresh: false, ct).ConfigureAwait(false);

    public async Task<SessionRecoveryResult> TryRestoreSessionAsync(
        string reason,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        await _restoreLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
                return SessionRecoveryResult.SuccessResult($"세션이 이미 유효합니다. ({reason})");

            var username = _settings.GetLastUsername().Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                await _sessionStore.ClearAsync().ConfigureAwait(false);
                return SessionRecoveryResult.FailureResult($"저장된 아이디가 없어 세션을 복구할 수 없습니다. ({reason})");
            }

            if (!_settings.GetRememberPassword())
            {
                await _sessionStore.ClearAsync().ConfigureAwait(false);
                return SessionRecoveryResult.FailureResult($"저장된 비밀번호가 없어 자동 로그인에 실패했습니다. ({reason})");
            }

            var password = await _settings.GetSavedPasswordAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(password))
            {
                await _sessionStore.ClearAsync().ConfigureAwait(false);
                return SessionRecoveryResult.FailureResult($"저장된 비밀번호를 찾지 못해 세션을 복구할 수 없습니다. ({reason})");
            }

            var loginRequest = new LoginRequest
            {
                Username = username,
                Password = password
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("auth/login"))
            {
                Content = JsonContent.Create(loginRequest, options: _jsonOptions)
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SessionRecoveryRequestTimeout);
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await _sessionStore.ClearAsync().ConfigureAwait(false);
                return SessionRecoveryResult.FailureResult($"저장된 로그인 정보가 만료되어 자동 로그인에 실패했습니다. ({reason})");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.Length > 200)
                    body = body[..200] + "...";

                return SessionRecoveryResult.FailureResult(
                    $"세션 복구 요청이 실패했습니다. ({reason}) {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim());
            }

            var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions, ct).ConfigureAwait(false);
            if (loginResponse is null || string.IsNullOrWhiteSpace(loginResponse.Token))
            {
                await _sessionStore.ClearAsync().ConfigureAwait(false);
                return SessionRecoveryResult.FailureResult($"세션 복구 응답에 토큰이 없어 자동 로그인을 완료하지 못했습니다. ({reason})");
            }

            await _sessionStore.SaveAsync(loginResponse).ConfigureAwait(false);
            return SessionRecoveryResult.SuccessResult($"자동 로그인으로 세션을 복구했습니다. ({reason})");
        }
        catch (HttpRequestException ex)
        {
            return SessionRecoveryResult.FailureResult($"세션 복구 요청이 실패했습니다. ({reason}) {ex.Message}".Trim());
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return SessionRecoveryResult.FailureResult($"세션 복구 요청이 지연되었습니다. ({reason}) {ex.Message}".Trim());
        }
        catch (Exception ex)
        {
            return SessionRecoveryResult.FailureResult($"세션 복구 중 오류가 발생했습니다. ({reason}) {ex.Message}".Trim());
        }
        finally
        {
            _restoreLock.Release();
        }
    }

    private Uri BuildUri(string relative)
        => new(new Uri(_settings.GetBaseUrl().TrimEnd('/') + "/"), relative.TrimStart('/'));
}
