using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RecycleBinPurgeSerializationTests
{
    [Fact]
    public async Task PurgeInvoice_AfterPriorLockHolderCommitsLinkedPaymentAndTransaction_RejectsAndPreservesAllRows()
    {
        var databaseName = $"recycle-invoice-purge-lock-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeperConnection = new SqliteConnection(connectionString);
        await keeperConnection.OpenAsync();

        var user = new TestCurrentUserContext();
        var options = CreateOptions(connectionString);
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var settlementId = Guid.NewGuid();
        long expectedRevision;

        await using (var seedContext = CreateDbContext(options, user))
        {
            await seedContext.Database.EnsureCreatedAsync();
            seedContext.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "영구삭제 잠금 회귀 거래처",
                NameMatchKey = "영구삭제잠금회귀거래처",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedContext.Invoices.Add(new Invoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "PURGE-LOCK-INVOICE-001",
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 26),
                TotalAmount = 10_000m,
                SupplyAmount = 10_000m,
                IsDeleted = true
            });
            await seedContext.SaveChangesAsync();
            expectedRevision = await seedContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
        }

        await using var lockOwnerContext = CreateDbContext(options, user);
        await using var purgeContext = CreateDbContext(options, user);
        var controller = CreateController(purgeContext, user);
        var lockScope = await InventoryMutationTransactionScope.BeginAsync(
            lockOwnerContext,
            serializeInventoryMutations: true);
        Task<ActionResult<RecycleBinMutationResultDto>>? purgeTask = null;

        try
        {
            purgeTask = controller.Purge(
                new RecycleBinMutationRequest
                {
                    Items =
                    [
                        new RecycleBinMutationTargetDto
                        {
                            EntityId = invoiceId,
                            Kind = "invoice",
                            ExpectedRevision = expectedRevision
                        }
                    ]
                },
                CancellationToken.None);

            await Task.Delay(250);
            Assert.False(purgeTask.IsCompleted);

            lockOwnerContext.Payments.Add(new Payment
            {
                Id = settlementId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 7, 26),
                Amount = 10_000m,
                Note = "선행 잠금 보유자가 확정한 수금"
            });
            lockOwnerContext.Transactions.Add(new TransactionRecord
            {
                Id = settlementId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 7, 26),
                TransactionKind = "invoice-receipt",
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "PURGE-LOCK-INVOICE-001",
                SettlementAmount = 10_000m,
                ReceiptTotal = 10_000m,
                Note = "선행 잠금 보유자가 확정한 연결 거래내역"
            });
            await lockOwnerContext.SaveChangesAsync();
            await lockScope.CommitAsync();
        }
        finally
        {
            await lockScope.DisposeAsync();
        }

        Assert.NotNull(purgeTask);
        var response = await purgeTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("연결된 거래내역", result.Message, StringComparison.Ordinal);

        await using var verificationContext = CreateDbContext(options, user);
        Assert.True(await verificationContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(invoice => invoice.Id == invoiceId && invoice.IsDeleted));
        Assert.True(await verificationContext.Payments
            .IgnoreQueryFilters()
            .AnyAsync(payment => payment.Id == settlementId && payment.InvoiceId == invoiceId));
        Assert.True(await verificationContext.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(transaction =>
                transaction.Id == settlementId &&
                transaction.LinkedInvoiceId == invoiceId));
        Assert.False(await verificationContext.RecycleBinPurgeRecords
            .IgnoreQueryFilters()
            .AnyAsync(record => record.Kind == "invoice" && record.EntityId == invoiceId));
    }

    [Fact]
    public async Task PurgeInventoryTransfer_AfterPriorLockHolderReactivatesTransfer_RejectsFreshStateAndPreservesRow()
    {
        var databaseName = $"recycle-transfer-purge-lock-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeperConnection = new SqliteConnection(connectionString);
        await keeperConnection.OpenAsync();

        var user = new TestCurrentUserContext();
        var options = CreateOptions(connectionString);
        var transferId = Guid.NewGuid();
        long expectedRevision;

        await using (var seedContext = CreateDbContext(options, user))
        {
            await seedContext.Database.EnsureCreatedAsync();
            seedContext.InventoryTransfers.Add(new InventoryTransfer
            {
                Id = transferId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferNumber = "PURGE-LOCK-TRANSFER-001",
                TransferDate = new DateOnly(2026, 7, 26),
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                IsDeleted = true
            });
            await seedContext.SaveChangesAsync();
            expectedRevision = await seedContext.InventoryTransfers
                .IgnoreQueryFilters()
                .Where(transfer => transfer.Id == transferId)
                .Select(transfer => transfer.Revision)
                .SingleAsync();
        }

        await using var lockOwnerContext = CreateDbContext(options, user);
        await using var purgeContext = CreateDbContext(options, user);
        var controller = CreateController(purgeContext, user);
        var lockScope = await InventoryMutationTransactionScope.BeginAsync(
            lockOwnerContext,
            serializeInventoryMutations: true);
        Task<ActionResult<RecycleBinMutationResultDto>>? purgeTask = null;

        try
        {
            purgeTask = controller.Purge(
                new RecycleBinMutationRequest
                {
                    Items =
                    [
                        new RecycleBinMutationTargetDto
                        {
                            EntityId = transferId,
                            Kind = "inventory-transfer",
                            ExpectedRevision = expectedRevision
                        }
                    ]
                },
                CancellationToken.None);

            await Task.Delay(250);
            Assert.False(purgeTask.IsCompleted);

            var transfer = await lockOwnerContext.InventoryTransfers
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == transferId);
            transfer.IsDeleted = false;
            await lockOwnerContext.SaveChangesAsync();
            await lockScope.CommitAsync();
        }
        finally
        {
            await lockScope.DisposeAsync();
        }

        Assert.NotNull(purgeTask);
        var response = await purgeTask!.WaitAsync(TimeSpan.FromSeconds(10));
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("활성 상태 재고이동", result.Message, StringComparison.Ordinal);

        await using var verificationContext = CreateDbContext(options, user);
        Assert.True(await verificationContext.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == transferId && !transfer.IsDeleted));
        Assert.False(await verificationContext.RecycleBinPurgeRecords
            .IgnoreQueryFilters()
            .AnyAsync(record => record.Kind == "inventory-transfer" && record.EntityId == transferId));
    }

    private static DbContextOptions<AppDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

    private static AppDbContext CreateDbContext(
        DbContextOptions<AppDbContext> options,
        ICurrentUserContext user)
        => new(options, user, new RevisionClock());

    private static RecycleBinController CreateController(
        AppDbContext dbContext,
        ICurrentUserContext user)
        => new(
            dbContext,
            new OfficeScopeService(user, dbContext),
            NoOpStoredFileReferenceReconciler.Instance,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext),
            NoOpStoredFileDeferredDeletionQueue.Instance);

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username { get; } = "recycle-purge-lock-test";
        public string TenantCode { get; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;

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
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, area, ownerId, fileId.ToString("N"), fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
