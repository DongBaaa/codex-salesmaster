using System.Net;
using System.Net.Http;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class DesktopClientUpgradeRequiredException
    : HttpRequestException
{
    public DesktopClientUpgradeRequiredException(
        string requestPath,
        ClientUpgradeRequiredResponse response)
        : base(
            "현재 거래플랜 PC 버전으로는 서버에 저장할 수 없습니다. 필수 업데이트를 적용해 주세요.",
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
