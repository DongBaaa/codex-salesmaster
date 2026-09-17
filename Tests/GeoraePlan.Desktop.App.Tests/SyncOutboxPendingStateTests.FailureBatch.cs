using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(256, 0)]
    [InlineData(256, 3)]
    public async Task FailedPush_UpdatesOnlyUnacknowledgedReceipts(int acknowledgedCount, int pendingCount)
    {
        await VerifyFailureBatchAsync(acknowledgedCount, pendingCount, raceField: null);
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("MutationId")]
    [InlineData("EntityId")]
    [InlineData("ExpectedRevision")]
    [InlineData("DeviceId")]
    [InlineData("BusinessDatabaseName")]
    [InlineData("TenantCode")]
    [InlineData("OfficeCode")]
    [InlineData("ResponsibleOfficeCode")]
    [InlineData("SessionId")]
    [InlineData("UserId")]
    [InlineData("PreparedAtUtc")]
    public async Task FailedPush_ConcurrentReceiptChangeBeforeUpdateRemainsUntouched(string raceField)
    {
        await VerifyFailureBatchAsync(0, 1, raceField);
    }

    private static async Task VerifyFailureBatchAsync(int acknowledgedCount, int pendingCount, string? raceField)
    {
        PrepareAppRoot($"sync-failure-batch-{acknowledgedCount}-{pendingCount}-{raceField}");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new FailureUpdateCounter();
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection).AddInterceptors(commands).Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var now = DateTime.UtcNow;
            var units = Enumerable.Range(0, acknowledgedCount + pendingCount).Select(i => new LocalUnit
            {
                Id = Guid.NewGuid(), Name = $"batch-unit-{i}", Revision = 7,
                IsActive = true, IsDirty = true, CreatedAtUtc = now.AddHours(-1), UpdatedAtUtc = now
            }).ToList();
            db.Units.AddRange(units);
            await db.SaveChangesAsync();
            var request = new SyncPushRequest
            {
                DeviceId = "failure-batch-device",
                Units = units.Select(unit => new UnitDto
                {
                    Id = unit.Id, Name = unit.Name, IsActive = unit.IsActive,
                    Revision = unit.Revision, ExpectedRevision = unit.Revision,
                    CreatedAtUtc = unit.CreatedAtUtc, UpdatedAtUtc = unit.UpdatedAtUtc
                }).ToList()
            };
            InvokeStampOutgoingMutations(request, request.DeviceId, session.SelectedBusinessDatabaseName);
            using var sync = CreateSyncService(db, session);
            await InvokeRecordPreparedMutationsAsync(sync, request, session);
            var snapshots = typeof(SyncService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(m => m.Name == "BuildPreparedMutationSnapshots" && m.GetParameters().Length == 2)
                .Invoke(sync, [request, null]);
            var receiptTask = (Task)typeof(SyncService)
                .GetMethod("CaptureCurrentPushMutationReceiptsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(sync, [request, snapshots, session, null, null, CancellationToken.None])!;
            await receiptTask;
            var receipts = receiptTask.GetType().GetProperty("Result")!.GetValue(receiptTask)!;
            Assert.Equal(units.Count, ((System.Collections.IEnumerable)receipts).Cast<object>().Count());
            var acknowledgedIds = units.Take(acknowledgedCount).Select(u => u.Id).ToList();
            await db.SyncOutboxEntries.Where(e => acknowledgedIds.Contains(e.EntityId))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "Acknowledged")
                    .SetProperty(e => e.ErrorMessage, "prior acknowledgement")
                    .SetProperty(e => e.AcceptedRevision, 88));
            var acknowledgedBefore = JsonSerializer.Serialize(await db.SyncOutboxEntries.AsNoTracking()
                .Where(e => e.Status == "Acknowledged").OrderBy(e => e.Id).ToListAsync());
            var unitsBefore = JsonSerializer.Serialize(await db.Units.AsNoTracking().OrderBy(e => e.Id).ToListAsync());
            var pending = await db.SyncOutboxEntries.AsNoTracking().Where(e => e.Status != "Acknowledged").ToListAsync();
            if (raceField is not null)
            {
                var row = Assert.Single(pending);
                // Another writer wins after candidate selection but immediately before the guarded UPDATE.
                commands.BeforeUpdate = () =>
                {
                    using var change = connection.CreateCommand();
                    change.CommandText = $"UPDATE SyncOutboxEntries SET \"{raceField}\" = $value, ErrorMessage = 'concurrent owner' WHERE Id = $id";
                    object value = raceField switch
                    {
                        "Status" => "Acknowledged",
                        "ExpectedRevision" => 99L,
                        "EntityId" or "SessionId" or "UserId" => Guid.NewGuid().ToString().ToUpperInvariant(),
                        "PreparedAtUtc" => "2030-01-01 00:00:00",
                        _ => "changed-owner"
                    };
                    change.Parameters.AddWithValue("$value", value);
                    change.Parameters.AddWithValue("$id", row.Id.ToString().ToUpperInvariant());
                    Assert.Equal(1, change.ExecuteNonQuery());
                };
            }
            commands.Enabled = true;
            await (Task)typeof(SyncService).GetMethod("TryMarkOutboxFailedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(sync, [request, "permission denied", null, receipts, CancellationToken.None])!;
            commands.Enabled = false;
            var updateCount = commands.UpdateCount;
            Assert.Equal(unitsBefore, JsonSerializer.Serialize(await db.Units.AsNoTracking().OrderBy(e => e.Id).ToListAsync()));
            if (raceField is null)
            {
                Assert.Equal(acknowledgedBefore, JsonSerializer.Serialize(await db.SyncOutboxEntries.AsNoTracking()
                    .Where(e => e.Status == "Acknowledged").OrderBy(e => e.Id).ToListAsync()));
                var failures = await db.SyncOutboxEntries.AsNoTracking().Where(e => e.Status != "Acknowledged").ToListAsync();
                Assert.Equal(pendingCount, failures.Count);
                Assert.All(failures, row => { Assert.Equal("Failed", row.Status); Assert.Equal("permission denied", row.ErrorMessage); });
            }
            else
            {
                Assert.Null(commands.BeforeUpdate);
                var row = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
                Assert.Equal("concurrent owner", row.ErrorMessage);
                Assert.Equal(raceField == "Status" ? "Acknowledged" : pending.Single().Status, row.Status);
            }
            Console.WriteLine($"failure_batch acknowledged={acknowledgedCount} pending={pendingCount} updates={updateCount} race={raceField ?? "none"}");
            Assert.Equal(pendingCount, updateCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class FailureUpdateCounter : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int UpdateCount { get; private set; }
        public Action? BeforeUpdate { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("SyncOutboxEntries", StringComparison.Ordinal))
            {
                UpdateCount++;
                var before = BeforeUpdate;
                BeforeUpdate = null;
                before?.Invoke();
            }
            return ValueTask.FromResult(result);
        }
    }
}
