using System.Net;

namespace GeoraePlan.Mobile.App.Services;

internal static class MobileRetryableNetworkFailure
{
    public static bool IsRetryable(Exception ex)
    {
        if (ex is TaskCanceledException or OperationCanceledException or TimeoutException)
            return true;

        if (IsSocketClosedOrTransportFailure(ex))
            return true;

        if (ex is HttpRequestException httpEx)
        {
            return httpEx.StatusCode is null
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
        }

        return false;
    }

    private static bool IsSocketClosedOrTransportFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException)
                return true;

            var typeName = current.GetType().FullName ?? string.Empty;
            if (typeName.Contains("SocketException", StringComparison.OrdinalIgnoreCase))
                return true;

            var message = current.Message ?? string.Empty;
            if (message.Contains("Socket closed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No route to host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string ToPendingSyncMessage(Exception ex)
    {
        return ex switch
        {
            TaskCanceledException or OperationCanceledException or TimeoutException
                => "네트워크 응답이 지연되어 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.TooManyRequests
                => "서버 요청이 일시적으로 많아 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.InternalServerError
                => "서버 오류(500)로 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.BadGateway
                => "서버 연결 관문 오류로 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.ServiceUnavailable
                => "서버가 일시적으로 응답하지 않아 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.GatewayTimeout
                => "서버 응답 시간이 초과되어 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            HttpRequestException
                => "네트워크 연결 문제로 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            IOException
                => "네트워크 연결이 끊겨 기기에 먼저 저장하고 서버 반영을 대기합니다.",
            _
                => IsSocketClosedOrTransportFailure(ex)
                    ? "네트워크 연결이 끊겨 기기에 먼저 저장하고 서버 반영을 대기합니다."
                    : string.IsNullOrWhiteSpace(ex.Message)
                    ? "일시적인 오류로 기기에 먼저 저장하고 서버 반영을 대기합니다."
                    : ex.Message
        };
    }
}
