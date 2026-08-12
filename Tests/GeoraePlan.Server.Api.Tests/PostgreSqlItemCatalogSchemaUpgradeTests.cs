using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlItemCatalogSchemaUpgradeTests
{
    [PostgreSqlFact]
    public async Task BusinessSchemaUpgrade_AddsOptionalItemCatalogColumnsToLegacyPostgreSql_AndIsIdempotent()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(
            PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            await using var dbContext = new AppDbContext(
                options,
                CreateAdminUser(),
                new RevisionClock());
            await dbContext.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            dbContext.Items.Add(new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "LEGACY POSTGRES OPTIONAL CATALOG ITEM",
                NameMatchKey = "LEGACYPOSTGRESOPTIONALCATALOGITEM",
                SpecificationOriginal = "PG-LEGACY-SPEC",
                SpecificationMatchKey = "PGLEGACYSPEC",
                Unit = "EA",
                BoxQuantity = 24m,
                StorageLocation = "REMOVED-LEGACY-LOCATION",
                CurrentStock = 12.5m,
                SimpleMemo = "preserve PostgreSQL existing item data",
                LastPurchaseDate = new DateOnly(2026, 7, 1),
                LastSaleDate = new DateOnly(2026, 7, 2)
            });
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE "Items"
                    DROP COLUMN "BoxQuantity",
                    DROP COLUMN "StorageLocation",
                    DROP COLUMN "LastPurchaseDate",
                    DROP COLUMN "LastSaleDate";
                """);

            Assert.Empty(await ReadItemCatalogColumnDefinitionsAsync(
                testDatabaseBuilder.ConnectionString));

            await InvokeEnsureBusinessDatabaseSchemaAsync(dbContext);

            var columnsAfterFirstRun = await ReadItemCatalogColumnDefinitionsAsync(
                testDatabaseBuilder.ConnectionString);
            Assert.Equal(4, columnsAfterFirstRun.Length);

            var boxQuantity = Assert.Single(
                columnsAfterFirstRun,
                column => column.Name == "BoxQuantity");
            Assert.Equal("numeric", boxQuantity.DataType);
            Assert.False(boxQuantity.IsNullable);
            Assert.Equal(18, boxQuantity.NumericPrecision);
            Assert.Equal(2, boxQuantity.NumericScale);
            Assert.Contains("0", boxQuantity.DefaultExpression ?? string.Empty, StringComparison.Ordinal);

            var storageLocation = Assert.Single(
                columnsAfterFirstRun,
                column => column.Name == "StorageLocation");
            Assert.Equal("text", storageLocation.DataType);
            Assert.False(storageLocation.IsNullable);
            Assert.Contains("''", storageLocation.DefaultExpression ?? string.Empty, StringComparison.Ordinal);

            var lastPurchaseDate = Assert.Single(
                columnsAfterFirstRun,
                column => column.Name == "LastPurchaseDate");
            Assert.Equal("date", lastPurchaseDate.DataType);
            Assert.True(lastPurchaseDate.IsNullable);
            Assert.Null(lastPurchaseDate.DefaultExpression);

            var lastSaleDate = Assert.Single(
                columnsAfterFirstRun,
                column => column.Name == "LastSaleDate");
            Assert.Equal("date", lastSaleDate.DataType);
            Assert.True(lastSaleDate.IsNullable);
            Assert.Null(lastSaleDate.DefaultExpression);

            dbContext.ChangeTracker.Clear();
            var migrated = await dbContext.Items
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal("LEGACY POSTGRES OPTIONAL CATALOG ITEM", migrated.NameOriginal);
            Assert.Equal("PG-LEGACY-SPEC", migrated.SpecificationOriginal);
            Assert.Equal("EA", migrated.Unit);
            Assert.Equal(12.5m, migrated.CurrentStock);
            Assert.Equal("preserve PostgreSQL existing item data", migrated.SimpleMemo);
            Assert.Equal(0m, migrated.BoxQuantity);
            Assert.Equal(string.Empty, migrated.StorageLocation);
            Assert.Null(migrated.LastPurchaseDate);
            Assert.Null(migrated.LastSaleDate);

            await InvokeEnsureBusinessDatabaseSchemaAsync(dbContext);

            Assert.Equal(
                columnsAfterFirstRun,
                await ReadItemCatalogColumnDefinitionsAsync(
                    testDatabaseBuilder.ConnectionString));
            dbContext.ChangeTracker.Clear();
            var migratedAfterSecondRun = await dbContext.Items
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal("LEGACY POSTGRES OPTIONAL CATALOG ITEM", migratedAfterSecondRun.NameOriginal);
            Assert.Equal(12.5m, migratedAfterSecondRun.CurrentStock);
            Assert.Equal(0m, migratedAfterSecondRun.BoxQuantity);
            Assert.Equal(string.Empty, migratedAfterSecondRun.StorageLocation);
            Assert.Null(migratedAfterSecondRun.LastPurchaseDate);
            Assert.Null(migratedAfterSecondRun.LastSaleDate);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    private static async Task InvokeEnsureBusinessDatabaseSchemaAsync(AppDbContext dbContext)
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureBusinessDatabaseSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = method!.Invoke(
            null,
            new object?[] { dbContext, NullLogger.Instance, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task<ItemCatalogColumnDefinition[]> ReadItemCatalogColumnDefinitionsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT column_name,
                   data_type,
                   is_nullable,
                   column_default,
                   numeric_precision,
                   numeric_scale
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = 'Items'
              AND column_name IN (
                  'BoxQuantity',
                  'StorageLocation',
                  'LastPurchaseDate',
                  'LastSaleDate')
            ORDER BY column_name;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<ItemCatalogColumnDefinition>();
        while (await reader.ReadAsync())
        {
            definitions.Add(new ItemCatalogColumnDefinition(
                reader.GetString(0),
                reader.GetString(1),
                string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }

        return definitions.ToArray();
    }

    private static async Task CreateDatabaseAsync(
        string maintenanceConnection,
        string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(
        string maintenanceConnection,
        string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    private sealed record ItemCatalogColumnDefinition(
        string Name,
        string DataType,
        bool IsNullable,
        string? DefaultExpression,
        int? NumericPrecision,
        int? NumericScale);

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

        public bool HasPermission(string permission)
            => IsAdmin ||
               IsGodMode ||
               Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }
}
