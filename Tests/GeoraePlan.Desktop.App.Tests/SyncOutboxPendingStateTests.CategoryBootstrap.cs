using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Fact]
    public async Task CategoryBootstrap_NewDatabaseReceivesServerDefaultsWithoutPendingWrites()
    {
        PrepareAppRoot("category-bootstrap-new");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await LocalDbInitializer.InitializeAsync(db);
            db.ChangeTracker.Clear();
            var categories = await db.CustomerCategories.AsNoTracking().ToListAsync();
            Assert.Equal(4, categories.Count);
            Assert.All(categories, row => { Assert.False(row.IsDirty); Assert.True(row.IsSystemDefault); });

            var serverTime = DateTime.UtcNow.AddDays(-1);
            var serverRows = DefaultCustomerCategories.All.Select((definition, i) => new CustomerCategoryDto
            {
                Id = definition.Id, Name = definition.Name, IsSystemDefault = true,
                Revision = 100 + i, CreatedAtUtc = serverTime, UpdatedAtUtc = serverTime
            }).ToList();
            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeApplyPullAsync(sync, new SyncPullResponse { CustomerCategories = serverRows });
            db.ChangeTracker.Clear();
            foreach (var expected in serverRows)
            {
                var row = await db.CustomerCategories.AsNoTracking().SingleAsync(x => x.Id == expected.Id);
                Assert.Equal(expected.Revision, row.Revision);
                Assert.Equal(expected.UpdatedAtUtc, row.UpdatedAtUtc);
                Assert.False(row.IsDirty);
            }
            Assert.False(await db.SyncOutboxEntries.AnyAsync());
            await LocalDbInitializer.InitializeAsync(db);
            Assert.All(await db.CustomerCategories.AsNoTracking().ToListAsync(), row => Assert.False(row.IsDirty));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CategoryBootstrap_PreservesExistingFlagsEditsAndTombstones(bool dirty, bool deleted)
    {
        PrepareAppRoot("category-bootstrap-existing");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var stamp = new DateTime(2026, 9, 1, 1, 2, 3, DateTimeKind.Utc);
            db.CustomerCategories.AddRange(DefaultCustomerCategories.All.Select((definition, i) => new LocalCustomerCategory
            {
                Id = definition.Id, Name = "사용자 지정 " + definition.Name, IsSystemDefault = i % 2 == 0,
                IsDirty = dirty, IsDeleted = deleted, Revision = 42 + i,
                CreatedAtUtc = stamp, UpdatedAtUtc = stamp
            }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = JsonSerializer.Serialize(await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync());
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await LocalDbInitializer.InitializeAsync(db);
                db.ChangeTracker.Clear();
                Assert.Equal(before, JsonSerializer.Serialize(await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync()));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
