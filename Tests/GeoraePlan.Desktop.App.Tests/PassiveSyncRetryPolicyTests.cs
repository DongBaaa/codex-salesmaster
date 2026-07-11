using System.Reflection;
using 거래플랜.Desktop.App;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PassiveSyncRetryPolicyTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 300)]
    [InlineData(20, 300)]
    public void PassiveSyncFailure_UsesBoundedExponentialBackoff(int failureCount, int expectedSeconds)
    {
        var method = typeof(MainWindow).GetMethod(
            "ComputePassiveSyncRetryDelay",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = Assert.IsType<TimeSpan>(method.Invoke(null, [failureCount]));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Fact]
    public void RealtimeRevisionMonitor_HonorsPassiveSyncBackoffAndResetsAfterSuccess()
    {
        var sourcePath = Path.Combine(
            ResolveProjectRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("var passiveRetryDelay = GetRemainingPassiveSyncRetryDelay();", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(passiveRetryDelay, ct);", source, StringComparison.Ordinal);
        Assert.Contains("RecordPassiveSyncFailure(reason);", source, StringComparison.Ordinal);
        Assert.Contains("ResetPassiveSyncFailureBackoff();", source, StringComparison.Ordinal);
    }

    private static string ResolveProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 프로젝트 루트를 찾지 못했습니다.");
    }
}
