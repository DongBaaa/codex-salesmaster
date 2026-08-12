using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DbInitializerItemDeduplicationSafetyTests : IDisposable
{
    private const string AutoCreatedRentalItemMemo = "렌탈 자산/설치현황 자동 동기화 생성";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public DbInitializerItemDeduplicationSafetyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbContext = new AppDbContext(options, new TestCurrentUserContext(), new RevisionClock());
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_ActiveCustomPriceGrade_PreservesItemsAndGrade()
    {
        var first = CreateItem(Guid.Parse("a1111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("a2222222-2222-2222-2222-222222222222"));
        var option = new PriceGradeOption
        {
            Id = Guid.Parse("a3333333-3333-3333-3333-333333333333"),
            Name = "Initializer safety grade",
            IsActive = true
        };
        var grade = new ItemPriceGrade
        {
            Id = Guid.Parse("a4444444-4444-4444-4444-444444444444"),
            ItemId = second.Id,
            PriceGradeOptionId = option.Id,
            PriceGradeName = option.Name,
            UnitPrice = 12345m,
            IsActive = true
        };
        _dbContext.AddRange(first, second, option, grade);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedGrade = await _dbContext.ItemPriceGrades.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(second.Id, storedGrade.ItemId);
        Assert.True(storedGrade.IsActive);
        Assert.False(storedGrade.IsDeleted);
        Assert.Equal(12345m, storedGrade.UnitPrice);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_InactiveCustomPriceGrade_PreservesItemsAndGrade()
    {
        var first = CreateItem(Guid.Parse("aa111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("aa222222-2222-2222-2222-222222222222"));
        var option = new PriceGradeOption
        {
            Id = Guid.Parse("aa333333-3333-3333-3333-333333333333"),
            Name = "Inactive initializer safety grade",
            IsActive = true
        };
        var grade = new ItemPriceGrade
        {
            Id = Guid.Parse("aa444444-4444-4444-4444-444444444444"),
            ItemId = second.Id,
            PriceGradeOptionId = option.Id,
            PriceGradeName = option.Name,
            UnitPrice = 54321m,
            IsActive = false
        };
        _dbContext.AddRange(first, second, option, grade);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedGrade = await _dbContext.ItemPriceGrades.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(second.Id, storedGrade.ItemId);
        Assert.False(storedGrade.IsActive);
        Assert.False(storedGrade.IsDeleted);
        Assert.Equal(54321m, storedGrade.UnitPrice);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_DeletedPriceGrade_RetainsGradeTombstoneWithItemTombstone()
    {
        var first = CreateItem(Guid.Parse("ab111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("ab222222-2222-2222-2222-222222222222"));
        var option = new PriceGradeOption
        {
            Id = Guid.Parse("ab333333-3333-3333-3333-333333333333"),
            Name = "Deleted initializer safety grade",
            IsActive = true
        };
        var grade = new ItemPriceGrade
        {
            Id = Guid.Parse("ab444444-4444-4444-4444-444444444444"),
            ItemId = second.Id,
            PriceGradeOptionId = option.Id,
            PriceGradeName = option.Name,
            UnitPrice = 9876m,
            IsActive = false,
            IsDeleted = true
        };
        _dbContext.AddRange(first, second, option, grade);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        var storedItems = await _dbContext.Items.IgnoreQueryFilters()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, storedItems.Count);
        Assert.False(storedItems[0].IsDeleted);
        Assert.True(storedItems[1].IsDeleted);

        var storedGrade = await _dbContext.ItemPriceGrades.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(second.Id, storedGrade.ItemId);
        Assert.True(storedGrade.IsDeleted);
        Assert.Equal(9876m, storedGrade.UnitPrice);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_NonZeroCurrentStock_PreservesGroup()
    {
        _dbContext.Items.AddRange(
            CreateItem(Guid.Parse("b1111111-1111-1111-1111-111111111111"), currentStock: 4m),
            CreateItem(Guid.Parse("b2222222-2222-2222-2222-222222222222"), currentStock: 4m));
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        var stored = await _dbContext.Items.IgnoreQueryFilters().OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, item => Assert.Equal(4m, item.CurrentStock));
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_NonZeroWarehouseStock_PreservesGroupAndStock()
    {
        var first = CreateItem(Guid.Parse("c1111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("c2222222-2222-2222-2222-222222222222"));
        var stock = new ItemWarehouseStock
        {
            ItemId = second.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 3m,
            UpdatedAtUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.AddRange(first, second, stock);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedStock = await _dbContext.ItemWarehouseStocks.SingleAsync();
        Assert.Equal(second.Id, storedStock.ItemId);
        Assert.Equal(3m, storedStock.Quantity);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_ZeroWarehouseStock_PreservesGroupAndStockIdentity()
    {
        var first = CreateItem(Guid.Parse("c3111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("c3222222-2222-2222-2222-222222222222"));
        var stock = new ItemWarehouseStock
        {
            ItemId = second.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 0m,
            UpdatedAtUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.AddRange(first, second, stock);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedStock = await _dbContext.ItemWarehouseStocks.SingleAsync();
        Assert.Equal(second.Id, storedStock.ItemId);
        Assert.Equal(0m, storedStock.Quantity);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_ConflictingNotesHiddenByLegacyKey_PreservesGroup()
    {
        var first = CreateItem(
            Guid.Parse("d1111111-1111-1111-1111-111111111111"),
            simpleMemo: AutoCreatedRentalItemMemo,
            notes: "first asset contract");
        var second = CreateItem(
            Guid.Parse("d2222222-2222-2222-2222-222222222222"),
            simpleMemo: AutoCreatedRentalItemMemo,
            notes: "second asset contract");
        _dbContext.Items.AddRange(first, second);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        var notes = await _dbContext.Items.IgnoreQueryFilters()
            .OrderBy(item => item.Id)
            .Select(item => item.Notes)
            .ToListAsync();
        Assert.Equal(new[] { "first asset contract", "second asset contract" }, notes);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_InventoryLedgerReference_PreservesGroupAndLedgerIdentity()
    {
        var first = CreateItem(Guid.Parse("f1111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("f2222222-2222-2222-2222-222222222222"));
        var ledger = new InventoryLedgerEntry
        {
            Id = Guid.Parse("f3333333-3333-3333-3333-333333333333"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ItemId = second.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            SourceType = "initializer-safety-test",
            SourceDocumentId = Guid.Parse("f4444444-4444-4444-4444-444444444444"),
            QuantityDelta = 0m,
            OccurredDate = new DateOnly(2026, 8, 5),
            CreatedAtUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.AddRange(first, second, ledger);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedLedger = await _dbContext.InventoryLedgerEntries.SingleAsync();
        Assert.Equal(second.Id, storedLedger.ItemId);
        Assert.Equal(ledger.Id, storedLedger.Id);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_ActiveEditSession_PreservesGroupAndSessionSubject()
    {
        var first = CreateItem(Guid.Parse("91111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("92222222-2222-2222-2222-222222222222"));
        var session = new ActiveEditSession
        {
            Id = Guid.Parse("93333333-3333-3333-3333-333333333333"),
            AppSessionId = Guid.Parse("94444444-4444-4444-4444-444444444444"),
            Username = "initializer-safety-user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScreenName = "품목/재고 관리",
            EntityType = nameof(Item),
            EntityId = second.Id.ToString("D"),
            EntityDisplayName = second.NameOriginal,
            MachineName = "initializer-safety-machine",
            OpenedAtUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        };
        _dbContext.AddRange(first, second, session);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedSession = await _dbContext.ActiveEditSessions.SingleAsync();
        Assert.Equal(second.Id.ToString("D"), storedSession.EntityId);
        Assert.Equal(nameof(Item), storedSession.EntityType);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_DuplicateInvoiceLineReference_PreservesGroupAndLineIdentity()
    {
        var first = CreateItem(Guid.Parse("81111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("82222222-2222-2222-2222-222222222222"));
        var customer = new Customer
        {
            Id = Guid.Parse("83333333-3333-3333-3333-333333333333"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Initializer safety customer",
            NameMatchKey = "INITIALIZERSAFETYCUSTOMER"
        };
        var invoice = new Invoice
        {
            Id = Guid.Parse("84444444-4444-4444-4444-444444444444"),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INITIALIZER-SAFETY-INVOICE",
            InvoiceDate = new DateOnly(2026, 8, 5),
            Lines =
            {
                new InvoiceLine
                {
                    Id = Guid.Parse("85666666-6666-6666-6666-666666666666"),
                    ItemId = first.Id,
                    ItemNameOriginal = first.NameOriginal,
                    SpecificationOriginal = first.SpecificationOriginal,
                    Quantity = 1m
                },
                new InvoiceLine
                {
                    Id = Guid.Parse("85777777-7777-7777-7777-777777777777"),
                    ItemId = first.Id,
                    ItemNameOriginal = first.NameOriginal,
                    SpecificationOriginal = first.SpecificationOriginal,
                    Quantity = 1m
                },
                new InvoiceLine
                {
                    Id = Guid.Parse("85555555-5555-5555-5555-555555555555"),
                    ItemId = second.Id,
                    ItemNameOriginal = second.NameOriginal,
                    SpecificationOriginal = second.SpecificationOriginal,
                    Quantity = 1m
                }
            }
        };
        _dbContext.AddRange(first, second, customer, invoice);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedLine = await _dbContext.InvoiceLines.IgnoreQueryFilters()
            .SingleAsync(line => line.Id == Guid.Parse("85555555-5555-5555-5555-555555555555"));
        Assert.Equal(second.Id, storedLine.ItemId);
        Assert.Equal(Guid.Parse("85555555-5555-5555-5555-555555555555"), storedLine.Id);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_DuplicateTransferLineReference_PreservesGroupAndLineIdentity()
    {
        var first = CreateItem(Guid.Parse("71111111-1111-1111-1111-111111111111"));
        var second = CreateItem(Guid.Parse("72222222-2222-2222-2222-222222222222"));
        var transfer = new InventoryTransfer
        {
            Id = Guid.Parse("73333333-3333-3333-3333-333333333333"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = "INITIALIZER-SAFETY-TRANSFER",
            TransferDate = new DateOnly(2026, 8, 5),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Lines =
            {
                new InventoryTransferLine
                {
                    Id = Guid.Parse("74555555-5555-5555-5555-555555555555"),
                    ItemId = first.Id,
                    ItemNameOriginal = first.NameOriginal,
                    SpecificationOriginal = first.SpecificationOriginal,
                    Quantity = 1m
                },
                new InventoryTransferLine
                {
                    Id = Guid.Parse("74666666-6666-6666-6666-666666666666"),
                    ItemId = first.Id,
                    ItemNameOriginal = first.NameOriginal,
                    SpecificationOriginal = first.SpecificationOriginal,
                    Quantity = 1m
                },
                new InventoryTransferLine
                {
                    Id = Guid.Parse("74444444-4444-4444-4444-444444444444"),
                    ItemId = second.Id,
                    ItemNameOriginal = second.NameOriginal,
                    SpecificationOriginal = second.SpecificationOriginal,
                    Quantity = 1m
                }
            }
        };
        _dbContext.AddRange(first, second, transfer);
        await _dbContext.SaveChangesAsync();

        await InvokeMergeDuplicateItemsAsync();

        Assert.Equal(2, await _dbContext.Items.IgnoreQueryFilters().CountAsync());
        var storedLine = await _dbContext.InventoryTransferLines.IgnoreQueryFilters()
            .SingleAsync(line => line.Id == Guid.Parse("74444444-4444-4444-4444-444444444444"));
        Assert.Equal(second.Id, storedLine.ItemId);
        Assert.Equal(Guid.Parse("74444444-4444-4444-4444-444444444444"), storedLine.Id);
    }

    [Fact]
    public async Task MergeDuplicateItemsAsync_ExactZeroStockGroupWithoutGrades_EmitsIncrementalSyncTombstone()
    {
        _dbContext.Items.AddRange(
            CreateItem(Guid.Parse("e1111111-1111-1111-1111-111111111111")),
            CreateItem(Guid.Parse("e2222222-2222-2222-2222-222222222222")));
        await _dbContext.SaveChangesAsync();
        var revisionBeforeMerge = await _dbContext.Items.IgnoreQueryFilters()
            .MaxAsync(item => item.Revision);

        await InvokeMergeDuplicateItemsAsync();

        var stored = await _dbContext.Items.IgnoreQueryFilters()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, stored.Count);
        var canonical = Assert.Single(stored, item => !item.IsDeleted);
        Assert.Equal(Guid.Parse("e1111111-1111-1111-1111-111111111111"), canonical.Id);
        Assert.Equal(0m, canonical.CurrentStock);

        var tombstone = Assert.Single(stored, item => item.IsDeleted);
        Assert.Equal(Guid.Parse("e2222222-2222-2222-2222-222222222222"), tombstone.Id);
        Assert.True(tombstone.Revision > revisionBeforeMerge);

        // This is the same revision predicate used by incremental item pull.
        var incrementalRows = await _dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Revision > revisionBeforeMerge)
            .ToListAsync();
        Assert.Contains(incrementalRows, item => item.Id == tombstone.Id && item.IsDeleted);
    }

    private async Task InvokeMergeDuplicateItemsAsync()
    {
        var method = typeof(DbInitializer).GetMethod(
            "MergeDuplicateItemsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var invocation = method!.Invoke(null, [_dbContext, CancellationToken.None]);
        await Assert.IsAssignableFrom<Task>(invocation);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private static Item CreateItem(
        Guid id,
        decimal currentStock = 0m,
        string simpleMemo = "",
        string notes = "")
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Initializer duplicate safety item",
            NameMatchKey = "INITIALIZERDUPLICATESAFETYITEM",
            SpecificationOriginal = "A4",
            SpecificationMatchKey = "A4",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = currentStock,
            SimpleMemo = simpleMemo,
            Notes = notes,
            CreatedAtUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
        };

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; }
        public string Username { get; init; } = "admin";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }
        public bool HasPermission(string permission) => IsAdmin;
    }
}
