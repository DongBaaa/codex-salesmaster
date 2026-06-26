using System.Net;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileConnectionTestService
{
    private static readonly TimeSpan ConnectionTestTimeout = TimeSpan.FromSeconds(8);

    private readonly SettingsService _settings;
    private readonly HttpClient _http = new();

    public MobileConnectionTestService(SettingsService settings)
    {
        _settings = settings;
    }

    public async Task<MobileConnectionTestResult> TestAsync(string? baseUrl, CancellationToken ct = default)
    {
        string normalizedBaseUrl;
        try
        {
            normalizedBaseUrl = _settings.NormalizeBaseUrlForConnectionTest(baseUrl);
        }
        catch (ArgumentException ex)
        {
            return MobileConnectionTestResult.Fail(string.Empty, ex.Message);
        }

        var healthUri = new Uri(new Uri(normalizedBaseUrl.TrimEnd('/') + "/"), "healthz");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConnectionTestTimeout);

            using var response = await _http.GetAsync(healthUri, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return MobileConnectionTestResult.Success(
                    normalizedBaseUrl,
                    $"연결 테스트 성공: {healthUri}");
            }

            var statusCode = (int)response.StatusCode;
            var hint = response.StatusCode == HttpStatusCode.NotFound
                ? "서버 주소는 열렸지만 /healthz 경로가 없습니다."
                : $"서버가 {(int)response.StatusCode} {response.ReasonPhrase} 응답을 반환했습니다.";
            return MobileConnectionTestResult.Fail(
                normalizedBaseUrl,
                $"연결 테스트 실패: {hint}",
                statusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MobileConnectionTestResult.Fail(
                normalizedBaseUrl,
                $"연결 테스트 실패: {ConnectionTestTimeout.TotalSeconds:0}초 안에 응답이 없습니다.");
        }
        catch (Exception ex)
        {
            return MobileConnectionTestResult.Fail(
                normalizedBaseUrl,
                $"연결 테스트 실패: {ex.Message}");
        }
    }
}

public sealed record MobileConnectionTestResult(
    bool IsSuccess,
    string NormalizedBaseUrl,
    string Message,
    int? StatusCode)
{
    public static MobileConnectionTestResult Success(string normalizedBaseUrl, string message)
        => new(true, normalizedBaseUrl, message, null);

    public static MobileConnectionTestResult Fail(string normalizedBaseUrl, string message, int? statusCode = null)
        => new(false, normalizedBaseUrl, message, statusCode);
}
