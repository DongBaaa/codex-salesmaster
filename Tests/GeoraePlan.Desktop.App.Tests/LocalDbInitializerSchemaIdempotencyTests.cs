using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using 거래플랜.Desktop.App.Data;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalDbInitializerSchemaIdempotencyTests
{
    [Fact]
    public async Task TryAddColumnAsync_AddsMissingColumn_WhenColumnDoesNotExist()
    {
        var tempRoot = CreateTempRoot("add-missing-column");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            var interceptor = new SqlCommandCaptureInterceptor();
            await using var db = CreateDbContext(dbPath, interceptor);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"SchemaProbe\" (\"Id\" INTEGER NOT NULL);");
            interceptor.Clear();

            await InvokeTryAddColumnAsync(db, "SchemaProbe", "ProbeValue", "TEXT NOT NULL DEFAULT ''");

            var columns = await GetTableColumnsAsync(db, "SchemaProbe");
            Assert.Contains("ProbeValue", columns);
            Assert.Contains(interceptor.NonQueryCommands, sql => ContainsAddColumnCommand(sql, "SchemaProbe", "ProbeValue"));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task TryAddColumnAsync_SkipsExistingColumn_CaseInsensitively()
    {
        var tempRoot = CreateTempRoot("skip-existing-column-case");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            var interceptor = new SqlCommandCaptureInterceptor();
            await using var db = CreateDbContext(dbPath, interceptor);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"SchemaProbe\" (\"Id\" INTEGER NOT NULL, \"TenantCode\" TEXT NOT NULL DEFAULT '');");
            interceptor.Clear();

            await InvokeTryAddColumnAsync(db, "SchemaProbe", "tenantcode", "TEXT NOT NULL DEFAULT ''");

            var columns = await GetTableColumnsAsync(db, "SchemaProbe");
            Assert.Single(columns, column => string.Equals(column, "TenantCode", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(interceptor.NonQueryCommands, sql => ContainsAddColumnCommand(sql, "SchemaProbe", "tenantcode"));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task TryAddColumnAsync_IgnoresUnsafeIdentifiers()
    {
        var tempRoot = CreateTempRoot("ignore-unsafe-identifier");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            var interceptor = new SqlCommandCaptureInterceptor();
            await using var db = CreateDbContext(dbPath, interceptor);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"SchemaProbe\" (\"Id\" INTEGER NOT NULL);");
            interceptor.Clear();

            await InvokeTryAddColumnAsync(db, "SchemaProbe", "Bad-Column", "TEXT NOT NULL DEFAULT ''");

            var columns = await GetTableColumnsAsync(db, "SchemaProbe");
            Assert.DoesNotContain(columns, column => string.Equals(column, "Bad-Column", StringComparison.Ordinal));
            Assert.Empty(interceptor.NonQueryCommands);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_SecondRun_DoesNotAttemptDuplicateAddColumn()
    {
        var tempRoot = CreateTempRoot("initialize-twice");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using (var firstDb = CreateDbContext(dbPath))
            {
                await firstDb.Database.EnsureDeletedAsync();
                await LocalDbInitializer.InitializeAsync(firstDb);
            }

            var interceptor = new SqlCommandCaptureInterceptor();
            await using var secondDb = CreateDbContext(dbPath, interceptor);

            await LocalDbInitializer.InitializeAsync(secondDb);

            Assert.True(await secondDb.Settings.AsNoTracking().AnyAsync(setting => setting.Key == "Theme"));
            Assert.True(await secondDb.Settings.AsNoTracking().AnyAsync(setting => setting.Key == "LocalDb.SchemaMaintenanceVersion" && setting.Value == "2026-08-01.2"));
            Assert.True(await secondDb.Settings.AsNoTracking().AnyAsync(setting => setting.Key == "Migration.NormalizeRentalOfficeData.v1" && setting.Value == "1"));
            Assert.True(await secondDb.Settings.AsNoTracking().AnyAsync(setting => setting.Key == "Migration.NormalizeRentalAssetOfficeOwnership.v1" && setting.Value == "1"));
            Assert.DoesNotContain(interceptor.NonQueryCommands, IsAlterTableAddColumnCommand);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task DropLegacyCompanyProfileOfficeUniqueIndexesAsync_PreopenedEfConnection_RemainsUsableBySameContext()
    {
        var tempRoot = CreateTempRoot("company-profile-legacy-index-connection");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using var db = CreateDbContext(dbPath);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE UNIQUE INDEX "UX_CompanyProfiles_OfficeCode_Legacy"
                ON "CompanyProfiles" ("OfficeCode");
                """);
            await db.Database.OpenConnectionAsync();
            Assert.Equal(
                System.Data.ConnectionState.Open,
                db.Database.GetDbConnection().State);

            await InvokeDropLegacyCompanyProfileOfficeUniqueIndexesAsync(db);

            Assert.Equal(
                System.Data.ConnectionState.Open,
                db.Database.GetDbConnection().State);
            Assert.False(await HasIndexAsync(
                db,
                "UX_CompanyProfiles_OfficeCode_Legacy"));
            _ = await db.CompanyProfiles.AsNoTracking().CountAsync();

            const string settingKey =
                "Test.LocalDbInitializer.LegacyIndexConnectionOwnership";
            var setting = new LocalSetting { Key = settingKey, Value = "written" };
            db.Settings.Add(setting);
            await db.SaveChangesAsync();
            setting.Value = "updated";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.Equal("updated", await db.Settings.AsNoTracking()
                .Where(current => current.Key == settingKey)
                .Select(current => current.Value)
                .SingleAsync());
            Assert.Equal(
                System.Data.ConnectionState.Open,
                db.Database.GetDbConnection().State);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_LegacyInventoryTransferTombstoneConflictPrimaryKey_UpgradesLosslesslyAndRemainsIdempotent()
    {
        var tempRoot = CreateTempRoot(
            "inventory-transfer-tombstone-conflict-legacy-pk");
        var dbPath = Path.Combine(tempRoot, "local.db");
        var usenetTransferId = Guid.NewGuid();
        var itworldTransferId = Guid.NewGuid();

        try
        {
            await using var db = CreateDbContext(dbPath);
            await LocalDbInitializer.InitializeAsync(db);
            await ReplaceInventoryTransferTombstoneConflictsWithLegacyTableAsync(
                db,
                usenetTransferId,
                itworldTransferId);

            await LocalDbInitializer.InitializeAsync(db);

            Assert.Equal(
                new[] { "BusinessDatabaseName", "TransferId" },
                await GetPrimaryKeyColumnsAsync(
                    db,
                    "InventoryTransferTombstoneConflicts"));
            Assert.False(await HasTableAsync(
                db,
                "InventoryTransferTombstoneConflicts_LegacyPrimaryKey"));

            var migrated = await db.InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .OrderBy(conflict => conflict.BusinessDatabaseName)
                .ThenBy(conflict => conflict.TransferId)
                .ToListAsync();
            Assert.Equal(2, migrated.Count);

            var usenet = Assert.Single(
                migrated,
                conflict => conflict.TransferId == usenetTransferId);
            Assert.Equal("USENET", usenet.BusinessDatabaseName);
            Assert.Equal("USENET_GROUP", usenet.TenantCode);
            Assert.Equal("local-usenet", usenet.LocalSnapshotJson);
            Assert.Equal("server-usenet", usenet.ServerTombstoneJson);
            Assert.Equal("[\"mutation-usenet\"]", usenet.OutboxMutationIdsJson);
            Assert.Equal(
                "D:\\legacy\\usenet-receive-evidence.pdf",
                usenet.ArchivedReceiveEvidencePath);
            Assert.Equal(7, usenet.LocalRevision);
            Assert.Equal(12, usenet.ServerRevision);
            Assert.Equal("Unresolved", usenet.Status);
            Assert.Equal(string.Empty, usenet.Resolution);
            Assert.Null(usenet.RecoveredTransferId);

            var itworld = Assert.Single(
                migrated,
                conflict => conflict.TransferId == itworldTransferId);
            Assert.Equal("ITWORLD", itworld.BusinessDatabaseName);
            Assert.Equal("ITWORLD", itworld.TenantCode);
            Assert.Equal("local-itworld", itworld.LocalSnapshotJson);
            Assert.Equal("server-itworld", itworld.ServerTombstoneJson);
            Assert.Equal(
                "D:\\legacy\\itworld-receive-evidence.pdf",
                itworld.ArchivedReceiveEvidencePath);

            db.InventoryTransferTombstoneConflicts.Add(
                new LocalInventoryTransferTombstoneConflict
                {
                    TransferId = usenetTransferId,
                    BusinessDatabaseName = "ITWORLD",
                    TenantCode = "ITWORLD",
                    SourceOfficeCode = "ITWORLD",
                    TargetOfficeCode = "ITWORLD",
                    LocalSnapshotJson = "same-transfer-other-business",
                    ServerTombstoneJson = "server-other-business",
                    OutboxMutationIdsJson = "[]",
                    ServerUpdatedAtUtc = new DateTime(
                        2026,
                        8,
                        1,
                        2,
                        0,
                        0,
                        DateTimeKind.Utc),
                    Status = "Unresolved",
                    DetectedAtUtc = new DateTime(
                        2026,
                        8,
                        1,
                        2,
                        1,
                        0,
                        DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(
                        2026,
                        8,
                        1,
                        2,
                        1,
                        0,
                        DateTimeKind.Utc)
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await LocalDbInitializer.InitializeAsync(db);

            Assert.Equal(
                new[] { "BusinessDatabaseName", "TransferId" },
                await GetPrimaryKeyColumnsAsync(
                    db,
                    "InventoryTransferTombstoneConflicts"));
            Assert.Equal(
                3,
                await db.InventoryTransferTombstoneConflicts
                    .AsNoTracking()
                    .CountAsync());
            Assert.True(await db.InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .AnyAsync(conflict =>
                    conflict.TransferId == usenetTransferId &&
                    conflict.BusinessDatabaseName == "USENET" &&
                    conflict.LocalSnapshotJson == "local-usenet"));
            Assert.True(await db.InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .AnyAsync(conflict =>
                    conflict.TransferId == usenetTransferId &&
                    conflict.BusinessDatabaseName == "ITWORLD" &&
                    conflict.LocalSnapshotJson ==
                    "same-transfer-other-business"));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_BackfillsCatalogExtensionPending_AndRemainsIdempotent()
    {
        var tempRoot = CreateTempRoot("item-catalog-extension-pending");
        var dbPath = Path.Combine(tempRoot, "local.db");
        var meaningfulItemId = Guid.NewGuid();
        var emptyItemId = Guid.NewGuid();
        var deletedItemId = Guid.NewGuid();
        var newEmptyItemId = Guid.NewGuid();

        try
        {
            await using (var firstDb = CreateDbContext(dbPath))
            {
                await firstDb.Database.EnsureDeletedAsync();
                await LocalDbInitializer.InitializeAsync(firstDb);
                firstDb.Items.AddRange(
                    new LocalItem
                    {
                        Id = meaningfulItemId,
                        NameOriginal = "catalog extension meaningful",
                        NameMatchKey = "catalog extension meaningful",
                        BoxQuantity = 12m,
                        StorageLocation = "A-01",
                        LastPurchaseDate = new DateOnly(2026, 7, 1),
                        CatalogExtensionSyncPending = false
                    },
                    new LocalItem
                    {
                        Id = emptyItemId,
                        NameOriginal = "catalog extension empty",
                        NameMatchKey = "catalog extension empty",
                        CatalogExtensionSyncPending = false
                    },
                    new LocalItem
                    {
                        Id = deletedItemId,
                        NameOriginal = "catalog extension deleted",
                        NameMatchKey = "catalog extension deleted",
                        StorageLocation = "D-01",
                        IsDeleted = true,
                        CatalogExtensionSyncPending = false
                    });
                await firstDb.SaveChangesAsync();
                await firstDb.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Items"
                    DROP COLUMN "CatalogExtensionSyncPending";
                    """);
                await firstDb.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "Settings"
                    WHERE "Key" = 'Migration.BackfillItemCatalogExtensionPending.v1';
                    """);
            }

            await using (var migratedDb = CreateDbContext(dbPath))
            {
                await LocalDbInitializer.InitializeAsync(migratedDb);

                var columns = await GetTableColumnsAsync(migratedDb, "Items");
                Assert.Contains("CatalogExtensionSyncPending", columns);
                var migratedItems = await migratedDb.Items.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item =>
                        item.Id == meaningfulItemId ||
                        item.Id == emptyItemId ||
                        item.Id == deletedItemId)
                    .ToDictionaryAsync(item => item.Id);
                Assert.True(migratedItems[meaningfulItemId].CatalogExtensionSyncPending);
                var migratedEmpty = migratedItems[emptyItemId];
                Assert.False(migratedEmpty.CatalogExtensionSyncPending);
                Assert.False(migratedItems[deletedItemId].CatalogExtensionSyncPending);

                migratedDb.Items.Add(new LocalItem
                {
                    Id = newEmptyItemId,
                    NameOriginal = "new catalog extension empty",
                    NameMatchKey = "new catalog extension empty",
                    CatalogExtensionSyncPending = false
                });
                await migratedDb.SaveChangesAsync();
                migratedDb.ChangeTracker.Clear();
                Assert.False(await migratedDb.Items.IgnoreQueryFilters()
                    .Where(item => item.Id == newEmptyItemId)
                    .Select(item => item.CatalogExtensionSyncPending)
                    .SingleAsync());

                await migratedDb.Items.IgnoreQueryFilters()
                    .Where(item => item.Id == meaningfulItemId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.CatalogExtensionSyncPending, false));
                await LocalDbInitializer.InitializeAsync(migratedDb);

                Assert.False(await migratedDb.Items.IgnoreQueryFilters()
                    .Where(item => item.Id == meaningfulItemId)
                    .Select(item => item.CatalogExtensionSyncPending)
                    .SingleAsync());
                Assert.False(await migratedDb.Items.IgnoreQueryFilters()
                    .Where(item => item.Id == emptyItemId)
                    .Select(item => item.CatalogExtensionSyncPending)
                    .SingleAsync());
                Assert.False(await migratedDb.Items.IgnoreQueryFilters()
                    .Where(item => item.Id == deletedItemId)
                    .Select(item => item.CatalogExtensionSyncPending)
                    .SingleAsync());
                Assert.False(await migratedDb.Items.IgnoreQueryFilters()
                    .Where(item => item.Id == newEmptyItemId)
                    .Select(item => item.CatalogExtensionSyncPending)
                    .SingleAsync());
            }
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_SecondRun_PreservesCanonicalSeedTimestamps()
    {
        var tempRoot = CreateTempRoot("initialize-twice-seed-timestamps");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            SeedTimestampSnapshot before;
            await using (var firstDb = CreateDbContext(dbPath))
            {
                await firstDb.Database.EnsureDeletedAsync();
                await LocalDbInitializer.InitializeAsync(firstDb);
                before = await CaptureSeedTimestampsAsync(firstDb);
            }

            await Task.Delay(50);

            SeedTimestampSnapshot after;
            await using (var secondDb = CreateDbContext(dbPath))
            {
                await LocalDbInitializer.InitializeAsync(secondDb);
                after = await CaptureSeedTimestampsAsync(secondDb);
            }

            Assert.NotEmpty(before.CompanyProfiles);
            Assert.NotEmpty(before.RentalManagementCompanies);
            Assert.NotEmpty(before.Warehouses);
            Assert.Equal(before.CompanyProfiles, after.CompanyProfiles);
            Assert.Equal(before.RentalManagementCompanies, after.RentalManagementCompanies);
            Assert.Equal(before.Warehouses, after.Warehouses);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static LocalDbContext CreateDbContext(string dbPath, DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}");

        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        return new LocalDbContext(builder.Options);
    }

    private static async Task
        ReplaceInventoryTransferTombstoneConflictsWithLegacyTableAsync(
            LocalDbContext db,
            Guid usenetTransferId,
            Guid itworldTransferId)
    {
        await db.Database.ExecuteSqlRawAsync(
            "DROP TABLE \"InventoryTransferTombstoneConflicts\";");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "InventoryTransferTombstoneConflicts" (
                "TransferId" TEXT NOT NULL
                    CONSTRAINT "PK_InventoryTransferTombstoneConflicts"
                    PRIMARY KEY,
                "BusinessDatabaseName" TEXT NOT NULL DEFAULT '',
                "TenantCode" TEXT NOT NULL DEFAULT '',
                "SourceOfficeCode" TEXT NOT NULL DEFAULT '',
                "TargetOfficeCode" TEXT NOT NULL DEFAULT '',
                "LocalSnapshotJson" TEXT NULL,
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
                "RecoveredTransferId" TEXT NULL
            );
            CREATE INDEX "IX_InventoryTransferTombstoneConflicts_Status_UpdatedAtUtc"
            ON "InventoryTransferTombstoneConflicts" ("Status", "UpdatedAtUtc");
            CREATE INDEX "IX_InventoryTransferTombstoneConflicts_BusinessScope_Status"
            ON "InventoryTransferTombstoneConflicts" (
                "BusinessDatabaseName",
                "TenantCode",
                "SourceOfficeCode",
                "TargetOfficeCode",
                "Status");
            """);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "InventoryTransferTombstoneConflicts" (
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
                "RecoveredTransferId")
            VALUES
                ({0}, ' usenet_group ', 'USENET_GROUP', 'USENET', 'YEONSU', {1},
                 'server-usenet', '["mutation-usenet"]', 'D:\legacy\usenet-receive-evidence.pdf', 7, 12,
                 '2026-08-01 01:20:00', 'Unresolved',
                 '2026-08-01 01:21:00', '2026-08-01 01:21:00', NULL, '', NULL),
                ({2}, 'itworld', 'ITWORLD', 'ITWORLD', 'ITWORLD', 'local-itworld',
                 'server-itworld', '["mutation-itworld"]', 'D:\legacy\itworld-receive-evidence.pdf', 3, 8,
                 '2026-08-01 01:30:00', 'Unresolved',
                 '2026-08-01 01:31:00', '2026-08-01 01:31:00', NULL, '', NULL);
            """,
            usenetTransferId.ToString("D").ToUpperInvariant(),
            "local-usenet",
            itworldTransferId.ToString("D").ToUpperInvariant());
        db.ChangeTracker.Clear();
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

    private static async Task<bool> HasIndexAsync(
        LocalDbContext db,
        string indexName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM \"sqlite_master\" " +
            "WHERE \"type\" = 'index' AND \"name\" = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<SeedTimestampSnapshot> CaptureSeedTimestampsAsync(LocalDbContext db)
    {
        var companyProfiles = (await db.CompanyProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Select(profile => new { profile.Id, profile.UpdatedAtUtc })
                .ToListAsync())
            .Select(profile => new SeedTimestampEntry(profile.Id.ToString("D"), profile.UpdatedAtUtc))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        var rentalManagementCompanies = (await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Select(company => new { company.Id, company.UpdatedAtUtc })
                .ToListAsync())
            .Select(company => new SeedTimestampEntry(company.Id.ToString("D"), company.UpdatedAtUtc))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        var warehouses = (await db.Warehouses
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Select(warehouse => new { warehouse.Id, warehouse.UpdatedAtUtc })
                .ToListAsync())
            .Select(warehouse => new SeedTimestampEntry(warehouse.Id.ToString("D"), warehouse.UpdatedAtUtc))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        return new SeedTimestampSnapshot(
            companyProfiles,
            rentalManagementCompanies,
            warehouses);
    }

    private static async Task InvokeTryAddColumnAsync(LocalDbContext db, string tableName, string columnName, string definition)
    {
        var method = typeof(LocalDbInitializer).GetMethod(
            "TryAddColumnAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { db, tableName, columnName, definition }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task InvokeDropLegacyCompanyProfileOfficeUniqueIndexesAsync(
        LocalDbContext db)
    {
        var method = typeof(LocalDbInitializer).GetMethod(
            "DropLegacyCompanyProfileOfficeUniqueIndexesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = method!.Invoke(null, [db]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task<IReadOnlyList<string>> GetTableColumnsAsync(LocalDbContext db, string tableName)
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
            {
                columns.Add(reader["name"]?.ToString() ?? string.Empty);
            }

            return columns;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static bool ContainsAddColumnCommand(string sql, string tableName, string columnName)
        => sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase)
           && sql.Contains($"\"{tableName}\"", StringComparison.OrdinalIgnoreCase)
           && sql.Contains($"\"{columnName}\"", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlterTableAddColumnCommand(string sql)
        => sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase);

    private static string CreateTempRoot(string scenario)
    {
        var tempRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "localdb-schema-tests",
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
            // Ignore temp cleanup failures during tests.
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

    private sealed class SqlCommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> NonQueryCommands { get; } = [];

        public void Clear() => NonQueryCommands.Clear();

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            NonQueryCommands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            NonQueryCommands.Add(command.CommandText);
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

    private sealed record SeedTimestampSnapshot(
        IReadOnlyList<SeedTimestampEntry> CompanyProfiles,
        IReadOnlyList<SeedTimestampEntry> RentalManagementCompanies,
        IReadOnlyList<SeedTimestampEntry> Warehouses);

    private sealed record SeedTimestampEntry(string Id, DateTime UpdatedAtUtc);
}
