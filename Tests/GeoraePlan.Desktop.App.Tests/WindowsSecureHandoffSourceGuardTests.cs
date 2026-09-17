using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class WindowsSecureHandoffSourceGuardTests
{
    [Fact]
    public void WindowsCodexHandoff_PinsSshIdentityHostAndVerifiedBundleWithoutInboundService()
    {
        var root = FindRepositoryRoot();
        var runner = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "windows",
            "Run-GeoraePlanCodexHandoff.ps1"));
        var installer = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "windows",
            "Install-GeoraePlanCodexHandoff.ps1"));

        Assert.Contains("$LinuxHost = '192.168.0.199'", runner, StringComparison.Ordinal);
        Assert.Contains("$LinuxPort = 2222", runner, StringComparison.Ordinal);
        Assert.Contains("$LinuxUser = 'itw'", runner, StringComparison.Ordinal);
        Assert.Contains("itwserver_codex_ed25519", runner, StringComparison.Ordinal);
        Assert.Contains("$expectedPublicKey = 'ssh-ed25519 ", runner, StringComparison.Ordinal);
        Assert.Contains("$expectedHostKey = 'ssh-ed25519 ", runner, StringComparison.Ordinal);
        Assert.Contains("'BatchMode=yes'", runner, StringComparison.Ordinal);
        Assert.Contains("'IdentitiesOnly=yes'", runner, StringComparison.Ordinal);
        Assert.Contains("'StrictHostKeyChecking=yes'", runner, StringComparison.Ordinal);
        Assert.Contains("UserKnownHostsFile=$knownHostsPath", runner, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256", runner, StringComparison.Ordinal);
        Assert.Contains("$actualBundleSha256 -ne $bundleSha256", runner, StringComparison.Ordinal);
        Assert.Contains("Expand-Archive -LiteralPath $bundlePath", runner, StringComparison.Ordinal);
        Assert.Contains("D:\\GeoraePlan-Codex-Handoff", runner, StringComparison.Ordinal);
        Assert.Contains("D:\\GeoraePlan-Codex-Run.cmd", installer, StringComparison.Ordinal);
        Assert.Contains("거래플랜 Codex 작업 실행.lnk", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordAuthentication", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-Service", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restart-Service", installer, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "tools")) &&
                Directory.Exists(Path.Combine(current.FullName, "Desktop")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
