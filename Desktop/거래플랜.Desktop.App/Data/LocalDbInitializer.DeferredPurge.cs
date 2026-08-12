using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static async Task EnsureDeferredRecycleBinPurgeRecordsTableAsync(LocalDbContext db)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "DeferredRecycleBinPurgeRecords" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_DeferredRecycleBinPurgeRecords" PRIMARY KEY,
                    "BusinessDatabaseName" TEXT NOT NULL DEFAULT '',
                    "TenantCode" TEXT NOT NULL DEFAULT '',
                    "OfficeCode" TEXT NOT NULL DEFAULT '',
                    "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT '',
                    "Kind" TEXT NOT NULL DEFAULT '',
                    "EntityId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    "Revision" INTEGER NOT NULL DEFAULT 0,
                    "PurgedAtUtc" TEXT NOT NULL,
                    "AttemptCount" INTEGER NOT NULL DEFAULT 0,
                    "LastAttemptedAtUtc" TEXT NULL,
                    "LastErrorMessage" TEXT NOT NULL DEFAULT '',
                    "NextAttemptAtUtc" TEXT NULL,
                    "AppliedAtUtc" TEXT NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "UpdatedAtUtc" TEXT NOT NULL
                );
                """);
            InvalidateSchemaColumnCache("DeferredRecycleBinPurgeRecords");

            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "BusinessDatabaseName", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "TenantCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "OfficeCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "Kind", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "EntityId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "Revision", "INTEGER NOT NULL DEFAULT 0");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "PurgedAtUtc", $"TEXT NOT NULL DEFAULT '{FallbackUtcText}'");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "AttemptCount", "INTEGER NOT NULL DEFAULT 0");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "LastAttemptedAtUtc", "TEXT NULL");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "LastErrorMessage", "TEXT NOT NULL DEFAULT ''");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "NextAttemptAtUtc", "TEXT NULL");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "AppliedAtUtc", "TEXT NULL");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "CreatedAtUtc", $"TEXT NOT NULL DEFAULT '{FallbackUtcText}'");
            await TryAddColumnAsync(db, "DeferredRecycleBinPurgeRecords", "UpdatedAtUtc", $"TEXT NOT NULL DEFAULT '{FallbackUtcText}'");

            await TryNormalizeDateTimeTextColumnAsync(db, "DeferredRecycleBinPurgeRecords", "PurgedAtUtc");
            await TryNormalizeDateTimeTextColumnAsync(db, "DeferredRecycleBinPurgeRecords", "CreatedAtUtc");
            await TryNormalizeDateTimeTextColumnAsync(db, "DeferredRecycleBinPurgeRecords", "UpdatedAtUtc");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(EnsureDeferredRecycleBinPurgeRecordsTableAsync), ex);
        }
    }
}
