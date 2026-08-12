using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class InventoryLedgerRebuildIdempotencyTests
{
    [Fact]
    public async Task RebuildAsync_WithoutOuterTransaction_WaitsForSerializedInventoryMutation()
    {
        var databaseName = $"inventory-ledger-lock-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeperConnection = new SqliteConnection(connectionString);
        await keeperConnection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var lockOwnerContext = new AppDbContext(
            options,
            new TestCurrentUserContext(),
            new RevisionClock());
        await using var rebuildContext = new AppDbContext(
            options,
            new TestCurrentUserContext(),
            new RevisionClock());
        await lockOwnerContext.Database.EnsureCreatedAsync();

        var lockScope = await InventoryMutationTransactionScope.BeginAsync(
            lockOwnerContext,
            serializeInventoryMutations: true);
        Task? rebuildTask = null;
        try
        {
            rebuildTask = new InventoryLedgerService(rebuildContext).RebuildAsync();
            await Task.Delay(150);
            Assert.False(rebuildTask.IsCompleted);
            await lockScope.CommitAsync();
        }
        finally
        {
            await lockScope.DisposeAsync();
        }

        Assert.NotNull(rebuildTask);
        await rebuildTask!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RebuildAsync_WhenSourceDataIsUnchanged_PreservesCompleteLedgerRowsAndAuditCount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new AppDbContext(
            options,
            new TestCurrentUserContext(),
            new RevisionClock());
        await dbContext.Database.EnsureCreatedAsync();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Inventory ledger idempotency customer",
            NameMatchKey = "INVENTORYLEDGERIDEMPOTENCYCUSTOMER",
            TradeType = "Sales"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Inventory ledger idempotency item",
            NameMatchKey = "INVENTORYLEDGERIDEMPOTENCYITEM",
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "IDEMPOTENCY-SALES-001",
            VersionGroupId = Guid.NewGuid(),
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 26),
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    Unit = item.Unit,
                    Quantity = 3m,
                    UnitPrice = 1_000m,
                    LineAmount = 3_000m,
                    ItemTrackingType = ItemTrackingTypes.Stock
                }
            ]
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var auditCountBeforeRebuild = await dbContext.AuditLogs.CountAsync();
        var service = new InventoryLedgerService(dbContext);

        await service.RebuildAsync();
        var firstRows = await ReadLedgerSnapshotAsync(dbContext);
        var auditCountAfterFirstRebuild = await dbContext.AuditLogs.CountAsync();

        await service.RebuildAsync();
        var secondRows = await ReadLedgerSnapshotAsync(dbContext);
        var auditCountAfterSecondRebuild = await dbContext.AuditLogs.CountAsync();

        Assert.Single(firstRows);
        Assert.Equal(firstRows, secondRows);
        Assert.Equal(auditCountBeforeRebuild, auditCountAfterFirstRebuild);
        Assert.Equal(auditCountAfterFirstRebuild, auditCountAfterSecondRebuild);
    }

    private static Task<LedgerSnapshot[]> ReadLedgerSnapshotAsync(AppDbContext dbContext)
        => dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .OrderBy(entry => entry.SourceType)
            .ThenBy(entry => entry.SourceDocumentId)
            .ThenBy(entry => entry.SourceLineId)
            .Select(entry => new LedgerSnapshot(
                entry.Id,
                entry.TenantCode,
                entry.OfficeCode,
                entry.ItemId,
                entry.WarehouseCode,
                entry.SourceType,
                entry.SourceDocumentId,
                entry.SourceLineId,
                entry.QuantityDelta,
                entry.OccurredDate,
                entry.Note,
                entry.CreatedAtUtc))
            .ToArrayAsync();

    private sealed record LedgerSnapshot(
        Guid Id,
        string TenantCode,
        string OfficeCode,
        Guid ItemId,
        string WarehouseCode,
        string SourceType,
        Guid SourceDocumentId,
        Guid? SourceLineId,
        decimal QuantityDelta,
        DateOnly OccurredDate,
        string Note,
        DateTime CreatedAtUtc);

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username { get; } = "inventory-ledger-idempotency-test";
        public string TenantCode { get; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;

        public bool HasPermission(string permission) => true;
    }
}
