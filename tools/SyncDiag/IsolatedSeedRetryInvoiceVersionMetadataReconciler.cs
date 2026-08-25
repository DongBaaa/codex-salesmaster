using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed record IsolatedSeedRetryInvoiceVersionMetadataReconcileResult(
    int RebasedInvoices,
    int RemovedStaleOutbox);

internal static class IsolatedSeedRetryInvoiceVersionMetadataReconciler
{
    internal const string ConflictReason =
        "Existing invoice version metadata cannot be changed in place.";

    internal static async Task<IsolatedSeedRetryInvoiceVersionMetadataReconcileResult> ReconcileAsync(
        LocalDbContext db,
        string serverDatabasePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An active isolated seed retry transaction is required.");

        var normalizedServerDatabasePath = ValidateServerDatabasePath(
            serverDatabasePath);
        var failedRows = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.EntityName == nameof(LocalInvoice) &&
                entry.Status == "Failed" &&
                entry.ErrorMessage.Contains(ConflictReason))
            .OrderBy(entry => entry.EntityId)
            .ThenBy(entry => entry.Id)
            .ToListAsync(ct);
        if (failedRows.Count == 0)
        {
            return new IsolatedSeedRetryInvoiceVersionMetadataReconcileResult(
                0,
                0);
        }

        var duplicateCandidate = failedRows
            .GroupBy(entry => entry.EntityId)
            .FirstOrDefault(group =>
                group.Key == Guid.Empty ||
                group.Count() != 1);
        if (duplicateCandidate is not null)
        {
            throw new InvalidOperationException(
                "The isolated invoice version metadata retry candidate set is ambiguous.");
        }

        var candidateIds = failedRows
            .Select(entry => entry.EntityId)
            .ToList();
        var localInvoices = await db.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => candidateIds.Contains(invoice.Id))
            .ToDictionaryAsync(invoice => invoice.Id, ct);
        if (localInvoices.Count != candidateIds.Count)
        {
            throw new InvalidOperationException(
                "An isolated invoice version metadata retry candidate is missing locally.");
        }

        var serverMetadata = await LoadServerMetadataAsync(
            normalizedServerDatabasePath,
            candidateIds,
            ct);
        if (serverMetadata.Count != candidateIds.Count)
        {
            throw new InvalidOperationException(
                "An isolated invoice version metadata retry candidate is missing on the isolated server.");
        }

        foreach (var candidateId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            var localInvoice = localInvoices[candidateId];
            var serverInvoice = serverMetadata[candidateId];
            if (!localInvoice.IsDirty ||
                localInvoice.IsDeleted ||
                serverInvoice.IsDeleted ||
                serverInvoice.Revision <= 0 ||
                serverInvoice.VersionNumber <= 0 ||
                localInvoice.CustomerId != serverInvoice.CustomerId ||
                (int)localInvoice.VoucherType != serverInvoice.VoucherType ||
                !SameRequiredScope(localInvoice.TenantCode, serverInvoice.TenantCode) ||
                !SameRequiredScope(localInvoice.OfficeCode, serverInvoice.OfficeCode) ||
                !SameRequiredScope(
                    localInvoice.ResponsibleOfficeCode,
                    serverInvoice.ResponsibleOfficeCode))
            {
                throw new InvalidOperationException(
                    "An isolated invoice version metadata retry candidate failed the scope or state contract.");
            }

            if (NormalizeVersionGroup(localInvoice.Id, localInvoice.VersionGroupId) ==
                    NormalizeVersionGroup(serverInvoice.Id, serverInvoice.VersionGroupId) &&
                Math.Max(1, localInvoice.VersionNumber) ==
                    serverInvoice.VersionNumber &&
                NormalizeOptionalGuid(localInvoice.PreviousVersionId) ==
                    NormalizeOptionalGuid(serverInvoice.PreviousVersionId))
            {
                throw new InvalidOperationException(
                    "An isolated invoice version metadata retry candidate no longer has a metadata mismatch.");
            }
        }

        foreach (var candidateId in candidateIds)
        {
            var localInvoice = localInvoices[candidateId];
            var serverInvoice = serverMetadata[candidateId];
            localInvoice.VersionGroupId = serverInvoice.VersionGroupId;
            localInvoice.VersionNumber = serverInvoice.VersionNumber;
            localInvoice.PreviousVersionId = serverInvoice.PreviousVersionId;
            localInvoice.IsLatestVersion = serverInvoice.IsLatestVersion;
            localInvoice.Revision = serverInvoice.Revision;
            localInvoice.IsDirty = true;
        }

        await db.SaveChangesAsync(ct);
        var removedStaleOutbox = await db.SyncOutboxEntries
            .Where(entry =>
                entry.EntityName == nameof(LocalInvoice) &&
                entry.Status != "Acknowledged" &&
                candidateIds.Contains(entry.EntityId))
            .ExecuteDeleteAsync(ct);
        if (removedStaleOutbox < candidateIds.Count)
        {
            throw new InvalidOperationException(
                "An isolated invoice version metadata retry outbox row changed during reconciliation.");
        }

        return new IsolatedSeedRetryInvoiceVersionMetadataReconcileResult(
            candidateIds.Count,
            removedStaleOutbox);
    }

    private static string ValidateServerDatabasePath(string serverDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(serverDatabasePath) ||
            !Path.IsPathFullyQualified(serverDatabasePath))
        {
            throw new InvalidOperationException(
                "The isolated server database path must be explicit and fully qualified.");
        }

        var normalizedPath = Path.GetFullPath(serverDatabasePath);
        if (!File.Exists(normalizedPath) ||
            (File.GetAttributes(normalizedPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The isolated server database must be an existing regular file.");
        }

        return normalizedPath;
    }

    private static async Task<Dictionary<Guid, ServerInvoiceVersionMetadata>> LoadServerMetadataAsync(
        string serverDatabasePath,
        IReadOnlyCollection<Guid> candidateIds,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = serverDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        var result = new Dictionary<Guid, ServerInvoiceVersionMetadata>();
        foreach (var candidateId in candidateIds)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id,
                       CustomerId,
                       TenantCode,
                       OfficeCode,
                       ResponsibleOfficeCode,
                       VoucherType,
                       VersionGroupId,
                       VersionNumber,
                       PreviousVersionId,
                       IsLatestVersion,
                       IsDeleted,
                       Revision
                FROM Invoices
                WHERE Id = $id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", candidateId.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                continue;

            var metadata = new ServerInvoiceVersionMetadata(
                ParseRequiredGuid(reader.GetString(0)),
                ParseRequiredGuid(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                ParseRequiredGuid(reader.GetString(6)),
                reader.GetInt32(7),
                reader.IsDBNull(8)
                    ? null
                    : ParseRequiredGuid(reader.GetString(8)),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetInt64(11));
            if (!result.TryAdd(metadata.Id, metadata) ||
                metadata.Id != candidateId)
            {
                throw new InvalidOperationException(
                    "The isolated server invoice metadata identifier contract failed.");
            }
        }

        return result;
    }

    private static Guid ParseRequiredGuid(string value)
        => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException(
                "The isolated server invoice metadata contains an invalid identifier.");

    private static Guid NormalizeVersionGroup(Guid invoiceId, Guid versionGroupId)
        => versionGroupId == Guid.Empty ? invoiceId : versionGroupId;

    private static Guid? NormalizeOptionalGuid(Guid? value)
        => value.HasValue && value.Value != Guid.Empty
            ? value.Value
            : null;

    private static bool SameRequiredScope(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(
               left.Trim(),
               right.Trim(),
               StringComparison.OrdinalIgnoreCase);

    private sealed record ServerInvoiceVersionMetadata(
        Guid Id,
        Guid CustomerId,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        int VoucherType,
        Guid VersionGroupId,
        int VersionNumber,
        Guid? PreviousVersionId,
        bool IsLatestVersion,
        bool IsDeleted,
        long Revision);
}
