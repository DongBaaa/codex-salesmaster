using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed record IsolatedSeedRetryRentalCompanyResult(int RebasedCompanies, int RemovedStaleOutbox);

// Only called by the isolated seed retry command, under its database/target leases.
// It does not change the production client's conflict resolution policy.
internal static class IsolatedSeedRetryRentalCompanyReconciler
{
    internal const string ConflictPrefix = "Expected revision mismatch.";

    internal static async Task<IsolatedSeedRetryRentalCompanyResult> ReconcileAsync(
        LocalDbContext db, string serverDatabasePath, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An active isolated seed retry transaction is required.");

        var failed = await db.SyncOutboxEntries.AsNoTracking()
            .Where(x => x.EntityName == nameof(LocalRentalManagementCompany) &&
                        x.Status == "Failed" &&
                        (x.ErrorMessage.StartsWith(ConflictPrefix) ||
                         (x.ErrorMessage.StartsWith("동기화 충돌 ") &&
                          x.ErrorMessage.Contains(" - " + ConflictPrefix))))
            .ToListAsync(ct);
        if (failed.Count == 0)
            return new(0, 0);
        if (failed.Any(x => x.Id == Guid.Empty || x.EntityId == Guid.Empty ||
                            x.SessionId == Guid.Empty || x.UserId == Guid.Empty ||
                            string.IsNullOrWhiteSpace(x.MutationId) || string.IsNullOrWhiteSpace(x.DeviceId)) ||
            failed.GroupBy(x => x.EntityId).Any(g => g.Count() != 1))
            throw new InvalidOperationException("Ambiguous isolated company retry identities.");

        if (!Path.IsPathFullyQualified(serverDatabasePath) || !File.Exists(serverDatabasePath))
            throw new InvalidOperationException("An existing absolute isolated server database path is required.");
        for (FileSystemInfo? path = new FileInfo(Path.GetFullPath(serverDatabasePath)); path is not null;
             path = path is FileInfo file ? file.Directory : ((DirectoryInfo)path).Parent)
            if ((path.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The isolated server path must not traverse a reparse point.");

        await using var server = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(serverDatabasePath), Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        await server.OpenAsync(ct);
        await using var serverSnapshot = await server.BeginTransactionAsync(ct);
        var changes = new List<(LocalRentalManagementCompany Company, LocalSyncOutboxEntry Outbox, long Revision)>();
        foreach (var row in failed)
        {
            var company = await db.RentalManagementCompanies.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == row.EntityId, ct)
                ?? throw new InvalidOperationException("The isolated company retry row is missing.");
            var tenant = company.Code switch
            {
                OfficeCodeCatalog.Itworld => TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Usenet or OfficeCodeCatalog.Yeonsu => TenantScopeCatalog.UsenetGroup,
                _ => throw new InvalidOperationException("Only canonical default company retries are supported.")
            };
            var business = company.Code == OfficeCodeCatalog.Itworld ? "ITWORLD" : "USENET";
            if (!company.IsDirty || company.IsDeleted || !company.IsSystemDefault ||
                company.Revision != row.ExpectedRevision || row.ExpectedRevision < 0 ||
                row.TenantCode != tenant || row.BusinessDatabaseName != business ||
                string.IsNullOrWhiteSpace(row.OfficeCode) || string.IsNullOrWhiteSpace(row.ResponsibleOfficeCode) ||
                await db.SyncOutboxEntries.CountAsync(x => x.EntityName == nameof(LocalRentalManagementCompany) &&
                    x.EntityId == company.Id && x.Status != "Acknowledged", ct) != 1)
                throw new InvalidOperationException("The isolated company retry scope or mutation state changed.");

            await using var command = server.CreateCommand();
            command.Transaction = (SqliteTransaction)serverSnapshot;
            // A newly seeded server creates its own IDs for these natural keys.
            // Include an ID match too, so an ID reused in another scope fails closed.
            command.CommandText = """
                SELECT Id, TenantCode, Code, Revision, IsSystemDefault, IsDeleted
                FROM RentalManagementCompanies
                WHERE Id = $id COLLATE NOCASE OR (TenantCode = $tenant AND Code = $code);
                """;
            command.Parameters.AddWithValue("$id", company.Id.ToString());
            command.Parameters.AddWithValue("$tenant", tenant);
            command.Parameters.AddWithValue("$code", company.Code);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("The isolated server company is missing.");
            var revision = reader.GetInt64(3);
            if (!Guid.TryParse(reader.GetString(0), out var serverId) || serverId == Guid.Empty ||
                reader.GetString(1) != tenant || reader.GetString(2) != company.Code ||
                !reader.GetBoolean(4) || reader.GetBoolean(5) || revision <= 0 || revision == company.Revision ||
                await reader.ReadAsync(ct))
                throw new InvalidOperationException("The isolated server company identity or version is ambiguous.");
            changes.Add((company, row, revision));
        }

        // Validate every candidate before changing any local row. Keep its ID,
        // business fields, timestamps and IsDirty; a subsequent real push must ack it.
        var removed = 0;
        foreach (var (company, row, revision) in changes)
        {
            company.Revision = revision;
            removed += await db.SyncOutboxEntries.Where(x => x.Id == row.Id &&
                x.EntityId == row.EntityId && x.EntityName == row.EntityName &&
                x.Status == row.Status && x.MutationId == row.MutationId &&
                x.DeviceId == row.DeviceId && x.ExpectedRevision == row.ExpectedRevision &&
                x.ErrorMessage == row.ErrorMessage && x.TenantCode == row.TenantCode &&
                x.BusinessDatabaseName == row.BusinessDatabaseName && x.OfficeCode == row.OfficeCode &&
                x.ResponsibleOfficeCode == row.ResponsibleOfficeCode &&
                x.SessionId == row.SessionId && x.UserId == row.UserId).ExecuteDeleteAsync(ct);
        }
        if (removed != changes.Count)
            throw new InvalidOperationException("An isolated company retry outbox changed during reconciliation.");
        await db.SaveChangesAsync(ct);
        return new(changes.Count, removed);
    }
}
