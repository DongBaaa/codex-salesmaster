using 거래플랜.Server.Api.Services;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RevisionClockTests
{
    [Fact]
    public void Initialize_SetsCurrentToMaxExistingRevision()
    {
        var clock = new RevisionClock();

        clock.Initialize(1234);

        Assert.Equal(1234, clock.Current);
    }

    [Fact]
    public void NextRevision_AlwaysMonotonicallyIncreases()
    {
        var clock = new RevisionClock();
        clock.Initialize(5000);

        var first = clock.NextRevision();
        var second = clock.NextRevision();

        Assert.True(first > 5000);
        Assert.True(second > first);
        Assert.Equal(second, clock.Current);
    }
}
