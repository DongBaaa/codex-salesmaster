using System.Text.Json;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Middleware;
using 거래플랜.Server.Api.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DatabaseInitializationGateMiddlewareTests
{
    [Fact]
    public async Task StartedInitialization_BlocksUnmarkedEndpointWithRetryAfter()
    {
        var state = new DatabaseInitializationState();
        state.MarkStarted();
        var (context, nextCalled) = await InvokeAsync(state);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);
        Assert.Equal("database_initialization_running", await ReadReasonAsync(context));
    }

    [Fact]
    public async Task FailedInitialization_BlocksUnmarkedEndpointWithRetryAfter()
    {
        var state = new DatabaseInitializationState();
        state.MarkStarted();
        state.MarkFailed(new InvalidOperationException("database unavailable"));
        var (context, nextCalled) = await InvokeAsync(state);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);
        Assert.Equal("database_initialization_failed", await ReadReasonAsync(context));
    }

    [Fact]
    public async Task CompletedInitialization_AllowsUnmarkedEndpoint()
    {
        var state = new DatabaseInitializationState();
        state.MarkStarted();
        state.MarkCompleted();
        var (context, nextCalled) = await InvokeAsync(state);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task MarkedEndpoint_IsAllowedWhenInitializationFailed()
    {
        var state = new DatabaseInitializationState();
        state.MarkStarted();
        state.MarkFailed(new InvalidOperationException("database unavailable"));
        var metadata = new EndpointMetadataCollection(
            new AllowDuringDatabaseInitializationAttribute());
        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            metadata,
            "initialization-safe endpoint");
        var (context, nextCalled) = await InvokeAsync(state, endpoint);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public void ControllerAllowlist_ContainsOnlyManifestAndDownloadActions()
    {
        var markedActions = typeof(UpdatesController).Assembly
            .GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type
                .GetMethods()
                .Where(method => method.DeclaringType == type))
            .Where(method => method.GetCustomAttributes(
                typeof(AllowDuringDatabaseInitializationAttribute),
                inherit: true).Length > 0)
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                $"{typeof(UpdatesController).FullName}.{nameof(UpdatesController.DownloadPackage)}",
                $"{typeof(UpdatesController).FullName}.{nameof(UpdatesController.GetManifestAsync)}",
                $"{typeof(UpdatesController).FullName}.{nameof(UpdatesController.HeadPackage)}"
            ],
            markedActions);
    }

    [Fact]
    public void HostedPipeline_AppliesCommonResponseMiddlewareBeforeGate_AndGatesBeforeAuthentication()
    {
        var programSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot().FullName,
            "Server",
            "거래플랜.Server.Api",
            "Program.cs"));

        var routing = programSource.IndexOf("app.UseRouting();", StringComparison.Ordinal);
        var gate = programSource.IndexOf(
            "app.UseMiddleware<DatabaseInitializationGateMiddleware>();",
            StringComparison.Ordinal);
        var forwardedHeaders = programSource.IndexOf(
            "app.UseForwardedHeaders();",
            StringComparison.Ordinal);
        var securityHeaders = programSource.IndexOf(
            "if (securityOptions.AddSecurityHeaders)",
            StringComparison.Ordinal);
        var rateLimiter = programSource.IndexOf(
            "app.UseRateLimiter();",
            StringComparison.Ordinal);
        var cors = programSource.IndexOf(
            "app.UseCors(\"DesktopClient\");",
            StringComparison.Ordinal);
        var authentication = programSource.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var authorization = programSource.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);
        var swagger = programSource.IndexOf("app.UseSwagger();", StringComparison.Ordinal);
        var markStarted = programSource.IndexOf(
            "databaseInitializationState.MarkStarted();",
            StringComparison.Ordinal);
        var backgroundTask = programSource.IndexOf(
            "var databaseInitializationTask = Task.Run",
            StringComparison.Ordinal);

        Assert.True(forwardedHeaders >= 0 && forwardedHeaders < securityHeaders);
        Assert.True(securityHeaders < routing);
        Assert.True(routing >= 0 && routing < gate);
        Assert.True(rateLimiter < 0 || (routing < rateLimiter && rateLimiter < cors));
        Assert.True(cors >= 0 && cors < gate);
        Assert.True(swagger < 0 || (gate < swagger && swagger < authentication));
        Assert.True(gate < authentication);
        Assert.True(authentication < authorization);
        Assert.True(markStarted >= 0 && markStarted < backgroundTask);

        var healthBlock = ReadSourceBlock(
            programSource,
            "app.MapGet(\"/healthz\"",
            "app.MapGet(\"/readyz\"");
        var readyBlock = ReadSourceBlock(
            programSource,
            "app.MapGet(\"/readyz\"",
            "var databaseInitializationState =");
        Assert.Equal(
            1,
            CountOccurrences(
                healthBlock,
                ".WithMetadata(new AllowDuringDatabaseInitializationAttribute())"));
        Assert.Equal(
            1,
            CountOccurrences(
                readyBlock,
                ".WithMetadata(new AllowDuringDatabaseInitializationAttribute())"));
        Assert.Equal(
            1,
            CountOccurrences(
                healthBlock,
                "clientCompatibility = ClientCompatibilityReadinessSnapshot.Create("));
        Assert.Equal(
            5,
            CountOccurrences(
                readyBlock,
                "clientCompatibility,"));
        Assert.Contains(
            "status = \"ready\"",
            readyBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileDeletionLeaseProtocol = StoredFileDeletionLease.ProtocolVersion",
            readyBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "message = ex.Message",
            readyBlock,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                programSource,
                ".WithMetadata(new AllowDuringDatabaseInitializationAttribute())"));
    }

    private static async Task<(DefaultHttpContext Context, Func<bool> NextCalled)> InvokeAsync(
        DatabaseInitializationState state,
        Endpoint? endpoint = null)
    {
        var nextCalled = false;
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);
        var middleware = new DatabaseInitializationGateMiddleware(httpContext =>
        {
            nextCalled = true;
            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, state);
        return (context, () => nextCalled);
    }

    private static async Task<string> ReadReasonAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("reason").GetString() ?? string.Empty;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadSourceBlock(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Source marker was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Source marker was not found after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Server")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Tests")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
