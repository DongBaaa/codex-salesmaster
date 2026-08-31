using System.Diagnostics;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TestProcessIsolationPowerShellTests
{
    [Fact]
    public async Task WindowsPowerShellChild_UsesCompatibleSystemModules()
    {
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(powerShellPath));

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "(Get-Command Get-FileHash).Source; (Get-Command Get-Acl).Source"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = (await standardOutput) + Environment.NewLine +
                     (await standardError);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Microsoft.PowerShell.Utility", output);
        Assert.Contains("Microsoft.PowerShell.Security", output);
        Assert.DoesNotContain("CommandNotFoundException", output);
        Assert.DoesNotContain("CouldNotAutoloadMatchingModule", output);
    }
}
