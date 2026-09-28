using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("Prepared", "same-owner")]
    [InlineData("Sent", "same-owner")]
    [InlineData("Failed", "same-owner")]
    [InlineData("Acknowledged", "same-owner")]
    [InlineData("Failed", "other-user")]
    [InlineData("Failed", "other-device")]
    [InlineData("Failed", "other-database")]
    [InlineData("Failed", "other-tenant")]
    [InlineData("Failed", "other-office")]
    [InlineData("Failed", "other-responsible-office")]
    [InlineData("Failed", "other-revision")]
    [InlineData("Failed", "empty-session")]
    [InlineData("Failed", "receipt-session-changed-during-push")]
    [InlineData("Failed", "login-changed-during-push")]
    public async Task PreparedPush_AfterRelogin_PreservesReceiptIdentityAndRequiresSameOwner(
        string status, string variant)
    {
        PrepareAppRoot($"georaeplan-relogin-receipt-{status}-{variant}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var originalSession = CreateAdminSession();
            var unit = new LocalUnit { Id = Guid.NewGuid(), Name = "Pending across restart",
                IsActive = true, IsDirty = true, Revision = 11,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10) };
            db.Units.Add(unit);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            // A restarted process prepares the persisted SQLite representation.
            unit = await db.Units.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            var request = new SyncPushRequest { DeviceId = "relogin-device",
                Units = [LocalMappings.ToDto(unit)] };
            InvokeStampOutgoingMutations(request, request.DeviceId, originalSession.SelectedBusinessDatabaseName);
            using (var firstSync = CreateSyncService(db, originalSession))
                await InvokeRecordPreparedMutationsAsync(firstSync, request, originalSession);
            var row = await db.SyncOutboxEntries.SingleAsync();
            row.Status = status;
            switch (variant)
            {
                case "other-user": row.UserId = Guid.NewGuid(); break;
                case "other-device": row.DeviceId = "other-device"; break;
                case "other-database": row.BusinessDatabaseName = "georaeplan_itworld"; break;
                case "other-tenant": row.TenantCode = TenantScopeCatalog.Itworld; break;
                case "other-office": row.OfficeCode = OfficeCodeCatalog.Yeonsu; break;
                case "other-responsible-office": row.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu; break;
                case "other-revision": row.ExpectedRevision++; break;
                case "empty-session": row.SessionId = Guid.Empty; break;
            }
            await db.SaveChangesAsync();
            var originalRowId = row.Id;
            var originalSessionId = row.SessionId;
            var originalPreparedAt = row.PreparedAtUtc;
            var originalMutationId = row.MutationId;
            var originalUserId = row.UserId;
            db.ChangeTracker.Clear();

            var newSession = new SessionState();
            newSession.SetSession("new-test-token", originalSession.User!, DateTime.UtcNow.AddDays(1));
            Assert.NotEqual(originalSession.SessionId, newSession.SessionId);
            // Exercise the real login registration before dispatch. Previously this
            // rewrote SessionId and bypassed the provenance assertions below.
            db.Settings.Add(new LocalSetting { Key = "Sync.DeviceId", Value = request.DeviceId });
            await db.SaveChangesAsync();
            var loginLocal = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), newSession);
            await loginLocal.RegisterLoginScopeAsync(newSession);

            var handler = new DelayedPushAckThenEmptyPullHandler(unit.Id, "Unit", 12,
                unit.UpdatedAtUtc.AddMinutes(1));
            var changesDuringPush = variant.EndsWith("during-push", StringComparison.Ordinal);
            if (!changesDuringPush)
                handler.ReleasePush();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var api = new ErpApiClient(http, newSession);
            using var retrySync = CreateSyncService(db, newSession);
            var retry = InvokePushPreparedRequestAsync(retrySync, api, newSession, request, null, null);
            if (changesDuringPush)
            {
                try
                {
                    await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    if (variant == "receipt-session-changed-during-push")
                    {
                        await using var concurrentDb = new LocalDbContext();
                        var concurrentRow = await concurrentDb.SyncOutboxEntries.SingleAsync();
                        concurrentRow.SessionId = Guid.NewGuid();
                        originalSessionId = concurrentRow.SessionId;
                        await concurrentDb.SaveChangesAsync();
                    }
                    else
                        newSession.SetSession("replacement-test-token", originalSession.User!, DateTime.UtcNow.AddDays(1));
                }
                finally { handler.ReleasePush(); }
            }
            var shouldAccept = variant == "same-owner" && status != "Acknowledged";
            if (shouldAccept)
                await retry;
            else
                Assert.Equal("SyncPullBlockedException", (await Assert.ThrowsAnyAsync<Exception>(() => retry)).GetType().Name);

            Assert.Equal(shouldAccept || changesDuringPush ? 1 : 0, handler.PushCount);
            db.ChangeTracker.Clear();
            var saved = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
            Assert.Equal(originalRowId, saved.Id);
            Assert.Equal(originalSessionId, saved.SessionId); // Keep original provenance immutable.
            Assert.Equal(originalPreparedAt, saved.PreparedAtUtc);
            Assert.Equal(originalMutationId, saved.MutationId); // Lost-response retry stays idempotent.
            Assert.Equal(originalUserId, saved.UserId);
            if (changesDuringPush)
                Assert.NotEqual("Acknowledged", saved.Status);
            else
                Assert.Equal(shouldAccept ? "Acknowledged" : status, saved.Status);
            var savedUnit = await db.Units.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.True(savedUnit.IsDirty == !shouldAccept,
                $"dirty={savedUnit.IsDirty}; revision={savedUnit.Revision}; expected={request.Units[0].ExpectedRevision}; updated={savedUnit.UpdatedAtUtc:O}; prepared={request.Units[0].UpdatedAtUtc:O}; currentHash={InvokeComputePreparedMutationPayloadHash("Unit", LocalMappings.ToDto(savedUnit))}; requestHash={InvokeComputePreparedMutationPayloadHash("Unit", request.Units[0])}");
            Assert.Equal(shouldAccept ? 12 : 11, savedUnit.Revision);
            if (shouldAccept)
                Assert.Equal(originalMutationId, Assert.Single((await handler.PushReceived.Task).Units).MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
