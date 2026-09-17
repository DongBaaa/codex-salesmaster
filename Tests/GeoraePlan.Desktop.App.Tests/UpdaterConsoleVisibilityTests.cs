using System.Diagnostics;
using 거래플랜.Updater;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UpdaterConsoleVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallerWorker_HidesConsoleWithoutChangingElevationOrOutputContract(bool elevated)
    {
        const string arguments = "-NoProfile -NonInteractive -File \"C:\\fixture path\\worker.ps1\" -SuppressUi";
        var info = Program.CreateInstallProcessStartInfo(Path.GetTempPath(), arguments, elevated);

        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.Equal("-WindowStyle Hidden " + arguments, info.Arguments);
        Assert.Equal(elevated, info.UseShellExecute);
        Assert.Equal(elevated ? "runas" : string.Empty, info.Verb);
        Assert.Equal(!elevated, info.RedirectStandardOutput);
        Assert.Equal(!elevated, info.RedirectStandardError);
        Assert.Equal(!elevated, info.CreateNoWindow);
    }

    [Fact]
    public async Task HiddenWorker_PreservesOutputAndFailureExitCode()
    {
        var info = Program.CreateInstallProcessStartInfo(
            Path.GetTempPath(),
            "-NoProfile -NonInteractive -Command \"[Console]::Out.WriteLine('worker-output'); [Console]::Error.WriteLine('worker-error'); exit 7\"",
            requiresElevation: false);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        Assert.Equal(7, process.ExitCode);
        Assert.Equal("worker-output", (await output).Trim());
        Assert.Equal("worker-error", (await error).Trim());
    }
}
