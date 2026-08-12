using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalManagementCompanyPushPayloadTests
{
    [Fact]
    public void BuildPayload_PrefersSingleDirtyRow_OverCleanNaturalKeyAlias()
    {
        var now = new DateTime(
            2026,
            7,
            30,
            9,
            0,
            0,
            DateTimeKind.Utc);
        var dirty = CreateCompany(
            Guid.NewGuid(),
            " UZNET ",
            "DIRTY-CANONICAL",
            revision: 7,
            updatedAtUtc: now,
            isDirty: true);
        var cleanAlias = CreateCompany(
            Guid.NewGuid(),
            "USENET",
            "STALE-CLEAN-ALIAS",
            revision: 99,
            updatedAtUtc: now.AddHours(1),
            isDirty: false);

        var payload =
            SyncService.BuildRentalManagementCompanyPushPayload(
                [dirty],
                [cleanAlias]);

        var selected = Assert.Single(payload);
        Assert.Equal(dirty.Id, selected.Id);
        Assert.Equal(dirty.Name, selected.Name);
        Assert.Equal(dirty.Revision, selected.Revision);
        Assert.DoesNotContain(
            payload,
            company => company.Id == cleanAlias.Id);
    }

    [Fact]
    public void BuildPayload_SelectsCleanAliasDeterministically()
    {
        var now = new DateTime(
            2026,
            7,
            30,
            9,
            0,
            0,
            DateTimeKind.Utc);
        var older = CreateCompany(
            Guid.NewGuid(),
            OfficeCodeCatalog.Itworld,
            "OLDER",
            revision: 5,
            updatedAtUtc: now,
            isDirty: false);
        var newer = CreateCompany(
            Guid.NewGuid(),
            " itworld ",
            "NEWER",
            revision: 6,
            updatedAtUtc: now.AddMinutes(1),
            isDirty: false);

        var payload =
            SyncService.BuildRentalManagementCompanyPushPayload(
                [],
                [older, newer]);

        Assert.Equal(newer.Id, Assert.Single(payload).Id);
    }

    [Fact]
    public void BuildPayload_CollapsesBlankUnknownAndSharedAliasesLikeServer()
    {
        var now = new DateTime(
            2026,
            7,
            30,
            9,
            0,
            0,
            DateTimeKind.Utc);
        var dirtyBlank = CreateCompany(
            Guid.NewGuid(),
            string.Empty,
            "DIRTY-BLANK",
            revision: 3,
            updatedAtUtc: now,
            isDirty: true);
        var cleanUnknown = CreateCompany(
            Guid.NewGuid(),
            "UNKNOWN-OFFICE",
            "CLEAN-UNKNOWN",
            revision: 99,
            updatedAtUtc: now.AddHours(2),
            isDirty: false);
        var cleanShared = CreateCompany(
            Guid.NewGuid(),
            OfficeCodeCatalog.Shared,
            "CLEAN-SHARED",
            revision: 100,
            updatedAtUtc: now.AddHours(3),
            isDirty: false);

        var payload =
            SyncService.BuildRentalManagementCompanyPushPayload(
                [dirtyBlank],
                [cleanUnknown, cleanShared]);

        Assert.Equal(dirtyBlank.Id, Assert.Single(payload).Id);
    }

    [Fact]
    public void BuildPayload_BlocksMultipleDirtyNaturalKeyRows_WithoutMutation()
    {
        var now = new DateTime(
            2026,
            7,
            30,
            9,
            0,
            0,
            DateTimeKind.Utc);
        var first = CreateCompany(
            Guid.NewGuid(),
            OfficeCodeCatalog.Usenet,
            "DIRTY-ONE",
            revision: 7,
            updatedAtUtc: now,
            isDirty: true);
        var second = CreateCompany(
            Guid.NewGuid(),
            " UZNET ",
            "DIRTY-TWO",
            revision: 7,
            updatedAtUtc: now,
            isDirty: true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SyncService.BuildRentalManagementCompanyPushPayload(
                [first, second],
                []));

        Assert.Contains(
            "blocked before mutation stamping or server submission",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.Equal("DIRTY-ONE", first.Name);
        Assert.Equal("DIRTY-TWO", second.Name);
    }

    [Fact]
    public async Task FlushPendingChangesAsync_BlocksMultipleDirtyNaturalKeyRows_BeforeServerCall()
    {
        await using var connection =
            new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options =
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = new DateTime(
            2026,
            7,
            30,
            9,
            0,
            0,
            DateTimeKind.Utc);
        var first = CreateCompany(
            Guid.NewGuid(),
            OfficeCodeCatalog.Usenet,
            "DIRTY-ONE",
            revision: 7,
            updatedAtUtc: now,
            isDirty: true);
        var second = CreateCompany(
            Guid.NewGuid(),
            " UZNET ",
            "DIRTY-TWO",
            revision: 7,
            updatedAtUtc: now,
            isDirty: true);
        db.RentalManagementCompanies.AddRange(first, second);
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession(
            "rental-settings-test-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "yeonsu",
                Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions =
                [
                    AppPermissionNames.RentalSettingsEdit
                ]
            });
        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(
            db,
            new OfficeAccessService(),
            dispatcher,
            session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CountingSyncHandler();
        var api = new ErpApiClient(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/")
            },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.False(await sync.FlushPendingChangesAsync());
        Assert.Equal(0, handler.PushCount);
        Assert.Equal(
            2,
            await db.RentalManagementCompanies
                .AsNoTracking()
                .CountAsync(company => company.IsDirty));
        Assert.Equal(
            new[] { "DIRTY-ONE", "DIRTY-TWO" },
            await db.RentalManagementCompanies
                .AsNoTracking()
                .OrderBy(company => company.Name)
                .Select(company => company.Name)
                .ToArrayAsync());
    }

    private static LocalRentalManagementCompany CreateCompany(
        Guid id,
        string code,
        string name,
        long revision,
        DateTime updatedAtUtc,
        bool isDirty)
        => new()
        {
            Id = id,
            Code = code,
            Name = name,
            IsActive = true,
            Revision = revision,
            IsDirty = isDirty,
            CreatedAtUtc = updatedAtUtc.AddDays(-1),
            UpdatedAtUtc = updatedAtUtc
        };

    private static async Task EnsureDiagnosticsDatabaseAsync()
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(AppPaths.LocalDbFile)!);
        var options =
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(
                    $"Data Source={AppPaths.LocalDbFile};Pooling=False")
                .Options;
        await using var diagnosticsDb =
            new LocalDbContext(options);
        await diagnosticsDb.Database.EnsureCreatedAsync();
    }

    private sealed class CountingSyncHandler : HttpMessageHandler
    {
        public int PushCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Equals(
                    "/sync/push",
                    StringComparison.OrdinalIgnoreCase))
            {
                PushCount++;
                return Task.FromResult(JsonResponse(
                    new SyncPushResult
                    {
                        CurrentServerRevision = 1
                    }));
            }

            if (request.RequestUri.AbsolutePath.Equals(
                    "/sync/pull",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 1
                    }));
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(object payload)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };
    }
}
