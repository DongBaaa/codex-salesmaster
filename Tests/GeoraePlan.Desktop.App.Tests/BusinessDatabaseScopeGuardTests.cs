using System.Net;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BusinessDatabaseScopeGuardTests
{
    [Fact]
    public void SessionState_SetBusinessDatabase_IgnoresTenantScopedAdmin()
    {
        var session = new SessionState();
        session.SetSession("tenant-admin-token", CreateUser(TenantScopeCatalog.ScopeTenantAll), DateTime.UtcNow.AddHours(1));

        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");

        Assert.Equal(TenantScopeCatalog.UsenetGroup, session.TenantCode);
        Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup), session.SelectedBusinessDatabaseName);
        Assert.False(session.HasSystemConfigurationScope);
    }

    [Fact]
    public void SessionState_SetBusinessDatabase_AllowsGlobalAdmin()
    {
        var session = new SessionState();
        session.SetSession("global-admin-token", CreateUser(TenantScopeCatalog.ScopeAdmin), DateTime.UtcNow.AddHours(1));

        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");

        Assert.Equal(TenantScopeCatalog.Itworld, session.TenantCode);
        Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld), session.SelectedBusinessDatabaseName);
        Assert.True(session.HasSystemConfigurationScope);
    }

    [Fact]
    public async Task ErpApiClient_DoesNotSendTenantHeader_ForTenantScopedAdmin()
    {
        var session = new SessionState();
        session.SetSession("tenant-admin-token", CreateUser(TenantScopeCatalog.ScopeTenantAll), DateTime.UtcNow.AddHours(1));
        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        var handler = new HeaderCaptureHandler();
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        await api.HeartbeatEditSessionAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = Guid.NewGuid(),
            AppSessionId = Guid.NewGuid(),
            ScreenName = "test",
            EntityType = "Customer",
            EntityId = Guid.NewGuid().ToString("D")
        });

        Assert.Null(handler.LastTenantHeader);
    }

    [Fact]
    public async Task ErpApiClient_SendsTenantHeader_ForGlobalAdmin()
    {
        var session = new SessionState();
        session.SetSession("global-admin-token", CreateUser(TenantScopeCatalog.ScopeAdmin), DateTime.UtcNow.AddHours(1));
        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        var handler = new HeaderCaptureHandler();
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        await api.HeartbeatEditSessionAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = Guid.NewGuid(),
            AppSessionId = Guid.NewGuid(),
            ScreenName = "test",
            EntityType = "Customer",
            EntityId = Guid.NewGuid().ToString("D")
        });

        Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld), handler.LastTenantHeader);
    }

    private static UserSessionDto CreateUser(string scopeType)
        => new()
        {
            UserId = Guid.NewGuid(),
            Username = "admin",
            Role = "Admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = scopeType
        };

    private sealed class HeaderCaptureHandler : HttpMessageHandler
    {
        public string? LastTenantHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastTenantHeader = request.Headers.TryGetValues("X-Tenant-Code", out var values)
                ? values.FirstOrDefault()
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EditSessionHeartbeatResponse
                {
                    ServerUtc = DateTime.UtcNow,
                    OtherEditors = []
                })
            });
        }
    }
}
