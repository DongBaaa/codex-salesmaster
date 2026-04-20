using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DbInitializerRegressionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public DbInitializerRegressionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var currentUser = new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options, currentUser, revisionClock);
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task EnsureInvoiceVersionColumnsAsync_DoesNotRequire_SourceWarehouseCode_BeforeRuntimeSchema()
    {
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"InvoiceLines\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Payments\";");
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Invoices\";");
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Invoices" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Invoices" PRIMARY KEY,
                "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL DEFAULT 0
            );
            """);
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var invoiceId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = createdAt.AddMinutes(5);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "Invoices" ("Id", "IsDeleted", "CreatedAtUtc", "UpdatedAtUtc", "Revision")
             VALUES ({invoiceId.ToString()}, 0, {createdAt}, {updatedAt}, 1);
             """);

        var method = typeof(DbInitializer).GetMethod(
            "EnsureInvoiceVersionColumnsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT \"VersionGroupId\", \"VersionNumber\", \"IsLatestVersion\" FROM \"Invoices\" WHERE \"Id\" = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", invoiceId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(invoiceId.ToString(), reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task EnsureOperationalRuntimeSchemaAsync_DoesNotDispose_DbConnection()
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureOperationalRuntimeSchemaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;

        await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1;");
    }

    [Fact]
    public async Task NormalizeInventoryTransferIntegrityAsync_RemovesCrossTenantTransfers()
    {
        var transferId = Guid.NewGuid();
        _dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Itworld,
            TransferNumber = "TR-INVALID-001",
            TransferDate = new DateOnly(2026, 4, 13),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        _dbContext.InventoryTransferLines.Add(new InventoryTransferLine
        {
            Id = Guid.NewGuid(),
            TransferId = transferId,
            ItemNameOriginal = "테스트 품목",
            Unit = "EA",
            Quantity = 1m
        });
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "NormalizeInventoryTransferIntegrityAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        Assert.False(await _dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(current => current.Id == transferId));
        Assert.False(await _dbContext.InventoryTransferLines.IgnoreQueryFilters().AnyAsync(current => current.TransferId == transferId));
    }

    [Fact]
    public async Task MergeBusinessDuplicateCustomersAsync_UsesResponsibleOfficeCode_ForDuplicateKey()
    {
        var duplicateA = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "연수 테스트 거래처",
            NameMatchKey = "연수테스트거래처",
            BusinessNumber = "123-45-67890",
            TradeType = "매출",
            Address = "인천 연수구 테스트로 1",
            Phone = "032-000-0000",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        var duplicateB = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "연수 테스트 거래처",
            NameMatchKey = "연수테스트거래처",
            BusinessNumber = "123-45-67890",
            TradeType = "매출",
            Address = "인천 연수구 테스트로 1",
            Phone = "032-000-0000",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        _dbContext.Customers.AddRange(duplicateA, duplicateB);
        await _dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "MergeBusinessDuplicateCustomersAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { _dbContext, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
        await _dbContext.SaveChangesAsync();

        var customers = await _dbContext.Customers.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted && current.NameOriginal == "연수 테스트 거래처")
            .ToListAsync();

        var remaining = Assert.Single(customers);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, remaining.ResponsibleOfficeCode);
    }
    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public bool HasPermission(string permission) => IsAdmin;
    }
}
