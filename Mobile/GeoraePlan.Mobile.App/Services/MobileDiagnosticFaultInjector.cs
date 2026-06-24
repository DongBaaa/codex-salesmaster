using System.Net;
using Microsoft.Maui.Storage;

namespace GeoraePlan.Mobile.App.Services;

public static class MobileDiagnosticFaultInjector
{
#if DEBUG
    private const string FaultFileName = "diagnostics/next-fault.txt";

    public static async Task ThrowIfConfiguredAsync(string relative, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(FileSystem.Current.AppDataDirectory, FaultFileName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return;

            var raw = (await File.ReadAllTextAsync(path, ct)).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return;

            var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
            var mode = parts[0].Trim().ToUpperInvariant();
            var target = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            if (!string.IsNullOrWhiteSpace(target) &&
                !relative.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Delete(path);

            switch (mode)
            {
                case "NETWORK":
                    throw new HttpRequestException("네트워크 연결을 확인한 후 다시 시도해 주세요.");
                case "401":
                    throw new MobileAuthenticationException(relative, "401 Unauthorized (diagnostic fault)");
            }

            if (TryGetDiagnosticStatusCode(mode, out var statusCode))
            {
                throw new HttpRequestException(
                    $"{(int)statusCode} {statusCode} (diagnostic fault)",
                    null,
                    statusCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not HttpRequestException && ex is not MobileAuthenticationException)
        {
            MobileAppLogger.Warn("DIAG", $"진단 장애 주입 설정을 읽지 못했습니다: {ex.Message}");
        }
    }

    private static bool TryGetDiagnosticStatusCode(string mode, out HttpStatusCode statusCode)
    {
        statusCode = mode switch
        {
            "400" => HttpStatusCode.BadRequest,
            "403" => HttpStatusCode.Forbidden,
            "404" => HttpStatusCode.NotFound,
            "422" => HttpStatusCode.UnprocessableEntity,
            "500" => HttpStatusCode.InternalServerError,
            _ => default
        };

        return statusCode != default;
    }
#else
    public static Task ThrowIfConfiguredAsync(string relative, CancellationToken ct) => Task.CompletedTask;
#endif
}
