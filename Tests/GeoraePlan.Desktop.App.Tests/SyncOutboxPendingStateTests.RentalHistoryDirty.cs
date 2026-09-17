using System.Reflection;
using System.Text.Json;
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
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task PullRentalHistory_PreservesPendingEditAndReceiptRegardlessOfServerRevision(
        long serverRevision, bool localDeleted)
    {
        PrepareAppRoot("rental-history-pending-pull");
        try
        {
            // Keep this fixture independent of AppPaths' process-wide cache and
            // of other sync tests that intentionally retain their database.
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var time = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var history = new LocalRentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), CustomerId = Guid.NewGuid(),
                BillingProfileId = Guid.NewGuid(), TenantCode = "USENET_GROUP",
                ResponsibleOfficeCode = "USENET", MonthlyFee = 176000,
                LinkedAtUtc = time, ChangeReason = "사용자 수정 이력", IsCurrent = false,
                UnlinkedAtUtc = time.AddDays(3), Revision = 10, IsDirty = true,
                IsDeleted = localDeleted, CreatedAtUtc = time, UpdatedAtUtc = time.AddDays(4)
            };
            db.RentalAssetAssignmentHistories.Add(history);
            var receipt = new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(), EntityId = history.Id,
                EntityName = nameof(LocalRentalAssetAssignmentHistory), MutationId = "pending-history-edit",
                DeviceId = "test-device", UserId = Guid.NewGuid(), SessionId = Guid.NewGuid(),
                TenantCode = "USENET_GROUP", OfficeCode = "USENET", ResponsibleOfficeCode = "USENET",
                BusinessDatabaseName = "USENET", ExpectedRevision = 10,
                Status = "Prepared", PreparedAtUtc = time.AddDays(4)
            };
            db.SyncOutboxEntries.Add(receipt);
            await db.SaveChangesAsync();
            var before = JsonSerializer.Serialize(LocalMappings.ToDto(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking().SingleAsync()));
            var receiptBefore = JsonSerializer.Serialize(await db.SyncOutboxEntries.AsNoTracking().SingleAsync());
            var incoming = LocalMappings.ToDto(history);
            incoming.Revision = serverRevision;
            incoming.ChangeReason = "서버 이력";
            incoming.MonthlyFee = 120000;
            incoming.IsCurrent = true;
            incoming.UnlinkedAtUtc = null;
            incoming.IsDeleted = false;
            incoming.UpdatedAtUtc = time.AddDays(5);
            using var sync = CreateSyncService(db, CreateAdminSession());
            var upsert = typeof(SyncService).GetMethod("UpsertPulledRentalAssetAssignmentHistoriesAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)upsert.Invoke(sync, [new[] { incoming }, CancellationToken.None])!;
            db.ChangeTracker.Clear();
            var actual = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.True(actual.IsDirty);
            Assert.Equal(before, JsonSerializer.Serialize(LocalMappings.ToDto(actual)));
            Assert.Equal(receiptBefore, JsonSerializer.Serialize(await db.SyncOutboxEntries.AsNoTracking().SingleAsync()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
