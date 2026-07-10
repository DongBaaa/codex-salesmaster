using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class InventoryMutationTransactionScopeTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"georaeplan-inventory-mutation-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(CreateAdminUser());
        await dbContext.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BeginAsync_SerializesInventoryMutationsForTheSameDatabase()
    {
        var firstUser = CreateAdminUser();
        var secondUser = CreateAdminUser();
        await using var firstDbContext = CreateDbContext(firstUser);
        var firstScope = await InventoryMutationTransactionScope.BeginAsync(
            firstDbContext,
            serializeInventoryMutations: true,
            CancellationToken.None);
        var secondAttempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var secondTask = Task.Run(async () =>
        {
            await using var secondDbContext = CreateDbContext(secondUser);
            secondAttempting.TrySetResult();
            await using var secondScope = await InventoryMutationTransactionScope.BeginAsync(
                secondDbContext,
                serializeInventoryMutations: true,
                CancellationToken.None);
            secondEntered.TrySetResult();
            await secondScope.CommitAsync();
        });

        try
        {
            await secondAttempting.Task;
            var prematureEntry = await Task.WhenAny(secondEntered.Task, Task.Delay(150));
            Assert.NotSame(secondEntered.Task, prematureEntry);

            await firstScope.CommitAsync();
        }
        finally
        {
            await firstScope.DisposeAsync();
        }

        await secondTask;
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task BeginAsync_CancellationWhileWaiting_DoesNotReleaseAnotherMutationLock()
    {
        await using var firstDbContext = CreateDbContext(CreateAdminUser());
        var firstScope = await InventoryMutationTransactionScope.BeginAsync(
            firstDbContext,
            serializeInventoryMutations: true,
            CancellationToken.None);

        try
        {
            await using var waitingDbContext = CreateDbContext(CreateAdminUser());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                InventoryMutationTransactionScope.BeginAsync(
                    waitingDbContext,
                    serializeInventoryMutations: true,
                    cancellation.Token));

            await firstScope.CommitAsync();
        }
        finally
        {
            await firstScope.DisposeAsync();
        }

        await using var nextDbContext = CreateDbContext(CreateAdminUser());
        await using var nextScope = await InventoryMutationTransactionScope.BeginAsync(
            nextDbContext,
            serializeInventoryMutations: true,
            CancellationToken.None);
        await nextScope.CommitAsync();
    }

    [Fact]
    public async Task ConcurrentInvoiceCreates_ApplyEveryStockDeltaAndKeepNegativeStockAllowed()
    {
        var customerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var seedDbContext = CreateDbContext(CreateAdminUser()))
        {
            seedDbContext.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Concurrent customer",
                NameMatchKey = "CONCURRENTCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDbContext.Items.Add(new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Concurrent stock item",
                NameMatchKey = "CONCURRENTSTOCKITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 0m
            });
            seedDbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 0m,
                Revision = 1
            });
            await seedDbContext.SaveChangesAsync();
        }

        var firstCreate = CreateInvoiceAsync("CONCURRENT-1", customerId, itemId);
        var secondCreate = CreateInvoiceAsync("CONCURRENT-2", customerId, itemId);
        var responses = await Task.WhenAll(firstCreate, secondCreate);

        Assert.All(responses, response => Assert.IsType<OkObjectResult>(response.Result));

        await using var verifyDbContext = CreateDbContext(CreateAdminUser());
        Assert.Equal(2, await verifyDbContext.Invoices.AsNoTracking().CountAsync());
        Assert.Equal(
            -2m,
            await verifyDbContext.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            -2m,
            await verifyDbContext.Items
                .AsNoTracking()
                .Where(item => item.Id == itemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
        var ledgerDeltas = await verifyDbContext.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.ItemId == itemId)
            .Select(entry => entry.QuantityDelta)
            .ToListAsync();
        Assert.Equal(-2m, ledgerDeltas.Sum());
    }

    private async Task<ActionResult<InvoiceDto>> CreateInvoiceAsync(
        string invoiceNumber,
        Guid customerId,
        Guid itemId)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        var invoiceId = Guid.NewGuid();

        return await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            InvoiceNumber = invoiceNumber,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 10),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemId = itemId,
                    ItemNameOriginal = "Concurrent stock item",
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1_000m,
                    LineAmount = 1_000m
                }
            ]
        }, CancellationToken.None);
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Cache=Shared;Default Timeout=10;Pooling=False")
            .Options;
        return new AppDbContext(options, currentUser, new RevisionClock());
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
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"INV-{invoiceDate:yyyyMMdd}-{customerId:N}");
    }
}
