using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DirectMutationLegacyReceiptTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestCurrentUserContext _currentUser;
    private readonly AppDbContext _dbContext;

    public DirectMutationLegacyReceiptTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _currentUser = new TestCurrentUserContext();
        _dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options,
            _currentUser,
            new RevisionClock());
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task InvoiceCreate_TrimmedLegacyEmptyHashRetry_ReturnsExistingEntityWithoutCreatingOrChangingInvoice()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "LEGACY RETRY CUSTOMER",
            NameMatchKey = "LEGACYRETRYCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "LEGACY-RETRY-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 23),
            Memo = "persisted payload"
        };
        _dbContext.AddRange(customer, invoice);
        await _dbContext.SaveChangesAsync();

        var mutationId = $"legacy:invoice:{invoice.Id:N}";
        _dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = $"  {mutationId.ToUpperInvariant()}  ",
            DeviceId = ProcessedSyncMutationRecorder.DirectApiDeviceId,
            EntityName = nameof(Invoice),
            EntityId = invoice.Id.ToString("D"),
            ExpectedRevision = 0,
            PayloadHash = string.Empty,
            ProcessedAtUtc = new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var persisted = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(current => current.Customer)
            .Include(current => current.Lines)
            .SingleAsync(current => current.Id == invoice.Id);
        var retry = persisted.ToDto();
        retry.Id = Guid.Empty;
        retry.Memo = "changed retry payload must not be applied";
        retry.ExpectedRevision = 0;
        retry.MutationId = mutationId;

        var response = await CreateController().Create(retry, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var returned = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(invoice.Id, returned.Id);
        Assert.Equal("persisted payload", returned.Memo);

        _dbContext.ChangeTracker.Clear();
        Assert.Equal(
            "persisted payload",
            await _dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(current => current.Id == invoice.Id)
                .Select(current => current.Memo)
                .SingleAsync());
        Assert.Equal(1, await _dbContext.Invoices.IgnoreQueryFilters().CountAsync());
        Assert.Single(await _dbContext.ProcessedSyncMutations.ToListAsync());
    }

    [Fact]
    public async Task CheckAsync_LegacyEmptyHashReceipt_StillRejectsEntityMetadataMismatch()
    {
        var persistedEntityId = Guid.NewGuid();
        const string mutationId = "legacy:metadata:mismatch";
        _dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = mutationId,
            DeviceId = ProcessedSyncMutationRecorder.DirectApiDeviceId,
            EntityName = nameof(Invoice),
            EntityId = persistedEntityId.ToString("D"),
            ExpectedRevision = 4,
            PayloadHash = string.Empty,
            ProcessedAtUtc = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var check = await ProcessedSyncMutationRecorder.CheckAsync(
            _dbContext,
            new InvoiceDto
            {
                Id = Guid.NewGuid(),
                ExpectedRevision = 4,
                MutationId = mutationId,
                Memo = "unverifiable changed payload"
            },
            nameof(Invoice),
            CancellationToken.None);

        Assert.Equal(DirectMutationStatus.Conflict, check.Status);
        Assert.Equal(persistedEntityId.ToString("D"), check.ExistingReceipt?.EntityId);
    }

    [Fact]
    public async Task CheckAsync_ServerReservedStockReceiptNamespace_IsRejectedBeforeDirectMutationWrite()
    {
        var mutationId =
            $"{ItemWarehouseStockMutationReceipt.MutationIdPrefix}forged-client-id";
        var check =
            await ProcessedSyncMutationRecorder.CheckAsync(
                _dbContext,
                new InvoiceDto
                {
                    Id = Guid.NewGuid(),
                    MutationId = mutationId,
                    MutationCreatedAtUtc =
                        new DateTime(
                            2026,
                            7,
                            30,
                            3,
                            0,
                            0,
                            DateTimeKind.Utc)
                },
                nameof(Invoice),
                CancellationToken.None);

        Assert.Equal(
            DirectMutationStatus.Conflict,
            check.Status);
        Assert.Equal(
            ProcessedSyncMutationRecorder
                .NormalizeMutationId(mutationId),
            check.MutationId);
        Assert.Contains(
            "server-reserved receipt namespace",
            check.ConflictReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(
            await _dbContext.ProcessedSyncMutations
                .ToListAsync());
    }

    private InvoicesController CreateController()
        => new(
            _dbContext,
            _currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(_currentUser, _dbContext),
            new InventoryLedgerService(_dbContext),
            new InvoiceStockSnapshotService(_dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(_dbContext));

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "admin";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }

        public bool HasPermission(string permission) => true;
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"INV-{invoiceDate:yyyyMMdd}-0001");
    }
}
