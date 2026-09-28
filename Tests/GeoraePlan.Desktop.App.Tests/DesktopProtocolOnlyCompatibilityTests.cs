using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DesktopProtocolOnlyCompatibilityTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-client")]
    [InlineData("invalid-minimum-version")]
    [InlineData("invalid-latest-version")]
    [InlineData("zero-minimum-build")]
    [InlineData("negative-latest-build")]
    [InlineData("partial-version")]
    [InlineData("no-protocol")]
    public async Task ProtocolOnlyRequirement_PreservesKnownBoundWithoutAcceptingMalformedPolicy(string mode)
    {
        var runtime = new DesktopClientRuntimeIdentity("1.1.741", 741, 3);
        var response = new ClientUpgradeRequiredResponse {
            Client = new ClientCompatibilityIdentityDto { AppId = DesktopClientIdentityProvider.DesktopAppId,
                Platform = DesktopClientIdentityProvider.DesktopPlatform, Version = runtime.Version,
                Build = runtime.Build, ProtocolVersion = runtime.ProtocolVersion },
            Required = new ClientCompatibilityPolicyDto { PolicyVersion = 1, RequiresUserAction = true, MinimumProtocolVersion = 4 } };
        switch (mode)
        {
            case "wrong-client": response.Client.Platform = "android"; break;
            case "invalid-minimum-version": response.Required.MinimumVersion = "not-a-version"; break;
            case "invalid-latest-version": response.Required.LatestVersion = "0.0"; break;
            case "zero-minimum-build": response.Required.MinimumBuild = 0; break;
            case "negative-latest-build": response.Required.LatestBuild = -1; break;
            case "partial-version": response.Required.MinimumVersion = "1.1.742"; break;
            case "no-protocol": response.Required.MinimumProtocolVersion = null; break;
        }
        using var content = JsonContent.Create(response);
        var exception = await DesktopUpgradeRequiredResponseParser.CreateExceptionAsync("/sync/pull", content);
        var evidence = DesktopCompatibilityPolicy.From426(exception, runtime, DateTime.UnixEpoch);
        Assert.True(DesktopCompatibilityPolicy.IsValidEvidenceShape(evidence));
        if (mode != "valid")
        {
            Assert.Equal(DesktopCompatibilityEvidenceKind.Opaque426, evidence.Kind);
            return;
        }
        Assert.Equal(DesktopCompatibilityEvidenceKind.Verified426, evidence.Kind);
        Assert.Equal(4, evidence.MinimumProtocolVersion);
        Assert.Equal(runtime.Version, evidence.MinimumVersion);
        Assert.Equal(runtime.Build, evidence.MinimumBuild);
        Assert.False(DesktopCompatibilityPolicy.RuntimeSatisfies(evidence, runtime with { Version = "1.1.742", Build = 742 }));
        Assert.True(DesktopCompatibilityPolicy.RuntimeSatisfies(evidence, runtime with { ProtocolVersion = 4 }));
        var stronger = evidence with { MinimumProtocolVersion = 5 };
        Assert.Equal(5, DesktopCompatibilityPolicy.Merge(stronger, evidence).MinimumProtocolVersion);
    }
}
