using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SoakObservationGuardTests
{
    [Fact]
    public void SoakObservation_IsReadOnlyAndPersistsIncrementalEvidence()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repoRoot,
            "tools",
            "verification",
            "Invoke-GeoraePlanSoakObservation.ps1");
        var source = File.ReadAllText(scriptPath);

        Assert.Contains("[int]$SampleCount = 1440", source, StringComparison.Ordinal);
        Assert.Contains("[int]$IntervalSeconds = 60", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireDesktopProcess", source, StringComparison.Ordinal);
        Assert.Contains("/healthz", source, StringComparison.Ordinal);
        Assert.Contains("/updates/manifest?channel=", source, StringComparison.Ordinal);
        Assert.Contains(
            "function Get-OptionalManifestVersion",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$Manifest.PSObject.Properties[$PackageName]",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$manifestJson.desktop.version",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$manifestJson.android.version",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Get-DesktopProcessSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("WorkingSetMb", source, StringComparison.Ordinal);
        Assert.Contains("Responding", source, StringComparison.Ordinal);
        Assert.Contains("AppendAllText($csvPath", source, StringComparison.Ordinal);
        Assert.Contains("soak_observation_report=", source, StringComparison.Ordinal);
        Assert.Contains("운영 데이터 생성, 수정, 삭제 API는 호출하지 않습니다.", source, StringComparison.Ordinal);

        Assert.DoesNotContain("-Method Post", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Put", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Patch", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Delete", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedRunAll_ContinuouslyObservesServerWithBoundedReadOnlyLogs()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repoRoot,
            "테스트 시행",
            "테스트-환경-준비.ps1");
        var source = File.ReadAllText(scriptPath);
        var healthProbeStart = source.IndexOf(
            "function Invoke-RuntimeHealthProbe",
            StringComparison.Ordinal);
        var healthProbeEnd = source.IndexOf(
            "function Remove-OldRuntimeServerLogs",
            healthProbeStart,
            StringComparison.Ordinal);

        Assert.True(healthProbeStart >= 0);
        Assert.True(healthProbeEnd > healthProbeStart);
        var healthProbeSource = source[healthProbeStart..healthProbeEnd];
        Assert.Contains("-Method Get", healthProbeSource, StringComparison.Ordinal);
        Assert.Contains("-TimeoutSec 1", healthProbeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("-Method Post", healthProbeSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Put", healthProbeSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Patch", healthProbeSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Delete", healthProbeSource, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("RuntimeLogs", source, StringComparison.Ordinal);
        Assert.Contains("health-observation.csv", source, StringComparison.Ordinal);
        Assert.Contains("health-observation.previous.csv", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::AppendAllText(", source, StringComparison.Ordinal);
        Assert.Contains("-MaximumSamplesPerFile 17280", source, StringComparison.Ordinal);
        Assert.Contains("$appProcess.WaitForExit(250)", source, StringComparison.Ordinal);
        Assert.Contains("$consecutiveHealthFailures -ge 3", source, StringComparison.Ordinal);
        Assert.Contains("Stop-RuntimeAppAfterServerFailure", source, StringComparison.Ordinal);
        Assert.Contains("MaximumBytesPerFile = 67108864", source, StringComparison.Ordinal);
        Assert.Contains("MaximumTotalBytes = 134217728", source, StringComparison.Ordinal);
        Assert.Contains("MaximumTotalBytes = 268435456", source, StringComparison.Ordinal);
        Assert.Contains(
            "Runtime server logs exceeded their total safety limit.",
            source,
            StringComparison.Ordinal);
        var serverRetryStart = source.IndexOf(
            "for ($attempt = 1; $attempt -le 10; $attempt++) {",
            StringComparison.Ordinal);
        var serverProcessStart = source.IndexOf(
            "$serverProcess = Start-HiddenServerProcess",
            serverRetryStart,
            StringComparison.Ordinal);
        Assert.True(serverRetryStart >= 0);
        Assert.True(serverProcessStart > serverRetryStart);
        Assert.Contains(
            "Remove-OldRuntimeServerLogs -LogRoot $runtimeLogRoot",
            source[serverRetryStart..serverProcessStart],
            StringComparison.Ordinal);
        var serverLauncherStart = source.IndexOf(
            "function Start-HiddenServerProcess",
            StringComparison.Ordinal);
        var serverLauncherEnd = source.IndexOf(
            "$dotnetExe = '__DOTNET_EXE__'",
            serverLauncherStart,
            StringComparison.Ordinal);
        Assert.True(serverLauncherStart >= 0);
        Assert.True(serverLauncherEnd > serverLauncherStart);
        Assert.Contains(
            "'Logging__LogLevel__Microsoft.AspNetCore' = 'Warning'",
            source[serverLauncherStart..serverLauncherEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command' = 'Warning'",
            source[serverLauncherStart..serverLauncherEnd],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Wait-Process -Id $appProcess.Id",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SoakObservation_IsUtf8BomForWindowsPowerShellCompatibility()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repoRoot,
            "tools",
            "verification",
            "Invoke-GeoraePlanSoakObservation.ps1");
        var bytes = File.ReadAllBytes(scriptPath);

        Assert.True(bytes.Length > 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public void PaidDeliveryGate_CanRequireFreshPassingSoakEvidence()
    {
        var repoRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "verification",
            "Invoke-GeoraePlanPaidDeliveryGate.ps1"));

        Assert.Contains("[string]$SoakEvidencePath", source, StringComparison.Ordinal);
        Assert.Contains("[int]$MaxSoakEvidenceAgeHours = 168", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireSoakPass", source, StringComparison.Ordinal);
        Assert.Contains("soak-observation-evidence", source, StringComparison.Ordinal);
        Assert.Contains("RequireSoakPass was specified, but SoakEvidencePath is empty.", source, StringComparison.Ordinal);
        Assert.Contains("$soakStatus -ne 'PASS'", source, StringComparison.Ordinal);
        Assert.Contains("$soakAge.TotalHours -gt $MaxSoakEvidenceAgeHours", source, StringComparison.Ordinal);
        Assert.Contains("장시간 관찰 PASS 증거를 확인했습니다", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
