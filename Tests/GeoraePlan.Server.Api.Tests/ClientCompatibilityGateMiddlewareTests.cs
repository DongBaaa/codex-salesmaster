using System.Net;
using System.Security.Claims;
using System.Text.Json;
using 거래플랜.Server.Api.Middleware;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ClientCompatibilityGateMiddlewareTests
{
    [Fact]
    public async Task DefaultAuditOnly_AllowsMissingIdentityOnMutation()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new ClientCompatibilityOptions(),
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Post, "/sync/push");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task AuditOnly_AllowsKnownOldMutation()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.AuditOnlyMode),
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Post, "/sync/push");
        AddIdentity(context, version: "0.2.81", build: 192, protocol: 1);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictBlock_ReturnsTyped426ForKnownOldMutation()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Post, "/sync/push");
        AddIdentity(context, version: "0.2.81", build: 192, protocol: 1);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal((int)HttpStatusCode.UpgradeRequired, context.Response.StatusCode);
        Assert.Equal("georaeplan-client", context.Response.Headers.Upgrade);
        context.Response.Body.Position = 0;
        var payload = await JsonSerializer.DeserializeAsync<ClientUpgradeRequiredResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal("client_upgrade_required", payload.Error);
        Assert.Equal("kr.georaeplan.mobile", payload.Client.AppId);
        Assert.Equal("android", payload.Client.Platform);
        Assert.Equal(193, payload.Required.MinimumBuild);
        Assert.Equal(2, payload.Required.MinimumProtocolVersion);
        Assert.Equal(7, payload.Required.PolicyVersion);
        Assert.True(payload.Required.RequiresUserAction);
    }

    [Fact]
    public async Task StrictBlock_Typed426AlwaysRequiresUserActionEvenIfRuntimeOptionsBypassValidation()
    {
        var options =
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode);
        options.Policies.Single()!.RequiresUserAction = false;
        var middleware = CreateMiddleware(
            options,
            _ => throw new InvalidOperationException(
                "Blocked requests must not reach the endpoint."));
        var context = CreateContext("POST", "/sync/push");
        AddIdentity(
            context,
            version: "0.2.81",
            build: 192,
            protocol: 1);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var payload =
            await JsonSerializer.DeserializeAsync<ClientUpgradeRequiredResponse>(
                context.Response.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
        Assert.NotNull(payload);
        Assert.True(payload.Required.RequiresUserAction);
    }

    [Fact]
    public async Task StrictBlock_AllowsCurrentKnownClient()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Delete, "/customers/00000000-0000-0000-0000-000000000001");
        AddIdentity(context, version: "0.2.82", build: 193, protocol: 2);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictBlock_BlocksMalformedMultiValueIdentityOnMutation()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Post, "/sync/push");
        AddIdentity(context, version: "0.2.81", build: 192, protocol: 1);
        context.Request.Headers[ClientCompatibilityHeaders.Build] =
            new StringValues(["192", "193"]);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictBlock_BlocksMissingIdentityOnMutation()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Post, "/sync/push");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("kr.georaeplan.mobile.spoof", "android")]
    [InlineData("kr.georaeplan.mobile", "android-preview")]
    public async Task StrictBlock_BlocksUnknownClientIdentityOnMutation(
        string appId,
        string platform)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Delete, "/payments/00000000-0000-0000-0000-000000000001");
        AddIdentity(context, version: "9.9.9", build: 9999, protocol: 999);
        context.Request.Headers[ClientCompatibilityHeaders.AppId] = appId;
        context.Request.Headers[ClientCompatibilityHeaders.Platform] = platform;

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictBlock_BlocksMatchingClientWhenRuntimePolicyIsMalformed()
    {
        var nextCalled = false;
        var options = CreateOptions(ClientCompatibilityOptions.StrictBlockMode);
        options.Policies.Single()!.MinimumVersion = "not-a-version";
        var middleware = CreateMiddleware(
            options,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Post, "/sync/push");
        AddIdentity(context, version: "9.9.9", build: 9999, protocol: 999);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
    }

    [Fact]
    public async Task AuditOnly_AllowsMatchingClientWhenRuntimePolicyIsMalformed()
    {
        var nextCalled = false;
        var options = CreateOptions(ClientCompatibilityOptions.AuditOnlyMode);
        options.Policies.Single()!.MinimumVersion = "not-a-version";
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode =
                    StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        var context = CreateContext(HttpMethods.Post, "/sync/push");
        AddIdentity(context, version: "9.9.9", build: 9999, protocol: 999);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task StrictBlock_AuditsKnownOldSafeAndLoginRequestsWithoutBlocking()
    {
        var logger = new ListLogger<ClientCompatibilityGateMiddleware>();
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            logger);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethods.Get, "/sync/pull"),
                     (HttpMethods.Post, "/auth/login"),
                     (HttpMethods.Post, "/updates/manifest")
                 })
        {
            var context = CreateContext(method, path);
            AddIdentity(context, version: "0.2.81", build: 192, protocol: 1);
            await middleware.InvokeAsync(context);
            Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        }

        Assert.Equal(
            3,
            logger.Messages.Count(message =>
                message.Contains("known_old_exempt_allowed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StrictBlock_NullBoundPolicyValuesFailClosedWithoutThrowing()
    {
        var logger = new ListLogger<ClientCompatibilityGateMiddleware>();
        var options = new ClientCompatibilityOptions
        {
            Mode = ClientCompatibilityOptions.StrictBlockMode,
            Policies =
            [
                null,
                new ClientCompatibilityPolicyOptions
                {
                    AppId = null,
                    Platform = null
                },
                new ClientCompatibilityPolicyOptions
                {
                    AppId = "kr.georaeplan.mobile",
                    Platform = "android",
                    PolicyVersion = 1,
                    MinimumVersion = null,
                    LatestVersion = null,
                    UpdateUrl = null,
                    UpgradeToken = null
                }
            ]
        };
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            logger);
        var context = CreateContext(HttpMethods.Post, "/sync/push");
        AddIdentity(context, version: "0.2.82", build: 193, protocol: 2);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                "malformed_policy_blocked",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StrictBlock_DoesNotTreatUpdatesPrefixCollisionAsRecoveryEndpoint()
    {
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            _ => Task.CompletedTask);
        var context = CreateContext(HttpMethods.Post, "/updates-malicious");
        AddIdentity(context, version: "0.2.81", build: 192, protocol: 1);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/sync/pull")]
    [InlineData("HEAD", "/sync/status")]
    [InlineData("OPTIONS", "/sync/push")]
    [InlineData("POST", "/auth/login")]
    [InlineData("POST", "/auth/refresh")]
    [InlineData("POST", "/updates/manifest")]
    [InlineData("POST", "/healthz")]
    [InlineData("POST", "/readyz")]
    public async Task StrictBlock_AlwaysAllowsSafeOrRecoveryEndpoints(
        string method,
        string path)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode),
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        var context = CreateContext(method, path);
        AddIdentity(context, version: "0.1.0", build: 1, protocol: 1);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public void OptionsValidator_AcceptsDefaultAuditOnlyWithoutPolicies()
    {
        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            new ClientCompatibilityOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("StrictBlock ")]
    [InlineData("strict")]
    [InlineData("")]
    public void OptionsValidator_RejectsUnknownOrWhitespaceMode(string mode)
    {
        var options = CreateOptions(ClientCompatibilityOptions.AuditOnlyMode);
        options.Mode = mode;

        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "ClientCompatibility:Mode",
                StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_RejectsDuplicateEnabledClientKeys()
    {
        var options = CreateOptions(ClientCompatibilityOptions.StrictBlockMode);
        var duplicate = CreateOptions(
                ClientCompatibilityOptions.StrictBlockMode)
            .Policies
            .Single()!;
        duplicate.AppId = " KR.GEORAEPLAN.MOBILE ";
        duplicate.Platform = " Android ";
        options.Policies.Add(duplicate);

        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "duplicate enabled policy key",
                StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_RejectsStrictModeWithoutUsablePolicy()
    {
        var options = new ClientCompatibilityOptions
        {
            Mode = ClientCompatibilityOptions.StrictBlockMode,
            Policies = []
        };

        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "requires at least one enabled policy",
                StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_RejectsMalformedVersionBuildAndUnsafeUpdateUrl()
    {
        var options = CreateOptions(ClientCompatibilityOptions.StrictBlockMode);
        var policy = options.Policies.Single()!;
        policy.MinimumVersion = "0.2.82-preview";
        policy.MinimumBuild = 0;
        policy.LatestVersion = "0.2.81";
        policy.LatestBuild = -1;
        policy.UpdateUrl = "http://example.test/update.apk";

        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("MinimumVersion", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("MinimumBuild", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("LatestBuild", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("UpdateUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_RejectsEnabledPolicyThatDeniesRequiredUserAction()
    {
        var options =
            CreateOptions(ClientCompatibilityOptions.AuditOnlyMode);
        options.Policies.Single()!.RequiresUserAction = false;

        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "RequiresUserAction must be true",
                StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_RejectsStrictModeUntilBothSupportedClientsHavePolicies()
    {
        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode));

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "appId='kr.georaeplan.desktop', platform='windows'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_AcceptsStrictModeWithBothSupportedClientPolicies()
    {
        var options =
            CreateOptions(ClientCompatibilityOptions.StrictBlockMode);
        options.Policies.Add(
            new ClientCompatibilityPolicyOptions
            {
                AppId = "kr.georaeplan.desktop",
                Platform = "windows",
                PolicyVersion = 7,
                RequiresUserAction = true,
                MinimumVersion = "1.1.689",
                MinimumBuild = 689,
                MinimumProtocolVersion = 1,
                LatestVersion = "1.1.689",
                LatestBuild = 689,
                UpdateUrl = "/updates/manifest?channel=stable"
            });

        var result = new ClientCompatibilityOptionsValidator().Validate(
            name: null,
            options);

        Assert.True(result.Succeeded);
    }

    private static ClientCompatibilityGateMiddleware CreateMiddleware(
        ClientCompatibilityOptions options,
        RequestDelegate next,
        ILogger<ClientCompatibilityGateMiddleware>? logger = null)
    {
        return new ClientCompatibilityGateMiddleware(
            next,
            Options.Create(options),
            logger ?? NullLogger<ClientCompatibilityGateMiddleware>.Instance);
    }

    private static ClientCompatibilityOptions CreateOptions(string mode)
    {
        return new ClientCompatibilityOptions
        {
            Mode = mode,
            Policies =
            [
                new ClientCompatibilityPolicyOptions
                {
                    AppId = "kr.georaeplan.mobile",
                    Platform = "android",
                    PolicyVersion = 7,
                    RequiresUserAction = true,
                    MinimumVersion = "0.2.82",
                    MinimumBuild = 193,
                    MinimumProtocolVersion = 2,
                    LatestVersion = "0.2.82",
                    LatestBuild = 193,
                    UpdateUrl = "/updates/manifest?channel=stable"
                }
            ]
        };
    }

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
                new Claim("tenant", "USENET_GROUP"),
                new Claim("office", "USENET")
            ],
            authenticationType: "test"));
        return context;
    }

    private static void AddIdentity(
        HttpContext context,
        string version,
        int build,
        int protocol)
    {
        context.Request.Headers[ClientCompatibilityHeaders.AppId] = "kr.georaeplan.mobile";
        context.Request.Headers[ClientCompatibilityHeaders.Platform] = "android";
        context.Request.Headers[ClientCompatibilityHeaders.Version] = version;
        context.Request.Headers[ClientCompatibilityHeaders.Build] = build.ToString();
        context.Request.Headers[ClientCompatibilityHeaders.Protocol] = protocol.ToString();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
