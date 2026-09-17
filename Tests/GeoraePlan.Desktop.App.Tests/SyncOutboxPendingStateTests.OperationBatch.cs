using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("sent", 16, "none")]
    [InlineData("sent", 256, "none")]
    [InlineData("sent", 256, "owner")]
    [InlineData("sent", 256, "transaction")]
    [InlineData("ack", 16, "none")]
    [InlineData("ack", 256, "none")]
    [InlineData("ack", 256, "owner")]
    [InlineData("ack", 256, "transaction")]
    public async Task OutboxOperation_BatchBoundsFullScansAndPreservesUnsavedBusinessEdits(string stage, int count, string ownership)
    {
        await VerifyOutboxOperationAsync(stage, count, ownership, stale: false);
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("ack")]
    public async Task OutboxOperation_StaleEpochCannotMutateReceipts(string stage)
    {
        await VerifyOutboxOperationAsync(stage, 16, "none", stale: true);
    }

    private static async Task VerifyOutboxOperationAsync(string stage, int count, string ownership, bool stale)
    {
        PrepareAppRoot($"outbox-operation-{stage}-{count}-{ownership}-{stale}");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!, "operation.db")
            }.ToString();
            var detections = 0;
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connectionString)
                .LogTo(_ => detections++, [CoreEventId.DetectChangesStarting], LogLevel.Debug).Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var now = DateTime.UtcNow;
            var units = Enumerable.Range(0, count).Select(i => new LocalUnit
            {
                Id = Guid.NewGuid(), Name = $"original-{i}", Revision = 7, IsDirty = true,
                IsActive = true, CreatedAtUtc = now.AddHours(-1), UpdatedAtUtc = now
            }).ToList();
            db.Units.AddRange(units);
            await db.SaveChangesAsync();
            var request = new SyncPushRequest
            {
                DeviceId = "operation-test-device",
                Units = units.Select(u => new UnitDto
                {
                    Id = u.Id, Name = u.Name, Revision = u.Revision, ExpectedRevision = u.Revision,
                    IsActive = u.IsActive, CreatedAtUtc = u.CreatedAtUtc, UpdatedAtUtc = u.UpdatedAtUtc
                }).ToList()
            };
            InvokeStampOutgoingMutations(request, request.DeviceId, session.SelectedBusinessDatabaseName);
            using var sync = CreateSyncService(db, session);
            await InvokeRecordPreparedMutationsAsync(sync, request, session);
            var snapshots = typeof(SyncService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(m => m.Name == "BuildPreparedMutationSnapshots" && m.GetParameters().Length == 2)
                .Invoke(sync, [request, null]);
            var capture = (Task)typeof(SyncService).GetMethod("CaptureCurrentPushMutationReceiptsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(sync, [request, snapshots, session, null, null, CancellationToken.None])!;
            await capture;
            var receipts = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
            Assert.Equal(count, ((System.Collections.IEnumerable)receipts).Cast<object>().Count());
            if (stage == "ack")
                await db.SyncOutboxEntries.ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "Sent"));
            var businessBefore = JsonSerializer.Serialize(await db.Units.AsNoTracking().OrderBy(u => u.Id).ToListAsync());
            var outboxBefore = JsonSerializer.Serialize(await db.SyncOutboxEntries.AsNoTracking().OrderBy(u => u.Id).ToListAsync());
            foreach (var unit in units) unit.Name += "-unsaved";
            var accepted = units.Select(u => new SyncAcceptedRevisionDto
            {
                EntityName = "Unit", EntityId = u.Id, Revision = 88, UpdatedAtUtc = now.AddMinutes(1)
            }).ToList();
            Task Execute() => stage == "sent"
                ? (Task)typeof(SyncService).GetMethod("MarkOutboxSentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(sync, [request, null, receipts, CancellationToken.None])!
                : (Task)typeof(SyncService).GetMethod("MarkOutboxAcknowledgedCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(sync, [request, accepted, null, session, null, receipts, CancellationToken.None])!;
            if (stale)
            {
                await using (var gate = await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None))
                await using (LocalDbContext.EnterRuntimeMutationGateOwnerScope(gate))
                {
                    await using var fresh = new LocalDbContext(options);
                    fresh.AdvanceRuntimeMutationEpoch();
                }
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(Execute);
                // Verify through a fresh context because the old context must stay stale.
                await using var verification = new LocalDbContext(options);
                Assert.Equal(outboxBefore, JsonSerializer.Serialize(await verification.SyncOutboxEntries.AsNoTracking().OrderBy(u => u.Id).ToListAsync()));
                Assert.Equal(businessBefore, JsonSerializer.Serialize(await verification.Units.AsNoTracking().OrderBy(u => u.Id).ToListAsync()));
                return;
            }
            var scans = 0;
            async Task Measure()
            {
                detections = 0;
                await Execute();
                scans = detections;
            }
            if (ownership == "owner")
            {
                await using var gate = await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None);
                await using var owner = LocalDbContext.EnterRuntimeMutationGateOwnerScope(gate);
                await Measure();
            }
            else if (ownership == "transaction")
            {
                await using var transaction = await db.BeginRuntimeMutationTransactionAsync(CancellationToken.None);
                await Measure();
                await transaction.CommitAsync();
            }
            else await Measure();
            Assert.Equal(businessBefore, JsonSerializer.Serialize(await db.Units.AsNoTracking().OrderBy(u => u.Id).ToListAsync()));
            var outbox = await db.SyncOutboxEntries.AsNoTracking().ToListAsync();
            Assert.Equal(count, outbox.Count);
            Assert.All(outbox, row =>
            {
                Assert.Equal(stage == "sent" ? "Sent" : "Acknowledged", row.Status);
                Assert.Equal(stage == "sent" ? 0 : 88, row.AcceptedRevision);
                Assert.Equal(session.SessionId, row.SessionId);
                Assert.Equal(session.User!.UserId, row.UserId);
            });
            Assert.All(units, unit =>
            {
                Assert.EndsWith("-unsaved", unit.Name);
                Assert.Equal(EntityState.Modified, db.Entry(unit).State);
                Assert.Equal(unit.Name[..^8], db.Entry(unit).Property(u => u.Name).OriginalValue);
                Assert.True(unit.IsDirty);
            });
            // A subsequent user save must still persist the preserved changes.
            await db.SaveChangesAsync();
            Assert.Equal(count, await db.Units.AsNoTracking().CountAsync(u => u.Name.EndsWith("-unsaved") && u.IsDirty));
            Console.WriteLine($"outbox_operation stage={stage} count={count} ownership={ownership} full_scans={scans}");
            Assert.InRange(scans, 0, 4);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
