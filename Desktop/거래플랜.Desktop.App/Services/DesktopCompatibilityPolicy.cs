using System.Globalization;
using System.IO;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal enum DesktopCompatibilityEvidenceKind
{
    Verified426 = 1,
    Opaque426 = 2
}

internal sealed record DesktopCompatibilityEvidence
{
    public DesktopCompatibilityEvidence()
    {
    }

    public required DesktopCompatibilityEvidenceKind Kind { get; init; }
    public required int PolicyVersion { get; init; }
    public required string MinimumVersion { get; init; }
    public required int MinimumBuild { get; init; }
    public required int MinimumProtocolVersion { get; init; }
    public required string LatestVersion { get; init; }
    public required int LatestBuild { get; init; }
    public required string ObservedVersion { get; init; }
    public required int ObservedBuild { get; init; }
    public required int ObservedProtocolVersion { get; init; }
    public required DateTime ObservedAtUtc { get; init; }
}

internal enum DesktopCompatibilityEvidenceState
{
    None = 0,
    Valid = 1,
    Unreadable = 2
}

internal sealed record DesktopCompatibilityEvidenceLoadResult(
    DesktopCompatibilityEvidenceState State,
    DesktopCompatibilityEvidence? Evidence,
    long Generation,
    string DiagnosticCode)
{
    public static DesktopCompatibilityEvidenceLoadResult None { get; } =
        new(
            DesktopCompatibilityEvidenceState.None,
            null,
            0,
            "none");
}

internal sealed record DesktopVerifiedStablePolicy(
    int PolicyVersion,
    bool RequiresUserAction,
    string MinimumVersion,
    int MinimumBuild,
    int MinimumProtocolVersion,
    string LatestVersion,
    int LatestBuild,
    AppUpdatePackageDto Package);

internal sealed record DesktopStablePolicyVerificationResult(
    bool IsVerified,
    DesktopVerifiedStablePolicy? Policy,
    string DiagnosticCode);

internal static class DesktopCompatibilityPolicy
{
    public static DesktopCompatibilityEvidence From426(
        DesktopClientUpgradeRequiredException exception,
        DesktopClientRuntimeIdentity runtime,
        DateTime observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var response = exception.Response;
        var required = response.Required;
        var client = response.Client;
        var verifiedIdentity =
            string.Equals(
                client.AppId,
                DesktopClientIdentityProvider.DesktopAppId,
                StringComparison.Ordinal) &&
            string.Equals(
                client.Platform,
                DesktopClientIdentityProvider.DesktopPlatform,
                StringComparison.Ordinal) &&
            string.Equals(
                client.Version,
                runtime.Version,
                StringComparison.Ordinal) &&
            client.Build == runtime.Build &&
            client.ProtocolVersion ==
                runtime.ProtocolVersion;

        if (verifiedIdentity &&
            TryNormalizeVerifiedRequirement(
                required,
                out var minimumVersion,
                out var minimumBuild,
                out var minimumProtocol,
                out var latestVersion,
                out var latestBuild))
        {
            return new DesktopCompatibilityEvidence
            {
                Kind =
                    DesktopCompatibilityEvidenceKind
                        .Verified426,
                PolicyVersion =
                    required.PolicyVersion,
                MinimumVersion = minimumVersion,
                MinimumBuild = minimumBuild,
                MinimumProtocolVersion =
                    minimumProtocol,
                LatestVersion = latestVersion,
                LatestBuild = latestBuild,
                ObservedVersion = runtime.Version,
                ObservedBuild = runtime.Build,
                ObservedProtocolVersion =
                    runtime.ProtocolVersion,
                ObservedAtUtc =
                    NormalizeUtc(observedAtUtc)
            };
        }

        return new DesktopCompatibilityEvidence
        {
            Kind =
                DesktopCompatibilityEvidenceKind.Opaque426,
            PolicyVersion = 0,
            MinimumVersion = string.Empty,
            MinimumBuild = 0,
            MinimumProtocolVersion = 0,
            LatestVersion = string.Empty,
            LatestBuild = 0,
            ObservedVersion = runtime.Version,
            ObservedBuild = runtime.Build,
            ObservedProtocolVersion =
                runtime.ProtocolVersion,
            ObservedAtUtc =
                NormalizeUtc(observedAtUtc)
        };
    }

    public static DesktopCompatibilityEvidence Merge(
        DesktopCompatibilityEvidence current,
        DesktopCompatibilityEvidence incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);

        if (current.Kind ==
            DesktopCompatibilityEvidenceKind.Opaque426)
        {
            return current;
        }

        if (incoming.Kind ==
            DesktopCompatibilityEvidenceKind.Opaque426)
        {
            return incoming;
        }

        if (incoming.PolicyVersion <
            current.PolicyVersion)
        {
            return current;
        }

        if (incoming.PolicyVersion >
            current.PolicyVersion)
        {
            return incoming;
        }

        return current with
        {
            MinimumVersion = MaxVersion(
                current.MinimumVersion,
                incoming.MinimumVersion),
            MinimumBuild = Math.Max(
                current.MinimumBuild,
                incoming.MinimumBuild),
            MinimumProtocolVersion = Math.Max(
                current.MinimumProtocolVersion,
                incoming.MinimumProtocolVersion),
            LatestVersion = MaxVersion(
                current.LatestVersion,
                incoming.LatestVersion),
            LatestBuild = Math.Max(
                current.LatestBuild,
                incoming.LatestBuild),
            ObservedAtUtc = current.ObservedAtUtc
        };
    }

    public static bool RuntimeStrictlyAdvancedWithoutRegression(
        DesktopCompatibilityEvidence evidence,
        DesktopClientRuntimeIdentity runtime)
    {
        var versionComparison = CompareVersion(
            runtime.Version,
            evidence.ObservedVersion);
        return versionComparison >= 0 &&
               runtime.Build >= evidence.ObservedBuild &&
               runtime.ProtocolVersion >=
               evidence.ObservedProtocolVersion &&
               (versionComparison > 0 ||
                runtime.Build > evidence.ObservedBuild ||
                runtime.ProtocolVersion >
                evidence.ObservedProtocolVersion);
    }

    public static bool RuntimeSatisfies(
        DesktopCompatibilityEvidence evidence,
        DesktopClientRuntimeIdentity runtime)
        => evidence.Kind ==
               DesktopCompatibilityEvidenceKind.Opaque426 ||
           (CompareVersion(
                    runtime.Version,
                    evidence.MinimumVersion) >= 0 &&
            runtime.Build >= evidence.MinimumBuild &&
            runtime.ProtocolVersion >=
            evidence.MinimumProtocolVersion);

    public static bool RuntimeSatisfies(
        DesktopVerifiedStablePolicy policy,
        DesktopClientRuntimeIdentity runtime)
        => CompareVersion(
               runtime.Version,
               policy.MinimumVersion) >= 0 &&
           runtime.Build >= policy.MinimumBuild &&
           runtime.ProtocolVersion >=
           policy.MinimumProtocolVersion;

    public static bool IsValidEvidenceShape(
        DesktopCompatibilityEvidence? evidence)
    {
        if (evidence is null ||
            evidence.ObservedAtUtc.Kind != DateTimeKind.Utc ||
            evidence.ObservedVersion is null ||
            evidence.MinimumVersion is null ||
            evidence.LatestVersion is null ||
            evidence.ObservedVersion.Length > 64 ||
            evidence.MinimumVersion.Length > 64 ||
            evidence.LatestVersion.Length > 64 ||
            !TryPositiveVersion(
                evidence.ObservedVersion,
                out _) ||
            evidence.ObservedBuild < 1 ||
            evidence.ObservedProtocolVersion < 1)
        {
            return false;
        }

        if (evidence.Kind ==
            DesktopCompatibilityEvidenceKind.Opaque426)
        {
            return evidence.PolicyVersion == 0 &&
                   evidence.MinimumVersion.Length == 0 &&
                   evidence.MinimumBuild == 0 &&
                   evidence.MinimumProtocolVersion == 0 &&
                   evidence.LatestVersion.Length == 0 &&
                   evidence.LatestBuild == 0;
        }

        return evidence.PolicyVersion > 0 &&
               TryPositiveVersion(
                   evidence.MinimumVersion,
                   out var minimum) &&
               evidence.MinimumBuild > 0 &&
               evidence.MinimumProtocolVersion > 0 &&
               TryPositiveVersion(
                   evidence.LatestVersion,
                   out var latest) &&
               latest >= minimum &&
               evidence.LatestBuild >=
               evidence.MinimumBuild;
    }

    private static bool TryNormalizeVerifiedRequirement(
        ClientCompatibilityPolicyDto required,
        out string minimumVersion,
        out int minimumBuild,
        out int minimumProtocol,
        out string latestVersion,
        out int latestBuild)
    {
        minimumVersion = string.Empty;
        minimumBuild = 0;
        minimumProtocol = 0;
        latestVersion = string.Empty;
        latestBuild = 0;

        if (required.PolicyVersion < 1 ||
            !required.RequiresUserAction ||
            required.MinimumBuild is not > 0 ||
            required.MinimumProtocolVersion is not > 0 ||
            required.LatestBuild is not > 0 ||
            !TryPositiveVersion(
                required.MinimumVersion,
                out var minimum) ||
            !TryPositiveVersion(
                required.LatestVersion,
                out var latest) ||
            latest < minimum ||
            required.LatestBuild <
            required.MinimumBuild)
        {
            return false;
        }

        minimumVersion = minimum.ToString();
        minimumBuild = required.MinimumBuild.Value;
        minimumProtocol =
            required.MinimumProtocolVersion.Value;
        latestVersion = latest.ToString();
        latestBuild = required.LatestBuild.Value;
        return true;
    }

    internal static bool TryPositiveVersion(
        string? value,
        out Version version)
    {
        if (!Version.TryParse(
                (value ?? string.Empty).Trim(),
                out version!) ||
            version.Major < 1)
        {
            version = new Version(0, 0);
            return false;
        }

        return true;
    }

    internal static int CompareVersion(
        string left,
        string right)
    {
        if (!TryPositiveVersion(left, out var leftVersion) ||
            !TryPositiveVersion(right, out var rightVersion))
        {
            return -1;
        }

        return leftVersion.CompareTo(rightVersion);
    }

    private static string MaxVersion(
        string left,
        string right)
        => CompareVersion(left, right) >= 0
            ? left
            : right;

    private static DateTime NormalizeUtc(
        DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
}

internal static class DesktopStablePolicyVerifier
{
    private const string StableChannel = "stable";
    private const string DesktopPackagePlatform = "desktop";

    public static DesktopStablePolicyVerificationResult Verify(
        AppUpdateManifestDto? manifest,
        Uri apiBaseUri)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (manifest is null)
        {
            return Invalid("manifest-null");
        }

        if (!string.Equals(
                manifest.Channel,
                StableChannel,
                StringComparison.Ordinal))
        {
            return Invalid("channel");
        }

        var package = manifest.Desktop;
        if (package is null)
            return Invalid("desktop-null");
        if (!string.Equals(
                package.Platform,
                DesktopPackagePlatform,
                StringComparison.Ordinal))
        {
            return Invalid("platform");
        }

        if (manifest.PolicyVersion is not > 0 ||
            package.PolicyVersion !=
            manifest.PolicyVersion ||
            manifest.RequiresUserAction is null ||
            package.RequiresUserAction !=
            manifest.RequiresUserAction ||
            manifest.ProtocolVersion is not > 0 ||
            package.ProtocolVersion !=
            manifest.ProtocolVersion ||
            !string.Equals(
                manifest.CompatibilityPolicy,
                package.CompatibilityPolicy,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                manifest.CompatibilityPolicy))
        {
            return Invalid("policy-disagreement");
        }

        if (!DesktopCompatibilityPolicy.TryPositiveVersion(
                package.MinimumSupportedVersion,
                out var minimumVersion) ||
            package.MinimumSupportedBuild is not > 0 ||
            package.MinimumSupportedProtocolVersion is not > 0 ||
            !DesktopCompatibilityPolicy.TryPositiveVersion(
                package.Version,
                out var latestVersion) ||
            package.Build is not > 0 ||
            latestVersion < minimumVersion ||
            package.Build <
            package.MinimumSupportedBuild ||
            package.ProtocolVersion.Value <
            package.MinimumSupportedProtocolVersion.Value)
        {
            return Invalid("version-shape");
        }

        if (!IsValidAsset(package, apiBaseUri))
            return Invalid("asset");

        return new DesktopStablePolicyVerificationResult(
            true,
            new DesktopVerifiedStablePolicy(
                manifest.PolicyVersion.Value,
                manifest.RequiresUserAction.Value,
                minimumVersion.ToString(),
                package.MinimumSupportedBuild.Value,
                package.MinimumSupportedProtocolVersion.Value,
                latestVersion.ToString(),
                package.Build.Value,
                package),
            "verified");
    }

    private static bool IsValidAsset(
        AppUpdatePackageDto package,
        Uri apiBaseUri)
    {
        if (package.FileSize <= 0 ||
            string.IsNullOrWhiteSpace(package.FileName) ||
            package.FileName.Length > 128 ||
            !string.Equals(
                Path.GetFileName(package.FileName),
                package.FileName,
                StringComparison.Ordinal) ||
            package.FileName.Any(char.IsControl) ||
            package.Sha256 is null ||
            package.Sha256.Length != 64 ||
            !package.Sha256.All(
                static character =>
                    Uri.IsHexDigit(character)) ||
            string.IsNullOrWhiteSpace(package.PackageUrl) ||
            package.PackageUrl.Length > 2048)
        {
            return false;
        }

        if (!Uri.TryCreate(
                apiBaseUri,
                package.PackageUrl,
                out var packageUri) ||
            !string.Equals(
                packageUri.Scheme,
                apiBaseUri.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                packageUri.Authority,
                apiBaseUri.Authority,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Uri.UnescapeDataString(
                    packageUri.Segments[^1]),
                package.FileName,
                StringComparison.Ordinal))
        {
            return false;
        }

        return packageUri.Scheme is "https" or "http";
    }

    private static DesktopStablePolicyVerificationResult Invalid(
        string diagnosticCode)
        => new(false, null, diagnosticCode);
}
