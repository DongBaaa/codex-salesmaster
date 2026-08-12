using System.Globalization;
using System.Net.Http;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal sealed record DesktopClientRuntimeIdentity(
    string Version,
    int Build,
    int ProtocolVersion);

/// <summary>
/// Supplies the stable client identity used by the server compatibility gate.
/// </summary>
public sealed class DesktopClientIdentityProvider
{
    public const string DesktopAppId = "kr.georaeplan.desktop";
    public const string DesktopPlatform = "windows";

    private readonly string _version;
    private readonly string _build;
    private readonly string _protocol;

    public DesktopClientIdentityProvider()
        : this(typeof(DesktopClientIdentityProvider).Assembly.GetName().Version)
    {
    }

    internal DesktopClientIdentityProvider(Version? assemblyVersion)
    {
        var resolvedVersion = assemblyVersion ?? new Version(1, 0, 0);
        _version = FormatVersion(resolvedVersion);
        _build = ResolvePositiveBuild(resolvedVersion)
            .ToString(CultureInfo.InvariantCulture);
        _protocol = Math.Max(
                1,
                ClientCompatibilityHeaders.CurrentProtocolVersion)
            .ToString(CultureInfo.InvariantCulture);
    }

    public void Apply(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);

        SetSingleValue(http, ClientCompatibilityHeaders.AppId, DesktopAppId);
        SetSingleValue(http, ClientCompatibilityHeaders.Platform, DesktopPlatform);
        SetSingleValue(http, ClientCompatibilityHeaders.Version, _version);
        SetSingleValue(http, ClientCompatibilityHeaders.Build, _build);
        SetSingleValue(http, ClientCompatibilityHeaders.Protocol, _protocol);
    }

    internal DesktopClientRuntimeIdentity GetRuntimeIdentity()
        => new(
            _version,
            int.Parse(
                _build,
                System.Globalization.NumberStyles.None,
                CultureInfo.InvariantCulture),
            int.Parse(
                _protocol,
                System.Globalization.NumberStyles.None,
                CultureInfo.InvariantCulture));

    private static string FormatVersion(Version version)
    {
        if (version.Build >= 0)
            return version.ToString(3);

        return version.Minor >= 0
            ? version.ToString(2)
            : Math.Max(1, version.Major).ToString(CultureInfo.InvariantCulture);
    }

    private static int ResolvePositiveBuild(Version version)
    {
        if (version.Build > 0)
            return version.Build;

        if (version.Revision > 0)
            return version.Revision;

        return 1;
    }

    private static void SetSingleValue(
        HttpClient http,
        string headerName,
        string value)
    {
        http.DefaultRequestHeaders.Remove(headerName);
        if (!http.DefaultRequestHeaders.TryAddWithoutValidation(headerName, value))
        {
            throw new InvalidOperationException(
                $"Desktop client identity header could not be configured: {headerName}");
        }
    }
}
