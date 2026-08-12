using Xunit;
using 거래플랜.Server.Api.Services;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ClientCompatibilityReadinessSnapshotTests
{
    [Fact]
    public void Create_DefaultsToObservableAuditOnlyState()
    {
        var snapshot =
            ClientCompatibilityReadinessSnapshot.Create(options: null);

        Assert.Equal(ClientCompatibilityOptions.AuditOnlyMode, snapshot.Mode);
        Assert.Equal(0, snapshot.ConfiguredPolicyCount);
        Assert.Equal(0, snapshot.EnabledPolicyCount);
        Assert.Empty(snapshot.Policies);
    }

    [Fact]
    public void Create_ReportsOnlyEnabledNonSecretPolicySummaryInStableOrder()
    {
        var options = new ClientCompatibilityOptions
        {
            Mode = ClientCompatibilityOptions.AuditOnlyMode,
            Policies =
            [
                new ClientCompatibilityPolicyOptions
                {
                    Enabled = true,
                    AppId = " kr.georaeplan.mobile ",
                    Platform = " android ",
                    PolicyVersion = 8,
                    RequiresUserAction = true,
                    MinimumVersion = "0.2.82",
                    MinimumBuild = 193,
                    MinimumProtocolVersion = 1,
                    LatestVersion = "0.2.82",
                    LatestBuild = 193,
                    UpdateUrl = "https://example.test/private-path"
                },
                new ClientCompatibilityPolicyOptions
                {
                    Enabled = false,
                    AppId = "disabled",
                    Platform = "windows"
                },
                new ClientCompatibilityPolicyOptions
                {
                    Enabled = true,
                    AppId = "kr.georaeplan.desktop",
                    Platform = "windows",
                    PolicyVersion = 7,
                    RequiresUserAction = true,
                    MinimumVersion = "1.1.689",
                    MinimumBuild = 689,
                    MinimumProtocolVersion = 1,
                    LatestVersion = "1.1.689",
                    LatestBuild = 689,
                    UpdateUrl = "/updates/private"
                }
            ]
        };

        var snapshot =
            ClientCompatibilityReadinessSnapshot.Create(options);

        Assert.Equal(ClientCompatibilityOptions.AuditOnlyMode, snapshot.Mode);
        Assert.Equal(3, snapshot.ConfiguredPolicyCount);
        Assert.Equal(2, snapshot.EnabledPolicyCount);
        Assert.Collection(
            snapshot.Policies,
            desktop =>
            {
                Assert.Equal("kr.georaeplan.desktop", desktop.AppId);
                Assert.Equal("windows", desktop.Platform);
                Assert.Equal(7, desktop.PolicyVersion);
                Assert.True(desktop.RequiresUserAction);
                Assert.Equal("1.1.689", desktop.MinimumVersion);
                Assert.Equal(689, desktop.MinimumBuild);
                Assert.Equal(1, desktop.MinimumProtocolVersion);
            },
            mobile =>
            {
                Assert.Equal("kr.georaeplan.mobile", mobile.AppId);
                Assert.Equal("android", mobile.Platform);
                Assert.Equal(8, mobile.PolicyVersion);
                Assert.True(mobile.RequiresUserAction);
                Assert.Equal("0.2.82", mobile.MinimumVersion);
                Assert.Equal(193, mobile.MinimumBuild);
                Assert.Equal(1, mobile.MinimumProtocolVersion);
            });

        var serialized = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("private-path", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateUrl", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("UpgradeToken", serialized, StringComparison.Ordinal);
        Assert.Contains(
            "\"RequiresUserAction\":true",
            serialized,
            StringComparison.Ordinal);
    }
}
