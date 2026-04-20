using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static readonly (string Table, string Column)[] ItemReferenceColumns =
    [
        ("InvoiceLines", "ItemId"),
        ("InvoiceLineSerials", "ItemId"),
        ("RentalAssets", "ItemId"),
        ("ItemWarehouseStocks", "ItemId"),
        ("SerialLedgers", "ItemId"),
        ("InventoryTransferLines", "ItemId"),
        ("InventoryMovements", "ItemId"),
        ("StockLayers", "ItemId")
    ];

    private static async Task NormalizeCaseVariantItemIdsAsync(LocalDbContext db)
    {
        await using var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        var duplicateKeys = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT lower("Id")
                                  FROM "Items"
                                  WHERE COALESCE("IsDeleted", 0) = 0
                                  GROUP BY lower("Id")
                                  HAVING COUNT(*) > 1
                                  """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var normalizedId = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(normalizedId))
                    duplicateKeys.Add(normalizedId);
            }
        }

        if (duplicateKeys.Count == 0)
            return;

        await using var transaction = await db.Database.BeginTransactionAsync();
        var dbTransaction = transaction.GetDbTransaction();
        foreach (var normalizedId in duplicateKeys)
        {
            var rows = await LoadCaseVariantItemRowsAsync(connection, dbTransaction, normalizedId);
            if (rows.Count <= 1)
                continue;

            var canonical = rows
                .OrderByDescending(current => current.Revision)
                .ThenByDescending(current => current.UpdatedAtUtc)
                .ThenByDescending(current => current.CreatedAtUtc)
                .ThenBy(current => current.Id, StringComparer.OrdinalIgnoreCase)
                .First();

            foreach (var (table, column) in ItemReferenceColumns)
                await UpdateCaseVariantItemReferenceAsync(connection, dbTransaction, table, column, normalizedId, canonical.Id);

            await DeleteCaseVariantDuplicateItemsAsync(connection, dbTransaction, normalizedId, canonical.Id);
        }

        await transaction.CommitAsync();
    }

    private static async Task<List<CaseVariantItemRow>> LoadCaseVariantItemRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string normalizedId)
    {
        var rows = new List<CaseVariantItemRow>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT "Id", "Revision", "UpdatedAtUtc", "CreatedAtUtc"
                              FROM "Items"
                              WHERE COALESCE("IsDeleted", 0) = 0
                                AND lower("Id") = $normalizedId
                              """;
        AddParameter(command, "$normalizedId", normalizedId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new CaseVariantItemRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? 0L : reader.GetInt64(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return rows;
    }

    private static async Task UpdateCaseVariantItemReferenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        string normalizedId,
        string canonicalId)
    {
        if (!SqlIdentifierPattern.IsMatch(table) || !SqlIdentifierPattern.IsMatch(column))
            throw new InvalidOperationException($"정규화할 수 없는 SQL 식별자입니다: {table}.{column}");

        if (string.Equals(table, "ItemWarehouseStocks", StringComparison.Ordinal) &&
            string.Equals(column, "ItemId", StringComparison.Ordinal))
        {
            await MergeCaseVariantItemWarehouseStocksAsync(connection, transaction, normalizedId, canonicalId);
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               UPDATE "{table}"
                               SET "{column}" = $canonicalId
                               WHERE "{column}" IS NOT NULL
                                 AND lower("{column}") = $normalizedId
                                 AND "{column}" <> $canonicalId
                               """;
        AddParameter(command, "$canonicalId", canonicalId);
        AddParameter(command, "$normalizedId", normalizedId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MergeCaseVariantItemWarehouseStocksAsync(
        DbConnection connection,
        DbTransaction transaction,
        string normalizedId,
        string canonicalId)
    {
        var stockRows = new List<CaseVariantItemWarehouseStockRow>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = """
                                        SELECT "WarehouseCode", SUM("Quantity"), MAX(COALESCE("UpdatedAtUtc", ''))
                                        FROM "ItemWarehouseStocks"
                                        WHERE lower("ItemId") = $normalizedId
                                        GROUP BY "WarehouseCode"
                                        """;
            AddParameter(selectCommand, "$normalizedId", normalizedId);
            await using var reader = await selectCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                stockRows.Add(new CaseVariantItemWarehouseStockRow(
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(1) ? 0m : reader.GetDecimal(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
            }
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                                        DELETE FROM "ItemWarehouseStocks"
                                        WHERE lower("ItemId") = $normalizedId
                                        """;
            AddParameter(deleteCommand, "$normalizedId", normalizedId);
            await deleteCommand.ExecuteNonQueryAsync();
        }

        foreach (var stockRow in stockRows)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                                        INSERT INTO "ItemWarehouseStocks" ("ItemId", "WarehouseCode", "Quantity", "UpdatedAtUtc")
                                        VALUES ($canonicalId, $warehouseCode, $quantity, $updatedAtUtc)
                                        """;
            AddParameter(insertCommand, "$canonicalId", canonicalId);
            AddParameter(insertCommand, "$warehouseCode", stockRow.WarehouseCode);
            AddParameter(insertCommand, "$quantity", stockRow.Quantity);
            AddParameter(insertCommand, "$updatedAtUtc", stockRow.UpdatedAtUtcRaw);
            await insertCommand.ExecuteNonQueryAsync();
        }
    }

    private static async Task DeleteCaseVariantDuplicateItemsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string normalizedId,
        string canonicalId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              DELETE FROM "Items"
                              WHERE lower("Id") = $normalizedId
                                AND "Id" <> $canonicalId
                              """;
        AddParameter(command, "$normalizedId", normalizedId);
        AddParameter(command, "$canonicalId", canonicalId);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record CaseVariantItemRow(
        string Id,
        long Revision,
        string UpdatedAtUtcRaw,
        string CreatedAtUtcRaw)
    {
        public DateTime UpdatedAtUtc { get; } = ParseUtc(UpdatedAtUtcRaw);
        public DateTime CreatedAtUtc { get; } = ParseUtc(CreatedAtUtcRaw);

        private static DateTime ParseUtc(string value)
            => DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : DateTime.MinValue;
    }

    private sealed record CaseVariantItemWarehouseStockRow(
        string WarehouseCode,
        decimal Quantity,
        string UpdatedAtUtcRaw);
}
