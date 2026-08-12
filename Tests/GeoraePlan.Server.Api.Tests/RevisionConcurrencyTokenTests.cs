using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RevisionConcurrencyTokenTests
{
    [Fact]
    public async Task Model_ConfiguresEveryTrackedRevisionAndWarehouseStockRevision_AsConcurrencyTokens()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await using var dbContext = CreateDbContext(connection);

        var trackedEntityTypes = dbContext.Model.GetEntityTypes()
            .Where(entityType => typeof(ITrackedEntity).IsAssignableFrom(entityType.ClrType))
            .ToList();

        Assert.NotEmpty(trackedEntityTypes);
        foreach (var entityType in trackedEntityTypes)
        {
            var revisionProperty = entityType.FindProperty(nameof(ITrackedEntity.Revision));
            Assert.NotNull(revisionProperty);
            Assert.True(
                revisionProperty.IsConcurrencyToken,
                $"{entityType.ClrType.Name}.{nameof(ITrackedEntity.Revision)} must be a concurrency token.");
        }

        var warehouseStockEntity = dbContext.Model.FindEntityType(typeof(ItemWarehouseStock));
        Assert.NotNull(warehouseStockEntity);
        var warehouseStockRevision = warehouseStockEntity.FindProperty(nameof(ItemWarehouseStock.Revision));
        Assert.NotNull(warehouseStockRevision);
        Assert.True(warehouseStockRevision.IsConcurrencyToken);
    }

    [Fact]
    public async Task TwoDbContexts_StaleSecondSave_ThrowsAndKeepsFirstPersistedValue()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        var customerId = Guid.NewGuid();

        await using (var setupContext = CreateDbContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
            setupContext.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Initial customer",
                NameMatchKey = "INITIALCUSTOMER",
                TradeType = "Sales"
            });
            await setupContext.SaveChangesAsync();
        }

        await using var firstContext = CreateDbContext(connection);
        await using var staleContext = CreateDbContext(connection);
        var firstCustomer = await firstContext.Customers.SingleAsync(customer => customer.Id == customerId);
        var staleCustomer = await staleContext.Customers.SingleAsync(customer => customer.Id == customerId);
        var originalRevision = staleCustomer.Revision;

        firstCustomer.NameOriginal = "First writer";
        await firstContext.SaveChangesAsync();
        var firstRevision = firstCustomer.Revision;

        staleCustomer.NameOriginal = "Stale second writer";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleContext.SaveChangesAsync());

        await using var verificationContext = CreateDbContext(connection);
        var persisted = await verificationContext.Customers
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);

        Assert.Equal("First writer", persisted.NameOriginal);
        Assert.Equal(firstRevision, persisted.Revision);
        Assert.NotEqual(originalRevision, persisted.Revision);
    }

    private static async Task<SqliteConnection> OpenInMemoryDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static AppDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        return new AppDbContext(
            options,
            new TestCurrentUserContext(),
            new RevisionClock());
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username { get; } = "revision-concurrency-test";
        public string TenantCode { get; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;

        public bool HasPermission(string permission) => true;
    }
}
