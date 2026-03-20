using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class DesktopAppUpdateCheckResult
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool IsUpdateAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
    public AppUpdatePackageDto? Package { get; set; }
}
