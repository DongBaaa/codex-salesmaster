using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncConflictActorGuardTests
{
    [Fact]
    public async Task PrepareCustomerRevisionRetry_DoesNotRequeue_WhenServerActorIsDifferentUser()
    {
        PrepareAppRoot("georaeplan-sync-conflict-actor-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var currentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var otherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var session = CreateSession(currentUserId, "admin");
            var customerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var localRevision = 10L;
            var serverRevision = 12L;
            var updatedAtUtc = new DateTime(2026, 6, 22, 9, 30, 0, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-CONFLICT-ACTOR";

            var customer = new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Local newer customer",
                NameMatchKey = "LOCALNEWERCUSTOMER",
                TradeType = CustomerTradeTypes.Sales,
                CreatedAtUtc = updatedAtUtc.AddDays(-10),
                UpdatedAtUtc = updatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.Customers.Add(customer);

            var clientSnapshot = LocalMappings.ToDto(customer);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = BuildMutationId(deviceId, nameof(LocalCustomer), clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalCustomer),
                EntityId = customerId,
                ExpectedRevision = localRevision,
                TenantCode = customer.TenantCode,
                OfficeCode = customer.OfficeCode,
                ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(customer);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-3);
            serverSnapshot.NameOriginal = "Other user server customer";
            serverSnapshot.NameMatchKey = "OTHERUSERSERVERCUSTOMER";

            var conflict = new ConflictLogDto
            {
                EntityName = "Customer",
                EntityId = customerId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot),
                ServerUserId = otherUserId,
                ServerUsername = "other-user"
            };

            using var sync = CreateSyncService(db, session);
            var prepared = await InvokeTryPrepareCustomerRevisionRetryAsync(sync, conflict, deviceId, session);

            Assert.False(prepared);

            var storedCustomer = await db.Customers.AsNoTracking().SingleAsync(current => current.Id == customerId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalCustomer) && entry.EntityId == customerId);
            Assert.Equal(localRevision, storedCustomer.Revision);
            Assert.True(storedCustomer.IsDirty);
            Assert.Equal("Sent", outboxRow.Status);
            Assert.Equal(localRevision, outboxRow.ExpectedRevision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<bool> InvokeTryPrepareCustomerRevisionRetryAsync(
        SyncService sync,
        ConflictLogDto conflict,
        string deviceId,
        SessionState session)
    {
        var method = typeof(SyncService).GetMethod(
            "TryPrepareCustomerRevisionRetryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<bool>)method!.Invoke(
            sync,
            [conflict, deviceId, session, CancellationToken.None])!;
        return await task;
    }

    private static string BuildMutationId(string deviceId, string entityName, SyncEntityDto entity)
    {
        var method = typeof(SyncService).GetMethod(
            "BuildMutationId",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [deviceId, entityName, entity])!;
    }

    private static SessionState CreateSession(Guid userId, string username)
    {
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                UserId = userId,
                Username = username,
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
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
