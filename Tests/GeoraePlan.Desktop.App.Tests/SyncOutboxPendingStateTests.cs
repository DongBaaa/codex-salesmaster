using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncOutboxPendingStateTests
{
    [Fact]
    public async Task HasPendingSyncChangesAsync_TreatsNonAcknowledgedOutboxAsPending()
    {
        PrepareAppRoot("georaeplan-outbox-pending-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            db.SyncOutboxEntries.Add(CreateOutboxEntry("Sent"));
            await db.SaveChangesAsync();

            Assert.True(await local.HasPendingSyncChangesAsync());

            var summary = await local.GetPendingSyncSummaryAsync();
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "동기화 전송 확인");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledgedForCleanEntities_RequiresPulledMatchingServerSnapshot()
    {
        PrepareAppRoot("georaeplan-outbox-reconcile-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "outbox 서버 확인 거래처",
                NameMatchKey = "outbox 서버 확인 거래처",
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now,
                Revision = 12,
                IsDirty = false
            });
            db.SyncOutboxEntries.Add(CreateOutboxEntry("Sent", nameof(LocalCustomer), customerId));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var changedWithoutServerEvidence = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                []);

            Assert.Equal(0, changedWithoutServerEvidence);
            Assert.Equal("Sent", await ReadOutboxStatusAsync(db));

            var mismatchDto = LocalMappings.ToDto(await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            mismatchDto.NameOriginal = "서버의 다른 거래처명";
            mismatchDto.NameMatchKey = "서버의 다른 거래처명";
            var changedWithMismatch = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [mismatchDto]);

            Assert.Equal(0, changedWithMismatch);
            Assert.Equal("Sent", await ReadOutboxStatusAsync(db));

            var matchingDto = LocalMappings.ToDto(await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            var changedWithMatchingServerSnapshot = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [matchingDto]);

            Assert.Equal(1, changedWithMatchingServerSnapshot);
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncOutboxDiagnosticsOperations_AreLimitedToCurrentSessionScope()
    {
        PrepareAppRoot("georaeplan-outbox-session-scope-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetSession = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);
            var usenetFailedId = Guid.NewGuid();
            var itworldFailedId = Guid.NewGuid();
            var usenetAcknowledgedId = Guid.NewGuid();
            var itworldAcknowledgedId = Guid.NewGuid();
            db.SyncOutboxEntries.AddRange(
                CreateOutboxEntry(
                    "Failed",
                    entryId: usenetFailedId,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Failed",
                    entryId: itworldFailedId,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld),
                CreateOutboxEntry(
                    "Acknowledged",
                    entryId: usenetAcknowledgedId,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Acknowledged",
                    entryId: itworldAcknowledgedId,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld));
            await db.SaveChangesAsync();

            var scopedEntries = await local.GetSyncOutboxEntriesAsync(usenetSession, 20);
            Assert.Equal(2, scopedEntries.Count);
            Assert.All(scopedEntries, entry => Assert.Equal(OfficeCodeCatalog.Usenet, entry.ResponsibleOfficeCode));

            var scopedSummary = await local.GetSyncOutboxSummaryAsync(usenetSession);
            Assert.Equal(2, scopedSummary.TotalCount);
            Assert.Equal(1, scopedSummary.FailedCount);
            Assert.Equal(1, scopedSummary.AcknowledgedCount);

            Assert.Equal(0, await local.ResetSyncOutboxEntriesForRetryAsync([itworldFailedId], usenetSession));
            Assert.Equal("Failed", await ReadOutboxStatusAsync(db, itworldFailedId));

            Assert.Equal(1, await local.ResetAllPendingSyncOutboxEntriesForRetryAsync(usenetSession));
            Assert.Equal("Prepared", await ReadOutboxStatusAsync(db, usenetFailedId));
            Assert.Equal("Failed", await ReadOutboxStatusAsync(db, itworldFailedId));

            Assert.Equal(1, await local.ClearAcknowledgedSyncOutboxEntriesAsync(usenetSession));
            Assert.False(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == usenetAcknowledgedId));
            Assert.True(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == itworldAcknowledgedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalSyncOutboxEntry CreateOutboxEntry(
        string status,
        string entityName = nameof(LocalCustomer),
        Guid? entityId = null,
        Guid? entryId = null,
        string tenantCode = TenantScopeCatalog.UsenetGroup,
        string officeCode = OfficeCodeCatalog.Usenet,
        string responsibleOfficeCode = OfficeCodeCatalog.Usenet)
        => new()
        {
            Id = entryId ?? Guid.NewGuid(),
            MutationId = $"test-device:{entityName}:{(entityId ?? Guid.NewGuid()):N}:1:{DateTime.UtcNow.Ticks}:0",
            DeviceId = "test-device",
            EntityName = entityName,
            EntityId = entityId ?? Guid.NewGuid(),
            ExpectedRevision = 1,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            Status = status,
            PreparedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            SentAtUtc = DateTime.UtcNow
        };

    private static Task<string> ReadOutboxStatusAsync(LocalDbContext db)
        => db.SyncOutboxEntries
            .AsNoTracking()
            .Select(entry => entry.Status)
            .SingleAsync();

    private static Task<string> ReadOutboxStatusAsync(LocalDbContext db, Guid entryId)
        => db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.Id == entryId)
            .Select(entry => entry.Status)
            .SingleAsync();

    private static async Task<int> InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<TLocal, TDto>(
        SyncService sync,
        IReadOnlyCollection<TDto> serverEntities)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        var method = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "MarkOutboxAcknowledgedForCleanEntitiesAsync" &&
                info.IsGenericMethodDefinition &&
                info.GetGenericArguments().Length == 2);
        var generic = method.MakeGenericMethod(typeof(TLocal), typeof(TDto));
        var task = (Task<int>)generic.Invoke(sync, [serverEntities, CancellationToken.None])!;
        return await task;
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "outbox-admin",
                Role = "Admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
        return session;
    }

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }

    private static SyncService CreateSyncService(LocalDbContext db, SessionState session)
    {
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
        return new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
