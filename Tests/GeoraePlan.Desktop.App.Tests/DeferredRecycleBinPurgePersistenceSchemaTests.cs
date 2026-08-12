using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DeferredRecycleBinPurgePersistenceSchemaTests
{
    private const string ScopeIndexName = "IX_DeferredRecycleBinPurgeRecords_Scope_Entity";
    private const string RetryIndexName = "IX_DeferredRecycleBinPurgeRecords_AppliedAtUtc_NextAttemptAtUtc";

    [Fact]
    public async Task InitializeAsync_CreatesUpgradesAndVerifiesDeferredPurgeSchemaIdempotently()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-deferred-purge-schema-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);

            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"DeferredRecycleBinPurgeRecords\";");
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "DeferredRecycleBinPurgeRecords" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_DeferredRecycleBinPurgeRecords" PRIMARY KEY
                );
                """);
            var legacyReceiptId = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"DeferredRecycleBinPurgeRecords\" (\"Id\") VALUES ({0});",
                legacyReceiptId);
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);

            var upgradedLegacyRecord = await db.DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .SingleAsync(record => record.Id == legacyReceiptId);
            Assert.Null(upgradedLegacyRecord.LastAttemptedAtUtc);
            Assert.Null(upgradedLegacyRecord.NextAttemptAtUtc);
            Assert.Null(upgradedLegacyRecord.AppliedAtUtc);
            var columns = await GetTableColumnsAsync(db, "DeferredRecycleBinPurgeRecords");
            Assert.Equal(
                new[]
                {
                    "Id",
                    "BusinessDatabaseName",
                    "TenantCode",
                    "OfficeCode",
                    "ResponsibleOfficeCode",
                    "Kind",
                    "EntityId",
                    "Revision",
                    "PurgedAtUtc",
                    "AttemptCount",
                    "LastAttemptedAtUtc",
                    "LastErrorMessage",
                    "NextAttemptAtUtc",
                    "AppliedAtUtc",
                    "CreatedAtUtc",
                    "UpdatedAtUtc"
                },
                columns);
            Assert.Equal(
                new[]
                {
                    "BusinessDatabaseName",
                    "TenantCode",
                    "OfficeCode",
                    "ResponsibleOfficeCode",
                    "Kind",
                    "EntityId"
                },
                await GetIndexColumnsAsync(db, ScopeIndexName));
            Assert.Equal(
                new[] { "AppliedAtUtc", "NextAttemptAtUtc" },
                await GetIndexColumnsAsync(db, RetryIndexName));

            var receiptId = Guid.NewGuid();
            db.DeferredRecycleBinPurgeRecords.Add(
                new LocalDeferredRecycleBinPurgeRecord
                {
                    Id = receiptId,
                    BusinessDatabaseName = "business-db",
                    TenantCode = "tenant",
                    OfficeCode = "office",
                    ResponsibleOfficeCode = "responsible-office",
                    Kind = "invoice",
                    EntityId = Guid.NewGuid(),
                    Revision = 42,
                    PurgedAtUtc = DateTime.UtcNow,
                    AttemptCount = 1,
                    LastAttemptedAtUtc = DateTime.UtcNow,
                    LastErrorMessage = "retry",
                    NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1),
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync($"DROP INDEX \"{ScopeIndexName}\";");
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);
            await LocalDbInitializer.InitializeAsync(db);

            Assert.Equal(
                receiptId,
                await db.DeferredRecycleBinPurgeRecords
                    .AsNoTracking()
                    .Where(record => record.Id == receiptId)
                    .Select(record => record.Id)
                    .SingleAsync());
            Assert.Equal(
                new[]
                {
                    "BusinessDatabaseName",
                    "TenantCode",
                    "OfficeCode",
                    "ResponsibleOfficeCode",
                    "Kind",
                    "EntityId"
                },
                await GetIndexColumnsAsync(db, ScopeIndexName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static LocalDbContext CreateDbContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new LocalDbContext(options);
    }

    private static async Task<IReadOnlyList<string>> GetTableColumnsAsync(
        LocalDbContext db,
        string tableName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
                columns.Add(reader["name"]?.ToString() ?? string.Empty);
            return columns;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task<IReadOnlyList<string>> GetIndexColumnsAsync(
        LocalDbContext db,
        string indexName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA index_info(\"{indexName}\");";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
                columns.Add(reader["name"]?.ToString() ?? string.Empty);
            return columns;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }
}
