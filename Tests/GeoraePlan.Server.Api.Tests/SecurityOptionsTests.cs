using 거래플랜.Server.Api.Security;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SecurityOptionsTests
{
    [Fact]
    public void DefaultLoginPermitLimit_AllowsOfficeStartupBurst()
    {
        var options = new SecurityOptions();

        Assert.True(options.LoginPermitLimitPerMinute >= 60);
    }
}
