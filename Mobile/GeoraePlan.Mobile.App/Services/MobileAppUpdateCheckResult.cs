using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileAppUpdateCheckResult
{
    public string CurrentVersion { get; set; } = string.Empty;
    public int CurrentBuild { get; set; }
    public int CurrentProtocolVersion { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public int? LatestBuild { get; set; }
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public int? MinimumSupportedBuild { get; set; }
    public int? MinimumSupportedProtocolVersion { get; set; }
    public int PolicyVersion { get; set; }
    public bool RequiresUserAction { get; set; }
    public bool ManifestVerified { get; set; }
    public string VerificationFailure { get; set; } = string.Empty;
    public bool IsUpdateAvailable { get; set; }
    public bool IsBelowMinimumSupportedVersion { get; set; }
    public bool IsBelowMinimumSupportedBuild { get; set; }
    public bool IsBelowMinimumSupportedProtocol { get; set; }
    public bool IsServerEnforced { get; set; }
    public bool CanPersistRequiredPolicy { get; set; }
    public bool RequiresImmediateUpdate =>
        IsServerEnforced ||
        IsBelowMinimumSupportedVersion ||
        IsBelowMinimumSupportedBuild ||
        IsBelowMinimumSupportedProtocol ||
        (IsUpdateAvailable &&
         (Package?.Mandatory == true || RequiresUserAction));
    public string Message { get; set; } = string.Empty;
    public AppUpdatePackageDto? Package { get; set; }
}
