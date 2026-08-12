using System.Data.Common;
using System.Reflection;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SyncPullReadSnapshotTests
{
    private const string PostgreSqlConnectionVariableName = "GEORAEPLAN_POSTGRES_TEST_CONNECTION";
    private const decimal BaselineStockQuantity = 8m;
    private const decimal UpdatedStockQuantity = 13m;

    [Fact]
    public async Task Sqlite_Pull_ReturnsWatermarkEntitiesAndAuthoritativeStocksFromOneReadSnapshot()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-sync-pull-snapshot-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ConnectionString;
        var baseOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (var initializationDb = CreateDbContext(baseOptions, CreateAdminUser()))
            {
                await initializationDb.Database.EnsureCreatedAsync();
                await initializationDb.Database.ExecuteSqlRawAsync(
                    "PRAGMA journal_mode=DELETE;");
                Assert.Equal(
                    "delete",
                    await ReadSqliteJournalModeAsync(initializationDb),
                    ignoreCase: true);
                await ConfigureSqliteRuntimeJournalModeAsync(initializationDb);
                Assert.Equal(
                    "wal",
                    await ReadSqliteJournalModeAsync(initializationDb),
                    ignoreCase: true);
            }

            var gate = new RentalAssetsReadGateInterceptor();
            var readerOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(gate)
                .Options;

            await AssertPullUsesOneReadSnapshotAsync(
                baseOptions,
                readerOptions,
                gate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteSqliteDatabaseFiles(databasePath);
        }
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("MEMORY")]
    public async Task SqliteFilePull_WithoutSafeRuntimeJournalMode_FailsFast(
        string unsafeJournalMode)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-sync-pull-delete-journal-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            var currentUser = CreateAdminUser();
            await using var dbContext = CreateDbContext(options, currentUser);
            await dbContext.Database.EnsureCreatedAsync();
            await dbContext.Database.OpenConnectionAsync();
            await using var journalModeCommand =
                dbContext.Database.GetDbConnection().CreateCommand();
            journalModeCommand.CommandText = unsafeJournalMode switch
            {
                "DELETE" => "PRAGMA journal_mode=DELETE;",
                "MEMORY" => "PRAGMA journal_mode=MEMORY;",
                _ => throw new InvalidOperationException(
                    $"Unexpected test journal mode: {unsafeJournalMode}")
            };
            var actualJournalMode = Convert.ToString(
                    await journalModeCommand.ExecuteScalarAsync())
                ?.Trim();
            Assert.Equal(
                unsafeJournalMode,
                actualJournalMode,
                ignoreCase: true);

            var controller = CreateController(dbContext, currentUser);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.Pull(0, CancellationToken.None));

            Assert.Contains(
                "requires WAL journal mode",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteSqliteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task SqliteLiteralInMemoryPull_AllowsMemoryJournalAndPreservesOpenConnection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(options, currentUser);
        await dbContext.Database.EnsureCreatedAsync();

        await ConfigureSqliteRuntimeJournalModeAsync(dbContext);
        Assert.Equal(
            "memory",
            await ReadSqliteJournalModeAsync(dbContext),
            ignoreCase: true);

        var response = AssertPullResponse(
            await CreateController(dbContext, currentUser)
                .Pull(0, CancellationToken.None));

        Assert.Equal(0, response.CurrentServerRevision);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Null(dbContext.Database.CurrentTransaction);
    }

    [Fact]
    public async Task SqliteNamedSharedMemory_FailsClosedForRuntimeInitializationAndPull()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"georaeplan-sync-pull-named-memory-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ConnectionString;
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(options, currentUser);
        await dbContext.Database.EnsureCreatedAsync();

        var initializationException =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ConfigureSqliteRuntimeJournalModeAsync(dbContext));
        Assert.Contains(
            "requires WAL journal mode",
            initializationException.Message,
            StringComparison.Ordinal);

        var pullException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateController(dbContext, currentUser)
                .Pull(0, CancellationToken.None));
        Assert.Contains(
            "requires WAL journal mode",
            pullException.Message,
            StringComparison.Ordinal);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Null(dbContext.Database.CurrentTransaction);
    }

    [PostgreSqlFact]
    public async Task PostgreSql_Pull_ReturnsWatermarkEntitiesAndAuthoritativeStocksFromOneReadSnapshot()
    {
        var configuredConnection =
            Environment.GetEnvironmentVariable(PostgreSqlConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(
            maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var baseOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            await using (var initializationDb =
                         CreateDbContext(baseOptions, CreateAdminUser()))
            {
                await initializationDb.Database.EnsureCreatedAsync();
            }

            var gate = new RentalAssetsReadGateInterceptor();
            var readerOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .AddInterceptors(gate)
                .Options;

            await AssertPullUsesOneReadSnapshotAsync(
                baseOptions,
                readerOptions,
                gate);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
            {
                await DropDatabaseAsync(
                    maintenanceBuilder.ConnectionString,
                    databaseName);
            }
        }
    }

    private static async Task AssertPullUsesOneReadSnapshotAsync(
        DbContextOptions<AppDbContext> writerOptions,
        DbContextOptions<AppDbContext> readerOptions,
        RentalAssetsReadGateInterceptor gate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        var itemId = Guid.NewGuid();
        var newCustomerId = Guid.NewGuid();
        long baselineRevision;
        long baselineStockRevision;

        await using (var seedDb = CreateDbContext(writerOptions, CreateAdminUser()))
        {
            seedDb.Items.Add(CreateItem(itemId));
            var stock = CreateStock(itemId, BaselineStockQuantity);
            seedDb.ItemWarehouseStocks.Add(stock);
            await seedDb.SaveChangesAsync(cancellationToken);

            baselineRevision =
                await seedDb.GetCommittedRevisionAsync(cancellationToken);
            baselineStockRevision = stock.Revision;
            Assert.True(baselineStockRevision > 0);
            Assert.True(baselineStockRevision <= baselineRevision);
        }

        var writerUser = CreateAdminUser("snapshot-writer");
        var readerUser = CreateAdminUser("snapshot-reader");
        await using var writerDb = CreateDbContext(writerOptions, writerUser);
        await using var readerDb = CreateDbContext(readerOptions, readerUser);
        var readerController = CreateController(readerDb, readerUser);

        var stockToUpdate = await writerDb.ItemWarehouseStocks
            .SingleAsync(
                stock => stock.ItemId == itemId &&
                         stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse,
                cancellationToken);
        stockToUpdate.Quantity = UpdatedStockQuantity;
        stockToUpdate.UpdatedAtUtc =
            new DateTime(2026, 7, 28, 1, 0, 0, DateTimeKind.Utc);
        var newCustomer = CreateCustomer(newCustomerId);
        writerDb.Customers.Add(newCustomer);

        var firstPullTask = readerController.Pull(0, cancellationToken);
        await gate.ReaderReached.WaitAsync(cancellationToken);
        try
        {
            await writerDb.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.ReleaseReader();
        }

        var firstPull = AssertPullResponse(
            await firstPullTask.WaitAsync(cancellationToken));
        Assert.Equal(baselineRevision, firstPull.CurrentServerRevision);
        Assert.DoesNotContain(
            firstPull.Customers,
            customer => customer.Id == newCustomerId);
        var firstStock = Assert.Single(
            firstPull.ItemWarehouseStocks,
            stock => stock.ItemId == itemId &&
                     stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(BaselineStockQuantity, firstStock.Quantity);
        Assert.Equal(baselineStockRevision, firstStock.Revision);
        Assert.True(firstStock.Revision <= firstPull.CurrentServerRevision);

        var secondPull = AssertPullResponse(
            await readerController.Pull(baselineRevision, cancellationToken));
        Assert.Contains(
            secondPull.Customers,
            customer => customer.Id == newCustomerId);
        var secondStock = Assert.Single(
            secondPull.ItemWarehouseStocks,
            stock => stock.ItemId == itemId &&
                     stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(UpdatedStockQuantity, secondStock.Quantity);
        Assert.Equal(stockToUpdate.Revision, secondStock.Revision);
        Assert.True(newCustomer.Revision > baselineRevision);
        Assert.True(secondStock.Revision > baselineRevision);
        Assert.True(newCustomer.Revision <= secondPull.CurrentServerRevision);
        Assert.True(secondStock.Revision <= secondPull.CurrentServerRevision);
    }

    private static Item CreateItem(Guid itemId)
        => new()
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "Sync pull snapshot item",
            NameMatchKey = "SYNCPULLSNAPSHOTITEM",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = BaselineStockQuantity
        };

    private static ItemWarehouseStock CreateStock(Guid itemId, decimal quantity)
        => new()
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = quantity,
            UpdatedAtUtc = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc)
        };

    private static Customer CreateCustomer(Guid customerId)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Sync pull snapshot concurrent customer",
            NameMatchKey = "SYNCPULLSNAPSHOTCONCURRENTCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };

    private static SyncPullResponse AssertPullResponse(
        ActionResult<SyncPullResponse> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SyncPullResponse>(ok.Value);
    }

    private static AppDbContext CreateDbContext(
        DbContextOptions<AppDbContext> options,
        TestCurrentUserContext currentUser)
        => new(options, currentUser, new RevisionClock());

    private static SyncController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new SyncController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            revisionClock,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static TestCurrentUserContext CreateAdminUser(string username = "admin")
        => new()
        {
            Username = username,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

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

    private static async Task ConfigureSqliteRuntimeJournalModeAsync(
        AppDbContext dbContext)
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureSqliteRuntimeJournalModeAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(
            method.Invoke(
                null,
                [dbContext, NullLogger.Instance, CancellationToken.None]));
        await task;
    }

    private static async Task<string> ReadSqliteJournalModeAsync(
        AppDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != System.Data.ConnectionState.Open;
        if (closeConnection)
            await dbContext.Database.OpenConnectionAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return Convert.ToString(await command.ExecuteScalarAsync())
                ?.Trim() ?? string.Empty;
        }
        finally
        {
            if (closeConnection)
                await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static void DeleteSqliteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class RentalAssetsReadGateInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _readerReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readerRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readerGateEntered;

        public Task ReaderReached => _readerReached.Task;

        public void ReleaseReader()
            => _readerRelease.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "\"RentalAssets\"",
                    StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref _readerGateEntered, 1, 0) == 0)
            {
                _readerReached.TrySetResult();
                await _readerRelease.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } =
            Array.Empty<string>();

        public bool HasPermission(string permission)
            => IsAdmin ||
               IsGodMode ||
               Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"INV-{invoiceDate:yyyyMMdd}-{customerId:N}");
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
