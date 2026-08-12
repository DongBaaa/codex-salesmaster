using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EphemeralPostgreSqlTestRunnerGuardTests
{
    [Fact]
    public void Runner_UsesLoopbackTrustInAUniqueManagedClusterAndCleansOnlyAfterSuccess()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "verification",
            "Invoke-GeoraePlanEphemeralPostgreSqlTests.ps1"));

        Assert.Contains(
            "Assert-ManagedChildPath",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'cluster-' + [Guid]::NewGuid().ToString('N')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'--auth-host=trust'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-o \"-p $port -h 127.0.0.1\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($testsPassed -and",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "($stopped -or -not $started)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item -LiteralPath $clusterRoot -Recurse -Force",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Password=",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "-h 0.0.0.0",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "tools")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }
}
