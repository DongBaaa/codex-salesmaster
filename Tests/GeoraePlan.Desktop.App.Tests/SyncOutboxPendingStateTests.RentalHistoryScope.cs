using System.Reflection;
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
    [InlineData("ITWORLD", "ITWORLD", "ITWORLD", false)]
    [InlineData("ITWORLD", "ITWORLD", "ITWORLD", true)]
    [InlineData("USENET_GROUP", "USENET", "USENET", false)]
    [InlineData("USENET_GROUP", "USENET", "USENET", true)]
    [InlineData("USENET_GROUP", "USENET", "YEONSU", false)]
    [InlineData("USENET_GROUP", "USENET", "YEONSU", true)]
    public async Task PullRentalHistory_PreservesContractScopeDespiteUnmappedLegacyOfficeColumn(
        string tenant, string office, string responsible, bool existing)
    {
        PrepareAppRoot("rental-history-office-pull");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            Assert.Null(db.Model.FindEntityType(typeof(LocalRentalAssetAssignmentHistory))!.FindProperty("OfficeCode"));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE RentalAssetAssignmentHistories ADD COLUMN OfficeCode TEXT NOT NULL DEFAULT 'SHARED'");
            var id = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var start = new DateTime(2025, 8, 8, 0, 0, 0, DateTimeKind.Utc);
            if (existing)
            {
                db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
                {
                    Id = id, AssetId = assetId, TenantCode = tenant,
                    ResponsibleOfficeCode = responsible, LinkedAtUtc = start, Revision = 1, IsDirty = false
                });
                await db.SaveChangesAsync();
            }
            var incoming = new RentalAssetAssignmentHistoryDto
            {
                Id = id, AssetId = assetId, TenantCode = tenant, OfficeCode = office,
                ResponsibleOfficeCode = responsible, LinkedAtUtc = start, MonthlyFee = 55000,
                IsCurrent = true, Revision = 2, CreatedAtUtc = start, UpdatedAtUtc = start.AddDays(1)
            };
            using var sync = CreateSyncService(db, CreateAdminSession());
            var upsert = typeof(SyncService).GetMethod("UpsertPulledRentalAssetAssignmentHistoriesAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)upsert.Invoke(sync, [new[] { incoming }, CancellationToken.None])!;
            await (Task)upsert.Invoke(sync, [new[] { incoming }, CancellationToken.None])!;
            db.ChangeTracker.Clear();
            var actual = await db.RentalAssetAssignmentHistories.AsNoTracking().SingleAsync();
            Assert.Equal(tenant, actual.TenantCode);
            Assert.Equal(responsible, actual.ResponsibleOfficeCode);
            var outgoing = LocalMappings.ToDto(actual);
            Assert.Equal(office, outgoing.OfficeCode);
            Assert.Equal(tenant, outgoing.TenantCode);
            Assert.Equal(responsible, outgoing.ResponsibleOfficeCode);
            Assert.Equal(assetId, actual.AssetId);
            Assert.Equal(start, actual.LinkedAtUtc);
            Assert.Equal(55000m, actual.MonthlyFee);
            Assert.Equal(2, actual.Revision);
            Assert.False(actual.IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
