using System.Text;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

internal static class MobileUpgradeRequiredResponseParser
{
    private const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumVersionLength = 64;
    private const int MaximumUrlLength = 2048;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow
        };

    public static async Task<ClientUpgradeRequiredResponse>
        ParseOrFallbackAsync(
            HttpContent? content,
            CancellationToken ct = default)
    {
        if (content is null ||
            content.Headers.ContentLength is > MaximumBodyBytes)
        {
            return CreateFallback();
        }

        try
        {
            var body = await ReadBoundedAsync(content, ct)
                .ConfigureAwait(false);
            if (body is null || body.Length == 0)
                return CreateFallback();

            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return CreateFallback();

            var parsed =
                JsonSerializer.Deserialize<ClientUpgradeRequiredResponse>(
                    document.RootElement.GetRawText(),
                    JsonOptions);
            return parsed is null
                ? CreateFallback()
                : Sanitize(parsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is JsonException or IOException or
                  DecoderFallbackException or NotSupportedException or
                  HttpRequestException or InvalidOperationException or
                  OperationCanceledException)
        {
            return CreateFallback();
        }
    }

    public static async Task<MobileClientUpgradeRequiredException>
        CreateExceptionAndPublishAsync(
            string requestPath,
            HttpContent? content,
            CancellationToken ct = default)
    {
        var response = await ParseOrFallbackAsync(content, ct)
            .ConfigureAwait(false);
        var exception = new MobileClientUpgradeRequiredException(
            requestPath,
            response);
        MobileClientUpgradeRequiredSignal.Publish(exception);
        return exception;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken ct)
    {
        await using var stream =
            await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ClientUpgradeRequiredResponse Sanitize(
        ClientUpgradeRequiredResponse parsed)
    {
        var required =
            parsed.Required ?? new ClientCompatibilityPolicyDto();
        var client =
            parsed.Client ?? new ClientCompatibilityIdentityDto();
        return new ClientUpgradeRequiredResponse
        {
            Error = "client_upgrade_required",
            // The transport body is not a trusted user-facing error surface.
            // Policy fields are normalized below; display text stays local.
            Message =
                "The server requires a supported TradePlan client update.",
            Upgrade = SanitizeToken(
                parsed.Upgrade,
                "georaeplan-client",
                maximumLength: 64),
            Client = new ClientCompatibilityIdentityDto
            {
                AppId = SanitizeToken(
                    client.AppId,
                    string.Empty,
                    maximumLength: 128),
                Platform = SanitizeToken(
                    client.Platform,
                    string.Empty,
                    maximumLength: 32),
                Version = SanitizeVersion(client.Version),
                Build = PositiveOrZero(client.Build),
                ProtocolVersion =
                    PositiveOrZero(client.ProtocolVersion)
            },
            Required = new ClientCompatibilityPolicyDto
            {
                PolicyVersion =
                    PositiveOrZero(required.PolicyVersion),
                RequiresUserAction = true,
                MinimumVersion =
                    SanitizeVersion(required.MinimumVersion),
                MinimumBuild = PositiveOrNull(
                    required.MinimumBuild),
                MinimumProtocolVersion = PositiveOrNull(
                    required.MinimumProtocolVersion),
                LatestVersion =
                    SanitizeVersion(required.LatestVersion),
                LatestBuild = PositiveOrNull(
                    required.LatestBuild),
                UpdateUrl = SanitizeUpdateUrl(required.UpdateUrl)
            }
        };
    }

    private static ClientUpgradeRequiredResponse CreateFallback()
        => new()
        {
            Error = "client_upgrade_required",
            Message =
                "The server requires a supported TradePlan client update.",
            Upgrade = "georaeplan-client",
            Required = new ClientCompatibilityPolicyDto
            {
                RequiresUserAction = true
            }
        };

    private static string SanitizeToken(
        string? value,
        string fallback,
        int maximumLength)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .Take(maximumLength)
            .Where(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_')
            .ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static string SanitizeVersion(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0 ||
            candidate.Length > MaximumVersionLength ||
            !Version.TryParse(candidate, out var parsed) ||
            parsed.Major < 0)
        {
            return string.Empty;
        }

        return parsed.ToString();
    }

    private static string SanitizeUpdateUrl(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0 ||
            candidate.Length > MaximumUrlLength ||
            candidate.Any(char.IsControl))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(
                candidate,
                UriKind.Absolute,
                out var absolute))
        {
            return string.Equals(
                    absolute.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                ? absolute.ToString()
                : string.Empty;
        }

        return Uri.TryCreate(candidate, UriKind.Relative, out _) &&
               !candidate.StartsWith("//", StringComparison.Ordinal)
            ? candidate
            : string.Empty;
    }

    private static int PositiveOrZero(int value)
        => value > 0 ? value : 0;

    private static int? PositiveOrNull(int? value)
        => value is > 0 ? value : null;
}
