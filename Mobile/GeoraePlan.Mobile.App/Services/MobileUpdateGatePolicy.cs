using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

internal sealed record MobileClientRuntimeIdentity(
    string Version,
    int Build,
    int ProtocolVersion);

internal sealed class MobileCachedUpdateRequirement
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int PolicyVersion { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public int? LatestBuild { get; set; }
    public string MinimumVersion { get; set; } = string.Empty;
    public int? MinimumBuild { get; set; }
    public int? MinimumProtocolVersion { get; set; }
    public bool Mandatory { get; set; }
    public bool RequiresUserAction { get; set; }
    public bool OpaqueServerEnforced { get; set; }
    public string ObservedClientVersion { get; set; } = string.Empty;
    public int? ObservedClientBuild { get; set; }
    public int? ObservedClientProtocolVersion { get; set; }
    public string Message { get; set; } = string.Empty;
    public AppUpdatePackageDto? Package { get; set; }
}

internal static class MobileUpdateGatePolicy
{
    private const int MaximumVersionLength = 64;

    public static MobileAppUpdateCheckResult
        EvaluateStableAndroidManifest(
            AppUpdateManifestDto? manifest,
            MobileClientRuntimeIdentity current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var package = manifest?.Android;
        if (manifest is null ||
            !string.Equals(
                manifest.Channel,
                "stable",
                StringComparison.Ordinal) ||
            package is null ||
            !string.Equals(
                package.Platform,
                "android",
                StringComparison.Ordinal) ||
            manifest.PolicyVersion is not > 0 ||
            package.PolicyVersion !=
            manifest.PolicyVersion ||
            manifest.ProtocolVersion is not > 0 ||
            package.ProtocolVersion !=
            manifest.ProtocolVersion ||
            manifest.RequiresUserAction is null ||
            package.RequiresUserAction !=
            manifest.RequiresUserAction ||
            string.IsNullOrWhiteSpace(
                manifest.CompatibilityPolicy) ||
            !string.Equals(
                manifest.CompatibilityPolicy,
                package.CompatibilityPolicy,
                StringComparison.Ordinal))
        {
            return Invalid(
                current,
                "stable/android 업데이트 정책의 채널·플랫폼·호환성 필드가 정확히 일치하지 않습니다.");
        }

        return EvaluateManifest(
            manifest,
            current,
            "stable");
    }

    public static MobileAppUpdateCheckResult
        EvaluateManualSettingsManifest(
            AppUpdateManifestDto? manifest,
            MobileClientRuntimeIdentity current,
            string requestedChannel)
        => EvaluateManifest(
            manifest,
            current,
            requestedChannel);

    public static void EnsureExactAndroidInstallerPackage(
        AppUpdatePackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!string.Equals(
                package.Platform,
                "android",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "검증된 android 업데이트 패키지만 설치할 수 있습니다.");
        }
    }

    public static MobileAppUpdateCheckResult EvaluateManifest(
        AppUpdateManifestDto? manifest,
        MobileClientRuntimeIdentity current,
        string requestedChannel = "stable")
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!TryNormalizeVersion(current.Version, out var currentVersion, out var parsedCurrentVersion) ||
            current.Build <= 0 ||
            current.ProtocolVersion <= 0)
        {
            return Invalid(
                current,
                "현재 앱 버전·빌드·프로토콜 식별값이 올바르지 않아 업데이트 정책을 확인할 수 없습니다.");
        }

        if (!string.Equals(
                requestedChannel,
                "stable",
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest?.Channel,
                "stable",
                StringComparison.Ordinal))
        {
            return Invalid(
                current with { Version = currentVersion },
                "업데이트 매니페스트 채널이 stable과 정확히 일치하지 않습니다.");
        }

        var package = manifest!.Android;
        if (package is null)
        {
            return Invalid(
                current with { Version = currentVersion },
                "배포된 안드로이드 업데이트 정보를 찾지 못했습니다.");
        }

        if (!string.Equals(
                package.Platform,
                "android",
                StringComparison.Ordinal))
        {
            return Invalid(
                current with { Version = currentVersion },
                "업데이트 매니페스트의 플랫폼이 안드로이드가 아닙니다.");
        }

        if (!TryNormalizeVersion(package.Version, out var latestVersion, out var parsedLatestVersion))
        {
            return Invalid(
                current with { Version = currentVersion },
                "업데이트 매니페스트의 최신 버전 형식이 올바르지 않습니다.");
        }

        if (!TryPositiveOptional(package.Build, out var latestBuild) ||
            !TryPositiveOptional(package.MinimumSupportedBuild, out var minimumBuild) ||
            !TryPositiveOptional(package.MinimumSupportedProtocolVersion, out var minimumProtocol) ||
            !TryPositiveOptional(package.ProtocolVersion ?? manifest?.ProtocolVersion, out var latestProtocol) ||
            !TryPositiveOptional(package.PolicyVersion ?? manifest?.PolicyVersion, out var policyVersion))
        {
            return Invalid(
                current with { Version = currentVersion },
                "업데이트 매니페스트의 빌드·프로토콜·정책 번호는 양수여야 합니다.");
        }

        var minimumVersion = string.Empty;
        Version? parsedMinimumVersion = null;
        if (!string.IsNullOrWhiteSpace(package.MinimumSupportedVersion))
        {
            if (!TryNormalizeVersion(
                    package.MinimumSupportedVersion,
                    out minimumVersion,
                    out var parsedMinimum))
            {
                return Invalid(
                    current with { Version = currentVersion },
                    "업데이트 매니페스트의 최소 지원 버전 형식이 올바르지 않습니다.");
            }

            parsedMinimumVersion = parsedMinimum;
        }
        else if (package.Mandatory)
        {
            // Legacy manifests expressed a hard update with mandatory=true only.
            minimumVersion = latestVersion;
            parsedMinimumVersion = parsedLatestVersion;
        }

        if (parsedMinimumVersion is not null &&
            parsedMinimumVersion.CompareTo(parsedLatestVersion) > 0)
        {
            return Invalid(
                current with { Version = currentVersion },
                "최소 지원 버전이 최신 배포 버전보다 높아 정책을 신뢰할 수 없습니다.");
        }

        if (minimumBuild.HasValue &&
            latestBuild.HasValue &&
            minimumBuild.Value > latestBuild.Value)
        {
            return Invalid(
                current with { Version = currentVersion },
                "최소 지원 빌드가 최신 배포 빌드보다 높아 정책을 신뢰할 수 없습니다.");
        }

        if (minimumProtocol.HasValue &&
            latestProtocol.HasValue &&
            minimumProtocol.Value > latestProtocol.Value)
        {
            return Invalid(
                current with { Version = currentVersion },
                "최소 지원 프로토콜이 최신 배포 프로토콜보다 높아 정책을 신뢰할 수 없습니다.");
        }

        var versionComparison = parsedLatestVersion.CompareTo(parsedCurrentVersion);
        var isUpdateAvailable =
            versionComparison > 0 ||
            (versionComparison == 0 &&
             latestBuild.HasValue &&
             latestBuild.Value > current.Build);
        var isBelowMinimumVersion =
            parsedMinimumVersion is not null &&
            parsedCurrentVersion.CompareTo(parsedMinimumVersion) < 0;
        var isBelowMinimumBuild =
            minimumBuild.HasValue &&
            current.Build < minimumBuild.Value;
        var isBelowMinimumProtocol =
            minimumProtocol.HasValue &&
            current.ProtocolVersion < minimumProtocol.Value;
        var requiresUserAction =
            package.RequiresUserAction ??
            manifest?.RequiresUserAction ??
            package.Mandatory;

        var result = new MobileAppUpdateCheckResult
        {
            CurrentVersion = currentVersion,
            CurrentBuild = current.Build,
            CurrentProtocolVersion = current.ProtocolVersion,
            LatestVersion = latestVersion,
            LatestBuild = latestBuild,
            MinimumSupportedVersion = minimumVersion,
            MinimumSupportedBuild = minimumBuild,
            MinimumSupportedProtocolVersion = minimumProtocol,
            PolicyVersion = policyVersion ?? 0,
            RequiresUserAction = requiresUserAction,
            ManifestVerified = true,
            IsUpdateAvailable = isUpdateAvailable,
            IsBelowMinimumSupportedVersion = isBelowMinimumVersion,
            IsBelowMinimumSupportedBuild = isBelowMinimumBuild,
            IsBelowMinimumSupportedProtocol = isBelowMinimumProtocol,
            CanPersistRequiredPolicy = true,
            Package = package
        };
        result.Message = BuildMessage(result);
        return result;
    }

    public static MobileAppUpdateCheckResult EvaluateUpgradeRequired(
        ClientUpgradeRequiredResponse response,
        MobileClientRuntimeIdentity current)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(current);

        if (!TryNormalizeVersion(current.Version, out var currentVersion, out var parsedCurrentVersion))
            currentVersion = "0.0.0";

        var required = response.Required ?? new ClientCompatibilityPolicyDto();
        var minimumVersion = string.Empty;
        Version? parsedMinimumVersion = null;
        var coherentThreshold = false;

        if (!string.IsNullOrWhiteSpace(required.MinimumVersion) &&
            TryNormalizeVersion(required.MinimumVersion, out minimumVersion, out var parsedMinimum))
        {
            parsedMinimumVersion = parsedMinimum;
            coherentThreshold |= parsedCurrentVersion is not null &&
                                 parsedCurrentVersion.CompareTo(parsedMinimum) < 0;
        }

        var latestVersion = currentVersion;
        Version? parsedLatestVersion = parsedCurrentVersion;
        if (!string.IsNullOrWhiteSpace(required.LatestVersion) &&
            TryNormalizeVersion(required.LatestVersion, out var normalizedLatest, out var parsedLatest))
        {
            latestVersion = normalizedLatest;
            parsedLatestVersion = parsedLatest;
            coherentThreshold |=
                parsedCurrentVersion is not null &&
                parsedLatest.CompareTo(parsedCurrentVersion) > 0;
        }

        var minimumBuild = required.MinimumBuild is > 0
            ? required.MinimumBuild
            : null;
        var latestBuild = required.LatestBuild is > 0
            ? required.LatestBuild
            : null;
        var minimumProtocol = required.MinimumProtocolVersion is > 0
            ? required.MinimumProtocolVersion
            : null;

        coherentThreshold |= minimumBuild.HasValue && current.Build < minimumBuild.Value;
        coherentThreshold |= minimumProtocol.HasValue && current.ProtocolVersion < minimumProtocol.Value;
        coherentThreshold |=
            latestBuild.HasValue &&
            parsedLatestVersion is not null &&
            parsedCurrentVersion is not null &&
            parsedLatestVersion.CompareTo(parsedCurrentVersion) == 0 &&
            current.Build < latestBuild.Value;

        var package = new AppUpdatePackageDto
        {
            Platform = "android",
            Version = latestVersion,
            Build = latestBuild,
            Mandatory = true,
            MinimumSupportedVersion = minimumVersion,
            MinimumSupportedBuild = minimumBuild,
            MinimumSupportedProtocolVersion = minimumProtocol,
            PolicyVersion = Math.Max(0, required.PolicyVersion),
            RequiresUserAction = true,
            PackageUrl = required.UpdateUrl ?? string.Empty
        };

        return new MobileAppUpdateCheckResult
        {
            CurrentVersion = currentVersion,
            CurrentBuild = Math.Max(1, current.Build),
            CurrentProtocolVersion = Math.Max(1, current.ProtocolVersion),
            LatestVersion = latestVersion,
            LatestBuild = latestBuild,
            MinimumSupportedVersion = minimumVersion,
            MinimumSupportedBuild = minimumBuild,
            MinimumSupportedProtocolVersion = minimumProtocol,
            PolicyVersion = Math.Max(0, required.PolicyVersion),
            RequiresUserAction = true,
            ManifestVerified = coherentThreshold,
            VerificationFailure = coherentThreshold
                ? string.Empty
                : "서버의 강제 업데이트 응답에 비교 가능한 최소 버전·빌드·프로토콜이 없습니다.",
            IsUpdateAvailable = true,
            IsBelowMinimumSupportedVersion =
                parsedMinimumVersion is not null &&
                parsedCurrentVersion is not null &&
                parsedCurrentVersion.CompareTo(parsedMinimumVersion) < 0,
            IsBelowMinimumSupportedBuild =
                minimumBuild.HasValue &&
                current.Build < minimumBuild.Value,
            IsBelowMinimumSupportedProtocol =
                minimumProtocol.HasValue &&
                current.ProtocolVersion < minimumProtocol.Value,
            IsServerEnforced = true,
            CanPersistRequiredPolicy = true,
            Package = package,
            Message = string.IsNullOrWhiteSpace(response.Message)
                ? "서버 정책에 따라 거래플랜 앱 업데이트가 필요합니다."
                : response.Message.Trim()
        };
    }

    public static MobileCachedUpdateRequirement? CreateCachedRequirement(
        MobileAppUpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.RequiresImmediateUpdate || !result.CanPersistRequiredPolicy)
            return null;

        return new MobileCachedUpdateRequirement
        {
            PolicyVersion = Math.Max(0, result.PolicyVersion),
            LatestVersion = result.LatestVersion,
            LatestBuild = result.LatestBuild,
            MinimumVersion = result.MinimumSupportedVersion,
            MinimumBuild = result.MinimumSupportedBuild,
            MinimumProtocolVersion = result.MinimumSupportedProtocolVersion,
            Mandatory = result.Package?.Mandatory == true || result.IsServerEnforced,
            RequiresUserAction = result.RequiresUserAction || result.IsServerEnforced,
            OpaqueServerEnforced =
                result.IsServerEnforced &&
                !result.ManifestVerified,
            ObservedClientVersion =
                result.IsServerEnforced && !result.ManifestVerified
                    ? result.CurrentVersion
                    : string.Empty,
            ObservedClientBuild =
                result.IsServerEnforced && !result.ManifestVerified
                    ? result.CurrentBuild
                    : null,
            ObservedClientProtocolVersion =
                result.IsServerEnforced && !result.ManifestVerified
                    ? result.CurrentProtocolVersion
                    : null,
            Message = TrimBounded(result.Message, 4096),
            Package = CreateCacheSafePackage(result)
        };
    }

    public static bool IsValidCachedRequirementShape(
        MobileCachedUpdateRequirement? requirement)
    {
        if (requirement is null ||
            requirement.SchemaVersion !=
            MobileCachedUpdateRequirement.CurrentSchemaVersion ||
            requirement.PolicyVersion < 0 ||
            requirement.Message is null ||
            requirement.Message.Length > 4096 ||
            (!requirement.Mandatory && !requirement.RequiresUserAction) ||
            !TryNormalizeVersion(
                requirement.LatestVersion,
                out var latestVersion,
                out var parsedLatestVersion) ||
            !TryPositiveOptional(requirement.LatestBuild, out _) ||
            !TryPositiveOptional(requirement.MinimumBuild, out _) ||
            !TryPositiveOptional(requirement.MinimumProtocolVersion, out _))
        {
            return false;
        }

        if (requirement.OpaqueServerEnforced)
        {
            if (!requirement.Mandatory ||
                !requirement.RequiresUserAction ||
                !TryNormalizeVersion(
                    requirement.ObservedClientVersion,
                    out _,
                    out _) ||
                requirement.ObservedClientBuild is not > 0 ||
                requirement.ObservedClientProtocolVersion is not > 0)
            {
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(requirement.ObservedClientVersion) ||
                 requirement.ObservedClientBuild is not null ||
                 requirement.ObservedClientProtocolVersion is not null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requirement.MinimumVersion))
        {
            if (!TryNormalizeVersion(
                    requirement.MinimumVersion,
                    out _,
                    out var parsedMinimumVersion) ||
                parsedMinimumVersion.CompareTo(parsedLatestVersion) > 0)
            {
                return false;
            }
        }

        if (requirement.MinimumBuild.HasValue &&
            requirement.LatestBuild.HasValue &&
            requirement.MinimumBuild.Value > requirement.LatestBuild.Value)
        {
            return false;
        }

        if (requirement.Package is null)
            return true;

        if (!IsValidCachePackage(requirement.Package, latestVersion))
            return false;

        var package = requirement.Package;
        var normalizedPackageMinimumVersion = string.Empty;
        if (!string.IsNullOrWhiteSpace(package.MinimumSupportedVersion) &&
            !TryNormalizeVersion(
                package.MinimumSupportedVersion,
                out normalizedPackageMinimumVersion,
                out _))
        {
            return false;
        }

        var normalizedRequirementMinimumVersion = string.Empty;
        if (!string.IsNullOrWhiteSpace(requirement.MinimumVersion) &&
            !TryNormalizeVersion(
                requirement.MinimumVersion,
                out normalizedRequirementMinimumVersion,
                out _))
        {
            return false;
        }

        return package.Build == requirement.LatestBuild &&
               string.Equals(
                   normalizedPackageMinimumVersion,
                   normalizedRequirementMinimumVersion,
                   StringComparison.Ordinal) &&
               package.MinimumSupportedBuild == requirement.MinimumBuild &&
               package.MinimumSupportedProtocolVersion ==
               requirement.MinimumProtocolVersion &&
               (package.PolicyVersion ?? 0) == requirement.PolicyVersion &&
               package.Mandatory == requirement.Mandatory &&
               (package.RequiresUserAction ?? package.Mandatory) ==
               requirement.RequiresUserAction;
    }

    public static bool IsRequiredFor(
        MobileCachedUpdateRequirement requirement,
        MobileClientRuntimeIdentity current)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(current);

        if (!IsValidCachedRequirementShape(requirement) ||
            !TryNormalizeVersion(current.Version, out _, out var currentVersion) ||
            current.Build <= 0 ||
            current.ProtocolVersion <= 0)
        {
            return false;
        }

        if (requirement.OpaqueServerEnforced)
        {
            if (!TryNormalizeVersion(
                    requirement.ObservedClientVersion,
                    out _,
                    out var observedVersion))
            {
                return true;
            }

            var versionComparison =
                currentVersion.CompareTo(observedVersion);
            var observedBuild =
                requirement.ObservedClientBuild.GetValueOrDefault();
            var observedProtocol =
                requirement.ObservedClientProtocolVersion.GetValueOrDefault();
            var buildComparison =
                current.Build.CompareTo(observedBuild);
            var protocolComparison =
                current.ProtocolVersion.CompareTo(observedProtocol);
            var advancedWithoutRegression =
                versionComparison >= 0 &&
                buildComparison >= 0 &&
                protocolComparison >= 0 &&
                (versionComparison > 0 ||
                 buildComparison > 0 ||
                 protocolComparison > 0);

            return !advancedWithoutRegression;
        }

        if (!string.IsNullOrWhiteSpace(requirement.MinimumVersion))
        {
            if (!TryNormalizeVersion(requirement.MinimumVersion, out _, out var minimumVersion))
                return false;
            if (currentVersion.CompareTo(minimumVersion) < 0)
                return true;
        }

        if (requirement.MinimumBuild is > 0 &&
            current.Build < requirement.MinimumBuild.Value)
        {
            return true;
        }

        if (requirement.MinimumProtocolVersion is > 0 &&
            current.ProtocolVersion < requirement.MinimumProtocolVersion.Value)
        {
            return true;
        }

        if (!requirement.Mandatory && !requirement.RequiresUserAction)
            return false;

        if (!TryNormalizeVersion(requirement.LatestVersion, out _, out var latestVersion))
            return false;

        var comparison = currentVersion.CompareTo(latestVersion);
        return comparison < 0 ||
               (comparison == 0 &&
                requirement.LatestBuild is > 0 &&
                current.Build < requirement.LatestBuild.Value);
    }

    public static bool CanVerifiedCompatibleResultClear(
        MobileCachedUpdateRequirement cached,
        MobileAppUpdateCheckResult compatible)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(compatible);

        if (!compatible.ManifestVerified || compatible.RequiresImmediateUpdate)
            return false;

        if (cached.OpaqueServerEnforced)
        {
            // An opaque server decision has no manifest proof that an equal
            // or unordered policy relaxed the block. A positive revision can
            // only be cleared by a strictly newer verified revision. Revision
            // zero remains blocked for this runtime identity; IsRequiredFor
            // performs the natural release after the app identity advances.
            return cached.PolicyVersion > 0 &&
                   compatible.PolicyVersion >
                   cached.PolicyVersion;
        }

        if (compatible.PolicyVersion > cached.PolicyVersion)
            return true;
        if (compatible.PolicyVersion < cached.PolicyVersion)
            return false;

        // A nonzero policy revision must be explicitly incremented to relax a block.
        if (cached.PolicyVersion > 0)
            return false;

        return CompareRelease(
                   compatible.LatestVersion,
                   compatible.LatestBuild,
                   cached.LatestVersion,
                   cached.LatestBuild) > 0;
    }

    public static bool IsIncomingRequirementAtLeastAsNew(
        MobileCachedUpdateRequirement incoming,
        MobileCachedUpdateRequirement existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existing);

        if (incoming.OpaqueServerEnforced &&
            incoming.PolicyVersion == 0)
        {
            return true;
        }

        if (existing.OpaqueServerEnforced &&
            existing.PolicyVersion == 0)
        {
            return false;
        }

        if (incoming.PolicyVersion != existing.PolicyVersion)
            return incoming.PolicyVersion > existing.PolicyVersion;

        var releaseComparison = CompareRelease(
            incoming.LatestVersion,
            incoming.LatestBuild,
            existing.LatestVersion,
            existing.LatestBuild);
        if (releaseComparison != 0)
            return releaseComparison > 0;

        if (incoming.OpaqueServerEnforced ||
            existing.OpaqueServerEnforced)
        {
            return true;
        }

        return HasThresholdsAtLeastAsRestrictiveAs(
            incoming,
            existing);
    }

    public static MobileCachedUpdateRequirement
        ResolveRequiredEvidenceForPersistence(
            MobileCachedUpdateRequirement incoming,
            MobileCachedUpdateRequirement existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existing);

        if (incoming.OpaqueServerEnforced &&
            incoming.PolicyVersion == 0)
        {
            return incoming;
        }

        if (existing.OpaqueServerEnforced &&
            existing.PolicyVersion == 0)
        {
            return existing;
        }

        if (incoming.PolicyVersion != existing.PolicyVersion)
        {
            return incoming.PolicyVersion > existing.PolicyVersion
                ? incoming
                : existing;
        }

        var releaseComparison = CompareRelease(
            incoming.LatestVersion,
            incoming.LatestBuild,
            existing.LatestVersion,
            existing.LatestBuild);
        if (releaseComparison != 0)
            return releaseComparison > 0 ? incoming : existing;

        // Preserve the established opaque 426 ordering. Its observed-runtime
        // evidence is not interchangeable with verified manifest thresholds.
        if (incoming.OpaqueServerEnforced ||
            existing.OpaqueServerEnforced)
        {
            return incoming;
        }

        var incomingDominates =
            HasThresholdsAtLeastAsRestrictiveAs(incoming, existing);
        var existingDominates =
            HasThresholdsAtLeastAsRestrictiveAs(existing, incoming);

        if (incomingDominates)
            return incoming;
        if (existingDominates)
            return existing;

        // Same revision and release, but each observation is stronger on a
        // different axis. Retain the coordinate-wise maximum so neither
        // verified minimum can be weakened by arrival order.
        var minimumVersion = MaximumMinimumVersion(
            incoming.MinimumVersion,
            existing.MinimumVersion);
        var minimumBuild = MaximumOptional(
            incoming.MinimumBuild,
            existing.MinimumBuild);
        var minimumProtocol = MaximumOptional(
            incoming.MinimumProtocolVersion,
            existing.MinimumProtocolVersion);
        var merged = new MobileCachedUpdateRequirement
        {
            PolicyVersion = incoming.PolicyVersion,
            LatestVersion = incoming.LatestVersion,
            LatestBuild = incoming.LatestBuild,
            MinimumVersion = minimumVersion,
            MinimumBuild = minimumBuild,
            MinimumProtocolVersion = minimumProtocol,
            Mandatory = incoming.Mandatory || existing.Mandatory,
            RequiresUserAction =
                incoming.RequiresUserAction ||
                existing.RequiresUserAction,
            Message = TrimBounded(incoming.Message, 4096)
        };

        var package = incoming.Package ?? existing.Package;
        if (package is not null)
        {
            merged.Package = CreateCacheSafePackage(
                new MobileAppUpdateCheckResult
                {
                    LatestVersion = merged.LatestVersion,
                    LatestBuild = merged.LatestBuild,
                    MinimumSupportedVersion = minimumVersion,
                    MinimumSupportedBuild = minimumBuild,
                    MinimumSupportedProtocolVersion = minimumProtocol,
                    PolicyVersion = merged.PolicyVersion,
                    RequiresUserAction = merged.RequiresUserAction,
                    IsServerEnforced = merged.Mandatory,
                    Package = package
                });
        }

        return merged;
    }

    public static MobileAppUpdateCheckResult FromCached(
        MobileCachedUpdateRequirement cached,
        MobileClientRuntimeIdentity current)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(current);

        return new MobileAppUpdateCheckResult
        {
            CurrentVersion = current.Version,
            CurrentBuild = current.Build,
            CurrentProtocolVersion = current.ProtocolVersion,
            LatestVersion = cached.LatestVersion,
            LatestBuild = cached.LatestBuild,
            MinimumSupportedVersion = cached.MinimumVersion,
            MinimumSupportedBuild = cached.MinimumBuild,
            MinimumSupportedProtocolVersion = cached.MinimumProtocolVersion,
            PolicyVersion = cached.PolicyVersion,
            RequiresUserAction = cached.RequiresUserAction,
            ManifestVerified =
                !cached.OpaqueServerEnforced,
            VerificationFailure =
                cached.OpaqueServerEnforced
                    ? "Persisted opaque server compatibility evidence."
                    : string.Empty,
            IsUpdateAvailable = true,
            IsBelowMinimumSupportedVersion =
                IsVersionBelow(current.Version, cached.MinimumVersion),
            IsBelowMinimumSupportedBuild =
                cached.MinimumBuild is > 0 &&
                current.Build < cached.MinimumBuild.Value,
            IsBelowMinimumSupportedProtocol =
                cached.MinimumProtocolVersion is > 0 &&
                current.ProtocolVersion < cached.MinimumProtocolVersion.Value,
            IsServerEnforced = cached.Mandatory || cached.RequiresUserAction,
            CanPersistRequiredPolicy = true,
            Package = cached.Package,
            Message = string.IsNullOrWhiteSpace(cached.Message)
                ? "이전에 확인된 서버 정책에 따라 앱 업데이트가 필요합니다."
                : cached.Message
        };
    }

    private static MobileAppUpdateCheckResult Invalid(
        MobileClientRuntimeIdentity current,
        string message)
        => new()
        {
            CurrentVersion = current.Version,
            CurrentBuild = Math.Max(1, current.Build),
            CurrentProtocolVersion = Math.Max(1, current.ProtocolVersion),
            LatestVersion = current.Version,
            ManifestVerified = false,
            VerificationFailure = message,
            Message = message
        };

    private static string BuildMessage(MobileAppUpdateCheckResult result)
    {
        if (result.IsBelowMinimumSupportedVersion)
        {
            return $"현재 안드로이드 버전({result.CurrentVersion})은 서버 최소 지원 버전({result.MinimumSupportedVersion})보다 낮아 업데이트가 필요합니다. 최신 버전 {result.LatestVersion}을 설치하세요.";
        }

        if (result.IsBelowMinimumSupportedBuild)
        {
            return $"현재 안드로이드 빌드({result.CurrentBuild})는 서버 최소 지원 빌드({result.MinimumSupportedBuild})보다 낮아 업데이트가 필요합니다.";
        }

        if (result.IsBelowMinimumSupportedProtocol)
        {
            return $"현재 앱 통신 프로토콜({result.CurrentProtocolVersion})은 서버 최소 지원 프로토콜({result.MinimumSupportedProtocolVersion})보다 낮아 업데이트가 필요합니다.";
        }

        if (result.IsUpdateAvailable &&
            (result.Package?.Mandatory == true || result.RequiresUserAction))
        {
            return $"필수 안드로이드 업데이트 {result.LatestVersion}이 준비되어 있습니다.";
        }

        return result.IsUpdateAvailable
            ? $"새 안드로이드 버전 {result.LatestVersion}이 준비되어 있습니다."
            : $"현재 버전({result.CurrentVersion})이 최신입니다.";
    }

    private static bool TryNormalizeVersion(
        string? raw,
        out string normalized,
        out Version parsed)
    {
        normalized = (raw ?? string.Empty).Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var metadataIndex = normalized.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
            normalized = normalized[..metadataIndex];

        if (normalized.Length == 0 || normalized.Length > MaximumVersionLength)
        {
            parsed = new Version(0, 0, 0);
            return false;
        }

        if (!Version.TryParse(normalized, out parsed!) || parsed.Major < 0)
        {
            parsed = new Version(0, 0, 0);
            return false;
        }

        normalized = parsed.ToString();
        return true;
    }

    private static bool TryPositiveOptional(int? value, out int? normalized)
    {
        if (!value.HasValue)
        {
            normalized = null;
            return true;
        }

        if (value.Value <= 0)
        {
            normalized = null;
            return false;
        }

        normalized = value.Value;
        return true;
    }

    private static int CompareRelease(
        string leftVersion,
        int? leftBuild,
        string rightVersion,
        int? rightBuild)
    {
        if (!TryNormalizeVersion(leftVersion, out _, out var left))
            left = new Version(0, 0, 0);
        if (!TryNormalizeVersion(rightVersion, out _, out var right))
            right = new Version(0, 0, 0);

        var comparison = left.CompareTo(right);
        if (comparison != 0)
            return comparison;

        return (leftBuild ?? 0).CompareTo(rightBuild ?? 0);
    }

    private static bool HasThresholdsAtLeastAsRestrictiveAs(
        MobileCachedUpdateRequirement left,
        MobileCachedUpdateRequirement right)
        => CompareMinimumVersion(
               left.MinimumVersion,
               right.MinimumVersion) >= 0 &&
           left.MinimumBuild.GetValueOrDefault()
               .CompareTo(right.MinimumBuild.GetValueOrDefault()) >= 0 &&
           left.MinimumProtocolVersion.GetValueOrDefault()
               .CompareTo(
                   right.MinimumProtocolVersion.GetValueOrDefault()) >= 0;

    private static int CompareMinimumVersion(
        string left,
        string right)
    {
        if (!TryNormalizeVersion(left, out _, out var parsedLeft))
            parsedLeft = new Version(0, 0, 0);
        if (!TryNormalizeVersion(right, out _, out var parsedRight))
            parsedRight = new Version(0, 0, 0);

        return parsedLeft.CompareTo(parsedRight);
    }

    private static string MaximumMinimumVersion(
        string left,
        string right)
    {
        var comparison = CompareMinimumVersion(left, right);
        var selected = comparison >= 0 ? left : right;
        return TryNormalizeVersion(selected, out var normalized, out _)
            ? normalized
            : string.Empty;
    }

    private static int? MaximumOptional(int? left, int? right)
    {
        var maximum = Math.Max(
            left.GetValueOrDefault(),
            right.GetValueOrDefault());
        return maximum > 0 ? maximum : null;
    }

    private static bool IsVersionBelow(string current, string minimum)
    {
        if (string.IsNullOrWhiteSpace(minimum) ||
            !TryNormalizeVersion(current, out _, out var parsedCurrent) ||
            !TryNormalizeVersion(minimum, out _, out var parsedMinimum))
        {
            return false;
        }

        return parsedCurrent.CompareTo(parsedMinimum) < 0;
    }

    private static AppUpdatePackageDto? CreateCacheSafePackage(
        MobileAppUpdateCheckResult result)
    {
        var package = result.Package;
        if (package is null ||
            !TryNormalizeVersion(package.Version, out var version, out _) ||
            !IsValidPackageUrl(package.PackageUrl) ||
            !IsSha256(package.Sha256) ||
            !IsSafeApkFileName(package.FileName) ||
            !IsPackageUrlFileNameMatch(
                package.PackageUrl,
                package.FileName) ||
            package.FileSize <= 0 ||
            !TryPositiveOptional(package.ProtocolVersion, out var protocol) ||
            !TryPositiveOptional(result.LatestBuild, out var build) ||
            !TryPositiveOptional(result.MinimumSupportedBuild, out var minimumBuild) ||
            !TryPositiveOptional(
                result.MinimumSupportedProtocolVersion,
                out var minimumProtocol) ||
            !TryPositiveOptional(
                result.PolicyVersion > 0 ? result.PolicyVersion : null,
                out var policyVersion))
        {
            return null;
        }

        var minimumVersion = string.Empty;
        if (!string.IsNullOrWhiteSpace(result.MinimumSupportedVersion))
        {
            if (!TryNormalizeVersion(
                    result.MinimumSupportedVersion,
                    out minimumVersion,
                    out _))
            {
                return null;
            }
        }

        return new AppUpdatePackageDto
        {
            Platform = "android",
            Version = version,
            Build = build,
            ProtocolVersion = protocol,
            Mandatory = package.Mandatory || result.IsServerEnforced,
            MinimumSupportedVersion = minimumVersion,
            MinimumSupportedBuild = minimumBuild,
            MinimumSupportedProtocolVersion = minimumProtocol,
            PolicyVersion = policyVersion,
            RequiresUserAction = result.RequiresUserAction ||
                                 result.IsServerEnforced,
            CompatibilityPolicy = TrimBounded(
                package.CompatibilityPolicy,
                128),
            PackageUrl = package.PackageUrl.Trim(),
            FileName = package.FileName.Trim(),
            Sha256 = package.Sha256.Trim().ToUpperInvariant(),
            FileSize = package.FileSize,
            Notes = string.Empty,
            ReleasedAtUtc = package.ReleasedAtUtc,
            Installers = []
        };
    }

    private static bool IsValidCachePackage(
        AppUpdatePackageDto package,
        string requiredLatestVersion)
    {
        if (!string.Equals(
                package.Platform,
                "android",
                StringComparison.Ordinal) ||
            !TryNormalizeVersion(package.Version, out var packageVersion, out _) ||
            !string.Equals(
                packageVersion,
                requiredLatestVersion,
                StringComparison.Ordinal) ||
            !IsValidPackageUrl(package.PackageUrl) ||
            !IsSha256(package.Sha256) ||
            !IsSafeApkFileName(package.FileName) ||
            !IsPackageUrlFileNameMatch(
                package.PackageUrl,
                package.FileName) ||
            package.FileSize <= 0 ||
            package.Notes is null ||
            package.Notes.Length > 4096 ||
            package.CompatibilityPolicy is null ||
            package.CompatibilityPolicy.Length > 128 ||
            package.Installers is null ||
            package.Installers.Count != 0 ||
            !TryPositiveOptional(package.Build, out _) ||
            !TryPositiveOptional(package.ProtocolVersion, out _) ||
            !TryPositiveOptional(package.MinimumSupportedBuild, out _) ||
            !TryPositiveOptional(package.MinimumSupportedProtocolVersion, out _) ||
            !TryPositiveOptional(package.PolicyVersion, out _))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(package.MinimumSupportedVersion) &&
            !TryNormalizeVersion(
                package.MinimumSupportedVersion,
                out _,
                out _))
        {
            return false;
        }

        return true;
    }

    private static bool IsValidPackageUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0 ||
            normalized.Length > 2048 ||
            normalized.Contains('\\') ||
            normalized.Contains('#') ||
            normalized.Contains('?'))
        {
            return false;
        }

        string path;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
        {
            var localHttp =
                absolute.IsLoopback &&
                string.Equals(
                    absolute.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase);
            if (!localHttp &&
                !string.Equals(
                    absolute.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(absolute.UserInfo) ||
                !string.IsNullOrEmpty(absolute.Fragment) ||
                !string.IsNullOrEmpty(absolute.Query))
            {
                return false;
            }

            path = absolute.AbsolutePath;
        }
        else
        {
            if (!Uri.TryCreate(normalized, UriKind.Relative, out _) ||
                !normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            path = normalized;
        }

        try
        {
            var decodedPath = Uri.UnescapeDataString(path);
            return !decodedPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..");
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 64 &&
               normalized.All(Uri.IsHexDigit);
    }

    private static bool IsPackageUrlFileNameMatch(
        string? packageUrl,
        string? fileName)
    {
        var normalizedUrl = (packageUrl ?? string.Empty).Trim();
        string path;
        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var absolute))
            path = absolute.AbsolutePath;
        else
            path = normalizedUrl;

        try
        {
            var decoded = Uri.UnescapeDataString(path);
            var slash = decoded.LastIndexOf('/');
            var urlFileName = slash >= 0 ? decoded[(slash + 1)..] : decoded;
            return string.Equals(
                urlFileName,
                (fileName ?? string.Empty).Trim(),
                StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsSafeApkFileName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length is > 0 and <= 255 &&
               !normalized.Contains('/') &&
               !normalized.Contains('\\') &&
               !normalized.Contains("..", StringComparison.Ordinal) &&
               string.Equals(
                   Path.GetExtension(normalized),
                   ".apk",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimBounded(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }
}
