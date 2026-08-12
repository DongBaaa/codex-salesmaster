using System.Net;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileClientUpgradeRequiredException : HttpRequestException
{
    public MobileClientUpgradeRequiredException(
        string requestPath,
        ClientUpgradeRequiredResponse response)
        : base(
            string.IsNullOrWhiteSpace(response?.Message)
                ? "거래플랜 앱 업데이트가 필요합니다."
                : response.Message.Trim(),
            inner: null,
            HttpStatusCode.UpgradeRequired)
    {
        RequestPath = requestPath ?? string.Empty;
        Response = response ??
            throw new ArgumentNullException(nameof(response));
    }

    public string RequestPath { get; }
    public ClientUpgradeRequiredResponse Response { get; }
}
