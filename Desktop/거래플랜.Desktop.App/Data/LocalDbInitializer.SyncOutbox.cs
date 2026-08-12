using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static async Task EnsureSyncOutboxTableAsync(LocalDbContext db)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "SyncOutboxEntries" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SyncOutboxEntries" PRIMARY KEY,
                    "MutationId" TEXT NOT NULL DEFAULT '',
                    "DeviceId" TEXT NOT NULL DEFAULT '',
                    "EntityName" TEXT NOT NULL DEFAULT '',
                    "EntityId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    "ExpectedRevision" INTEGER NOT NULL DEFAULT 0,
                    "TenantCode" TEXT NOT NULL DEFAULT '',
                    "OfficeCode" TEXT NOT NULL DEFAULT '',
                    "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT '',
                    "BusinessDatabaseName" TEXT NOT NULL DEFAULT '',
                    "SessionId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    "UserId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    "Status" TEXT NOT NULL DEFAULT 'Prepared',
                    "ErrorMessage" TEXT NOT NULL DEFAULT '',
                    "PreparedAtUtc" TEXT NOT NULL,
                    "SentAtUtc" TEXT NULL,
                    "AcknowledgedAtUtc" TEXT NULL,
                    "AcceptedRevision" INTEGER NOT NULL DEFAULT 0,
                    "AcceptedUpdatedAtUtc" TEXT NULL
                );
                """);

            await TryAddColumnAsync(db, "SyncOutboxEntries", "TenantCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "SyncOutboxEntries", "OfficeCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "SyncOutboxEntries", "ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "SyncOutboxEntries", "BusinessDatabaseName", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "SyncOutboxEntries", "SessionId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
            await TryAddColumnAsync(db, "SyncOutboxEntries", "UserId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
            await TryAddColumnAsync(db, "SyncOutboxEntries", "AcceptedRevision", "INTEGER NOT NULL DEFAULT 0");
            await TryAddColumnAsync(db, "SyncOutboxEntries", "AcceptedUpdatedAtUtc", "TEXT NULL");
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SyncOutboxEntries_MutationId\" ON \"SyncOutboxEntries\" (\"MutationId\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_SyncOutboxEntries_Status_PreparedAtUtc\" ON \"SyncOutboxEntries\" (\"Status\", \"PreparedAtUtc\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_SyncOutboxEntries_Scope_Status_PreparedAtUtc\" ON \"SyncOutboxEntries\" (\"TenantCode\", \"OfficeCode\", \"ResponsibleOfficeCode\", \"Status\", \"PreparedAtUtc\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_SyncOutboxEntries_SupersedeScope_Status_PreparedAtUtc\" ON \"SyncOutboxEntries\" (\"EntityName\", \"EntityId\", \"TenantCode\", \"OfficeCode\", \"ResponsibleOfficeCode\", \"BusinessDatabaseName\", \"DeviceId\", \"SessionId\", \"UserId\", \"Status\", \"PreparedAtUtc\");");
            await TryNormalizeDateTimeTextColumnAsync(db, "SyncOutboxEntries", "PreparedAtUtc");
            await TryNormalizeDateTimeTextColumnAsync(db, "SyncOutboxEntries", "SentAtUtc");
            await TryNormalizeDateTimeTextColumnAsync(db, "SyncOutboxEntries", "AcknowledgedAtUtc");
            await TryNormalizeDateTimeTextColumnAsync(db, "SyncOutboxEntries", "AcceptedUpdatedAtUtc");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(EnsureSyncOutboxTableAsync), ex);
        }
    }
}
