using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task EnsureOperationalRuntimeSchemaAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        await EnsureRuntimeColumnAsync(
            dbContext,
            tableName: "ConflictLogs",
            columnName: "Status",
            sqliteColumnDefinition: "TEXT NOT NULL DEFAULT 'Open'",
            postgresColumnDefinition: "text NOT NULL DEFAULT 'Open'",
            cancellationToken);
        await EnsureRuntimeColumnAsync(
            dbContext,
            tableName: "ConflictLogs",
            columnName: "ResolvedAtUtc",
            sqliteColumnDefinition: "TEXT NULL",
            postgresColumnDefinition: "timestamp with time zone NULL",
            cancellationToken);
        await EnsureRuntimeColumnAsync(
            dbContext,
            tableName: "ConflictLogs",
            columnName: "ResolutionNote",
            sqliteColumnDefinition: "TEXT NOT NULL DEFAULT ''",
            postgresColumnDefinition: "text NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureRuntimeColumnAsync(
            dbContext,
            tableName: "Invoices",
            columnName: "SourceWarehouseCode",
            sqliteColumnDefinition: $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.UsenetMainWarehouse}'",
            postgresColumnDefinition: $"text NOT NULL DEFAULT '{OfficeCodeCatalog.UsenetMainWarehouse}'",
            cancellationToken);

        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "ProcessedSyncMutations" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ProcessedSyncMutations" PRIMARY KEY,
                    "MutationId" TEXT COLLATE NOCASE NOT NULL DEFAULT '',
                    "DeviceId" TEXT NOT NULL DEFAULT '',
                    "EntityName" TEXT NOT NULL DEFAULT '',
                    "EntityId" TEXT NOT NULL DEFAULT '',
                    "ExpectedRevision" INTEGER NOT NULL DEFAULT 0,
                    "PayloadHash" TEXT NOT NULL DEFAULT '',
                    "ProcessedAtUtc" TEXT NOT NULL
                );
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "InventoryLedgerEntries" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_InventoryLedgerEntries" PRIMARY KEY,
                    "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                    "OfficeCode" TEXT NOT NULL DEFAULT 'ALL',
                    "ItemId" TEXT NOT NULL,
                    "WarehouseCode" TEXT NOT NULL DEFAULT '',
                    "SourceType" TEXT NOT NULL DEFAULT '',
                    "SourceDocumentId" TEXT NOT NULL,
                    "SourceLineId" TEXT NULL,
                    "QuantityDelta" TEXT NOT NULL DEFAULT '0',
                    "OccurredDate" TEXT NOT NULL,
                    "Note" TEXT NOT NULL DEFAULT '',
                    "CreatedAtUtc" TEXT NOT NULL
                );
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "RentalAssetAssignmentHistories" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_RentalAssetAssignmentHistories" PRIMARY KEY,
                    "AssetId" TEXT NOT NULL,
                    "BillingProfileId" TEXT NULL,
                    "CustomerId" TEXT NULL,
                    "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                    "OfficeCode" TEXT NOT NULL DEFAULT 'SHARED',
                    "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT 'USENET',
                    "CustomerName" TEXT NOT NULL DEFAULT '',
                    "InstallLocation" TEXT NOT NULL DEFAULT '',
                    "BillingProfileDisplay" TEXT NOT NULL DEFAULT '',
                    "ItemName" TEXT NOT NULL DEFAULT '',
                    "MachineNumber" TEXT NOT NULL DEFAULT '',
                    "ManagementNumber" TEXT NOT NULL DEFAULT '',
                    "MonthlyFee" TEXT NOT NULL DEFAULT '0',
                    "ContractStartDate" TEXT NULL,
                    "ContractEndDate" TEXT NULL,
                    "ChangeReason" TEXT NOT NULL DEFAULT '',
                    "IsCurrent" INTEGER NOT NULL DEFAULT 1,
                    "LinkedAtUtc" TEXT NOT NULL,
                    "UnlinkedAtUtc" TEXT NULL,
                    "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                    "CreatedAtUtc" TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z',
                    "UpdatedAtUtc" TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z',
                    "Revision" INTEGER NOT NULL DEFAULT 0
                );
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "ActiveEditSessions" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ActiveEditSessions" PRIMARY KEY,
                    "AppSessionId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    "Username" TEXT NOT NULL DEFAULT '',
                    "OfficeCode" TEXT NOT NULL DEFAULT '',
                    "TenantCode" TEXT NOT NULL DEFAULT 'USENET_GROUP',
                    "ScreenName" TEXT NOT NULL DEFAULT '',
                    "EntityType" TEXT NOT NULL DEFAULT '',
                    "EntityId" TEXT NOT NULL DEFAULT '',
                    "EntityDisplayName" TEXT NOT NULL DEFAULT '',
                    "MachineName" TEXT NOT NULL DEFAULT '',
                    "OpenedAtUtc" TEXT NOT NULL,
                    "LastHeartbeatUtc" TEXT NOT NULL,
                    "ExpiresAtUtc" TEXT NOT NULL
                );
                """,
                cancellationToken);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "ProcessedSyncMutations" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "MutationId" text NOT NULL DEFAULT '',
                    "DeviceId" text NOT NULL DEFAULT '',
                    "EntityName" text NOT NULL DEFAULT '',
                    "EntityId" text NOT NULL DEFAULT '',
                    "ExpectedRevision" bigint NOT NULL DEFAULT 0,
                    "PayloadHash" text NOT NULL DEFAULT '',
                    "ProcessedAtUtc" timestamp with time zone NOT NULL
                );
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "InventoryLedgerEntries" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                    "OfficeCode" text NOT NULL DEFAULT 'ALL',
                    "ItemId" uuid NOT NULL,
                    "WarehouseCode" text NOT NULL DEFAULT '',
                    "SourceType" text NOT NULL DEFAULT '',
                    "SourceDocumentId" uuid NOT NULL,
                    "SourceLineId" uuid NULL,
                    "QuantityDelta" numeric(18,2) NOT NULL DEFAULT 0,
                    "OccurredDate" date NOT NULL,
                    "Note" text NOT NULL DEFAULT '',
                    "CreatedAtUtc" timestamp with time zone NOT NULL
                );
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "RentalAssetAssignmentHistories" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "AssetId" uuid NOT NULL,
                    "BillingProfileId" uuid NULL,
                    "CustomerId" uuid NULL,
                    "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                    "OfficeCode" text NOT NULL DEFAULT 'SHARED',
                    "ResponsibleOfficeCode" text NOT NULL DEFAULT 'USENET',
                    "CustomerName" text NOT NULL DEFAULT '',
                    "InstallLocation" text NOT NULL DEFAULT '',
                    "BillingProfileDisplay" text NOT NULL DEFAULT '',
                    "ItemName" text NOT NULL DEFAULT '',
                    "MachineNumber" text NOT NULL DEFAULT '',
                    "ManagementNumber" text NOT NULL DEFAULT '',
                    "MonthlyFee" numeric(18,2) NOT NULL DEFAULT 0,
                    "ContractStartDate" date NULL,
                    "ContractEndDate" date NULL,
                    "ChangeReason" text NOT NULL DEFAULT '',
                    "IsCurrent" boolean NOT NULL DEFAULT true,
                    "LinkedAtUtc" timestamp with time zone NOT NULL,
                    "UnlinkedAtUtc" timestamp with time zone NULL,
                    "IsDeleted" boolean NOT NULL DEFAULT false,
                    "CreatedAtUtc" timestamp with time zone NOT NULL DEFAULT TIMESTAMPTZ '1970-01-01 00:00:00+00',
                    "UpdatedAtUtc" timestamp with time zone NOT NULL DEFAULT TIMESTAMPTZ '1970-01-01 00:00:00+00',
                    "Revision" bigint NOT NULL DEFAULT 0
                );
                """,
                cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "ActiveEditSessions" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "AppSessionId" uuid NOT NULL,
                    "Username" text NOT NULL DEFAULT '',
                    "OfficeCode" text NOT NULL DEFAULT '',
                    "TenantCode" text NOT NULL DEFAULT 'USENET_GROUP',
                    "ScreenName" text NOT NULL DEFAULT '',
                    "EntityType" text NOT NULL DEFAULT '',
                    "EntityId" text NOT NULL DEFAULT '',
                    "EntityDisplayName" text NOT NULL DEFAULT '',
                    "MachineName" text NOT NULL DEFAULT '',
                    "OpenedAtUtc" timestamp with time zone NOT NULL,
                    "LastHeartbeatUtc" timestamp with time zone NOT NULL,
                    "ExpiresAtUtc" timestamp with time zone NOT NULL
                );
                """,
                cancellationToken);
        }

        await EnsureRuntimeColumnAsync(dbContext, "ProcessedSyncMutations", "PayloadHash", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await using var canonicalizationTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await CanonicalizeLegacyMutationIdsAsync(dbContext, cancellationToken);
        var canonicalMutationIdIndexSql = providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            ? """
              CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProcessedSyncMutations_MutationId_TrimmedCanonical"
              ON "ProcessedSyncMutations" (lower(btrim("MutationId")));
              """
            : """
              CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProcessedSyncMutations_MutationId_TrimmedCanonical"
              ON "ProcessedSyncMutations" (lower(trim("MutationId")));
              """;
        await dbContext.Database.ExecuteSqlRawAsync(canonicalMutationIdIndexSql, cancellationToken);
        if (canonicalizationTransaction is not null)
            await canonicalizationTransaction.CommitAsync(cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "OfficeCode", "TEXT NOT NULL DEFAULT 'SHARED'", "text NOT NULL DEFAULT 'SHARED'", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "BillingProfileDisplay", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "ItemName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "MachineNumber", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "ManagementNumber", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "MonthlyFee", "TEXT NOT NULL DEFAULT '0'", "numeric(18,2) NOT NULL DEFAULT 0", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "ContractStartDate", "TEXT NULL", "date NULL", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "ContractEndDate", "TEXT NULL", "date NULL", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "ChangeReason", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "IsDeleted", "INTEGER NOT NULL DEFAULT 0", "boolean NOT NULL DEFAULT false", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "CreatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'", "timestamp with time zone NOT NULL DEFAULT TIMESTAMPTZ '1970-01-01 00:00:00+00'", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "UpdatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'", "timestamp with time zone NOT NULL DEFAULT TIMESTAMPTZ '1970-01-01 00:00:00+00'", cancellationToken);
        await EnsureRuntimeColumnAsync(dbContext, "RentalAssetAssignmentHistories", "Revision", "INTEGER NOT NULL DEFAULT 0", "bigint NOT NULL DEFAULT 0", cancellationToken);

        var conflictStatusBackfillSql = providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            ? """
              UPDATE "ConflictLogs"
              SET "Status" = 'Resolved'
              WHERE "ResolvedAtUtc" IS NOT NULL AND "Status" <> 'Resolved';
              UPDATE "ConflictLogs"
              SET "Status" = 'Open'
              WHERE "ResolvedAtUtc" IS NULL AND ("Status" IS NULL OR "Status" = '');
              """
            : """
              UPDATE "ConflictLogs"
              SET "Status" = 'Resolved'
              WHERE "ResolvedAtUtc" IS NOT NULL AND "Status" <> 'Resolved';
              UPDATE "ConflictLogs"
              SET "Status" = 'Open'
              WHERE "ResolvedAtUtc" IS NULL AND ("Status" IS NULL OR "Status" = '');
              """;

        foreach (var sql in new[]
                 {
                     "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ProcessedSyncMutations_MutationId\" ON \"ProcessedSyncMutations\" (\"MutationId\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_AuditLogs_EntityName_EntityId_CreatedAtUtc\" ON \"AuditLogs\" (\"EntityName\", \"EntityId\", \"CreatedAtUtc\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_ConflictLogs_Status_CreatedAtUtc\" ON \"ConflictLogs\" (\"Status\", \"CreatedAtUtc\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_ConflictLogs_EntityName_EntityId_Status\" ON \"ConflictLogs\" (\"EntityName\", \"EntityId\", \"Status\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_ConflictLogs_ResolvedAtUtc\" ON \"ConflictLogs\" (\"ResolvedAtUtc\");",
                     conflictStatusBackfillSql,
                     "CREATE INDEX IF NOT EXISTS \"IX_InventoryLedgerEntries_ItemId_OccurredDate\" ON \"InventoryLedgerEntries\" (\"ItemId\", \"OccurredDate\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_InventoryLedgerEntries_WarehouseCode\" ON \"InventoryLedgerEntries\" (\"WarehouseCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalAssetAssignmentHistories_AssetId_IsCurrent\" ON \"RentalAssetAssignmentHistories\" (\"AssetId\", \"IsCurrent\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalAssetAssignmentHistories_BillingProfileId\" ON \"RentalAssetAssignmentHistories\" (\"BillingProfileId\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalAssetAssignmentHistories_Revision\" ON \"RentalAssetAssignmentHistories\" (\"Revision\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_ActiveEditSessions_EntityType_EntityId_ExpiresAtUtc\" ON \"ActiveEditSessions\" (\"EntityType\", \"EntityId\", \"ExpiresAtUtc\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_ActiveEditSessions_LastHeartbeatUtc\" ON \"ActiveEditSessions\" (\"LastHeartbeatUtc\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_ActiveEditSessions_Username\" ON \"ActiveEditSessions\" (\"Username\");"
                 })
        {
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ignoredDbInitializerException)
        {
            TraceIgnoredDbInitializerException(ignoredDbInitializerException);
        }
        }
    }

    private static async Task CanonicalizeLegacyMutationIdsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var receipts = await dbContext.ProcessedSyncMutations
            .AsNoTracking()
            .Select(receipt => new LegacyMutationReceipt(
                receipt.Id,
                receipt.MutationId,
                receipt.ProcessedAtUtc))
            .ToListAsync(cancellationToken);
        var usedMutationIds = new HashSet<string>(
            receipts.Select(receipt => NormalizeLegacyMutationId(receipt.MutationId)),
            StringComparer.Ordinal);

        foreach (var group in receipts
                     .GroupBy(
                         receipt => NormalizeLegacyMutationId(receipt.MutationId),
                         StringComparer.Ordinal))
        {
            var orderedReceipts = group
                .OrderBy(receipt => receipt.ProcessedAtUtc)
                .ThenBy(receipt => receipt.Id)
                .ToList();
            var canonicalMutationId = group.Key;

            foreach (var duplicate in orderedReceipts.Skip(1))
            {
                var sentinelMutationId = CreateLegacyMutationSentinel(
                    duplicate,
                    usedMutationIds);
                await UpdateLegacyMutationIdAsync(
                    dbContext,
                    duplicate.Id,
                    sentinelMutationId,
                    cancellationToken);
                usedMutationIds.Add(sentinelMutationId);
            }

            // Keep the retained receipt's original spelling. Historical non-empty
            // payload hashes include the trimmed mutation-id casing, so rewriting
            // the id would make an identical post-upgrade replay look like a conflict.
            usedMutationIds.Add(canonicalMutationId);
        }
    }

    private static string NormalizeLegacyMutationId(string? mutationId)
        => string.IsNullOrWhiteSpace(mutationId)
            ? string.Empty
            : mutationId.Trim().ToLowerInvariant();

    private static string CreateLegacyMutationSentinel(
        LegacyMutationReceipt duplicate,
        ISet<string> usedMutationIds)
    {
        var baseSentinel =
            $"__legacy_duplicate__:{duplicate.Id:N}:{NormalizeLegacyMutationId(duplicate.MutationId)}";
        var sentinel = baseSentinel;
        var collisionSuffix = 0;
        while (usedMutationIds.Contains(sentinel))
        {
            collisionSuffix++;
            sentinel = $"{baseSentinel}:{collisionSuffix}";
        }

        return sentinel;
    }

    private static Task<int> UpdateLegacyMutationIdAsync(
        AppDbContext dbContext,
        Guid receiptId,
        string mutationId,
        CancellationToken cancellationToken)
        => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE "ProcessedSyncMutations"
             SET "MutationId" = {mutationId}
             WHERE "Id" = {receiptId};
             """,
            cancellationToken);

    private sealed record LegacyMutationReceipt(
        Guid Id,
        string MutationId,
        DateTime ProcessedAtUtc);

    private static async Task EnsureOperationalPermissionDefaultsAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var persistedUsers = await dbContext.Users
            .IgnoreQueryFilters()
            .Include(user => user.Permissions)
            .ToListAsync(cancellationToken);
        // 최초 설치에서는 seed 계정들이 아직 SaveChanges 전 Local에만 존재한다. DB 쿼리 결과만
        // 순회하면 일반/연수구/ITWORLD 계정에 기본 권한이 한 번도 부여되지 않는다.
        var users = dbContext.Users.Local
            .Concat(persistedUsers)
            .GroupBy(user => user.Id)
            .Select(group => group.First())
            .ToList();

        foreach (var user in users)
        {
            if (user.IsDeleted)
                continue;

            var desiredPermissions = ResolveDefaultOperationalPermissions(user);
            EnsurePermissions(user, desiredPermissions);
        }
    }

    private static IReadOnlyCollection<string> ResolveDefaultOperationalPermissions(UserAccount user)
    {
        if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            return AllPermissions();

        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(user.OfficeCode);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(user.TenantCode, normalizedOfficeCode);

        if (string.Equals(normalizedOfficeCode, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                PermissionNames.CustomerEdit,
                PermissionNames.ItemEdit,
                PermissionNames.InvoiceEdit,
                PermissionNames.PaymentEdit,
                PermissionNames.InventoryReset,
                PermissionNames.RentalProfileEdit,
                PermissionNames.RentalAssetEdit,
                PermissionNames.RentalViewAll,
                PermissionNames.RentalEditAll,
                PermissionNames.DeliveryViewAll,
                PermissionNames.DeliveryEdit,
                PermissionNames.RentalSettingsEdit,
                PermissionNames.RentalImport
            ];
        }

        if (string.Equals(normalizedTenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                PermissionNames.CustomerEdit,
                PermissionNames.ItemEdit,
                PermissionNames.InvoiceEdit,
                PermissionNames.PaymentEdit,
                PermissionNames.InventoryReset,
                PermissionNames.RentalProfileEdit,
                PermissionNames.RentalAssetEdit,
                PermissionNames.RentalViewAll,
                PermissionNames.RentalEditAll,
                PermissionNames.DeliveryViewAll,
                PermissionNames.RentalSettingsEdit,
                PermissionNames.RentalImport,
                PermissionNames.DeliveryEdit
            ];
        }

        return
        [
            PermissionNames.CustomerEdit,
            PermissionNames.ItemEdit,
            PermissionNames.InvoiceEdit,
            PermissionNames.PaymentEdit,
            PermissionNames.InventoryReset,
            PermissionNames.RentalProfileEdit,
            PermissionNames.RentalAssetEdit,
            PermissionNames.DeliveryEdit
        ];
    }

    private static async Task EnsureRuntimeColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string columnName,
        string sqliteColumnDefinition,
        string postgresColumnDefinition,
        CancellationToken cancellationToken)
    {
        if (await RuntimeColumnExistsAsync(dbContext, tableName, columnName, cancellationToken))
            return;

        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        var columnDefinition = providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            ? postgresColumnDefinition
            : sqliteColumnDefinition;

        var sql = "ALTER TABLE \"" + tableName + "\" ADD COLUMN \"" + columnName + "\" " + columnDefinition + ";";
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task<bool> RuntimeColumnExistsAsync(AppDbContext dbContext, string tableName, string columnName, CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = dbContext.Database.GetDbConnection();
            var providerName = dbContext.Database.ProviderName ?? string.Empty;
            await using var command = connection.CreateCommand();
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(reader[1]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }

            command.CommandText = "SELECT 1 FROM information_schema.columns WHERE table_name = @tableName AND column_name = @columnName LIMIT 1;";
            AddParameter(command, "tableName", tableName);
            AddParameter(command, "columnName", columnName);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            return scalar is not null && scalar != DBNull.Value;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
