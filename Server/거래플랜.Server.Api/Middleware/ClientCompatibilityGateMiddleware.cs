using System.Globalization;
using System.Security.Claims;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace 거래플랜.Server.Api.Middleware;

public sealed class ClientCompatibilityGateMiddleware
{
    private const int MaximumAppIdLength = 128;
    private const int MaximumPlatformLength = 32;
    private const int MaximumVersionLength = 64;
    private const int MaximumIntegerLength = 16;

    private readonly RequestDelegate _next;
    private readonly IOptions<ClientCompatibilityOptions> _options;
    private readonly ILogger<ClientCompatibilityGateMiddleware> _logger;

    public ClientCompatibilityGateMiddleware(
        RequestDelegate next,
        IOptions<ClientCompatibilityOptions> options,
        ILogger<ClientCompatibilityGateMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHealthRequest(context.Request))
        {
            await _next(context);
            return;
        }

        var mayBlock = MayBlock(context.Request);
        var options = _options.Value;
        var parseResult = ClientCompatibilityIdentityParser.Parse(context.Request.Headers);
        if (!parseResult.Success)
        {
            if (mayBlock && options.IsStrictBlockMode)
            {
                LogOutcome(
                    context.User,
                    $"{parseResult.Outcome}_blocked",
                    identity: null);
                await WriteUpgradeRequiredAsync(
                    context,
                    identity: null,
                    policy: null);
                return;
            }

            LogOutcome(
                context.User,
                $"{parseResult.Outcome}_allowed",
                identity: null);
            await _next(context);
            return;
        }

        var identity = parseResult.Identity!;
        var policy = FindPolicy(options, identity);
        if (policy is null)
        {
            if (mayBlock && options.IsStrictBlockMode)
            {
                LogOutcome(context.User, "unknown_client_blocked", identity);
                await WriteUpgradeRequiredAsync(
                    context,
                    identity,
                    policy: null);
                return;
            }

            LogOutcome(context.User, "unknown_client_allowed", identity);
            await _next(context);
            return;
        }

        var policyEvaluation = EvaluatePolicy(identity, policy);
        if (policyEvaluation == ClientPolicyEvaluation.MalformedPolicy)
        {
            if (mayBlock && options.IsStrictBlockMode)
            {
                LogOutcome(context.User, "malformed_policy_blocked", identity);
                await WriteUpgradeRequiredAsync(
                    context,
                    identity,
                    policy: null);
                return;
            }

            LogOutcome(context.User, "malformed_policy_allowed", identity);
            await _next(context);
            return;
        }

        if (policyEvaluation == ClientPolicyEvaluation.Current)
        {
            LogOutcome(context.User, "compatible_allowed", identity);
            await _next(context);
            return;
        }

        if (!mayBlock)
        {
            LogOutcome(context.User, "known_old_exempt_allowed", identity);
            await _next(context);
            return;
        }

        if (!options.IsStrictBlockMode)
        {
            LogOutcome(context.User, "known_old_audit_allowed", identity);
            await _next(context);
            return;
        }

        var upgradeToken = NormalizeUpgradeToken(policy.UpgradeToken);
        LogOutcome(context.User, "known_old_blocked", identity);
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.Headers.Upgrade = upgradeToken;
        await context.Response.WriteAsJsonAsync(
            new ClientUpgradeRequiredResponse
            {
                Message = "이 작업을 계속하려면 거래플랜 앱을 업데이트해야 합니다.",
                Upgrade = upgradeToken,
                Client = new ClientCompatibilityIdentityDto
                {
                    AppId = identity.AppId,
                    Platform = identity.Platform,
                    Version = identity.Version,
                    Build = identity.Build,
                    ProtocolVersion = identity.ProtocolVersion
                },
                Required = new ClientCompatibilityPolicyDto
                {
                    PolicyVersion = policy.PolicyVersion,
                    // Every 426 is a hard write gate. Keep the wire contract
                    // fail-closed even if runtime configuration bypasses
                    // startup validation.
                    RequiresUserAction = true,
                    MinimumVersion = policy.MinimumVersion?.Trim() ?? string.Empty,
                    MinimumBuild = policy.MinimumBuild,
                    MinimumProtocolVersion = policy.MinimumProtocolVersion,
                    LatestVersion = policy.LatestVersion?.Trim() ?? string.Empty,
                    LatestBuild = policy.LatestBuild,
                    UpdateUrl = policy.UpdateUrl?.Trim() ?? string.Empty
                }
            },
            cancellationToken: context.RequestAborted);
    }

    private static async Task WriteUpgradeRequiredAsync(
        HttpContext context,
        ClientCompatibilityIdentity? identity,
        ClientCompatibilityPolicyOptions? policy)
    {
        var upgradeToken = NormalizeUpgradeToken(policy?.UpgradeToken);
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.Headers.Upgrade = upgradeToken;
        await context.Response.WriteAsJsonAsync(
            new ClientUpgradeRequiredResponse
            {
                Message = "A supported TradePlan client update is required.",
                Upgrade = upgradeToken,
                Client = new ClientCompatibilityIdentityDto
                {
                    AppId = identity?.AppId ?? string.Empty,
                    Platform = identity?.Platform ?? string.Empty,
                    Version = identity?.Version ?? string.Empty,
                    Build = identity?.Build ?? 0,
                    ProtocolVersion = identity?.ProtocolVersion ?? 0
                },
                Required = new ClientCompatibilityPolicyDto
                {
                    PolicyVersion = Math.Max(0, policy?.PolicyVersion ?? 0),
                    RequiresUserAction = true,
                    MinimumVersion =
                        policy?.MinimumVersion?.Trim() ?? string.Empty,
                    MinimumBuild = policy?.MinimumBuild,
                    MinimumProtocolVersion =
                        policy?.MinimumProtocolVersion,
                    LatestVersion =
                        policy?.LatestVersion?.Trim() ?? string.Empty,
                    LatestBuild = policy?.LatestBuild,
                    UpdateUrl = policy?.UpdateUrl?.Trim() ?? string.Empty
                }
            },
            cancellationToken: context.RequestAborted);
    }

    private void LogOutcome(
        ClaimsPrincipal user,
        string outcome,
        ClientCompatibilityIdentity? identity)
    {
        var logLevel = outcome switch
        {
            "compatible_allowed" => LogLevel.Debug,
            "malformed_policy_allowed" => LogLevel.Error,
            _ when outcome.EndsWith(
                "_blocked",
                StringComparison.Ordinal) => LogLevel.Warning,
            _ => LogLevel.Information
        };
        _logger.Log(
            logLevel,
            "Client compatibility outcome {Outcome}; appId={AppId}; platform={Platform}; version={Version}; build={Build}; protocol={Protocol}; tenant={Tenant}; office={Office}; userId={UserId}.",
            outcome,
            identity?.AppId ?? "missing",
            identity?.Platform ?? "missing",
            identity?.Version ?? "missing",
            identity?.Build,
            identity?.ProtocolVersion,
            NormalizeClaim(user.FindFirstValue("tenant")),
            NormalizeClaim(user.FindFirstValue("office")),
            NormalizeClaim(user.FindFirstValue(ClaimTypes.NameIdentifier)));
    }

    private static bool IsHealthRequest(HttpRequest request)
    {
        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        return string.Equals(path, "/healthz", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, "/readyz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MayBlock(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) ||
            HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        return !string.Equals(path, "/auth/login", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(path, "/auth/refresh", StringComparison.OrdinalIgnoreCase) &&
               !request.Path.StartsWithSegments("/updates", StringComparison.OrdinalIgnoreCase);
    }

    private static ClientCompatibilityPolicyOptions? FindPolicy(
        ClientCompatibilityOptions options,
        ClientCompatibilityIdentity identity)
    {
        return (options.Policies ?? [])
            .FirstOrDefault(policy =>
                policy is not null &&
                policy.Enabled &&
                string.Equals(policy.AppId?.Trim(), identity.AppId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(policy.Platform?.Trim(), identity.Platform, StringComparison.OrdinalIgnoreCase));
    }

    private static ClientPolicyEvaluation EvaluatePolicy(
        ClientCompatibilityIdentity identity,
        ClientCompatibilityPolicyOptions policy)
    {
        if (policy.PolicyVersion <= 0 ||
            policy.MinimumBuild is <= 0 ||
            policy.MinimumProtocolVersion is <= 0 ||
            policy.LatestBuild is not > 0 ||
            !IsSafeRuntimeUpdateUrl(policy.UpdateUrl) ||
            !IsSafeRuntimeToken(policy.UpgradeToken))
        {
            return ClientPolicyEvaluation.MalformedPolicy;
        }

        Version? minimumVersion = null;
        var minimumVersionText = policy.MinimumVersion?.Trim() ?? string.Empty;
        if (minimumVersionText.Length > 0 &&
            !Version.TryParse(minimumVersionText, out minimumVersion))
        {
            return ClientPolicyEvaluation.MalformedPolicy;
        }

        if (minimumVersionText.Length == 0 &&
            policy.MinimumBuild is null &&
            policy.MinimumProtocolVersion is null)
        {
            return ClientPolicyEvaluation.MalformedPolicy;
        }

        var latestVersionText = policy.LatestVersion?.Trim() ?? string.Empty;
        if (!Version.TryParse(latestVersionText, out var latestVersion) ||
            (minimumVersion is not null && latestVersion < minimumVersion) ||
            (policy.MinimumBuild is { } policyMinimumBuild &&
             policy.LatestBuild < policyMinimumBuild))
        {
            return ClientPolicyEvaluation.MalformedPolicy;
        }

        var knownOld =
            policy.MinimumBuild is { } minimumBuild &&
            identity.Build < minimumBuild;
        knownOld |=
            policy.MinimumProtocolVersion is { } minimumProtocol &&
            identity.ProtocolVersion < minimumProtocol;

        if (minimumVersion is not null)
            knownOld |= identity.ParsedVersion < minimumVersion;

        return knownOld
            ? ClientPolicyEvaluation.KnownOld
            : ClientPolicyEvaluation.Current;
    }

    private static bool IsSafeRuntimeToken(string? value)
    {
        var token = value?.Trim() ?? string.Empty;
        return token.Length is > 0 and <= 64 &&
               token.All(static character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '-' or '_');
    }

    private static bool IsSafeRuntimeUpdateUrl(string? value)
    {
        var url = value?.Trim() ?? string.Empty;
        if (url.Length == 0 ||
            url.Any(static character => char.IsControl(character)))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return string.Equals(
                absolute.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase);
        }

        return Uri.TryCreate(url, UriKind.Relative, out _) &&
               !url.StartsWith("//", StringComparison.Ordinal);
    }

    private static string NormalizeUpgradeToken(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .Take(64)
            .Where(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_')
            .ToArray());
        return normalized.Length == 0 ? "georaeplan-client" : normalized;
    }

    private static string NormalizeClaim(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .Take(64)
            .Where(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_' or '@')
            .ToArray());
        return normalized.Length == 0 ? "anonymous" : normalized;
    }

    private enum ClientPolicyEvaluation
    {
        Current,
        KnownOld,
        MalformedPolicy
    }

    internal sealed record ClientCompatibilityIdentity(
        string AppId,
        string Platform,
        string Version,
        Version ParsedVersion,
        int Build,
        int ProtocolVersion);

    internal sealed record ClientCompatibilityParseResult(
        bool Success,
        string Outcome,
        ClientCompatibilityIdentity? Identity);

    internal static class ClientCompatibilityIdentityParser
    {
        public static ClientCompatibilityParseResult Parse(IHeaderDictionary headers)
        {
            var appId = ReadSingle(headers, ClientCompatibilityHeaders.AppId, MaximumAppIdLength);
            var platform = ReadSingle(headers, ClientCompatibilityHeaders.Platform, MaximumPlatformLength);
            var version = ReadSingle(headers, ClientCompatibilityHeaders.Version, MaximumVersionLength);
            var build = ReadSingle(headers, ClientCompatibilityHeaders.Build, MaximumIntegerLength);
            var protocol = ReadSingle(headers, ClientCompatibilityHeaders.Protocol, MaximumIntegerLength);

            if (appId.Missing || platform.Missing || version.Missing || build.Missing || protocol.Missing)
            {
                return new ClientCompatibilityParseResult(false, "missing_identity", null);
            }

            if (appId.Malformed ||
                platform.Malformed ||
                version.Malformed ||
                build.Malformed ||
                protocol.Malformed ||
                !IsToken(appId.Value!, allowPlus: false) ||
                !IsToken(platform.Value!, allowPlus: false) ||
                !Version.TryParse(version.Value, out var parsedVersion) ||
                !int.TryParse(build.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBuild) ||
                parsedBuild <= 0 ||
                !int.TryParse(protocol.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedProtocol) ||
                parsedProtocol <= 0)
            {
                return new ClientCompatibilityParseResult(false, "malformed_identity", null);
            }

            return new ClientCompatibilityParseResult(
                true,
                "identity_parsed",
                new ClientCompatibilityIdentity(
                    appId.Value!.ToLowerInvariant(),
                    platform.Value!.ToLowerInvariant(),
                    version.Value!,
                    parsedVersion,
                    parsedBuild,
                    parsedProtocol));
        }

        private static HeaderValue ReadSingle(
            IHeaderDictionary headers,
            string headerName,
            int maximumLength)
        {
            if (!headers.TryGetValue(headerName, out StringValues values) || values.Count == 0)
                return HeaderValue.MissingValue;

            if (values.Count != 1)
                return HeaderValue.MalformedValue;

            var value = values[0]?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return HeaderValue.MissingValue;

            if (value.Length > maximumLength ||
                value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
            {
                return HeaderValue.MalformedValue;
            }

            return new HeaderValue(value, Missing: false, Malformed: false);
        }

        private static bool IsToken(string value, bool allowPlus)
        {
            return value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_' ||
                (allowPlus && character == '+'));
        }

        private sealed record HeaderValue(string? Value, bool Missing, bool Malformed)
        {
            public static readonly HeaderValue MissingValue = new(null, Missing: true, Malformed: false);
            public static readonly HeaderValue MalformedValue = new(null, Missing: false, Malformed: true);
        }
    }
}
