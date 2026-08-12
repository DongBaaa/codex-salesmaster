using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RecycleBinRestoreCascadeRevisionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestCurrentUserContext _currentUser = new();

    public RecycleBinRestoreCascadeRevisionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task RestoreContract_NewerParentTombstone_RejectsWithoutWrites()
    {
        await using var dbContext = CreateDbContext();
        var customer = CreateDeletedCustomer();
        var contract = CreateDeletedContract(customer.Id);
        dbContext.AddRange(customer, contract);
        await dbContext.SaveChangesAsync();
        var expectedRevision = contract.Revision;

        customer.Notes = "newer parent deletion intent";
        await dbContext.SaveChangesAsync();
        var beforeCustomer = await ReadTrackedStateAsync<Customer>(dbContext, customer.Id);
        var beforeContract = await ReadTrackedStateAsync<CustomerContract>(dbContext, contract.Id);

        var result = await RestoreAsync(dbContext, "contract", contract.Id, expectedRevision);

        Assert.False(result.Success);
        Assert.Contains("같은 삭제", result.Message, StringComparison.Ordinal);
        Assert.Equal(beforeCustomer, await ReadTrackedStateAsync<Customer>(dbContext, customer.Id));
        Assert.Equal(beforeContract, await ReadTrackedStateAsync<CustomerContract>(dbContext, contract.Id));
    }

    [Fact]
    public async Task RestoreInvoice_NewerSiblingAndCustomerTombstones_RejectsWithoutStockOrLedgerWrites()
    {
        await using var dbContext = CreateDbContext();
        var customer = CreateDeletedCustomer();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "cascade guard stock item",
            NameMatchKey = "cascadeguardstockitem",
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 20m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 20m
        });
        await dbContext.SaveChangesAsync();

        var groupId = Guid.NewGuid();
        var selected = CreateDeletedInvoice(customer.Id, groupId, 1, isLatest: false);
        var sibling = CreateDeletedInvoice(customer.Id, groupId, 2, isLatest: true);
        var selectedLine = new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = selected.Id,
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            ItemTrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            Quantity = 3m,
            UnitPrice = 100m,
            LineAmount = 300m,
            IsDeleted = true
        };
        dbContext.AddRange(customer, selected, sibling, selectedLine);
        await dbContext.SaveChangesAsync();
        var expectedRevision = selected.Revision;

        sibling.Memo = "newer sibling deletion intent";
        customer.Notes = "newer customer deletion intent";
        await dbContext.SaveChangesAsync();

        var beforeSelected = await ReadTrackedStateAsync<Invoice>(dbContext, selected.Id);
        var beforeSibling = await ReadTrackedStateAsync<Invoice>(dbContext, sibling.Id);
        var beforeCustomer = await ReadTrackedStateAsync<Customer>(dbContext, customer.Id);
        var beforeStock = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(current => current.ItemId == item.Id)
            .Select(current => new { current.Quantity, current.Revision })
            .SingleAsync();
        var beforeItem = await ReadTrackedStateAsync<Item>(dbContext, item.Id);
        var beforeLedger = await dbContext.InventoryLedgerEntries.AsNoTracking().CountAsync();

        var result = await RestoreAsync(dbContext, "invoice", selected.Id, expectedRevision);

        Assert.False(result.Success);
        Assert.Contains("같은 삭제", result.Message, StringComparison.Ordinal);
        Assert.Equal(beforeSelected, await ReadTrackedStateAsync<Invoice>(dbContext, selected.Id));
        Assert.Equal(beforeSibling, await ReadTrackedStateAsync<Invoice>(dbContext, sibling.Id));
        Assert.Equal(beforeCustomer, await ReadTrackedStateAsync<Customer>(dbContext, customer.Id));
        Assert.Equal(beforeStock, await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(current => current.ItemId == item.Id)
            .Select(current => new { current.Quantity, current.Revision })
            .SingleAsync());
        Assert.Equal(beforeItem, await ReadTrackedStateAsync<Item>(dbContext, item.Id));
        Assert.Equal(beforeLedger, await dbContext.InventoryLedgerEntries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task RestoreContract_SameDeletionFingerprint_RestoresParentAndChildAtomically()
    {
        await using var dbContext = CreateDbContext();
        var customer = CreateDeletedCustomer();
        var contract = CreateDeletedContract(customer.Id);
        dbContext.AddRange(customer, contract);
        await dbContext.SaveChangesAsync();
        var deletedCustomerRevision = customer.Revision;
        var deletedContractRevision = contract.Revision;
        Assert.Equal(customer.UpdatedAtUtc, contract.UpdatedAtUtc);

        var result = await RestoreAsync(dbContext, "contract", contract.Id, deletedContractRevision);

        Assert.True(result.Success, result.Message);
        var restoredCustomer = await ReadTrackedStateAsync<Customer>(dbContext, customer.Id);
        var restoredContract = await ReadTrackedStateAsync<CustomerContract>(dbContext, contract.Id);
        Assert.False(restoredCustomer.IsDeleted);
        Assert.False(restoredContract.IsDeleted);
        Assert.True(restoredCustomer.Revision > deletedCustomerRevision);
        Assert.True(restoredContract.Revision > deletedContractRevision);
    }

    [Fact]
    public async Task RestoreContract_ConcurrentParentChangeAfterGuard_RollsBackChildRestore()
    {
        var interceptor = new ConcurrentParentChangeInterceptor();
        await using var dbContext = CreateDbContext(interceptor);
        var customer = CreateDeletedCustomer();
        var contract = CreateDeletedContract(customer.Id);
        contract.IsPrimary = true;
        dbContext.AddRange(customer, contract);
        await dbContext.SaveChangesAsync();
        var expectedRevision = contract.Revision;
        var deletedCustomerRevision = customer.Revision;
        var deletedContractState = await ReadTrackedStateAsync<CustomerContract>(dbContext, contract.Id);
        interceptor.Arm(customer.Id);

        var result = await RestoreAsync(dbContext, "contract", contract.Id, expectedRevision);

        Assert.False(result.Success);
        dbContext.ChangeTracker.Clear();
        var concurrentCustomer = await ReadTrackedStateAsync<Customer>(dbContext, customer.Id);
        var preservedContract = await ReadTrackedStateAsync<CustomerContract>(dbContext, contract.Id);
        Assert.True(concurrentCustomer.IsDeleted);
        Assert.True(concurrentCustomer.Revision > deletedCustomerRevision);
        Assert.Equal(deletedContractState, preservedContract);
    }

    [Fact]
    public async Task RestoreInvoice_SameEventActiveDetachedTransaction_RelinksSuccessfully()
    {
        await using var dbContext = CreateDbContext();
        var fixture = await SeedInvoiceCascadeAsync(dbContext);

        var result = await RestoreAsync(
            dbContext,
            "invoice",
            fixture.InvoiceId,
            fixture.InvoiceRevision);

        Assert.True(result.Success, result.Message);
        dbContext.ChangeTracker.Clear();
        var invoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == fixture.InvoiceId);
        var payment = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == fixture.PaymentId);
        var transaction = await dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == fixture.TransactionId);
        Assert.False(invoice.IsDeleted);
        Assert.False(payment.IsDeleted);
        Assert.False(transaction.IsDeleted);
        Assert.Equal(invoice.Id, transaction.LinkedInvoiceId);
        Assert.Equal(payment.Amount, transaction.SettlementAmount);
    }

    [Fact]
    public async Task RestoreInvoice_NewerActiveDetachedTransaction_RejectsAllWrites()
    {
        await using var dbContext = CreateDbContext();
        var fixture = await SeedInvoiceCascadeAsync(dbContext);
        var transaction = await dbContext.Transactions
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == fixture.TransactionId);
        transaction.Note = "edited after invoice deletion";
        await dbContext.SaveChangesAsync();
        var before = await ReadInvoiceCascadeSnapshotAsync(dbContext, fixture);

        var result = await RestoreAsync(
            dbContext,
            "invoice",
            fixture.InvoiceId,
            fixture.InvoiceRevision);

        Assert.False(result.Success);
        Assert.Contains("같은 삭제", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, await ReadInvoiceCascadeSnapshotAsync(dbContext, fixture));
    }

    [Fact]
    public async Task RestorePayment_NewerActiveDetachedTransaction_RejectsWithoutContaminatingNextBatchItem()
    {
        await using var dbContext = CreateDbContext();
        var fixture = await SeedInvoiceCascadeAsync(dbContext);
        var transaction = await dbContext.Transactions
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == fixture.TransactionId);
        transaction.Note = "edited after payment deletion";
        await dbContext.SaveChangesAsync();

        var nextItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "batch item after failed payment restore",
            NameMatchKey = "batchitemafterfailedpaymentrestore",
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            IsDeleted = true
        };
        dbContext.Items.Add(nextItem);
        await dbContext.SaveChangesAsync();
        var before = await ReadInvoiceCascadeSnapshotAsync(dbContext, fixture);

        var controller = CreateController(dbContext);
        var response = await controller.Restore(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = fixture.PaymentId,
                    Kind = "payment",
                    ExpectedRevision = fixture.PaymentRevision
                },
                new RecycleBinMutationTargetDto
                {
                    EntityId = nextItem.Id,
                    Kind = "item",
                    ExpectedRevision = nextItem.Revision
                }
            ]
        }, CancellationToken.None);

        var payload = Assert.IsType<RecycleBinMutationResultDto>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(2, payload.Results.Count);
        Assert.False(payload.Results[0].Success);
        Assert.Contains("같은 삭제", payload.Results[0].Message, StringComparison.Ordinal);
        Assert.True(payload.Results[1].Success, payload.Results[1].Message);
        Assert.Equal(1, payload.SucceededCount);
        Assert.Equal(before, await ReadInvoiceCascadeSnapshotAsync(dbContext, fixture));
        Assert.False(await dbContext.Items
            .IgnoreQueryFilters()
            .Where(current => current.Id == nextItem.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    private async Task<RecycleBinMutationItemResultDto> RestoreAsync(
        AppDbContext dbContext,
        string kind,
        Guid entityId,
        long expectedRevision)
    {
        var controller = CreateController(dbContext);
        var response = await controller.Restore(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = entityId,
                    Kind = kind,
                    ExpectedRevision = expectedRevision
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        return Assert.Single(payload.Results);
    }

    private AppDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection);
        if (interceptors.Length > 0)
            optionsBuilder.AddInterceptors(interceptors);

        var dbContext = new AppDbContext(optionsBuilder.Options, _currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private RecycleBinController CreateController(AppDbContext dbContext)
        => new(
            dbContext,
            new OfficeScopeService(_currentUser, dbContext),
            NoOpStoredFileReferenceReconciler.Instance,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext),
            NoOpStoredFileDeferredDeletionQueue.Instance);

    private static async Task<InvoiceCascadeFixture> SeedInvoiceCascadeAsync(AppDbContext dbContext)
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "active transaction cascade item",
            NameMatchKey = "activetransactioncascadeitem",
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 25m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 25m
        });
        await dbContext.SaveChangesAsync();

        var customer = CreateDeletedCustomer();
        customer.IsDeleted = false;
        var invoice = CreateDeletedInvoice(customer.Id, Guid.NewGuid(), 1, isLatest: true);
        var line = new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            ItemTrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            Quantity = 2m,
            UnitPrice = 100m,
            LineAmount = 200m,
            IsDeleted = true
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 8, 9),
            Amount = 200m,
            Note = "deleted with invoice",
            IsDeleted = true
        };
        var transaction = new TransactionRecord
        {
            Id = payment.Id,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 8, 9),
            TransactionKind = "전표수금",
            LinkedInvoiceId = null,
            LinkedInvoiceNumber = string.Empty,
            SettlementAmount = 0m,
            ReceiptTotal = payment.Amount,
            BankReceipt = payment.Amount,
            Note = "active transaction detached with invoice",
            IsDeleted = false
        };
        var paymentAttachment = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            FileName = "payment-proof.pdf",
            MimeType = "application/pdf",
            StoragePath = "payment-proof.pdf",
            IsDeleted = true
        };
        var transactionAttachment = new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            FileName = "transaction-proof.pdf",
            MimeType = "application/pdf",
            StoragePath = "transaction-proof.pdf",
            IsDeleted = false
        };
        dbContext.AddRange(
            customer,
            invoice,
            line,
            payment,
            transaction,
            paymentAttachment,
            transactionAttachment);
        await dbContext.SaveChangesAsync();
        Assert.Equal(invoice.UpdatedAtUtc, payment.UpdatedAtUtc);
        Assert.Equal(invoice.UpdatedAtUtc, transaction.UpdatedAtUtc);

        return new InvoiceCascadeFixture(
            invoice.Id,
            invoice.Revision,
            line.Id,
            payment.Id,
            payment.Revision,
            transaction.Id,
            paymentAttachment.Id,
            transactionAttachment.Id,
            item.Id);
    }

    private static async Task<InvoiceCascadeSnapshot> ReadInvoiceCascadeSnapshotAsync(
        AppDbContext dbContext,
        InvoiceCascadeFixture fixture)
    {
        dbContext.ChangeTracker.Clear();
        var stock = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Where(current => current.ItemId == fixture.ItemId)
            .Select(current => new { current.Quantity, current.Revision })
            .SingleAsync();
        return new InvoiceCascadeSnapshot(
            await ReadTrackedStateAsync<Invoice>(dbContext, fixture.InvoiceId),
            await ReadTrackedStateAsync<Payment>(dbContext, fixture.PaymentId),
            await ReadTrackedStateAsync<TransactionRecord>(dbContext, fixture.TransactionId),
            await ReadTrackedStateAsync<PaymentAttachment>(dbContext, fixture.PaymentAttachmentId),
            await ReadTrackedStateAsync<TransactionAttachment>(dbContext, fixture.TransactionAttachmentId),
            await ReadTrackedStateAsync<Item>(dbContext, fixture.ItemId),
            await dbContext.InvoiceLines
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(current => current.Id == fixture.InvoiceLineId)
                .Select(current => current.IsDeleted)
                .SingleAsync(),
            stock.Quantity,
            stock.Revision,
            await dbContext.InventoryLedgerEntries.AsNoTracking().CountAsync());
    }

    private static Customer CreateDeletedCustomer()
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "cascade guard customer",
            NameMatchKey = "cascadeguardcustomer",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        };

    private static CustomerContract CreateDeletedContract(Guid customerId)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = "cascade-guard.pdf",
            IsDeleted = true
        };

    private static Invoice CreateDeletedInvoice(
        Guid customerId,
        Guid versionGroupId,
        int versionNumber,
        bool isLatest)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = $"CASCADE-{versionNumber:000}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, versionNumber),
            VersionGroupId = versionGroupId,
            VersionNumber = versionNumber,
            IsLatestVersion = isLatest,
            IsDeleted = true
        };

    private static Task<TrackedState> ReadTrackedStateAsync<TEntity>(AppDbContext dbContext, Guid id)
        where TEntity : TrackedEntity
        => dbContext.Set<TEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == id)
            .Select(current => new TrackedState(
                current.IsDeleted,
                current.UpdatedAtUtc,
                current.Revision))
            .SingleAsync();

    public void Dispose() => _connection.Dispose();

    private sealed record TrackedState(bool IsDeleted, DateTime UpdatedAtUtc, long Revision);

    private sealed record InvoiceCascadeFixture(
        Guid InvoiceId,
        long InvoiceRevision,
        Guid InvoiceLineId,
        Guid PaymentId,
        long PaymentRevision,
        Guid TransactionId,
        Guid PaymentAttachmentId,
        Guid TransactionAttachmentId,
        Guid ItemId);

    private sealed record InvoiceCascadeSnapshot(
        TrackedState Invoice,
        TrackedState Payment,
        TrackedState Transaction,
        TrackedState PaymentAttachment,
        TrackedState TransactionAttachment,
        TrackedState Item,
        bool InvoiceLineIsDeleted,
        decimal StockQuantity,
        long StockRevision,
        int LedgerCount);

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username { get; } = "cascade-guard-admin";
        public string TenantCode { get; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public IReadOnlyCollection<string> Permissions { get; } = [PermissionNames.DataBackupRestore];
        public bool HasPermission(string permission) => true;
    }

    private sealed class ConcurrentParentChangeInterceptor : DbCommandInterceptor
    {
        private Guid? _customerId;
        private int _customerContractReads;

        public void Arm(Guid customerId)
        {
            _customerId = customerId;
            _customerContractReads = 0;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_customerId is not Guid customerId ||
                !command.CommandText.Contains("CustomerContracts", StringComparison.Ordinal) ||
                ++_customerContractReads < 2)
            {
                return result;
            }

            _customerId = null;
            await using var concurrentCommand = command.Connection!.CreateCommand();
            concurrentCommand.CommandText =
                "UPDATE \"Customers\" " +
                "SET \"Revision\" = \"Revision\" + 100, \"UpdatedAtUtc\" = @updatedAtUtc " +
                "WHERE \"Id\" = @id;";
            AddParameter(concurrentCommand, "@updatedAtUtc", DateTime.UtcNow.AddSeconds(1));
            AddParameter(concurrentCommand, "@id", customerId);
            await concurrentCommand.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
