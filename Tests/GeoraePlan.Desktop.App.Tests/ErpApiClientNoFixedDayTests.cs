using System.Net;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ErpApiClientNoFixedDayTests
{
    [Theory]
    [InlineData(0, false, "후불")]
    [InlineData(1, false, "후불")]
    [InlineData(1, true, "후불")]
    [InlineData(0, false, "당월")]
    [InlineData(1, false, "당월")]
    [InlineData(2, false, "당월")]
    [InlineData(2, true, "당월")]
    public async Task NoFixedDay_VerifiesServerAndOriginalAccountBeforeSending(int capability, bool switchAccount, string advanceMode)
    {
        var session = new SessionState();
        session.SetSession("test-session", User("first"), DateTime.UtcNow.AddDays(1));
        var handler = new CapabilityHandler(capability, switchAccount ? () => session.SetSession("changed-session", User("second"), DateTime.UtcNow.AddDays(1)) : null);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, session);
        var request = new SyncPushRequest
        {
            RentalBillingProfiles = [new RentalBillingProfileDto
            {
                Id = Guid.NewGuid(), BillingDayMode = RentalBillingScheduleRules.BillingDayModeNoFixedDay,
                BillingAdvanceMode = advanceMode, BillingDay = 0, MonthlyAmount = 55000, MutationId = "retained-original-mutation"
            }]
        };
        if (capability < (advanceMode == "당월" ? 2 : 1) || switchAccount)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.PushAsync(request));
            Assert.Equal(0, handler.PushCount);
            Assert.Equal("retained-original-mutation", request.RentalBillingProfiles.Single().MutationId);
        }
        else
        {
            var result = await api.PushAsync(request);
            Assert.Equal(1, result!.AcceptedCount);
            Assert.Equal(1, handler.PushCount);
        }
        Assert.Equal(1, handler.StatusCount);
    }

    [Fact]
    public async Task Pull_AdvertisesSupportedBillingScheduleVersion()
    {
        var session = new SessionState();
        session.SetSession("test-session", User("first"), DateTime.UtcNow.AddDays(1));
        var handler = new CapabilityHandler(1);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, session);
        await api.PullAsync(123);
        Assert.Contains("sinceRev=123", handler.PullQuery);
        Assert.Contains($"rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}", handler.PullQuery);
    }

    private static UserSessionDto User(string username) => new()
    {
        Username = username, Role = DomainConstants.RoleAdmin,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeAdmin
    };

    private sealed class CapabilityHandler(int capability, Action? onStatus = null) : HttpMessageHandler
    {
        public int PushCount { get; private set; }
        public int StatusCount { get; private set; }
        public string PullQuery { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/sync/status")
            {
                StatusCount++;
                onStatus?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncStatusDto { RentalBillingScheduleVersion = capability })
                };
            }
            if (request.RequestUri.AbsolutePath == "/sync/pull")
            {
                PullQuery = request.RequestUri.Query;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SyncPullResponse()) };
            }
            Assert.Equal("/sync/push", request.RequestUri.AbsolutePath);
            PushCount++;
            var body = await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken);
            Assert.Equal(RentalBillingScheduleRules.ScheduleCapabilityVersion, body!.RentalBillingScheduleVersion);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SyncPushResult { AcceptedCount = 1 }) };
        }
    }
}
