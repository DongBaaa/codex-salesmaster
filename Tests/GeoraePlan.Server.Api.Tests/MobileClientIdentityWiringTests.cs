using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class MobileClientIdentityWiringTests
{
    [Fact]
    public void MobileApiAndRecoveryRequestsShareOneIdentityProvider()
    {
        var repositoryRoot = FindRepositoryRoot();
        var providerSource = ReadSource(
            repositoryRoot,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileClientIdentityProvider.cs");
        var apiClientSource = ReadSource(
            repositoryRoot,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "GeoraePlanApiClient.cs");
        var recoverySource = ReadSource(
            repositoryRoot,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileSessionRecoveryService.cs");
        var mauiProgramSource = ReadSource(
            repositoryRoot,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "MauiProgram.cs");

        Assert.Contains("ClientCompatibilityHeaders.AppId", providerSource, StringComparison.Ordinal);
        Assert.Contains("ClientCompatibilityHeaders.Platform", providerSource, StringComparison.Ordinal);
        Assert.Contains("ClientCompatibilityHeaders.Version", providerSource, StringComparison.Ordinal);
        Assert.Contains("ClientCompatibilityHeaders.Build", providerSource, StringComparison.Ordinal);
        Assert.Contains("ClientCompatibilityHeaders.Protocol", providerSource, StringComparison.Ordinal);
        Assert.Contains("request.Headers.Remove(name)", providerSource, StringComparison.Ordinal);
        Assert.Contains("_clientIdentity.Apply(request);", apiClientSource, StringComparison.Ordinal);
        Assert.Contains("using var request = await requestFactory();", apiClientSource, StringComparison.Ordinal);
        Assert.Contains("_clientIdentity.Apply(request);", recoverySource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<MobileClientIdentityProvider>()", mauiProgramSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerPipelineRunsCompatibilityGateAfterAuthorizationBeforeControllers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programSource = ReadSource(
            repositoryRoot,
            "Server",
            "거래플랜.Server.Api",
            "Program.cs");

        var authenticationIndex = programSource.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var authorizationIndex = programSource.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);
        var compatibilityIndex = programSource.IndexOf(
            "app.UseMiddleware<ClientCompatibilityGateMiddleware>();",
            StringComparison.Ordinal);
        var controllersIndex = programSource.IndexOf("app.MapControllers();", StringComparison.Ordinal);

        Assert.True(authenticationIndex >= 0);
        Assert.True(authorizationIndex > authenticationIndex);
        Assert.True(compatibilityIndex > authorizationIndex);
        Assert.True(controllersIndex > compatibilityIndex);
    }

    private static string ReadSource(
        DirectoryInfo repositoryRoot,
        params string[] segments)
    {
        return File.ReadAllText(
            Path.Combine(
                new[] { repositoryRoot.FullName }
                    .Concat(segments)
                    .ToArray()));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 repository root was not found.");
    }
}
