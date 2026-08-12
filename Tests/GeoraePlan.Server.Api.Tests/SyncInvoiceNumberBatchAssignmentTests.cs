using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncInvoiceNumberBatchAssignmentTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;
    private readonly SyncController _controller;

    public SyncInvoiceNumberBatchAssignmentTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var currentUser = new TestCurrentUserContext();
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options, currentUser, revisionClock);
        _dbContext.Database.EnsureCreated();
        _controller = new SyncController(
            _dbContext,
            currentUser,
            new InvoiceNumberService(_dbContext),
            new OfficeScopeService(currentUser, _dbContext),
            new StubCentralFileStorage(),
            revisionClock,
            new InventoryLedgerService(_dbContext),
            new InvoiceStockSnapshotService(_dbContext, revisionClock),
            new RentalAssignmentHistoryService(_dbContext),
            new RentalSettlementRecalculationService(_dbContext));
    }

    [Fact]
    public async Task Push_AssignsDistinctConsecutiveInvoiceAndTaxNumbers_ToMultipleNewInvoicesInOneBatch()
    {
        var customer = CreateCustomer("Batch number customer");
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            InvoiceNumber = "202607-0041",
            TaxInvoiceIssued = true,
            TaxInvoiceNumber = "TAX-202607-0100",
            VersionGroupId = Guid.NewGuid(),
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 1)
        });
        await _dbContext.SaveChangesAsync();

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "same-push-invoice-number-batch",
            Invoices =
            [
                CreateInvoiceDto(
                    firstId,
                    customer,
                    new DateOnly(2026, 7, 20),
                    new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc)),
                CreateInvoiceDto(
                    secondId,
                    customer,
                    new DateOnly(2026, 7, 21),
                    new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc))
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal("202607-0042", result.AssignedInvoiceNumbers[firstId]);
        Assert.Equal("202607-0043", result.AssignedInvoiceNumbers[secondId]);
        Assert.Equal("TAX-202607-0101", result.AssignedTaxInvoiceNumbers[firstId]);
        Assert.Equal("TAX-202607-0102", result.AssignedTaxInvoiceNumbers[secondId]);

        _dbContext.ChangeTracker.Clear();
        var stored = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == firstId || invoice.Id == secondId)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.InvoiceNumber,
                invoice.TaxInvoiceNumber
            })
            .ToListAsync();

        var storedFirst =
            Assert.Single(stored, invoice => invoice.Id == firstId);
        var storedSecond =
            Assert.Single(stored, invoice => invoice.Id == secondId);
        Assert.Equal("202607-0042", storedFirst.InvoiceNumber);
        Assert.Equal("202607-0043", storedSecond.InvoiceNumber);
        Assert.Equal("TAX-202607-0101", storedFirst.TaxInvoiceNumber);
        Assert.Equal("TAX-202607-0102", storedSecond.TaxInvoiceNumber);
    }

    [Fact]
    public async Task Push_AssignsDistinctConsecutiveTaxNumbers_ToMultipleBlankTaxNumbersInOneBatch()
    {
        var customer = CreateCustomer("Tax batch number customer");
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(CreateStoredInvoice(
            customer,
            "202607-0001",
            "TAX-202607-0100",
            new DateOnly(2026, 7, 1)));
        await _dbContext.SaveChangesAsync();

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = CreateInvoiceDto(
            firstId,
            customer,
            new DateOnly(2026, 7, 20),
            new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
        first.InvoiceNumber = "MANUAL-TAX-BATCH-001";
        var second = CreateInvoiceDto(
            secondId,
            customer,
            new DateOnly(2026, 7, 21),
            new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc));
        second.InvoiceNumber = "MANUAL-TAX-BATCH-002";

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "same-push-tax-number-batch",
            Invoices = [first, second]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal("TAX-202607-0101", result.AssignedTaxInvoiceNumbers[firstId]);
        Assert.Equal("TAX-202607-0102", result.AssignedTaxInvoiceNumbers[secondId]);

        _dbContext.ChangeTracker.Clear();
        var storedNumbers = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == firstId || invoice.Id == secondId)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.TaxInvoiceNumber
            })
            .ToListAsync();
        Assert.Equal(
            "TAX-202607-0101",
            Assert.Single(
                storedNumbers,
                invoice => invoice.Id == firstId)
                .TaxInvoiceNumber);
        Assert.Equal(
            "TAX-202607-0102",
            Assert.Single(
                storedNumbers,
                invoice => invoice.Id == secondId)
                .TaxInvoiceNumber);
    }

    [Fact]
    public async Task Push_AssignsConsecutiveNumbers_WhenABlankExistingInvoiceAndBlankNewInvoiceShareTheBatch()
    {
        var customer = CreateCustomer("Modified and added batch customer");
        var existingBlank = CreateStoredInvoice(
            customer,
            string.Empty,
            string.Empty,
            new DateOnly(2026, 7, 19));
        existingBlank.TaxInvoiceIssued = true;
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.AddRange(
            CreateStoredInvoice(
                customer,
                "202607-0041",
                "TAX-202607-0100",
                new DateOnly(2026, 7, 1)),
            existingBlank);
        await _dbContext.SaveChangesAsync();

        var existingUpdate = CreateInvoiceDto(
            existingBlank.Id,
            customer,
            existingBlank.InvoiceDate,
            new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc));
        existingUpdate.VersionGroupId =
            existingBlank.VersionGroupId;
        existingUpdate.Revision = existingBlank.Revision;
        existingUpdate.ExpectedRevision =
            existingBlank.Revision;
        var newId = Guid.NewGuid();
        var newInvoice = CreateInvoiceDto(
            newId,
            customer,
            new DateOnly(2026, 7, 20),
            new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "modified-and-added-number-batch",
            Invoices = [existingUpdate, newInvoice]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "202607-0042",
            result.AssignedInvoiceNumbers[existingBlank.Id]);
        Assert.Equal(
            "202607-0043",
            result.AssignedInvoiceNumbers[newId]);
        Assert.Equal(
            "TAX-202607-0101",
            result.AssignedTaxInvoiceNumbers[existingBlank.Id]);
        Assert.Equal(
            "TAX-202607-0102",
            result.AssignedTaxInvoiceNumbers[newId]);

        _dbContext.ChangeTracker.Clear();
        var storedNumbers = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice =>
                invoice.Id == existingBlank.Id ||
                invoice.Id == newId)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.InvoiceNumber,
                invoice.TaxInvoiceNumber
            })
            .ToListAsync();
        var storedExisting = Assert.Single(
            storedNumbers,
            invoice => invoice.Id == existingBlank.Id);
        var storedNew = Assert.Single(
            storedNumbers,
            invoice => invoice.Id == newId);
        Assert.Equal("202607-0042", storedExisting.InvoiceNumber);
        Assert.Equal("202607-0043", storedNew.InvoiceNumber);
        Assert.Equal(
            "TAX-202607-0101",
            storedExisting.TaxInvoiceNumber);
        Assert.Equal(
            "TAX-202607-0102",
            storedNew.TaxInvoiceNumber);
    }

    [Fact]
    public async Task Push_BlankNumbersBeforeLaterExplicitNumbers_ReserveTheWholeValidBatch()
    {
        var customer = CreateCustomer("Future explicit number customer");
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(CreateStoredInvoice(
            customer,
            "202607-0041",
            "TAX-202607-0100",
            new DateOnly(2026, 7, 1)));
        await _dbContext.SaveChangesAsync();

        var blankId = Guid.NewGuid();
        var explicitId = Guid.NewGuid();
        var blank = CreateInvoiceDto(
            blankId,
            customer,
            new DateOnly(2026, 7, 20),
            new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
        var explicitNumber = CreateInvoiceDto(
            explicitId,
            customer,
            new DateOnly(2026, 7, 21),
            new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc));
        explicitNumber.InvoiceNumber = "202607-0042";
        explicitNumber.TaxInvoiceNumber = "tax-202607-0101";

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "future-explicit-invoice-number-reservation",
            Invoices = [blank, explicitNumber]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal("202607-0043", result.AssignedInvoiceNumbers[blankId]);
        Assert.Equal("TAX-202607-0102", result.AssignedTaxInvoiceNumbers[blankId]);
        Assert.DoesNotContain(explicitId, result.AssignedInvoiceNumbers.Keys);
        Assert.DoesNotContain(explicitId, result.AssignedTaxInvoiceNumbers.Keys);

        _dbContext.ChangeTracker.Clear();
        var storedBlank = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .SingleAsync(invoice => invoice.Id == blankId);
        var storedExplicit = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .SingleAsync(invoice => invoice.Id == explicitId);
        Assert.Equal("202607-0043", storedBlank.InvoiceNumber);
        Assert.Equal("TAX-202607-0102", storedBlank.TaxInvoiceNumber);
        Assert.Equal("202607-0042", storedExplicit.InvoiceNumber);
        Assert.Equal("tax-202607-0101", storedExplicit.TaxInvoiceNumber);
    }

    [Fact]
    public async Task Push_AllowsAValidVersionChainToReuseItsExplicitDocumentNumbers()
    {
        var customer = CreateCustomer("Version number reuse customer");
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = CreateInvoiceDto(
            firstId,
            customer,
            new DateOnly(2026, 7, 10),
            new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));
        first.InvoiceNumber = "VERSION-CHAIN-DOCUMENT";
        first.TaxInvoiceNumber = "TAX-VERSION-CHAIN-DOCUMENT";
        first.IsLatestVersion = false;
        first.Revision = 1_000;
        first.ExpectedRevision = 1_000;
        var second = CreateInvoiceDto(
            secondId,
            customer,
            new DateOnly(2026, 7, 11),
            new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));
        second.InvoiceNumber = first.InvoiceNumber;
        second.TaxInvoiceNumber = first.TaxInvoiceNumber;
        second.VersionGroupId = firstId;
        second.VersionNumber = 2;
        second.PreviousVersionId = firstId;
        second.Revision = 2_000;
        second.ExpectedRevision = 2_000;

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "same-version-group-number-reuse",
            Invoices = [second, first]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Empty(result.AssignedInvoiceNumbers);
        Assert.Empty(result.AssignedTaxInvoiceNumbers);

        _dbContext.ChangeTracker.Clear();
        var stored = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.VersionGroupId == firstId)
            .OrderBy(invoice => invoice.VersionNumber)
            .ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(
            stored,
            invoice => Assert.Equal(
                "VERSION-CHAIN-DOCUMENT",
                invoice.InvoiceNumber));
        Assert.All(
            stored,
            invoice => Assert.Equal(
                "TAX-VERSION-CHAIN-DOCUMENT",
                invoice.TaxInvoiceNumber));
    }

    [Fact]
    public async Task Push_ExactMutationReplay_ReconstructsAssignedNumberMaps()
    {
        var customer = CreateCustomer("Exact replay number customer");
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoiceDto(
            invoiceId,
            customer,
            new DateOnly(2026, 7, 20),
            new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
        invoice.MutationId = $"invoice-number-replay-{Guid.NewGuid():N}";
        var request = new SyncPushRequest
        {
            DeviceId = "invoice-number-exact-replay",
            Invoices = [invoice]
        };

        var firstResponse =
            await _controller.Push(request, CancellationToken.None);
        var firstOk =
            Assert.IsType<OkObjectResult>(firstResponse.Result);
        var firstResult =
            Assert.IsType<SyncPushResult>(firstOk.Value);
        Assert.Equal("202607-0001", firstResult.AssignedInvoiceNumbers[invoiceId]);
        Assert.Equal(
            "TAX-202607-0001",
            firstResult.AssignedTaxInvoiceNumbers[invoiceId]);

        var replayResponse =
            await _controller.Push(request, CancellationToken.None);
        var replayOk =
            Assert.IsType<OkObjectResult>(replayResponse.Result);
        var replayResult =
            Assert.IsType<SyncPushResult>(replayOk.Value);
        Assert.Equal(1, replayResult.AcceptedCount);
        Assert.Equal(1, replayResult.DuplicateMutationCount);
        Assert.Equal(
            "202607-0001",
            replayResult.AssignedInvoiceNumbers[invoiceId]);
        Assert.Equal(
            "TAX-202607-0001",
            replayResult.AssignedTaxInvoiceNumbers[invoiceId]);

        Assert.Equal(
            1,
            await _dbContext.Invoices
                .IgnoreQueryFilters()
                .CountAsync(invoice => invoice.Id == invoiceId));

        _dbContext.ChangeTracker.Clear();
        var storedRevision = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(stored => stored.Id == invoiceId)
            .Select(stored => stored.Revision)
            .SingleAsync();
        var delete = CreateInvoiceDto(
            invoiceId,
            customer,
            new DateOnly(2026, 7, 20),
            new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));
        delete.InvoiceNumber = "202607-0001";
        delete.TaxInvoiceNumber = "TAX-202607-0001";
        delete.IsDeleted = true;
        delete.Revision = storedRevision;
        delete.ExpectedRevision = storedRevision;
        delete.MutationId =
            $"invoice-number-delete-{Guid.NewGuid():N}";

        var deleteResponse = await _controller.Push(
            new SyncPushRequest
            {
                DeviceId = "invoice-number-delete-after-replay",
                Invoices = [delete]
            },
            CancellationToken.None);
        var deleteOk =
            Assert.IsType<OkObjectResult>(deleteResponse.Result);
        var deleteResult =
            Assert.IsType<SyncPushResult>(deleteOk.Value);
        Assert.Equal(1, deleteResult.AcceptedCount);
        Assert.Equal(0, deleteResult.ConflictCount);

        var replayAfterDeleteResponse =
            await _controller.Push(request, CancellationToken.None);
        var replayAfterDeleteOk =
            Assert.IsType<OkObjectResult>(
                replayAfterDeleteResponse.Result);
        var replayAfterDeleteResult =
            Assert.IsType<SyncPushResult>(
                replayAfterDeleteOk.Value);
        Assert.Equal(
            "202607-0001",
            replayAfterDeleteResult
                .AssignedInvoiceNumbers[invoiceId]);
        Assert.Equal(
            "TAX-202607-0001",
            replayAfterDeleteResult
                .AssignedTaxInvoiceNumbers[invoiceId]);
    }

    [Fact]
    public async Task NumberServices_PreserveCustomerAndMonthScopes_AndIgnoreMalformedSequences()
    {
        var customer = CreateCustomer("Scoped number customer");
        var otherCustomer = CreateCustomer("Other scoped number customer");
        _dbContext.Customers.AddRange(customer, otherCustomer);
        _dbContext.Invoices.AddRange(
            CreateStoredInvoice(
                customer,
                "202607-0007",
                "TAX-202607-0007",
                new DateOnly(2026, 7, 1)),
            CreateStoredInvoice(
                customer,
                "202608-0015",
                "TAX-202608-0040",
                new DateOnly(2026, 8, 1)),
            CreateStoredInvoice(
                otherCustomer,
                "202607-NOT-A-SEQUENCE",
                "TAX-202607-NOT-A-SEQUENCE",
                new DateOnly(2026, 7, 2)),
            CreateStoredInvoice(
                otherCustomer,
                "202607-0020",
                "tax-202607-0088",
                new DateOnly(2026, 7, 3)));
        await _dbContext.SaveChangesAsync();

        _dbContext.Invoices.Add(CreateStoredInvoice(
            customer,
            "202607-0099",
            "tax-202607-0099",
            new DateOnly(2026, 7, 4)));
        var trackedNullNumbers = CreateStoredInvoice(
            customer,
            "TRACKED-NULL",
            "TAX-TRACKED-NULL",
            new DateOnly(2026, 7, 5));
        trackedNullNumbers.InvoiceNumber = null!;
        trackedNullNumbers.TaxInvoiceNumber = null!;
        _dbContext.Invoices.Add(trackedNullNumbers);

        var invoiceNumberService = new InvoiceNumberService(_dbContext);
        Assert.Equal(
            "202607-0100",
            await invoiceNumberService.GenerateAsync(
                customer.Id,
                new DateOnly(2026, 7, 31),
                ["202608-9999", "CUSTOM-9999"]));
        Assert.Equal(
            "202607-0021",
            await invoiceNumberService.GenerateAsync(
                otherCustomer.Id,
                new DateOnly(2026, 7, 31)));
        Assert.Equal(
            "202608-0016",
            await invoiceNumberService.GenerateAsync(
                customer.Id,
                new DateOnly(2026, 8, 31)));
        Assert.Equal(
            "TAX-202607-0100",
            await TaxInvoiceNumberAssignmentService.GenerateAsync(
                _dbContext,
                new DateOnly(2026, 7, 31),
                ["TAX-202608-9999", "CUSTOM-9999"]));
        Assert.Equal(
            "TAX-202608-0041",
            await TaxInvoiceNumberAssignmentService.GenerateAsync(
                _dbContext,
                new DateOnly(2026, 8, 31)));
    }

    [Fact]
    public async Task NumberServices_FailClosed_WhenASequenceIsExhausted()
    {
        var customer = CreateCustomer("Exhausted number customer");
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(CreateStoredInvoice(
            customer,
            $"202610-{int.MaxValue}",
            $"TAX-202610-{int.MaxValue}",
            new DateOnly(2026, 10, 1)));
        await _dbContext.SaveChangesAsync();

        var invoiceNumberService = new InvoiceNumberService(_dbContext);
        var invoiceException =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => invoiceNumberService.GenerateAsync(
                    customer.Id,
                    new DateOnly(2026, 10, 31)));
        Assert.Contains(
            "exhausted",
            invoiceException.Message,
            StringComparison.OrdinalIgnoreCase);

        var taxException =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => TaxInvoiceNumberAssignmentService.GenerateAsync(
                    _dbContext,
                    new DateOnly(2026, 10, 31)));
        Assert.Contains(
            "exhausted",
            taxException.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NumberServices_ReuseRolledBackNumbers_AfterTheContextIsCleared()
    {
        var customer = CreateCustomer("Rollback number customer");
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        await using (var transaction =
                     await _dbContext.Database.BeginTransactionAsync())
        {
            var invoiceNumberService = new InvoiceNumberService(_dbContext);
            var invoice = CreateStoredInvoice(
                customer,
                await invoiceNumberService.GenerateAsync(
                    customer.Id,
                    new DateOnly(2026, 9, 1)),
                string.Empty,
                new DateOnly(2026, 9, 1));
            invoice.TaxInvoiceIssued = true;
            invoice.TaxInvoiceNumber =
                await TaxInvoiceNumberAssignmentService.GenerateAsync(
                    _dbContext,
                    invoice.InvoiceDate);
            _dbContext.Invoices.Add(invoice);
            await _dbContext.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        _dbContext.ChangeTracker.Clear();
        var afterRollbackService = new InvoiceNumberService(_dbContext);
        Assert.Equal(
            "202609-0001",
            await afterRollbackService.GenerateAsync(
                customer.Id,
                new DateOnly(2026, 9, 30)));
        Assert.Equal(
            "TAX-202609-0001",
            await TaxInvoiceNumberAssignmentService.GenerateAsync(
                _dbContext,
                new DateOnly(2026, 9, 30)));
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Customer CreateCustomer(string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
            TradeType = CustomerClassificationNormalizer.Sales
        };

    private static Invoice CreateStoredInvoice(
        Customer customer,
        string invoiceNumber,
        string taxInvoiceNumber,
        DateOnly invoiceDate)
    {
        var id = Guid.NewGuid();
        return new Invoice
        {
            Id = id,
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            InvoiceNumber = invoiceNumber,
            TaxInvoiceIssued = !string.IsNullOrWhiteSpace(taxInvoiceNumber),
            TaxInvoiceNumber = taxInvoiceNumber,
            VersionGroupId = id,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = invoiceDate
        };
    }

    private static InvoiceDto CreateInvoiceDto(
        Guid id,
        Customer customer,
        DateOnly invoiceDate,
        DateTime createdAtUtc) =>
        new()
        {
            Id = id,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            InvoiceNumber = string.Empty,
            TaxInvoiceIssued = true,
            TaxInvoiceNumber = string.Empty,
            VersionGroupId = id,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = invoiceDate,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            Lines = []
        };

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "same-push-number-test";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission) => true;
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null) =>
            fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
