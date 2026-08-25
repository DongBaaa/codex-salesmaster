namespace GeoraePlan.Mobile.App.Services;

internal sealed class MobilePackageDownloadClient
{
    private readonly HttpClient _http;
    private readonly MobileClientIdentityProvider _clientIdentity;

    public MobilePackageDownloadClient(
        HttpClient http,
        MobileClientIdentityProvider clientIdentity)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clientIdentity = clientIdentity ??
            throw new ArgumentNullException(nameof(clientIdentity));
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string expectedSha256,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var packageUri = request.RequestUri;
        ArgumentNullException.ThrowIfNull(packageUri);
        if (request.Method != HttpMethod.Get || !packageUri.IsAbsoluteUri)
            throw new InvalidOperationException("APK 다운로드 주소는 절대 URI여야 합니다.");

        var normalizedSha256 = (expectedSha256 ?? string.Empty).Trim();
        if (normalizedSha256.Length != 64 ||
            normalizedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "APK 다운로드 요청의 SHA256 형식이 올바르지 않습니다.");
        }

        _clientIdentity.Apply(request);
        return await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }
}
