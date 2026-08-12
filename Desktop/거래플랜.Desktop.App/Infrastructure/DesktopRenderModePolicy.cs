using System.Windows.Interop;
using System.Windows.Media;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class DesktopRenderModePolicy
{
    internal static bool ShouldForceSoftwareRendering(bool isTestRuntime)
        => isTestRuntime;

    internal static bool Apply(
        bool isTestRuntime,
        Action forceSoftwareRendering)
    {
        ArgumentNullException.ThrowIfNull(forceSoftwareRendering);

        if (!ShouldForceSoftwareRendering(isTestRuntime))
            return false;

        forceSoftwareRendering();
        return true;
    }

    internal static bool ApplyForCurrentRuntime()
        => Apply(
            AppRuntimeInfo.IsTestRuntime,
            static () =>
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly);
}
