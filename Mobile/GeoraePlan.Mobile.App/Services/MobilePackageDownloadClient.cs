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
        Uri packageUri,
        string expectedSha256,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        if (!packageUri.IsAbsoluteUri)
            throw new InvalidOperationException("APK 다운로드 주소는 절대 URI여야 합니다.");

        var normalizedSha256 = (expectedSha256 ?? string.Empty).Trim();
        if (normalizedSha256.Length != 64 ||
            normalizedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "APK 다운로드 요청의 SHA256 형식이 올바르지 않습니다.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
        _clientIdentity.Apply(request);
        return await _http
            .SendAsync(request, completionOption, ct)
            .ConfigureAwait(false);
    }
}
