using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileClientIdentityProvider
{
    private const int MaximumAppIdLength = 128;
    private const int MaximumPlatformLength = 32;
    private const int MaximumVersionLength = 64;

    private readonly string _appId;
    private readonly string _platform;
    private readonly string _version;
    private readonly string _build;
    private readonly string _protocol;

    public MobileClientIdentityProvider()
        : this(
            AppInfo.Current.PackageName,
            "android",
            AppInfo.Current.VersionString,
            AppInfo.Current.BuildString,
            ClientCompatibilityHeaders.CurrentProtocolVersion)
    {
    }

    internal MobileClientIdentityProvider(
        string? appId,
        string? platform,
        string? version,
        string? build,
        int protocolVersion)
    {
        _appId = NormalizeToken(appId, MaximumAppIdLength, "kr.georaeplan.mobile");
        _platform = NormalizeToken(platform, MaximumPlatformLength, "android");
        _version = NormalizeVersion(version);
        _build = NormalizePositiveInteger(build);
        _protocol = Math.Max(1, protocolVersion).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Apply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        SetSingleValue(request, ClientCompatibilityHeaders.AppId, _appId);
        SetSingleValue(request, ClientCompatibilityHeaders.Platform, _platform);
        SetSingleValue(request, ClientCompatibilityHeaders.Version, _version);
        SetSingleValue(request, ClientCompatibilityHeaders.Build, _build);
        SetSingleValue(request, ClientCompatibilityHeaders.Protocol, _protocol);
    }

    internal MobileClientRuntimeIdentity GetRuntimeIdentity()
        => new(
            _version,
            int.Parse(
                _build,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(
                _protocol,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture));

    private static void SetSingleValue(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string NormalizeToken(string? value, int maximumLength, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return fallback;

        if (normalized.Length > maximumLength)
            normalized = normalized[..maximumLength];

        var sanitized = new string(normalized
            .Where(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_')
            .ToArray());
        return sanitized.Length == 0 ? fallback : sanitized;
    }

    private static string NormalizeVersion(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var metadataIndex = normalized.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
            normalized = normalized[..metadataIndex];

        if (normalized.Length > MaximumVersionLength)
            normalized = normalized[..MaximumVersionLength];

        return Version.TryParse(normalized, out var parsed) &&
               parsed.Major >= 0
            ? parsed.ToString()
            : "0.0.0";
    }

    private static string NormalizePositiveInteger(string? value)
    {
        return int.TryParse(
                   value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var parsed) &&
               parsed > 0
            ? parsed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "1";
    }
}
