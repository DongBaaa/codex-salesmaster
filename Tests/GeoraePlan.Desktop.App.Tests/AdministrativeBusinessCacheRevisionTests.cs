using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AdministrativeBusinessCacheRevisionTests
{
    [Fact]
    public async Task SharedMirrorReset_ClearsAdministrativeBusinessCacheRevisions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();
        var session = CreateAdminSession();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        await local.SetSettingAsync("Sync.AdminBusinessCacheRevision.USENET", "100");
        await local.SetSettingAsync("Sync.AdminBusinessCacheRevision.ITWORLD", "200");

        await local.ResetSharedMirrorCacheAsync();

        Assert.Null(await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.USENET"));
        Assert.Null(await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.ITWORLD"));
    }

    [Fact]
    public async Task AdministrativeBusinessCache_ReusesPersistedRevisionAfterServiceRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db);
        var diagnostics = new SyncDiagnosticsService(session);
        var handler = new AdministrativeCachePullHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);

        using (var firstSync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics))
        {
            Assert.True(await firstSync.EnsureAdministrativeBusinessCachesAsync());
        }

        Assert.Equal("100", await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.USENET"));
        Assert.Equal("200", await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.ITWORLD"));

        handler.ClearRequests();
        using (var restartedSync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics))
        {
            Assert.True(await restartedSync.EnsureAdministrativeBusinessCachesAsync());
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(handler.Requests, request => request.DatabaseName == "USENET" && request.SinceRevision == 100);
        Assert.Contains(handler.Requests, request => request.DatabaseName == "ITWORLD" && request.SinceRevision == 200);
        Assert.All(handler.Requests, request => Assert.True(request.RentalAdministrationOnly));
    }

    private static LocalDbContext CreateDbContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options);

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "admin-cache-token",
            new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
        return session;
    }

    private sealed class AdministrativeCachePullHandler : HttpMessageHandler
    {
        public List<PullRequest> Requests { get; } = [];

        public void ClearRequests() => Requests.Clear();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var databaseName = request.Headers.TryGetValues("X-Tenant-Code", out var values)
                ? values.Single()
                : string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            var sinceRevision = ParseLongQueryValue(query, "sinceRev");
            var rentalAdministrationOnly = string.Equals(
                ParseStringQueryValue(query, "rentalAdministrationOnly"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            Requests.Add(new PullRequest(databaseName, sinceRevision, rentalAdministrationOnly));

            var currentRevision = string.Equals(databaseName, "ITWORLD", StringComparison.OrdinalIgnoreCase)
                ? 200L
                : 100L;
            var json = JsonSerializer.Serialize(new SyncPullResponse
            {
                CurrentServerRevision = currentRevision
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private static long ParseLongQueryValue(string query, string key)
            => long.TryParse(ParseStringQueryValue(query, key), out var value) ? value : 0L;

        private static string ParseStringQueryValue(string query, string key)
            => query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                .Select(parts => Uri.UnescapeDataString(parts[1]))
                .FirstOrDefault() ?? string.Empty;
    }

    private sealed record PullRequest(
        string DatabaseName,
        long SinceRevision,
        bool RentalAdministrationOnly);
}
