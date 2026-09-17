using System.Net;
using System.Net.Http.Json;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReadiness_RejectsMissingOrOfflineSessionBeforeDataAccess(bool offline)
    {
        var session = offline
            ? CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet)
            : new SessionState();
        var result = await UpdateReadinessService.EnsureReadyForUpdateAsync(null!, null!, session);
        Assert.False(result.CanProceed);
        Assert.False(result.SyncAttempted);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReadiness_BlocksUnsentDirtyDataButAllowsCleanDatabase(bool dirty)
    {
        PrepareAppRoot("georaeplan-update-readiness-dirty");
        try
        {
            await using var db = new LocalDbContext();
            // AppPaths is pinned for the test process; reset its isolated DB between cases.
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var unit = new LocalUnit
            {
                Id = Guid.NewGuid(), Name = "update guard preserved unit", Revision = 7,
                IsActive = true, IsDirty = dirty, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            };
            db.Units.Add(unit);
            await db.SaveChangesAsync();
            var handler = new ForbiddenPushThenEmptyPullHandler("synthetic server refusal");
            using var sync = CreateSyncService(db, session, handler);
            var result = await UpdateReadinessService.EnsureReadyForUpdateAsync(local, sync, session)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(!dirty, result.CanProceed);
            Assert.Equal(dirty, result.SyncAttempted);
            if (dirty)
            {
                Assert.True(handler.PushCount > 0);
                Assert.True(result.RemainingDirtyCount > 0);
            }
            else Assert.Equal(0, handler.PushCount);
            db.ChangeTracker.Clear();
            var stored = await db.Units.SingleAsync(x => x.Id == unit.Id);
            Assert.Equal(unit.Name, stored.Name);
            Assert.Equal(7, stored.Revision);
            Assert.Equal(dirty, stored.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("Sent")]
    [InlineData("Failed")]
    public async Task UpdateReadiness_BlocksOutboxWithoutDirtyRows(string status)
    {
        PrepareAppRoot("georaeplan-update-readiness-outbox");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var entry = CreateOutboxEntry(status);
            db.SyncOutboxEntries.Add(entry);
            await db.SaveChangesAsync();
            using var sync = CreateSyncService(db, session, new ForbiddenPushThenEmptyPullHandler("synthetic refusal"));
            Assert.Equal(0, await local.CountDirtyAsync());
            var result = await UpdateReadinessService.EnsureReadyForUpdateAsync(local, sync, session)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(result.CanProceed);
            Assert.True(result.SyncAttempted);
            Assert.Equal(0, result.RemainingDirtyCount);
            Assert.Equal(status == "Failed" ? 0 : 1, result.RemainingPendingOutboxCount);
            Assert.Equal(status == "Failed" ? 1 : 0, result.RemainingFailedOutboxCount);
            if (status == "Failed") Assert.Contains("실패 1건", result.Message);
            var stored = await db.SyncOutboxEntries.AsNoTracking().SingleAsync(x => x.Id == entry.Id);
            Assert.NotEqual("Acknowledged", stored.Status);
            Assert.Equal(entry.MutationId, stored.MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReadiness_PreservesDirtyAndFailedOutboxDuringTransportFailure(bool cancelDuringPush)
    {
        PrepareAppRoot("georaeplan-update-readiness-transport-failure");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var unit = new LocalUnit
            {
                Id = Guid.NewGuid(), Name = "transport failure preserved unit", Revision = 7,
                IsActive = true, IsDirty = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            };
            var failed = CreateOutboxEntry("Failed");
            db.Units.Add(unit);
            db.SyncOutboxEntries.Add(failed);
            await db.SaveChangesAsync();
            using var cancellation = new CancellationTokenSource();
            var handler = new UpdateReadinessTransportFailureHandler(cancelDuringPush, cancellation);
            using var sync = CreateSyncService(db, session, handler);
            var result = await UpdateReadinessService.EnsureReadyForUpdateAsync(local, sync, session, cancellation.Token)
                // API retries (1 + 2 seconds) inside sync retries (2 + 4 seconds)
                // can consume 15 seconds before the final failure is returned.
                .WaitAsync(TimeSpan.FromSeconds(45));
            Assert.True(handler.PushObserved);
            Assert.Equal(cancelDuringPush ? 1 : 9, handler.PushCount);
            Assert.False(result.CanProceed);
            Assert.True(result.SyncAttempted);
            Assert.True(result.RemainingDirtyCount > 0);
            Assert.True(result.RemainingFailedOutboxCount > 0);
            Assert.Equal(cancelDuringPush, cancellation.IsCancellationRequested);
            db.ChangeTracker.Clear();
            var storedUnit = await db.Units.AsNoTracking().SingleAsync(x => x.Id == unit.Id);
            Assert.Equal("transport failure preserved unit", storedUnit.Name);
            Assert.Equal(7, storedUnit.Revision);
            Assert.True(storedUnit.IsDirty);
            var storedFailure = await db.SyncOutboxEntries.AsNoTracking().SingleAsync(x => x.Id == failed.Id);
            Assert.Equal("Failed", storedFailure.Status);
            Assert.Equal(failed.MutationId, storedFailure.MutationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class UpdateReadinessTransportFailureHandler(
        bool cancelDuringPush, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public bool PushObserved { get; private set; }
        public int PushCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/sync/push")
            {
                PushObserved = true;
                PushCount++;
                if (cancelDuringPush)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new HttpRequestException("synthetic connection failure");
            }
            if (request.RequestUri?.AbsolutePath == "/sync/pull")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse())
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
