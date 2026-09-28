using System.Net;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EnvironmentSettingsScopeInitializationTests
{
    [Theory]
    [InlineData("User", "USENET_GROUP", "YEONSU", "OfficeOnly", false)]
    [InlineData("Admin", "USENET_GROUP", "USENET", "TenantAll", false)]
    [InlineData("Admin", "ITWORLD", "ITWORLD", "TenantAll", false)]
    [InlineData("Admin", "USENET_GROUP", "USENET", "Admin", true)]
    public async Task InitialReadLoadsOnlyAuthorizedConfigurationAndOwnScope(
        string role, string tenant, string office, string scope, bool systemAdministrator)
    {
        using var fixture = new Fixture(role, tenant, office, scope);
        await fixture.ViewModel.InitializeTenantConfigurationAsync();

        Assert.Equal(systemAdministrator ? 1 : 0, fixture.Handler.SettingsReads);
        Assert.Equal(1, fixture.Handler.ScopeReads);
        Assert.Equal(systemAdministrator, fixture.ViewModel.CanManageTenantConfiguration);
        var area = Assert.Single(fixture.ViewModel.CurrentScopeMatrixAreas);
        Assert.Equal([office], area.ReadableOfficeCodes);
        Assert.Equal([office], area.WritableOfficeCodes);
        Assert.Contains(office, fixture.ViewModel.CurrentScopeMatrixSummary);
        Assert.Equal(0, fixture.Handler.Writes);
    }

    [Theory]
    [InlineData("User", "OfficeOnly")]
    [InlineData("Admin", "TenantAll")]
    [InlineData("Admin", "Admin")]
    public async Task OfflineInitialReadUsesLocalHintWithoutServerRequests(string role, string scope)
    {
        using var fixture = new Fixture(role, "USENET_GROUP", "USENET", scope, offline: true);
        await fixture.ViewModel.InitializeTenantConfigurationAsync();

        Assert.Equal(0, fixture.Handler.SettingsReads);
        Assert.Equal(0, fixture.Handler.ScopeReads);
        Assert.Equal(0, fixture.Handler.Writes);
        Assert.False(fixture.ViewModel.CanManageTenantConfiguration);
        Assert.Empty(fixture.ViewModel.CurrentScopeMatrixAreas);
        Assert.Equal("오프라인", fixture.ViewModel.CurrentScopeMatrixGeneratedAtText);
    }

    [Theory]
    [InlineData("User", "OfficeOnly")]
    [InlineData("Admin", "TenantAll")]
    public async Task DirectPrivilegedReloadRemainsBlocked(string role, string scope)
    {
        using var fixture = new Fixture(role, "USENET_GROUP", "USENET", scope);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.ViewModel.ReloadTenantConfigurationCommand.ExecuteAsync(null));
        Assert.Equal(0, fixture.Handler.SettingsReads);
        Assert.Equal(0, fixture.Handler.ScopeReads);
        Assert.Equal(0, fixture.Handler.Writes);
    }

    [Fact]
    public async Task AuthorizedConfigurationFailureIsNotSilentlySkipped()
    {
        using var fixture = new Fixture("Admin", "USENET_GROUP", "USENET", "Admin");
        fixture.Handler.FailSettings = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ViewModel.InitializeTenantConfigurationAsync());
        Assert.Equal(1, fixture.Handler.SettingsReads);
        Assert.Equal(0, fixture.Handler.ScopeReads);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        internal SettingsHandler Handler { get; }
        internal EnvironmentSettingsViewModel ViewModel { get; }

        internal Fixture(string role, string tenant, string office, string scope, bool offline = false)
        {
            var user = new UserSessionDto
            {
                UserId = Guid.NewGuid(), Username = "scope-fixture", Role = role,
                TenantCode = tenant, OfficeCode = office, ScopeType = scope
            };
            var session = new SessionState();
            if (offline) session.SetOfflineSession(user);
            else session.SetSession("isolated-test-token", user, DateTime.UtcNow.AddHours(1));
            Handler = new SettingsHandler(session);
            _http = new HttpClient(Handler) { BaseAddress = new Uri("http://localhost/") };
            var api = new ErpApiClient(_http, session);
            ViewModel = new EnvironmentSettingsViewModel(
                null!, session, api, null!, null!, null!, null!, null!, null!, null!, null!);
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class SettingsHandler(SessionState session) : HttpMessageHandler
    {
        internal int SettingsReads;
        internal int ScopeReads;
        internal int Writes;
        internal bool FailSettings;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) Writes++;
            if (request.RequestUri!.AbsolutePath == "/tenant-settings")
            {
                SettingsReads++;
                return Task.FromResult(FailSettings
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new TenantConfigurationSnapshotDto()) });
            }
            if (request.RequestUri.AbsolutePath == "/runtime/scope-matrix")
            {
                ScopeReads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ScopeMatrixSnapshotDto
                    {
                        Username = session.User!.Username, TenantCode = session.TenantCode,
                        OfficeCode = session.OfficeCode, ScopeType = session.ScopeType,
                        Areas = [new ScopeMatrixAreaDto
                        {
                            AreaCode = "items", AreaDisplayName = "품목",
                            ReadableOfficeCodes = [session.OfficeCode], WritableOfficeCodes = [session.OfficeCode]
                        }]
                    })
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
