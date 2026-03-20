using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace 거래플랜.Server.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revisionClock = scope.ServiceProvider.GetRequiredService<RevisionClock>();
        var hostEnvironment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(DbInitializer).FullName ?? nameof(DbInitializer));
        var seedUsersOptions = scope.ServiceProvider.GetRequiredService<IOptions<SeedUsersOptions>>().Value;
        var connectionResolver = scope.ServiceProvider.GetRequiredService<ITenantDatabaseConnectionResolver>();

        await EnsureBusinessDatabaseSchemaAsync(dbContext, cancellationToken);

        var dedicatedBusinessConnections = connectionResolver.GetDedicatedBusinessConnections();
        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await EnsureDedicatedBusinessDatabaseExistsAsync(connectionInfo, logger, cancellationToken);
            await using var tenantDbContext = CreateDbContext(connectionInfo, revisionClock);
            await EnsureBusinessDatabaseSchemaAsync(tenantDbContext, cancellationToken);
        }

        var maxRevision = await GetMaxRevisionAsync(dbContext, cancellationToken);
        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await using var tenantDbContext = CreateDbContext(connectionInfo, revisionClock);
            maxRevision = Math.Max(maxRevision, await GetMaxRevisionAsync(tenantDbContext, cancellationToken));
        }

        revisionClock.Initialize(maxRevision);

        if (seedUsersOptions.EnableSeedUsers)
        {
            LogSeedUserWarnings(hostEnvironment, logger, seedUsersOptions);

            await EnsureSeedUserAsync(
                dbContext,
                logger,
                username: "admin",
                password: seedUsersOptions.AdminPassword,
                role: "Admin",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                scopeType: TenantScopeCatalog.ScopeAdmin,
                grantAllPermissions: true,
                updatePasswordIfExists: false,
                cancellationToken);

            await EnsureSeedUserAsync(
                dbContext,
                logger,
                username: "user",
                password: seedUsersOptions.UserPassword,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly,
                grantAllPermissions: false,
                updatePasswordIfExists: false,
                cancellationToken);

            await EnsureSeedUserAsync(
                dbContext,
                logger,
                username: "itw",
                password: seedUsersOptions.ItwPassword,
                role: "User",
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                scopeType: TenantScopeCatalog.ScopeTenantAll,
                grantAllPermissions: false,
                updatePasswordIfExists: seedUsersOptions.UpdateExistingItwPassword,
                cancellationToken);
        }
        else
        {
            logger.LogInformation("Seed user creation is disabled by configuration.");
        }

        await EnsureReferenceDataAsync(dbContext, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await using var tenantDbContext = CreateDbContext(connectionInfo, revisionClock);
            await SyncTenantConfigurationAsync(dbContext, tenantDbContext, cancellationToken);
            await EnsureReferenceDataAsync(tenantDbContext, cancellationToken);
            await tenantDbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Dedicated business database initialized for tenant {TenantCode}.", connectionInfo.TenantCode);
        }
    }

    private static async Task EnsureBusinessDatabaseSchemaAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseSchemaAsync(dbContext, cancellationToken);

        await EnsureCustomerContractsTableAsync(dbContext, cancellationToken);
        await EnsurePaymentAttachmentsTableAsync(dbContext, cancellationToken);
        await EnsureItemWarehouseStocksTableAsync(dbContext, cancellationToken);
        await EnsureCustomerTradeTypeColumnAsync(dbContext, cancellationToken);
        await EnsureUserOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureUserTenantScopeColumnsAsync(dbContext, cancellationToken);
        await EnsureTenantDefinitionsTableAsync(dbContext, cancellationToken);
        await EnsureTenantOfficeDefinitionsTableAsync(dbContext, cancellationToken);
        await EnsureDataSharingPoliciesTableAsync(dbContext, cancellationToken);
        await EnsureItemCatalogColumnsAsync(dbContext, cancellationToken);
        await EnsureCustomerMasterOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerMasterTenantCodeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureCustomerTenantCodeColumnAsync(dbContext, cancellationToken);
        await EnsureItemOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureItemTenantCodeColumnAsync(dbContext, cancellationToken);
        await EnsureInvoiceOfficeCodeColumnAsync(dbContext, cancellationToken);
        await EnsureInvoiceTenantCodeColumnAsync(dbContext, cancellationToken);
    }

    private static async Task EnsureReferenceDataAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultTenantConfigurationAsync(dbContext, cancellationToken);
        await EnsureDefaultCustomerCategoriesAsync(dbContext, cancellationToken);

        if (!await dbContext.Units.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            dbContext.Units.AddRange(
                new Unit { Name = "EA", IsActive = true },
                new Unit { Name = "SET", IsActive = true },
                new Unit { Name = "대", IsActive = true },
                new Unit { Name = "개", IsActive = true },
                new Unit { Name = "박스", IsActive = true });
        }

        if (!await dbContext.CompanyProfiles.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            dbContext.CompanyProfiles.Add(new CompanyProfile
            {
                TradeName = "기본 상호",
                Representative = "대표자",
                BusinessNumber = string.Empty,
                BusinessType = string.Empty,
                BusinessItem = string.Empty,
                Address = string.Empty,
                ContactNumber = string.Empty,
                Email = string.Empty,
                BankAccountText = "입금용 계좌번호를 입력하세요."
            });
        }
    }

    private static async Task SyncTenantConfigurationAsync(
        AppDbContext sourceDbContext,
        AppDbContext targetDbContext,
        CancellationToken cancellationToken)
    {
        var tenantDefinitions = await sourceDbContext.TenantDefinitions.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var targetTenantDefinitions = await targetDbContext.TenantDefinitions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var source in tenantDefinitions)
        {
            var target = targetTenantDefinitions.FirstOrDefault(current =>
                string.Equals(current.TenantCode, source.TenantCode, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                target = new TenantDefinition();
                targetDbContext.TenantDefinitions.Add(target);
                targetTenantDefinitions.Add(target);
            }

            target.TenantCode = source.TenantCode;
            target.DisplayName = source.DisplayName;
            target.StorageMode = source.StorageMode;
            target.Description = source.Description;
            target.IsActive = source.IsActive;
            target.IsDeleted = source.IsDeleted;
        }

        var officeDefinitions = await sourceDbContext.TenantOfficeDefinitions.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var targetOfficeDefinitions = await targetDbContext.TenantOfficeDefinitions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var source in officeDefinitions)
        {
            var target = targetOfficeDefinitions.FirstOrDefault(current =>
                string.Equals(current.OfficeCode, source.OfficeCode, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                target = new TenantOfficeDefinition();
                targetDbContext.TenantOfficeDefinitions.Add(target);
                targetOfficeDefinitions.Add(target);
            }

            target.TenantCode = source.TenantCode;
            target.OfficeCode = source.OfficeCode;
            target.DisplayName = source.DisplayName;
            target.IsHeadOffice = source.IsHeadOffice;
            target.IsActive = source.IsActive;
            target.IsDeleted = source.IsDeleted;
        }

        var sharingPolicies = await sourceDbContext.DataSharingPolicies.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var targetSharingPolicies = await targetDbContext.DataSharingPolicies.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var source in sharingPolicies)
        {
            var target = targetSharingPolicies.FirstOrDefault(current => current.Id == source.Id);
            if (target is null)
            {
                target = new DataSharingPolicy { Id = source.Id };
                targetDbContext.DataSharingPolicies.Add(target);
                targetSharingPolicies.Add(target);
            }

            target.SourceTenantCode = source.SourceTenantCode;
            target.SourceOfficeCode = source.SourceOfficeCode;
            target.TargetTenantCode = source.TargetTenantCode;
            target.TargetOfficeCode = source.TargetOfficeCode;
            target.ShareCustomers = source.ShareCustomers;
            target.ShareItems = source.ShareItems;
            target.ShareInvoices = source.ShareInvoices;
            target.SharePayments = source.SharePayments;
            target.ShareContracts = source.ShareContracts;
            target.ShareReports = source.ShareReports;
            target.ShareRentals = source.ShareRentals;
            target.ShareDeliveries = source.ShareDeliveries;
            target.AllowTargetWrite = source.AllowTargetWrite;
            target.Note = source.Note;
            target.IsActive = source.IsActive;
            target.IsDeleted = source.IsDeleted;
        }
    }

    private static AppDbContext CreateDbContext(TenantDatabaseConnectionInfo connectionInfo, RevisionClock revisionClock)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        if (connectionInfo.UseSqlite)
        {
            optionsBuilder.UseSqlite(connectionInfo.ConnectionString);
        }
        else
        {
            optionsBuilder.UseNpgsql(connectionInfo.ConnectionString);
        }

        return new AppDbContext(optionsBuilder.Options, SystemCurrentUserContext.Instance, revisionClock);
    }

    private static async Task EnsureDedicatedBusinessDatabaseExistsAsync(
        TenantDatabaseConnectionInfo connectionInfo,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (connectionInfo.UseSqlite || !connectionInfo.IsDedicatedBusinessDatabase)
            return;

        var targetBuilder = new NpgsqlConnectionStringBuilder(connectionInfo.ConnectionString);
        var targetDatabaseName = targetBuilder.Database?.Trim();
        if (string.IsNullOrWhiteSpace(targetDatabaseName))
            return;

        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionInfo.ConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT 1 FROM pg_database WHERE datname = @databaseName";
        existsCommand.Parameters.AddWithValue("databaseName", targetDatabaseName);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken) is not null;
        if (exists)
            return;

        var escapedDatabaseName = targetDatabaseName.Replace("\"", "\"\"");
        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE \"{escapedDatabaseName}\"";
        await createCommand.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation(
            "Created dedicated business database {DatabaseName} for tenant {TenantCode}.",
            targetDatabaseName,
            connectionInfo.TenantCode);
    }

    private static async Task EnsureDatabaseSchemaAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsSqlite())
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        var migrationsAssembly = dbContext.Database.GetService<IMigrationsAssembly>();
        if (migrationsAssembly.Migrations.Count > 0)
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
            return;
        }

        var relationalDatabaseCreator = dbContext.Database.GetService<IRelationalDatabaseCreator>();
        if (!await relationalDatabaseCreator.ExistsAsync(cancellationToken))
        {
            await relationalDatabaseCreator.CreateAsync(cancellationToken);
        }

        if (!await HasTableAsync(dbContext, "Users", cancellationToken))
        {
            await relationalDatabaseCreator.CreateTablesAsync(cancellationToken);
        }
    }

    private static async Task<bool> HasTableAsync(
        AppDbContext dbContext,
        string tableName,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            var providerName = dbContext.Database.ProviderName ?? string.Empty;

            command.CommandText = providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                ? "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName LIMIT 1;"
                : "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @tableName LIMIT 1;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tableName";
            parameter.DbType = DbType.String;
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is not null && result != DBNull.Value;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureTenantDefinitionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantDefinitions" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "TenantCode" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL DEFAULT '',
                        "StorageMode" TEXT NOT NULL DEFAULT 'SharedBusinessDatabase',
                        "Description" TEXT NOT NULL DEFAULT '',
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantDefinitions_TenantCode\" ON \"TenantDefinitions\" (\"TenantCode\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantDefinitions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "TenantCode" text NOT NULL,
                        "DisplayName" text NOT NULL DEFAULT '',
                        "StorageMode" text NOT NULL DEFAULT 'SharedBusinessDatabase',
                        "Description" text NOT NULL DEFAULT '',
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantDefinitions_TenantCode\" ON \"TenantDefinitions\" (\"TenantCode\");",
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureTenantOfficeDefinitionsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantOfficeDefinitions" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "TenantCode" TEXT NOT NULL,
                        "OfficeCode" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL DEFAULT '',
                        "IsHeadOffice" INTEGER NOT NULL DEFAULT 0,
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"OfficeCode\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_TenantCode_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"TenantCode\", \"OfficeCode\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "TenantOfficeDefinitions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "TenantCode" text NOT NULL,
                        "OfficeCode" text NOT NULL,
                        "DisplayName" text NOT NULL DEFAULT '',
                        "IsHeadOffice" boolean NOT NULL DEFAULT FALSE,
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"OfficeCode\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TenantOfficeDefinitions_TenantCode_OfficeCode\" ON \"TenantOfficeDefinitions\" (\"TenantCode\", \"OfficeCode\");",
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureDataSharingPoliciesTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "DataSharingPolicies" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0,
                        "SourceTenantCode" TEXT NOT NULL,
                        "SourceOfficeCode" TEXT NOT NULL,
                        "TargetTenantCode" TEXT NOT NULL,
                        "TargetOfficeCode" TEXT NOT NULL,
                        "ShareCustomers" INTEGER NOT NULL DEFAULT 1,
                        "ShareItems" INTEGER NOT NULL DEFAULT 0,
                        "ShareInvoices" INTEGER NOT NULL DEFAULT 1,
                        "SharePayments" INTEGER NOT NULL DEFAULT 1,
                        "ShareContracts" INTEGER NOT NULL DEFAULT 1,
                        "ShareReports" INTEGER NOT NULL DEFAULT 1,
                        "ShareRentals" INTEGER NOT NULL DEFAULT 1,
                        "ShareDeliveries" INTEGER NOT NULL DEFAULT 1,
                        "AllowTargetWrite" INTEGER NOT NULL DEFAULT 0,
                        "Note" TEXT NOT NULL DEFAULT '',
                        "IsActive" INTEGER NOT NULL DEFAULT 1
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE INDEX IF NOT EXISTS "IX_DataSharingPolicies_SourceTarget"
                    ON "DataSharingPolicies" ("SourceTenantCode", "SourceOfficeCode", "TargetTenantCode", "TargetOfficeCode");
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "DataSharingPolicies" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE,
                        "CreatedAtUtc" timestamptz NOT NULL,
                        "UpdatedAtUtc" timestamptz NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0,
                        "SourceTenantCode" text NOT NULL,
                        "SourceOfficeCode" text NOT NULL,
                        "TargetTenantCode" text NOT NULL,
                        "TargetOfficeCode" text NOT NULL,
                        "ShareCustomers" boolean NOT NULL DEFAULT TRUE,
                        "ShareItems" boolean NOT NULL DEFAULT FALSE,
                        "ShareInvoices" boolean NOT NULL DEFAULT TRUE,
                        "SharePayments" boolean NOT NULL DEFAULT TRUE,
                        "ShareContracts" boolean NOT NULL DEFAULT TRUE,
                        "ShareReports" boolean NOT NULL DEFAULT TRUE,
                        "ShareRentals" boolean NOT NULL DEFAULT TRUE,
                        "ShareDeliveries" boolean NOT NULL DEFAULT TRUE,
                        "AllowTargetWrite" boolean NOT NULL DEFAULT FALSE,
                        "Note" text NOT NULL DEFAULT '',
                        "IsActive" boolean NOT NULL DEFAULT TRUE
                    );
                    """,
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE INDEX IF NOT EXISTS "IX_DataSharingPolicies_SourceTarget"
                    ON "DataSharingPolicies" ("SourceTenantCode", "SourceOfficeCode", "TargetTenantCode", "TargetOfficeCode");
                    """,
                    cancellationToken);
            }
        }
        catch
        {
            // Table may already exist or provider may not support IF NOT EXISTS in the same way.
        }
    }

    private static async Task EnsureDefaultTenantConfigurationAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await UpsertTenantDefinitionAsync(
            dbContext,
            tenantCode: TenantScopeCatalog.UsenetGroup,
            displayName: "USENET / 연수구",
            storageMode: TenantScopeCatalog.StorageSharedDatabase,
            description: "유즈넷과 연수구는 같은 업체 권역/공용 업무 DB를 사용합니다.",
            cancellationToken);

        await UpsertTenantDefinitionAsync(
            dbContext,
            tenantCode: TenantScopeCatalog.Itworld,
            displayName: "ITWORLD",
            storageMode: TenantScopeCatalog.StorageDedicatedDatabase,
            description: "아이티월드는 별도 업체 권역으로 분리해 운영합니다.",
            cancellationToken);

        await UpsertTenantOfficeDefinitionAsync(dbContext, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, "유즈넷", true, cancellationToken);
        await UpsertTenantOfficeDefinitionAsync(dbContext, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu, "연수구", false, cancellationToken);
        await UpsertTenantOfficeDefinitionAsync(dbContext, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "아이티월드", true, cancellationToken);

        await UpsertDataSharingPolicyAsync(
            dbContext,
            sourceTenantCode: TenantScopeCatalog.UsenetGroup,
            sourceOfficeCode: OfficeCodeCatalog.Yeonsu,
            targetTenantCode: TenantScopeCatalog.UsenetGroup,
            targetOfficeCode: OfficeCodeCatalog.Usenet,
            shareCustomers: true,
            shareItems: true,
            shareInvoices: true,
            sharePayments: true,
            shareContracts: true,
            shareReports: true,
            shareRentals: true,
            shareDeliveries: true,
            allowTargetWrite: false,
            note: "연수구에서 등록/수정한 데이터는 유즈넷 상급권한 계정에서 조회할 수 있습니다.",
            cancellationToken);
    }

    private static async Task EnsureCustomerTradeTypeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN \"TradeType\" TEXT NOT NULL DEFAULT '매출';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Customers\" ADD COLUMN IF NOT EXISTS \"TradeType\" text NOT NULL DEFAULT '매출';",
                    cancellationToken);
            }
        }
        catch
        {
            // Existing databases may already contain the column.
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Customers"
                SET "TradeType" = CASE
                    WHEN COALESCE(TRIM("TradeType"), '') IN ('', '판매', '매출처', '매출') THEN '매출'
                    WHEN COALESCE(TRIM("TradeType"), '') IN ('매입처', '매입') THEN '매입'
                    WHEN COALESCE(TRIM("TradeType"), '') IN ('판매/매입', '매출/매입', '매입/매출') THEN '매출/매입'
                    ELSE TRIM("TradeType")
                END;
                """,
                cancellationToken);
        }
        catch
        {
            // Ignore if legacy schema does not yet expose the column for some reason.
        }
    }

    private static async Task<long> GetMaxRevisionAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var revisions = new List<long>
        {
            await dbContext.Users.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CompanyProfiles.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.TenantDefinitions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.TenantOfficeDefinitions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.DataSharingPolicies.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Units.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CustomerCategories.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CustomerMasters.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Customers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.CustomerContracts.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Items.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Invoices.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0,
            await dbContext.Payments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0
        };

        return revisions.Max();
    }

    private static async Task EnsureUserOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN \"OfficeCode\" TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"OfficeCode\" text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Users"
                SET "OfficeCode" = CASE
                    WHEN COALESCE(TRIM("OfficeCode"), '') = ''
                        THEN CASE
                            WHEN LOWER(COALESCE(TRIM("Username"), '')) = 'itw' THEN 'ITWORLD'
                            WHEN LOWER(COALESCE(TRIM("Role"), '')) = 'user' THEN 'YEONSU'
                            ELSE 'USENET'
                        END
                    WHEN UPPER(TRIM("OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("OfficeCode") = '유즈넷'
                        THEN 'USENET'
                    WHEN UPPER(TRIM("OfficeCode")) = 'ITWORLD' OR TRIM("OfficeCode") = '아이티월드'
                        THEN 'ITWORLD'
                    WHEN UPPER(TRIM("OfficeCode")) = 'YEONSU' OR TRIM("OfficeCode") IN ('연수구', '연수구 사무실')
                        THEN 'YEONSU'
                    ELSE CASE
                        WHEN LOWER(COALESCE(TRIM("Username"), '')) = 'itw' THEN 'ITWORLD'
                        WHEN LOWER(COALESCE(TRIM("Role"), '')) = 'user' THEN 'YEONSU'
                        ELSE 'USENET'
                    END
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureUserTenantScopeColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN \"TenantCode\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN \"ScopeType\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.ScopeOfficeOnly}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"TenantCode\" text NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"ScopeType\" text NOT NULL DEFAULT '{TenantScopeCatalog.ScopeOfficeOnly}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "Users"
                SET "TenantCode" = CASE
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.Itworld}'
                    ELSE '{TenantScopeCatalog.UsenetGroup}'
                END,
                    "ScopeType" = CASE
                    WHEN LOWER(COALESCE(TRIM("Role"), '')) = 'admin' THEN '{TenantScopeCatalog.ScopeAdmin}'
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.ScopeTenantAll}'
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Usenet}' THEN '{TenantScopeCatalog.ScopeTenantAll}'
                    ELSE '{TenantScopeCatalog.ScopeOfficeOnly}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Users_TenantCode_OfficeCode\" ON \"Users\" (\"TenantCode\", \"OfficeCode\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureCustomerMasterOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureOfficeScopeColumnAsync(
            dbContext,
            tableName: "CustomerMasters",
            indexName: "IX_CustomerMasters_OfficeCode",
            cancellationToken);
    }

    private static async Task EnsureCustomerMasterTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureTenantScopeColumnAsync(
            dbContext,
            tableName: "CustomerMasters",
            indexName: "IX_CustomerMasters_TenantCode",
            cancellationToken);
    }

    private static async Task EnsureCustomerOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureOfficeScopeColumnAsync(
            dbContext,
            tableName: "Customers",
            indexName: "IX_Customers_OfficeCode",
            cancellationToken);
    }

    private static async Task EnsureCustomerTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureTenantScopeColumnAsync(
            dbContext,
            tableName: "Customers",
            indexName: "IX_Customers_TenantCode",
            cancellationToken);
    }

    private static async Task EnsureItemOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureOfficeScopeColumnAsync(
            dbContext,
            tableName: "Items",
            indexName: "IX_Items_OfficeCode",
            cancellationToken);
    }

    private static async Task EnsureItemTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureTenantScopeColumnAsync(
            dbContext,
            tableName: "Items",
            indexName: "IX_Items_TenantCode",
            cancellationToken);
    }

    private static async Task EnsureInvoiceOfficeCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

#pragma warning disable EF1002
        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Invoices" ADD COLUMN "OfficeCode" TEXT NOT NULL DEFAULT '';
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Invoices" ADD COLUMN IF NOT EXISTS "OfficeCode" text NOT NULL DEFAULT '';
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"""
                    UPDATE "Invoices"
                    SET "OfficeCode" = CASE
                        WHEN COALESCE(TRIM("OfficeCode"), '') = '' THEN COALESCE((
                            SELECT CASE
                                WHEN COALESCE(TRIM("Customers"."OfficeCode"), '') = '' THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) IN ('ALL', 'SHARED') OR TRIM("Customers"."OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("Customers"."OfficeCode") = '유즈넷' THEN 'USENET'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) = 'ITWORLD' OR TRIM("Customers"."OfficeCode") = '아이티월드' THEN 'ITWORLD'
                                WHEN UPPER(TRIM("Customers"."OfficeCode")) = 'YEONSU' OR TRIM("Customers"."OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                                ELSE '{OfficeCodeCatalog.Shared}'
                            END
                            FROM "Customers"
                            WHERE "Customers"."Id" = "Invoices"."CustomerId"
                        ), '{OfficeCodeCatalog.Shared}')
                        WHEN UPPER(TRIM("OfficeCode")) IN ('ALL', 'SHARED') OR TRIM("OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                        WHEN UPPER(TRIM("OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("OfficeCode") = '유즈넷' THEN 'USENET'
                        WHEN UPPER(TRIM("OfficeCode")) = 'ITWORLD' OR TRIM("OfficeCode") = '아이티월드' THEN 'ITWORLD'
                        WHEN UPPER(TRIM("OfficeCode")) = 'YEONSU' OR TRIM("OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                        ELSE '{OfficeCodeCatalog.Shared}'
                    END;
                    """,
                    cancellationToken);
            }
            else
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"""
                    UPDATE "Invoices" AS invoice
                    SET "OfficeCode" = CASE
                        WHEN COALESCE(TRIM(invoice."OfficeCode"), '') = '' THEN COALESCE(
                            CASE
                                WHEN COALESCE(TRIM(customer."OfficeCode"), '') = '' THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM(customer."OfficeCode")) IN ('ALL', 'SHARED') OR TRIM(customer."OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                                WHEN UPPER(TRIM(customer."OfficeCode")) IN ('USENET', 'UZNET') OR TRIM(customer."OfficeCode") = '유즈넷' THEN 'USENET'
                                WHEN UPPER(TRIM(customer."OfficeCode")) = 'ITWORLD' OR TRIM(customer."OfficeCode") = '아이티월드' THEN 'ITWORLD'
                                WHEN UPPER(TRIM(customer."OfficeCode")) = 'YEONSU' OR TRIM(customer."OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                                ELSE '{OfficeCodeCatalog.Shared}'
                            END,
                            '{OfficeCodeCatalog.Shared}')
                        WHEN UPPER(TRIM(invoice."OfficeCode")) IN ('ALL', 'SHARED') OR TRIM(invoice."OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                        WHEN UPPER(TRIM(invoice."OfficeCode")) IN ('USENET', 'UZNET') OR TRIM(invoice."OfficeCode") = '유즈넷' THEN 'USENET'
                        WHEN UPPER(TRIM(invoice."OfficeCode")) = 'ITWORLD' OR TRIM(invoice."OfficeCode") = '아이티월드' THEN 'ITWORLD'
                        WHEN UPPER(TRIM(invoice."OfficeCode")) = 'YEONSU' OR TRIM(invoice."OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                        ELSE '{OfficeCodeCatalog.Shared}'
                    END
                    FROM "Customers" AS customer
                    WHERE customer."Id" = invoice."CustomerId";

                    UPDATE "Invoices"
                    SET "OfficeCode" = '{OfficeCodeCatalog.Shared}'
                    WHERE COALESCE(TRIM("OfficeCode"), '') = '';
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS "IX_Invoices_OfficeCode" ON "Invoices" ("OfficeCode");
                """,
                cancellationToken);
        }
        catch
        {
        }
#pragma warning restore EF1002
    }

    private static async Task EnsureInvoiceTenantCodeColumnAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Invoices\" ADD COLUMN \"TenantCode\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"Invoices\" ADD COLUMN IF NOT EXISTS \"TenantCode\" text NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "Invoices"
                SET "TenantCode" = CASE
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.Itworld}'
                    ELSE '{TenantScopeCatalog.UsenetGroup}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Invoices_TenantCode\" ON \"Invoices\" (\"TenantCode\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureOfficeScopeColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

#pragma warning disable EF1002
        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN \"OfficeCode\" TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Shared}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"OfficeCode\" text NOT NULL DEFAULT '{OfficeCodeCatalog.Shared}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "{tableName}"
                SET "OfficeCode" = CASE
                    WHEN COALESCE(TRIM("OfficeCode"), '') = '' THEN '{OfficeCodeCatalog.Shared}'
                    WHEN UPPER(TRIM("OfficeCode")) IN ('ALL', 'SHARED') OR TRIM("OfficeCode") IN ('공용', '전체') THEN '{OfficeCodeCatalog.Shared}'
                    WHEN UPPER(TRIM("OfficeCode")) IN ('USENET', 'UZNET') OR TRIM("OfficeCode") = '유즈넷' THEN 'USENET'
                    WHEN UPPER(TRIM("OfficeCode")) = 'ITWORLD' OR TRIM("OfficeCode") = '아이티월드' THEN 'ITWORLD'
                    WHEN UPPER(TRIM("OfficeCode")) = 'YEONSU' OR TRIM("OfficeCode") IN ('연수구', '연수구 사무실') THEN 'YEONSU'
                    ELSE '{OfficeCodeCatalog.Shared}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" (\"OfficeCode\");",
                cancellationToken);
        }
        catch
        {
        }
#pragma warning restore EF1002
    }

    private static async Task EnsureTenantScopeColumnAsync(
        AppDbContext dbContext,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

#pragma warning disable EF1002
        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN \"TenantCode\" TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{tableName}\" ADD COLUMN IF NOT EXISTS \"TenantCode\" text NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}';",
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"""
                UPDATE "{tableName}"
                SET "TenantCode" = CASE
                    WHEN UPPER(TRIM(COALESCE("OfficeCode", ''))) = '{OfficeCodeCatalog.Itworld}' THEN '{TenantScopeCatalog.Itworld}'
                    ELSE '{TenantScopeCatalog.UsenetGroup}'
                END;
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" (\"TenantCode\");",
                cancellationToken);
        }
        catch
        {
        }
#pragma warning restore EF1002
    }

    private static async Task EnsureItemCatalogColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        var columns = new (string Name, string SqliteDefinition, string PostgresDefinition)[]
        {
            ("CategoryName", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''"),
            ("CurrentStock", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("SafetyStock", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PurchasePrice", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("SalePrice", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("RetailPrice", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PriceGradeA", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PriceGradeB", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("PriceGradeC", "REAL NOT NULL DEFAULT 0", "numeric(18,2) NOT NULL DEFAULT 0"),
            ("SimpleMemo", "TEXT NOT NULL DEFAULT ''", "text NOT NULL DEFAULT ''")
        };

        foreach (var (name, sqliteDefinition, postgresDefinition) in columns)
        {
            try
            {
#pragma warning disable EF1002
                if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE \"Items\" ADD COLUMN \"{name}\" {sqliteDefinition};",
                        cancellationToken);
                }
                else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE \"Items\" ADD COLUMN IF NOT EXISTS \"{name}\" {postgresDefinition};",
                        cancellationToken);
                }
#pragma warning restore EF1002
            }
            catch
            {
                // Existing databases may already contain the column.
            }
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Items"
                SET "CategoryName" = COALESCE("CategoryName", ''),
                    "SimpleMemo" = COALESCE("SimpleMemo", '');
                """,
                cancellationToken);
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Items_CategoryName\" ON \"Items\" (\"CategoryName\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureCustomerContractsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "CustomerContracts" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_CustomerContracts" PRIMARY KEY,
                        "CustomerId" TEXT NOT NULL,
                        "ContractType" TEXT NOT NULL DEFAULT '거래계약서',
                        "FileName" TEXT NOT NULL DEFAULT '',
                        "MimeType" TEXT NOT NULL DEFAULT 'application/pdf',
                        "FileSize" INTEGER NOT NULL DEFAULT 0,
                        "FileHash" TEXT NOT NULL DEFAULT '',
                        "Description" TEXT NOT NULL DEFAULT '',
                        "SignedDate" TEXT NULL,
                        "ExpireDate" TEXT NULL,
                        "IsPrimary" INTEGER NOT NULL DEFAULT 0,
                        "UploadedByUsername" TEXT NOT NULL DEFAULT '',
                        "UploadedAtUtc" TEXT NOT NULL,
                        "FileContent" BLOB NOT NULL DEFAULT X'',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "CustomerContracts" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "CustomerId" uuid NOT NULL,
                        "ContractType" text NOT NULL DEFAULT '거래계약서',
                        "FileName" text NOT NULL DEFAULT '',
                        "MimeType" text NOT NULL DEFAULT 'application/pdf',
                        "FileSize" bigint NOT NULL DEFAULT 0,
                        "FileHash" text NOT NULL DEFAULT '',
                        "Description" text NOT NULL DEFAULT '',
                        "SignedDate" date NULL,
                        "ExpireDate" date NULL,
                        "IsPrimary" boolean NOT NULL DEFAULT false,
                        "UploadedByUsername" text NOT NULL DEFAULT '',
                        "UploadedAtUtc" timestamp with time zone NOT NULL,
                        "FileContent" bytea NOT NULL DEFAULT ''::bytea,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId\" ON \"CustomerContracts\" (\"CustomerId\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId_IsPrimary\" ON \"CustomerContracts\" (\"CustomerId\", \"IsPrimary\");",
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId\" ON \"CustomerContracts\" (\"CustomerId\");",
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId_IsPrimary\" ON \"CustomerContracts\" (\"CustomerId\", \"IsPrimary\");",
                    cancellationToken);
            }
        }
        catch
        {
        }
    }

    private static async Task EnsurePaymentAttachmentsTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "PaymentAttachments" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PaymentAttachments" PRIMARY KEY,
                        "PaymentId" TEXT NOT NULL,
                        "AttachmentType" TEXT NOT NULL DEFAULT '내역첨부',
                        "FileName" TEXT NOT NULL DEFAULT '',
                        "MimeType" TEXT NOT NULL DEFAULT '',
                        "FileSize" INTEGER NOT NULL DEFAULT 0,
                        "FileHash" TEXT NOT NULL DEFAULT '',
                        "Description" TEXT NOT NULL DEFAULT '',
                        "UploadedAtUtc" TEXT NOT NULL,
                        "FileContent" BLOB NOT NULL DEFAULT X'',
                        "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        "Revision" INTEGER NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "PaymentAttachments" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "PaymentId" uuid NOT NULL,
                        "AttachmentType" text NOT NULL DEFAULT '내역첨부',
                        "FileName" text NOT NULL DEFAULT '',
                        "MimeType" text NOT NULL DEFAULT '',
                        "FileSize" bigint NOT NULL DEFAULT 0,
                        "FileHash" text NOT NULL DEFAULT '',
                        "Description" text NOT NULL DEFAULT '',
                        "UploadedAtUtc" timestamp with time zone NOT NULL,
                        "FileContent" bytea NOT NULL DEFAULT ''::bytea,
                        "IsDeleted" boolean NOT NULL DEFAULT false,
                        "CreatedAtUtc" timestamp with time zone NOT NULL,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        "Revision" bigint NOT NULL DEFAULT 0
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_PaymentAttachments_PaymentId\" ON \"PaymentAttachments\" (\"PaymentId\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task EnsureItemWarehouseStocksTableAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;

        try
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ItemWarehouseStocks" (
                        "ItemId" TEXT NOT NULL,
                        "WarehouseCode" TEXT NOT NULL,
                        "Quantity" REAL NOT NULL DEFAULT 0,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        CONSTRAINT "PK_ItemWarehouseStocks" PRIMARY KEY ("ItemId", "WarehouseCode")
                    );
                    """,
                    cancellationToken);
            }
            else if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ItemWarehouseStocks" (
                        "ItemId" uuid NOT NULL,
                        "WarehouseCode" text NOT NULL,
                        "Quantity" numeric(18,2) NOT NULL DEFAULT 0,
                        "UpdatedAtUtc" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_ItemWarehouseStocks" PRIMARY KEY ("ItemId", "WarehouseCode")
                    );
                    """,
                    cancellationToken);
            }
        }
        catch
        {
        }

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_ItemWarehouseStocks_WarehouseCode\" ON \"ItemWarehouseStocks\" (\"WarehouseCode\");",
                cancellationToken);
        }
        catch
        {
        }
    }

    private static string[] AllPermissions() =>
    [
        PermissionNames.CompanyProfileEdit,
        PermissionNames.AmountViewSales,
        PermissionNames.AmountViewPurchase,
        PermissionNames.SettingsEdit,
        PermissionNames.DataBackupRestore,
        PermissionNames.RentalViewAll,
        PermissionNames.RentalEditAll,
        PermissionNames.RentalSettingsEdit,
        PermissionNames.RentalImport
    ];

    private static async Task UpsertTenantDefinitionAsync(
        AppDbContext dbContext,
        string tenantCode,
        string displayName,
        string storageMode,
        string description,
        CancellationToken cancellationToken)
    {
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode);
        var normalizedStorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(storageMode);
        var entity = dbContext.TenantDefinitions.Local.FirstOrDefault(current =>
                         string.Equals(current.TenantCode, normalizedTenantCode, StringComparison.OrdinalIgnoreCase))
                     ?? await dbContext.TenantDefinitions.IgnoreQueryFilters()
                         .FirstOrDefaultAsync(current => current.TenantCode == normalizedTenantCode, cancellationToken);

        if (entity is null)
        {
            entity = new TenantDefinition
            {
                TenantCode = normalizedTenantCode
            };
            dbContext.TenantDefinitions.Add(entity);
        }

        entity.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? TenantScopeCatalog.GetTenantDisplayName(normalizedTenantCode)
            : displayName.Trim();
        entity.StorageMode = normalizedStorageMode;
        entity.Description = description?.Trim() ?? string.Empty;
        entity.IsActive = true;
        entity.IsDeleted = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task UpsertTenantOfficeDefinitionAsync(
        AppDbContext dbContext,
        string tenantCode,
        string officeCode,
        string displayName,
        bool isHeadOffice,
        CancellationToken cancellationToken)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOfficeCode);
        var entity = dbContext.TenantOfficeDefinitions.Local.FirstOrDefault(current =>
                         string.Equals(current.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase))
                     ?? await dbContext.TenantOfficeDefinitions.IgnoreQueryFilters()
                         .FirstOrDefaultAsync(current => current.OfficeCode == normalizedOfficeCode, cancellationToken);

        if (entity is null)
        {
            entity = new TenantOfficeDefinition
            {
                OfficeCode = normalizedOfficeCode
            };
            dbContext.TenantOfficeDefinitions.Add(entity);
        }

        entity.TenantCode = normalizedTenantCode;
        entity.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? OfficeCodeCatalog.GetOfficeDisplayName(normalizedOfficeCode)
            : displayName.Trim();
        entity.IsHeadOffice = isHeadOffice;
        entity.IsActive = true;
        entity.IsDeleted = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task UpsertDataSharingPolicyAsync(
        AppDbContext dbContext,
        string sourceTenantCode,
        string sourceOfficeCode,
        string targetTenantCode,
        string targetOfficeCode,
        bool shareCustomers,
        bool shareItems,
        bool shareInvoices,
        bool sharePayments,
        bool shareContracts,
        bool shareReports,
        bool shareRentals,
        bool shareDeliveries,
        bool allowTargetWrite,
        string note,
        CancellationToken cancellationToken)
    {
        var normalizedSourceOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(sourceOfficeCode);
        var normalizedTargetOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(targetOfficeCode);
        var normalizedSourceTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(sourceTenantCode, normalizedSourceOffice);
        var normalizedTargetTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(targetTenantCode, normalizedTargetOffice);

        var entity = dbContext.DataSharingPolicies.Local.FirstOrDefault(current =>
                         string.Equals(current.SourceTenantCode, normalizedSourceTenant, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(current.SourceOfficeCode, normalizedSourceOffice, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(current.TargetTenantCode, normalizedTargetTenant, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(current.TargetOfficeCode, normalizedTargetOffice, StringComparison.OrdinalIgnoreCase))
                     ?? await dbContext.DataSharingPolicies.IgnoreQueryFilters()
                         .FirstOrDefaultAsync(current =>
                                 current.SourceTenantCode == normalizedSourceTenant &&
                                 current.SourceOfficeCode == normalizedSourceOffice &&
                                 current.TargetTenantCode == normalizedTargetTenant &&
                                 current.TargetOfficeCode == normalizedTargetOffice,
                             cancellationToken);

        if (entity is null)
        {
            entity = new DataSharingPolicy();
            dbContext.DataSharingPolicies.Add(entity);
        }

        entity.SourceTenantCode = normalizedSourceTenant;
        entity.SourceOfficeCode = normalizedSourceOffice;
        entity.TargetTenantCode = normalizedTargetTenant;
        entity.TargetOfficeCode = normalizedTargetOffice;
        entity.ShareCustomers = shareCustomers;
        entity.ShareItems = shareItems;
        entity.ShareInvoices = shareInvoices;
        entity.SharePayments = sharePayments;
        entity.ShareContracts = shareContracts;
        entity.ShareReports = shareReports;
        entity.ShareRentals = shareRentals;
        entity.ShareDeliveries = shareDeliveries;
        entity.AllowTargetWrite = allowTargetWrite;
        entity.Note = note?.Trim() ?? string.Empty;
        entity.IsActive = true;
        entity.IsDeleted = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task EnsureSeedUserAsync(
        AppDbContext dbContext,
        ILogger logger,
        string username,
        string? password,
        string role,
        string tenantCode,
        string officeCode,
        string scopeType,
        bool grantAllPermissions,
        bool updatePasswordIfExists,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = username.Trim();
        var normalizedPassword = NormalizeSeedPassword(password);

        if (string.IsNullOrWhiteSpace(normalizedPassword))
        {
            logger.LogWarning("Seed user '{Username}' skipped because password was not configured.", normalizedUsername);
            return;
        }
        var user = dbContext.Users.Local.FirstOrDefault(current =>
            string.Equals(current.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            user = await dbContext.Users.IgnoreQueryFilters()
                .Include(current => current.Permissions)
                .FirstOrDefaultAsync(current => current.Username == normalizedUsername, cancellationToken);
        }

        if (user is null)
        {
            user = new UserAccount
            {
                Username = normalizedUsername,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(normalizedPassword),
                Role = role,
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode),
                OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode),
                ScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType),
                IsActive = true,
                IsDeleted = false
            };
            dbContext.Users.Add(user);
        }
        else
        {
            if (updatePasswordIfExists)
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(normalizedPassword);

            user.Role = role;
            user.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode);
            user.OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
            user.ScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType);
            user.IsActive = true;
            user.IsDeleted = false;
            user.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (grantAllPermissions)
            EnsurePermissions(user, AllPermissions());
    }

    private static string? NormalizeSeedPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return null;

        var trimmed = password.Trim();
        if (trimmed.StartsWith("CHANGE_THIS_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("__DISABLE__", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static void LogSeedUserWarnings(
        IHostEnvironment hostEnvironment,
        ILogger logger,
        SeedUsersOptions seedUsersOptions)
    {
        if (hostEnvironment.IsDevelopment() || !seedUsersOptions.WarnOnDefaultPasswords)
            return;

        if (seedUsersOptions.UsesDefaultAdminPassword())
        {
            logger.LogWarning("SeedUsers: admin password is still using the default value. 운영 전 반드시 변경하세요.");
        }

        if (seedUsersOptions.UsesDefaultUserPassword())
        {
            logger.LogWarning("SeedUsers: user password is still using the default value. 운영 전 반드시 변경하거나 비활성화하세요.");
        }

        if (seedUsersOptions.UsesDefaultItwPassword())
        {
            logger.LogWarning("SeedUsers: itw password is still using the default value. 운영 전 반드시 변경하거나 비활성화하세요.");
        }
    }

    private static void EnsurePermissions(UserAccount user, IEnumerable<string> permissions)
    {
        var existing = user.Permissions
            .Select(permission => permission.Permission)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var permission in permissions)
        {
            if (existing.Contains(permission))
                continue;

            user.Permissions.Add(new UserPermission
            {
                UserId = user.Id,
                Permission = permission,
                User = user
            });
        }
    }

    private static async Task EnsureDefaultCustomerCategoriesAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var categories = await dbContext.CustomerCategories.IgnoreQueryFilters().ToListAsync(cancellationToken);

        foreach (var definition in DefaultCustomerCategories.All)
        {
            var canonical = categories.FirstOrDefault(category => category.Id == definition.Id);
            if (canonical is null)
            {
                canonical = new CustomerCategory
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    IsSystemDefault = true,
                    IsDeleted = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };

                dbContext.CustomerCategories.Add(canonical);
                categories.Add(canonical);
            }
            else
            {
                var canonicalName = string.IsNullOrWhiteSpace(canonical.Name)
                    ? definition.Name
                    : DefaultCustomerCategories.NormalizeName(canonical.Name);
                TouchCanonicalCategory(canonical, canonicalName, isSystemDefault: true);
            }
        }

        var groups = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.Name))
            .GroupBy(category => DefaultCustomerCategories.NormalizeName(category.Name), StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var canonical = ResolveCanonicalCategory(group);
            TouchCanonicalCategory(canonical, DefaultCustomerCategories.NormalizeName(canonical.Name), canonical.IsSystemDefault);

            var duplicateIds = group
                .Where(category => category.Id != canonical.Id)
                .Select(category => category.Id)
                .Distinct()
                .ToList();

            if (duplicateIds.Count == 0)
                continue;

            var duplicateIdSet = duplicateIds.ToHashSet();

            var customerMasters = await dbContext.CustomerMasters.IgnoreQueryFilters()
                .Where(customer => customer.CategoryId.HasValue && duplicateIdSet.Contains(customer.CategoryId.Value))
                .ToListAsync(cancellationToken);
            foreach (var customerMaster in customerMasters)
                customerMaster.CategoryId = canonical.Id;

            var customers = await dbContext.Customers.IgnoreQueryFilters()
                .Where(customer => customer.CategoryId.HasValue && duplicateIdSet.Contains(customer.CategoryId.Value))
                .ToListAsync(cancellationToken);
            foreach (var customer in customers)
                customer.CategoryId = canonical.Id;

            foreach (var duplicate in group.Where(category => category.Id != canonical.Id))
                duplicate.IsDeleted = true;
        }
    }

    private static CustomerCategory ResolveCanonicalCategory(IGrouping<string, CustomerCategory> group)
    {
        if (DefaultCustomerCategories.TryGetByName(group.Key, out var definition))
        {
            var fixedCategory = group.FirstOrDefault(category => category.Id == definition.Id);
            if (fixedCategory is not null)
                return fixedCategory;
        }

        return group
            .OrderBy(category => category.IsDeleted)
            .ThenByDescending(category => category.IsSystemDefault)
            .ThenBy(category => category.CreatedAtUtc)
            .ThenBy(category => category.Id)
            .First();
    }

    private static void TouchCanonicalCategory(
        CustomerCategory category,
        string normalizedName,
        bool isSystemDefault)
    {
        category.Name = normalizedName;
        category.IsSystemDefault = category.IsSystemDefault || isSystemDefault;
        category.IsDeleted = false;
    }

    private sealed class SystemCurrentUserContext : ICurrentUserContext
    {
        public static SystemCurrentUserContext Instance { get; } = new();

        public Guid? UserId => null;
        public string Username => "system";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool HasPermission(string permission) => true;
    }
}
