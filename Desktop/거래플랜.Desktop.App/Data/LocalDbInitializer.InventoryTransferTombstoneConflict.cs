using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private const string InventoryTransferTombstoneConflictTableName =
        "InventoryTransferTombstoneConflicts";
    private const string
        InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName =
            "InventoryTransferTombstoneConflicts_LegacyPrimaryKey";

    private static readonly string[]
        InventoryTransferTombstoneConflictColumns =
        [
            "TransferId",
            "BusinessDatabaseName",
            "TenantCode",
            "SourceOfficeCode",
            "TargetOfficeCode",
            "LocalSnapshotJson",
            "ServerTombstoneJson",
            "OutboxMutationIdsJson",
            "ArchivedReceiveEvidencePath",
            "LocalRevision",
            "ServerRevision",
            "ServerUpdatedAtUtc",
            "Status",
            "DetectedAtUtc",
            "UpdatedAtUtc",
            "ResolvedAtUtc",
            "Resolution",
            "RecoveredTransferId"
        ];

    private static async Task EnsureInventoryTransferTombstoneConflictsTableAsync(
        LocalDbContext db)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                BuildInventoryTransferTombstoneConflictCreateTableSql(
                    ifNotExists: true));
            InvalidateSchemaColumnCache(
                InventoryTransferTombstoneConflictTableName);

            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "BusinessDatabaseName", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "TenantCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "SourceOfficeCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "TargetOfficeCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "LocalSnapshotJson", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "ServerTombstoneJson", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "OutboxMutationIdsJson", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "ArchivedReceiveEvidencePath", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "LocalRevision", "INTEGER NOT NULL DEFAULT 0");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "ServerRevision", "INTEGER NOT NULL DEFAULT 0");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "ServerUpdatedAtUtc", $"TEXT NOT NULL DEFAULT '{FallbackUtcText}'");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "Status", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "DetectedAtUtc", $"TEXT NOT NULL DEFAULT '{FallbackUtcText}'");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "UpdatedAtUtc", $"TEXT NOT NULL DEFAULT '{FallbackUtcText}'");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "ResolvedAtUtc", "TEXT NULL");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "Resolution", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, InventoryTransferTombstoneConflictTableName, "RecoveredTransferId", "TEXT NULL");

            await TryNormalizeDateTimeTextColumnAsync(
                db,
                InventoryTransferTombstoneConflictTableName,
                "ServerUpdatedAtUtc");
            await TryNormalizeDateTimeTextColumnAsync(
                db,
                InventoryTransferTombstoneConflictTableName,
                "DetectedAtUtc");
            await TryNormalizeDateTimeTextColumnAsync(
                db,
                InventoryTransferTombstoneConflictTableName,
                "UpdatedAtUtc");

            if (!await HasExpectedInventoryTransferTombstoneConflictPrimaryKeyAsync(
                    db))
            {
                await RebuildInventoryTransferTombstoneConflictPrimaryKeyAsync(
                    db);
            }

            if (!await HasExpectedInventoryTransferTombstoneConflictPrimaryKeyAsync(
                    db))
            {
                throw new InvalidOperationException(
                    "재고이동 원격삭제 충돌 테이블의 기본키가 " +
                    "(BusinessDatabaseName, TransferId) 복합키가 아닙니다.");
            }

            await TryCreateIndexAsync(
                db,
                "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransferTombstoneConflicts_Status_UpdatedAtUtc\" ON \"InventoryTransferTombstoneConflicts\" (\"Status\", \"UpdatedAtUtc\");");
            await TryCreateIndexAsync(
                db,
                "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransferTombstoneConflicts_BusinessScope_Status\" ON \"InventoryTransferTombstoneConflicts\" (\"BusinessDatabaseName\", \"TenantCode\", \"SourceOfficeCode\", \"TargetOfficeCode\", \"Status\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(
                nameof(EnsureInventoryTransferTombstoneConflictsTableAsync),
                ex);
        }
    }

    private static string
        BuildInventoryTransferTombstoneConflictCreateTableSql(
            bool ifNotExists)
    {
        var existenceGuard = ifNotExists ? "IF NOT EXISTS " : string.Empty;
        return $$"""
            CREATE TABLE {{existenceGuard}}"InventoryTransferTombstoneConflicts" (
                "TransferId" TEXT NOT NULL,
                "BusinessDatabaseName" TEXT NOT NULL DEFAULT '',
                "TenantCode" TEXT NOT NULL DEFAULT '',
                "SourceOfficeCode" TEXT NOT NULL DEFAULT '',
                "TargetOfficeCode" TEXT NOT NULL DEFAULT '',
                "LocalSnapshotJson" TEXT NOT NULL DEFAULT '',
                "ServerTombstoneJson" TEXT NOT NULL DEFAULT '',
                "OutboxMutationIdsJson" TEXT NOT NULL DEFAULT '',
                "ArchivedReceiveEvidencePath" TEXT NOT NULL DEFAULT '',
                "LocalRevision" INTEGER NOT NULL DEFAULT 0,
                "ServerRevision" INTEGER NOT NULL DEFAULT 0,
                "ServerUpdatedAtUtc" TEXT NOT NULL,
                "Status" TEXT NOT NULL DEFAULT '',
                "DetectedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "ResolvedAtUtc" TEXT NULL,
                "Resolution" TEXT NOT NULL DEFAULT '',
                "RecoveredTransferId" TEXT NULL,
                CONSTRAINT "PK_InventoryTransferTombstoneConflicts"
                    PRIMARY KEY ("BusinessDatabaseName", "TransferId")
            );
            """;
    }

    private static async Task
        RebuildInventoryTransferTombstoneConflictPrimaryKeyAsync(
            LocalDbContext db)
    {
        if (await HasInventoryTransferTombstoneConflictTableAsync(
                db,
                InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName))
        {
            throw new InvalidOperationException(
                "재고이동 원격삭제 충돌 기본키 migration 임시 테이블이 이미 존재합니다.");
        }

        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.BeginRuntimeMutationTransactionAsync()
            : null;

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {QuoteSqlIdentifier(InventoryTransferTombstoneConflictTableName)} " +
                $"RENAME TO {QuoteSqlIdentifier(InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName)};");
            InvalidateSchemaColumnCache(
                InventoryTransferTombstoneConflictTableName);
            InvalidateSchemaColumnCache(
                InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName);

            await db.Database.ExecuteSqlRawAsync(
                BuildInventoryTransferTombstoneConflictCreateTableSql(
                    ifNotExists: false));
            InvalidateSchemaColumnCache(
                InventoryTransferTombstoneConflictTableName);

            var copiedRowCount =
                await CopyLegacyInventoryTransferTombstoneConflictsAsync(db);
            var legacyRowCount =
                await CountInventoryTransferTombstoneConflictRowsAsync(
                    db,
                    InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName);
            if (copiedRowCount != legacyRowCount)
            {
                throw new InvalidOperationException(
                    "재고이동 원격삭제 충돌 기본키 migration 중 행 수가 일치하지 않습니다: " +
                    $"legacy={legacyRowCount}, copied={copiedRowCount}");
            }

            if (!await HasExpectedInventoryTransferTombstoneConflictPrimaryKeyAsync(
                    db))
            {
                throw new InvalidOperationException(
                    "재고이동 원격삭제 충돌 기본키 migration 사후조건이 충족되지 않았습니다.");
            }

            await db.Database.ExecuteSqlRawAsync(
                "DROP TABLE \"InventoryTransferTombstoneConflicts_LegacyPrimaryKey\";");
            InvalidateSchemaColumnCache(
                InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName);

            if (transaction is not null)
                await transaction.CommitAsync();
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync();

            throw;
        }
        finally
        {
            InvalidateSchemaColumnCache(
                InventoryTransferTombstoneConflictTableName);
            InvalidateSchemaColumnCache(
                InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName);
        }
    }

    private static async Task<int>
        CopyLegacyInventoryTransferTombstoneConflictsAsync(LocalDbContext db)
    {
        var quotedColumns = string.Join(
            ", ",
            InventoryTransferTombstoneConflictColumns.Select(
                QuoteSqlIdentifier));
        var connection = db.Database.GetDbConnection();
        var rows = new List<object[]>();

        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction =
                db.Database.CurrentTransaction?.GetDbTransaction();
            selectCommand.CommandText =
                $"SELECT {quotedColumns} FROM " +
                $"{QuoteSqlIdentifier(InventoryTransferTombstoneConflictLegacyPrimaryKeyTableName)} " +
                "ORDER BY \"TransferId\";";
            await using var reader = await selectCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                var businessDatabaseName = ReadSchemaText(values[1]);
                var tenantCode = ReadSchemaText(values[2]);
                values[1] = TenantScopeCatalog.GetDatabaseName(
                    string.IsNullOrWhiteSpace(businessDatabaseName)
                        ? tenantCode
                        : businessDatabaseName);
                rows.Add(values);
            }
        }

        if (rows.Count == 0)
            return 0;

        var parameterNames = Enumerable.Range(
                0,
                InventoryTransferTombstoneConflictColumns.Length)
            .Select(index => $"$p{index}")
            .ToArray();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction =
            db.Database.CurrentTransaction?.GetDbTransaction();
        insertCommand.CommandText =
            $"INSERT INTO {QuoteSqlIdentifier(InventoryTransferTombstoneConflictTableName)} " +
            $"({quotedColumns}) VALUES ({string.Join(", ", parameterNames)});";
        foreach (var parameterName in parameterNames)
        {
            var parameter = insertCommand.CreateParameter();
            parameter.ParameterName = parameterName;
            insertCommand.Parameters.Add(parameter);
        }

        var copiedRowCount = 0;
        foreach (var values in rows)
        {
            for (var index = 0; index < values.Length; index++)
            {
                insertCommand.Parameters[index].Value =
                    values[index] ?? DBNull.Value;
            }

            copiedRowCount += await insertCommand.ExecuteNonQueryAsync();
        }

        return copiedRowCount;
    }

    private static string ReadSchemaText(object? value)
        => value is null or DBNull
            ? string.Empty
            : value.ToString()?.Trim() ?? string.Empty;

    private static async Task<long>
        CountInventoryTransferTombstoneConflictRowsAsync(
            LocalDbContext db,
            string tableName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            $"SELECT COUNT(*) FROM {QuoteSqlIdentifier(tableName)};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool>
        HasExpectedInventoryTransferTombstoneConflictPrimaryKeyAsync(
            LocalDbContext db)
    {
        var primaryKeyColumns =
            await GetInventoryTransferTombstoneConflictPrimaryKeyColumnsAsync(
                db);
        return primaryKeyColumns.Count == 2 &&
               string.Equals(
                   primaryKeyColumns[0],
                   "BusinessDatabaseName",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   primaryKeyColumns[1],
                   "TransferId",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<string>>
        GetInventoryTransferTombstoneConflictPrimaryKeyColumnsAsync(
            LocalDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            $"PRAGMA table_info({QuoteSqlIdentifier(InventoryTransferTombstoneConflictTableName)});";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<(int Order, string Name)>();
        while (await reader.ReadAsync())
        {
            var order = Convert.ToInt32(reader["pk"]);
            if (order <= 0)
                continue;

            columns.Add(
                (order, reader["name"]?.ToString() ?? string.Empty));
        }

        return columns
            .OrderBy(column => column.Order)
            .Select(column => column.Name)
            .ToArray();
    }

    private static async Task<bool>
        HasInventoryTransferTombstoneConflictTableAsync(
            LocalDbContext db,
            string tableName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            "SELECT 1 FROM \"sqlite_master\" " +
            "WHERE \"type\" = 'table' AND \"name\" = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync() is not null;
    }
}
