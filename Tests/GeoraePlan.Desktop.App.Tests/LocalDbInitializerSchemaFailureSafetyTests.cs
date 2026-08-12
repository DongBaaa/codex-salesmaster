using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using 거래플랜.Desktop.App.Data;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalDbInitializerSchemaFailureSafetyTests
{
    private const string SchemaMaintenanceVersionKey = "LocalDb.SchemaMaintenanceVersion";
    private const string SchemaMaintenanceVersion = "2026-08-01.2";
    private static readonly string[] DeferredRequiredIndexNames =
    [
        "IX_RentalAssets_AssetKey",
        "IX_RentalAssets_ManagementId",
        "IX_RentalAssets_ManagementNumber",
        "IX_Transactions_ResponsibleOfficeCode",
        "IX_TransactionAttachments_TransactionStatus",
        "IX_InventoryTransfers_TransferStatus",
        "IX_InventoryTransferLines_TransferItem",
        "IX_RentalAssetAssignmentHistories_Revision"
    ];

    [Fact]
    public async Task InitializeAsync_MalformedRequiredTable_InvalidatesMarkerAndRollsBackPartialRepairs()
    {
        var tempRoot = CreateTempRoot("fail-closed");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await ReplaceSyncOutboxWithMalformedTableAsync(db);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => LocalDbInitializer.InitializeAsync(db));

            Assert.False(await HasCurrentSchemaMarkerAsync(db));
            var columns = await GetTableColumnsAsync(db, "SyncOutboxEntries");
            Assert.Equal(new[] { "Id" }, columns);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_LegacyTombstoneConflictCopyFailure_RollsBackPrimaryKeyRebuildAndCanRetry()
    {
        var tempRoot = CreateTempRoot(
            "inventory-transfer-tombstone-conflict-pk-rollback");
        var dbPath = Path.Combine(tempRoot, "local.db");
        var validTransferId = Guid.Parse(
            "00000000-0000-0000-0000-000000000001");
        var malformedTransferId = Guid.Parse(
            "00000000-0000-0000-0000-000000000002");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await ReplaceInventoryTransferTombstoneConflictsWithMalformedLegacyTableAsync(
                db,
                validTransferId,
                malformedTransferId);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => LocalDbInitializer.InitializeAsync(db));

            Assert.False(await HasCurrentSchemaMarkerAsync(db));
            Assert.Equal(
                new[] { "TransferId" },
                await GetPrimaryKeyColumnsAsync(
                    db,
                    "InventoryTransferTombstoneConflicts"));
            Assert.False(await HasTableAsync(
                db,
                "InventoryTransferTombstoneConflicts_LegacyPrimaryKey"));
            Assert.Equal(
                new[]
                {
                    "TransferId",
                    "TenantCode",
                    "SourceOfficeCode",
                    "TargetOfficeCode",
                    "LocalSnapshotJson",
                    "ServerTombstoneJson",
                    "OutboxMutationIdsJson",
                    "LocalRevision",
                    "ServerRevision",
                    "ServerUpdatedAtUtc",
                    "Status",
                    "DetectedAtUtc",
                    "UpdatedAtUtc",
                    "ResolvedAtUtc",
                    "Resolution"
                },
                await GetTableColumnsAsync(
                    db,
                    "InventoryTransferTombstoneConflicts"));
            Assert.Equal(
                2,
                await GetLegacyInventoryTransferTombstoneConflictRowCountAsync(
                    db));
            Assert.Equal(
                "valid-local-snapshot",
                await GetLegacyLocalSnapshotAsync(db, validTransferId));
            Assert.Null(await GetLegacyLocalSnapshotAsync(
                db,
                malformedTransferId));

            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "InventoryTransferTombstoneConflicts"
                SET "LocalSnapshotJson" = 'repaired-local-snapshot'
                WHERE "TransferId" = {0};
                """,
                malformedTransferId.ToString("D").ToUpperInvariant());
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);
            await LocalDbInitializer.InitializeAsync(db);

            Assert.True(await HasCurrentSchemaMarkerAsync(db));
            Assert.Equal(
                new[] { "BusinessDatabaseName", "TransferId" },
                await GetPrimaryKeyColumnsAsync(
                    db,
                    "InventoryTransferTombstoneConflicts"));
            Assert.False(await HasTableAsync(
                db,
                "InventoryTransferTombstoneConflicts_LegacyPrimaryKey"));
            var recovered = await db.InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .SingleAsync(conflict =>
                    conflict.TransferId == malformedTransferId &&
                    conflict.BusinessDatabaseName == "USENET");
            Assert.Equal("repaired-local-snapshot", recovered.LocalSnapshotJson);
            Assert.Equal("server-snapshot", recovered.ServerTombstoneJson);
            Assert.Equal(4, recovered.LocalRevision);
            Assert.Equal(9, recovered.ServerRevision);
            var valid = await db.InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .SingleAsync(conflict =>
                    conflict.TransferId == validTransferId &&
                    conflict.BusinessDatabaseName == "USENET");
            Assert.Equal("valid-local-snapshot", valid.LocalSnapshotJson);
            Assert.Equal("valid-server-snapshot", valid.ServerTombstoneJson);
            Assert.Equal(2, await db.InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .CountAsync());
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_AfterFailedSchemaIsRepaired_RecordsOneMarkerAndRemainsIdempotent()
    {
        var tempRoot = CreateTempRoot("failure-recovery");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await ReplaceSyncOutboxWithMalformedTableAsync(db);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => LocalDbInitializer.InitializeAsync(db));

            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"SyncOutboxEntries\";");
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);
            await LocalDbInitializer.InitializeAsync(db);

            Assert.True(await HasCurrentSchemaMarkerAsync(db));
            Assert.Equal(
                1,
                await db.Settings
                    .IgnoreQueryFilters()
                    .CountAsync(setting =>
                        setting.Key == SchemaMaintenanceVersionKey &&
                        setting.Value == SchemaMaintenanceVersion));

            var columns = await GetTableColumnsAsync(db, "SyncOutboxEntries");
            Assert.Contains("MutationId", columns);
            Assert.Contains("TenantCode", columns);
            Assert.Contains("OfficeCode", columns);
            Assert.Contains("ResponsibleOfficeCode", columns);
            Assert.Contains("BusinessDatabaseName", columns);
            Assert.Contains("SessionId", columns);
            Assert.Contains("UserId", columns);
            Assert.Contains("AcceptedRevision", columns);
            Assert.Contains("AcceptedUpdatedAtUtc", columns);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_ExistingTablesWithMissingDeferredIndexes_RecreatesEveryRequiredIndex()
    {
        var tempRoot = CreateTempRoot("deferred-index-repair");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);

            foreach (var indexName in DeferredRequiredIndexNames)
                await DropIndexAsync(db, indexName);
            await DeleteSchemaMarkerAsync(db);

            await LocalDbInitializer.InitializeAsync(db);

            Assert.True(await HasCurrentSchemaMarkerAsync(db));
            foreach (var indexName in DeferredRequiredIndexNames)
                Assert.True(await HasIndexAsync(db, indexName), $"필수 후행 인덱스가 복구되지 않았습니다: {indexName}");
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_SameNamedWrongRequiredIndex_ReplacesItsDefinition()
    {
        var tempRoot = CreateTempRoot("wrong-required-index-definition");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await db.Database.ExecuteSqlRawAsync(
                "DROP INDEX IF EXISTS \"IX_SyncOutboxEntries_MutationId\";");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX \"IX_SyncOutboxEntries_MutationId\" ON \"SyncOutboxEntries\" (\"Status\");");
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);

            Assert.True(await HasCurrentSchemaMarkerAsync(db));
            var metadata = await GetIndexMetadataAsync(
                db,
                "SyncOutboxEntries",
                "IX_SyncOutboxEntries_MutationId");
            Assert.NotNull(metadata);
            Assert.True(metadata!.IsUnique);
            Assert.Equal(new[] { "MutationId" }, metadata.Columns);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_SameNamedMalformedSupersedeScopeIndex_RepairsDefinitionAndRemainsIdempotent()
    {
        const string indexName = "IX_SyncOutboxEntries_SupersedeScope_Status_PreparedAtUtc";
        var expectedColumns = new[]
        {
            "EntityName",
            "EntityId",
            "TenantCode",
            "OfficeCode",
            "ResponsibleOfficeCode",
            "BusinessDatabaseName",
            "DeviceId",
            "SessionId",
            "UserId",
            "Status",
            "PreparedAtUtc"
        };
        var tempRoot = CreateTempRoot("malformed-supersede-scope-index");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await db.Database.ExecuteSqlRawAsync(
                $"DROP INDEX IF EXISTS \"{indexName}\";");
            await db.Database.ExecuteSqlRawAsync(
                $"""
                CREATE UNIQUE INDEX "{indexName}"
                ON "SyncOutboxEntries" ("Status", "PreparedAtUtc")
                WHERE "Status" = 'Prepared';
                """);
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);

            var repairedSql = await GetIndexSqlAsync(db, indexName);
            var repairedMetadata = await GetIndexMetadataAsync(
                db,
                "SyncOutboxEntries",
                indexName);
            Assert.NotNull(repairedMetadata);
            Assert.False(repairedMetadata!.IsUnique);
            Assert.False(repairedMetadata.IsPartial);
            Assert.Equal(expectedColumns, repairedMetadata.Columns);

            await LocalDbInitializer.InitializeAsync(db);

            Assert.True(await HasCurrentSchemaMarkerAsync(db));
            Assert.Equal(repairedSql, await GetIndexSqlAsync(db, indexName));
            var idempotentMetadata = await GetIndexMetadataAsync(
                db,
                "SyncOutboxEntries",
                indexName);
            Assert.NotNull(idempotentMetadata);
            Assert.False(idempotentMetadata!.IsUnique);
            Assert.False(idempotentMetadata.IsPartial);
            Assert.Equal(expectedColumns, idempotentMetadata.Columns);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("IF NOT EXISTS")]
    public async Task InitializeAsync_WrongPartialIndexStringLiteral_ReplacesItsDefinition(
        string wrongLiteral)
    {
        var tempRoot = CreateTempRoot("wrong-partial-index-literal");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await db.Database.ExecuteSqlRawAsync(
                "DROP INDEX IF EXISTS \"IX_RentalAssets_AssetKey\";");
            var escapedWrongLiteral = wrongLiteral.Replace("'", "''", StringComparison.Ordinal);
            var wrongIndexSql =
                $"""
                CREATE UNIQUE INDEX "IX_RentalAssets_AssetKey"
                ON "RentalAssets" ("TenantCode", "AssetKey")
                WHERE COALESCE("IsDeleted", 0) = 0
                  AND COALESCE(TRIM("AssetKey"), '{escapedWrongLiteral}') <> '{escapedWrongLiteral}';
                """;
            await db.Database.ExecuteSqlRawAsync(wrongIndexSql);
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);

            var repairedSql = await GetIndexSqlAsync(
                db,
                "IX_RentalAssets_AssetKey");
            Assert.NotNull(repairedSql);
            Assert.Contains(
                "COALESCE(TRIM(\"AssetKey\"), '') <> ''",
                repairedSql!,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"COALESCE(TRIM(\"AssetKey\"), '{escapedWrongLiteral}') <> '{escapedWrongLiteral}'",
                repairedSql,
                StringComparison.Ordinal);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_DuplicateActiveRentalAssetKey_RollsBackIndexesAndDoesNotRecordMarker()
    {
        var tempRoot = CreateTempRoot("deferred-index-duplicate");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await db.Database.ExecuteSqlRawAsync(
                "DROP INDEX IF EXISTS \"IX_RentalAssets_AssetKey\";");
            await db.Database.ExecuteSqlRawAsync(
                "DROP INDEX IF EXISTS \"IX_Transactions_ResponsibleOfficeCode\";");

            const string duplicateAssetKey = "DUPLICATE-ACTIVE-ASSET";
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = Guid.NewGuid(),
                    AssetKey = duplicateAssetKey,
                    IsDeleted = false
                },
                new LocalRentalAsset
                {
                    Id = Guid.NewGuid(),
                    AssetKey = duplicateAssetKey,
                    IsDeleted = false
                });
            await db.SaveChangesAsync();
            await DeleteSchemaMarkerAsync(db);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => LocalDbInitializer.InitializeAsync(db));

            Assert.False(await HasCurrentSchemaMarkerAsync(db));
            Assert.False(await HasIndexAsync(db, "IX_RentalAssets_AssetKey"));
            Assert.False(await HasIndexAsync(db, "IX_Transactions_ResponsibleOfficeCode"));
            Assert.Equal(
                2,
                await db.RentalAssets
                    .IgnoreQueryFilters()
                    .CountAsync(asset => asset.AssetKey == duplicateAssetKey));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static LocalDbContext CreateDbContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new LocalDbContext(options);
    }

    private static async Task ReplaceSyncOutboxWithMalformedTableAsync(LocalDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"SyncOutboxEntries\";");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "SyncOutboxEntries" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SyncOutboxEntries" PRIMARY KEY
            );
            """);
        db.ChangeTracker.Clear();
    }

    private static async Task
        ReplaceInventoryTransferTombstoneConflictsWithMalformedLegacyTableAsync(
            LocalDbContext db,
            Guid validTransferId,
            Guid malformedTransferId)
    {
        await db.Database.ExecuteSqlRawAsync(
            "DROP TABLE \"InventoryTransferTombstoneConflicts\";");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "InventoryTransferTombstoneConflicts" (
                "TransferId" TEXT NOT NULL
                    CONSTRAINT "PK_InventoryTransferTombstoneConflicts"
                    PRIMARY KEY,
                "TenantCode" TEXT NOT NULL DEFAULT '',
                "SourceOfficeCode" TEXT NOT NULL DEFAULT '',
                "TargetOfficeCode" TEXT NOT NULL DEFAULT '',
                "LocalSnapshotJson" TEXT NULL,
                "ServerTombstoneJson" TEXT NOT NULL DEFAULT '',
                "OutboxMutationIdsJson" TEXT NOT NULL DEFAULT '',
                "LocalRevision" INTEGER NOT NULL DEFAULT 0,
                "ServerRevision" INTEGER NOT NULL DEFAULT 0,
                "ServerUpdatedAtUtc" TEXT NOT NULL,
                "Status" TEXT NOT NULL DEFAULT '',
                "DetectedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "ResolvedAtUtc" TEXT NULL,
                "Resolution" TEXT NOT NULL DEFAULT ''
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "InventoryTransferTombstoneConflicts" (
                "TransferId",
                "TenantCode",
                "SourceOfficeCode",
                "TargetOfficeCode",
                "LocalSnapshotJson",
                "ServerTombstoneJson",
                "OutboxMutationIdsJson",
                "LocalRevision",
                "ServerRevision",
                "ServerUpdatedAtUtc",
                "Status",
                "DetectedAtUtc",
                "UpdatedAtUtc",
                "ResolvedAtUtc",
                "Resolution")
            VALUES
                ({0}, 'USENET_GROUP', 'USENET', 'YEONSU',
                 'valid-local-snapshot', 'valid-server-snapshot',
                 '["mutation-valid"]', 3, 8,
                 '2026-08-01 02:50:00', 'Unresolved',
                 '2026-08-01 02:51:00', '2026-08-01 02:51:00', NULL, ''),
                ({1}, 'USENET_GROUP', 'USENET', 'YEONSU', NULL,
                 'server-snapshot', '["mutation-failure"]', 4, 9,
                 '2026-08-01 03:00:00', 'Unresolved',
                 '2026-08-01 03:01:00', '2026-08-01 03:01:00', NULL, '');
            """,
            validTransferId.ToString("D").ToUpperInvariant(),
            malformedTransferId.ToString("D").ToUpperInvariant());
        db.ChangeTracker.Clear();
    }

    private static async Task<long>
        GetLegacyInventoryTransferTombstoneConflictRowCountAsync(
            LocalDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose =
            connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM \"InventoryTransferTombstoneConflicts\";";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task<string?> GetLegacyLocalSnapshotAsync(
        LocalDbContext db,
        Guid transferId)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose =
            connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"LocalSnapshotJson\" " +
                "FROM \"InventoryTransferTombstoneConflicts\" " +
                "WHERE \"TransferId\" = $transferId;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$transferId";
            parameter.Value = transferId.ToString("D").ToUpperInvariant();
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? null : result.ToString();
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static Task<bool> HasCurrentSchemaMarkerAsync(LocalDbContext db)
        => db.Settings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(setting =>
                setting.Key == SchemaMaintenanceVersionKey &&
                setting.Value == SchemaMaintenanceVersion);

    private static async Task DeleteSchemaMarkerAsync(LocalDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"Settings\" WHERE \"Key\" = {0};",
            SchemaMaintenanceVersionKey);
        db.ChangeTracker.Clear();
    }

    private static async Task<bool> HasIndexAsync(LocalDbContext db, string indexName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"name\" = $name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = indexName;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync() is not null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task DropIndexAsync(LocalDbContext db, string indexName)
    {
        if (indexName.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("안전하지 않은 인덱스 이름입니다.", nameof(indexName));

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP INDEX IF EXISTS \"{indexName}\";";
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task<IndexMetadata?> GetIndexMetadataAsync(
        LocalDbContext db,
        string tableName,
        string indexName)
    {
        if (tableName.Any(character => !char.IsLetterOrDigit(character) && character != '_') ||
            indexName.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("안전하지 않은 스키마 식별자입니다.");
        }

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            bool? isUnique = null;
            bool? isPartial = null;
            await using (var listCommand = connection.CreateCommand())
            {
                listCommand.CommandText = $"PRAGMA index_list(\"{tableName}\");";
                await using var reader = await listCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!string.Equals(
                            reader["name"]?.ToString(),
                            indexName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    isUnique = Convert.ToInt32(reader["unique"]) == 1;
                    isPartial = Convert.ToInt32(reader["partial"]) == 1;
                    break;
                }
            }

            if (!isUnique.HasValue || !isPartial.HasValue)
                return null;

            var columns = new List<string>();
            await using (var infoCommand = connection.CreateCommand())
            {
                infoCommand.CommandText = $"PRAGMA index_info(\"{indexName}\");";
                await using var reader = await infoCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    columns.Add(reader["name"]?.ToString() ?? string.Empty);
            }

            return new IndexMetadata(isUnique.Value, isPartial.Value, columns);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task<string?> GetIndexSqlAsync(
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
            command.CommandText =
                "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'index' AND \"name\" = $name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = indexName;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync() as string;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
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

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(
        LocalDbContext db,
        string tableName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose =
            connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<(int Order, string Name)>();
            while (await reader.ReadAsync())
            {
                var order = Convert.ToInt32(reader["pk"]);
                if (order <= 0)
                    continue;

                columns.Add((order, reader["name"]?.ToString() ?? string.Empty));
            }

            return columns
                .OrderBy(column => column.Order)
                .Select(column => column.Name)
                .ToArray();
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task<bool> HasTableAsync(
        LocalDbContext db,
        string tableName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose =
            connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM \"sqlite_master\" " +
                "WHERE \"type\" = 'table' AND \"name\" = $name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync() is not null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static string CreateTempRoot(string scenario)
    {
        var tempRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "localdb-schema-failure-tests",
            $"{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(tempRoot))
            return;

        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
            // Test cleanup must not hide the schema assertion result.
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private sealed record IndexMetadata(
        bool IsUnique,
        bool IsPartial,
        IReadOnlyList<string> Columns);
}
