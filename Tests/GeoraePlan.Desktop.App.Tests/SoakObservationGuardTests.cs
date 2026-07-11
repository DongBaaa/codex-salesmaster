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
