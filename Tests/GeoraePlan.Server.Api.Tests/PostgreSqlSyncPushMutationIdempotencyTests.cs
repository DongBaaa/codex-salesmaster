using System.Data.Common;
using System.Reflection;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlIntegrationCollection
{
    public const string Name = "PostgreSQL integration";
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName)))
        {
            Skip = $"Set {PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName} to run this test.";
        }
    }
}

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlSyncPushMutationIdempotencyTests
{
    internal const string ConnectionVariableName = "GEORAEPLAN_POSTGRES_TEST_CONNECTION";

    [PostgreSqlFact]
    public async Task ConcurrentSaveChanges_AllocateDistinctCommittedRevisionsAcrossDbContexts()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            await using (var initializationDb = CreateDbContext(options, CreateAdminUser()))
                await initializationDb.Database.EnsureCreatedAsync();

            await using var firstDb = CreateDbContext(options, CreateAdminUser());
            await using var secondDb = CreateDbContext(options, CreateAdminUser());
            var firstCustomer = CreateRevisionCustomer("POSTGRES-FIRST-COMMITTED-REVISION");
            var secondCustomer = CreateRevisionCustomer("POSTGRES-SECOND-COMMITTED-REVISION");
            firstDb.Customers.Add(firstCustomer);
            secondDb.Customers.Add(secondCustomer);

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCount = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            async Task SaveFrom(AppDbContext dbContext, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref readyCount) == 2)
                    startGate.TrySetResult();

                await startGate.Task.WaitAsync(cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await Task.WhenAll(
                    SaveFrom(firstDb, timeout.Token),
                    SaveFrom(secondDb, timeout.Token))
                .WaitAsync(timeout.Token);

            Assert.True(firstCustomer.Revision > 0);
            Assert.True(secondCustomer.Revision > 0);
            Assert.NotEqual(firstCustomer.Revision, secondCustomer.Revision);

            await using var verificationDb = CreateDbContext(options, CreateAdminUser());
            var committedRevisions = await verificationDb.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer =>
                    customer.Id == firstCustomer.Id ||
                    customer.Id == secondCustomer.Id)
                .Select(customer => customer.Revision)
                .OrderBy(revision => revision)
                .ToListAsync(timeout.Token);
            Assert.Equal(
                new[] { firstCustomer.Revision, secondCustomer.Revision }.OrderBy(revision => revision),
                committedRevisions);
            Assert.Equal(committedRevisions.Max(), await verificationDb.GetCommittedRevisionAsync(timeout.Token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task Pull_DoesNotExposeUncommittedRowOrRevision_AndExposesBothAfterCommit()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            await using (var initializationDb = CreateDbContext(options, CreateAdminUser()))
                await initializationDb.Database.EnsureCreatedAsync();

            var writerUser = CreateAdminUser();
            var readerUser = CreateAdminUser();
            await using var writerDb = CreateDbContext(options, writerUser);
            await using var readerDb = CreateDbContext(options, readerUser);
            var readerController = CreateController(readerDb, readerUser);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await using var transaction = await writerDb.Database.BeginTransactionAsync(timeout.Token);
            var uncommittedCustomer =
                CreateRevisionCustomer("POSTGRES-UNCOMMITTED-WATERMARK-CUSTOMER");
            writerDb.Customers.Add(uncommittedCustomer);
            await writerDb.SaveChangesAsync(timeout.Token);

            var beforeCommitResponse = await readerController.Pull(0, timeout.Token);
            var beforeCommitOk = Assert.IsType<OkObjectResult>(beforeCommitResponse.Result);
            var beforeCommit = Assert.IsType<SyncPullResponse>(beforeCommitOk.Value);
            Assert.DoesNotContain(
                beforeCommit.Customers,
                customer => customer.Id == uncommittedCustomer.Id);
            Assert.Equal(0, beforeCommit.CurrentServerRevision);

            await transaction.CommitAsync(timeout.Token);

            var afterCommitResponse = await readerController.Pull(0, timeout.Token);
            var afterCommitOk = Assert.IsType<OkObjectResult>(afterCommitResponse.Result);
            var afterCommit = Assert.IsType<SyncPullResponse>(afterCommitOk.Value);
            Assert.Contains(
                afterCommit.Customers,
                customer => customer.Id == uncommittedCustomer.Id);
            Assert.Equal(uncommittedCustomer.Revision, afterCommit.CurrentServerRevision);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentCustomerMutationPushes_AcceptOnceAndReturnOneDuplicate()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var firstUser = CreateAdminUser();
            var secondUser = CreateAdminUser();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;

            await using (var initializationDb = CreateDbContext(options, CreateAdminUser()))
                await initializationDb.Database.EnsureCreatedAsync();

            await using var firstDb = CreateDbContext(options, firstUser);
            await using var secondDb = CreateDbContext(options, secondUser);
            var firstController = CreateController(firstDb, firstUser);
            var secondController = CreateController(secondDb, secondUser);
            var customerId = Guid.NewGuid();
            var mutationId = $"postgres-concurrent-customer:{customerId:N}";
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCount = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            async Task<ActionResult<SyncPushResult>> PushFrom(
                SyncController controller,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref readyCount) == 2)
                    startGate.TrySetResult();

                await startGate.Task.WaitAsync(cancellationToken);
                return await controller.Push(
                    CreateCustomerRequest(customerId, mutationId),
                    cancellationToken);
            }

            var responses = await Task.WhenAll(
                    PushFrom(firstController, timeout.Token),
                    PushFrom(secondController, timeout.Token))
                .WaitAsync(timeout.Token);
            var results = responses.Select(AssertOk).ToList();

            Assert.All(results, result => Assert.Equal(0, result.ConflictCount));
            Assert.Equal(
                1,
                results.Sum(result => result.AcceptedCount - result.DuplicateMutationCount));
            Assert.Equal(1, results.Sum(result => result.DuplicateMutationCount));
            Assert.Equal(2, results.Sum(result => result.AcceptedCount));

            await using var verificationDb = CreateDbContext(options, CreateAdminUser());
            Assert.Equal(
                1,
                await verificationDb.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .CountAsync(customer => customer.Id == customerId, timeout.Token));
            Assert.Equal(
                1,
                await verificationDb.ProcessedSyncMutations
                    .AsNoTracking()
                    .CountAsync(receipt => receipt.MutationId == mutationId, timeout.Token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentSourceEditAndTargetReceipt_SerializesAndReceivesExactlyOnceWithoutStockOrLedgerDuplication()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var sourceDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Host = "127.0.0.1",
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var targetDatabaseBuilder = new NpgsqlConnectionStringBuilder(sourceDatabaseBuilder.ConnectionString)
        {
            Host = "localhost"
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var sourceOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(sourceDatabaseBuilder.ConnectionString)
                .Options;
            var targetOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(targetDatabaseBuilder.ConnectionString)
                .Options;
            var admin = CreateAdminUser("postgres-transfer-seed");
            await using (var initializationDb = CreateDbContext(sourceOptions, admin))
                await initializationDb.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var transferId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var sourceMutationId = $"postgres-transfer-source-edit:{transferId:N}";
            var receiptMutationId = $"postgres-transfer-target-receipt:{transferId:N}";
            var seededAtUtc = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);

            await using (var seedDb = CreateDbContext(sourceOptions, admin))
            {
                seedDb.Items.Add(new Item
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "PostgreSQL concurrent transfer item",
                    NameMatchKey = "POSTGRESQLCONCURRENTTRANSFERITEM",
                    Unit = "EA",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 8m
                });
                seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 8m,
                    UpdatedAtUtc = seededAtUtc
                });
                seedDb.InventoryTransfers.Add(new InventoryTransfer
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = $"PG-TR-{transferId:N}"[..24],
                    TransferDate = new DateOnly(2026, 7, 31),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    CreatedByUsername = "postgres-transfer-source",
                    RequestedByUsername = "postgres-transfer-source",
                    RequestedAtUtc = seededAtUtc.AddMinutes(-10),
                    CreatedAtUtc = seededAtUtc.AddMinutes(-10),
                    UpdatedAtUtc = seededAtUtc,
                    LastSavedByUsername = "postgres-transfer-source",
                    LastSavedAtUtc = seededAtUtc,
                    Lines =
                    [
                        new InventoryTransferLine
                        {
                            Id = lineId,
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "PostgreSQL concurrent transfer item",
                            Unit = "EA",
                            Quantity = 2m,
                            ReceivedQuantity = 2m
                        }
                    ]
                });
                await seedDb.SaveChangesAsync();
                await new InventoryLedgerService(seedDb).RebuildAsync();
            }

            InventoryTransfer staleTransfer;
            await using (var snapshotDb = CreateDbContext(sourceOptions, admin))
            {
                staleTransfer = await snapshotDb.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Include(transfer => transfer.Lines)
                    .SingleAsync(transfer => transfer.Id == transferId);
            }

            var sourceUser = CreateDeliveryUser(
                "postgres-transfer-source",
                OfficeCodeCatalog.Usenet);
            var targetUser = CreateInventoryDeliveryAdminUser(
                "postgres-transfer-target",
                OfficeCodeCatalog.Yeonsu);

            static InventoryTransferDto BuildSourceEditDto(
                InventoryTransfer transfer,
                string username,
                string mutationId,
                DateTime changedAtUtc)
            {
                var dto = transfer.ToDto();
                dto.ExpectedRevision = transfer.Revision;
                dto.UpdatedAtUtc = changedAtUtc;
                dto.Memo = "source edit racing with target receipt";
                dto.LastSavedByUsername = username;
                dto.LastSavedAtUtc = changedAtUtc;
                dto.MutationId = mutationId;
                dto.MutationCreatedAtUtc = changedAtUtc;
                return dto;
            }

            var sourceDto = BuildSourceEditDto(
                staleTransfer,
                sourceUser.Username,
                sourceMutationId,
                seededAtUtc.AddMinutes(1));

            static InventoryTransferDto BuildReceiptDto(
                InventoryTransfer transfer,
                string username,
                string mutationId,
                DateTime changedAtUtc)
            {
                var dto = transfer.ToDto();
                dto.ExpectedRevision = transfer.Revision;
                dto.UpdatedAtUtc = changedAtUtc;
                dto.TransferStatus = InventoryTransferStatusNormalizer.Received;
                dto.LastSavedByUsername = username;
                dto.LastSavedAtUtc = changedAtUtc;
                dto.ReceivedByUsername = username;
                dto.ReceivedAtUtc = changedAtUtc;
                dto.ReceiveMemo = "target receipt racing with source edit";
                dto.LastStatusChangedByUsername = username;
                dto.LastStatusChangedAtUtc = changedAtUtc;
                dto.MutationId = mutationId;
                dto.MutationCreatedAtUtc = changedAtUtc;
                foreach (var line in dto.Lines.Where(line => !line.IsDeleted))
                {
                    line.ReceivedQuantity = line.Quantity;
                    line.QuantityDifference = 0m;
                    line.ReceiptRemark = "received once";
                }

                return dto;
            }

            static ItemWarehouseStockDto BuildDestinationStockDto(
                Guid itemId,
                DateTime updatedAtUtc)
                => new()
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 2m,
                    UpdatedAtUtc = updatedAtUtc,
                    Revision = 0,
                    ExpectedRevision = 0
                };

            var receiptDto = BuildReceiptDto(
                staleTransfer,
                targetUser.Username,
                receiptMutationId,
                seededAtUtc.AddMinutes(2));
            var destinationStockUpdatedAtUtc = seededAtUtc.AddMinutes(2);
            var sourceRequest = new SyncPushRequest
            {
                DeviceId = "postgres-transfer-source-device",
                InventoryTransfers = [sourceDto]
            };
            var sourceReplayRequest = new SyncPushRequest
            {
                DeviceId = sourceRequest.DeviceId,
                InventoryTransfers =
                [
                    BuildSourceEditDto(
                        staleTransfer,
                        sourceUser.Username,
                        sourceMutationId,
                        seededAtUtc.AddMinutes(1))
                ]
            };
            var receiptRequest = new SyncPushRequest
            {
                DeviceId = "postgres-transfer-target-device",
                ItemWarehouseStocks =
                [
                    BuildDestinationStockDto(
                        itemId,
                        destinationStockUpdatedAtUtc)
                ],
                InventoryTransfers = [receiptDto]
            };
            var startGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCount = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            async Task<ActionResult<SyncPushResult>> PushFrom(
                DbContextOptions<AppDbContext> options,
                TestCurrentUserContext user,
                SyncPushRequest request,
                CancellationToken cancellationToken)
            {
                await using var dbContext = CreateDbContext(options, user);
                var controller = CreateController(dbContext, user);
                if (Interlocked.Increment(ref readyCount) == 2)
                    startGate.TrySetResult();

                await startGate.Task.WaitAsync(cancellationToken);
                return await controller.Push(request, cancellationToken);
            }

            Assert.NotEqual(
                sourceDatabaseBuilder.Host,
                targetDatabaseBuilder.Host);
            var concurrentResponses = await Task.WhenAll(
                    PushFrom(sourceOptions, sourceUser, sourceRequest, timeout.Token),
                    PushFrom(targetOptions, targetUser, receiptRequest, timeout.Token))
                .WaitAsync(timeout.Token);
            var concurrentResults = concurrentResponses
                .Select(AssertOk)
                .ToList();
            Assert.Equal(1, concurrentResults.Sum(result => result.AcceptedCount));
            Assert.Equal(1, concurrentResults.Sum(result => result.ConflictCount));
            Assert.Equal(0, concurrentResults.Sum(result => result.DuplicateMutationCount));

            InventoryTransfer currentTransfer;
            await using (var currentDb = CreateDbContext(sourceOptions, admin))
            {
                currentTransfer = await currentDb.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Include(transfer => transfer.Lines)
                    .SingleAsync(transfer => transfer.Id == transferId, timeout.Token);
                var destinationStock = await currentDb.ItemWarehouseStocks
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        stock =>
                            stock.ItemId == itemId &&
                            stock.WarehouseCode ==
                            OfficeCodeCatalog.YeonsuMainWarehouse,
                        timeout.Token);
                var destinationStockReceiptCount =
                    await currentDb.ProcessedSyncMutations
                        .AsNoTracking()
                        .CountAsync(
                            receipt =>
                                receipt.EntityName ==
                                nameof(ItemWarehouseStock) &&
                                receipt.EntityId ==
                                $"{itemId:D}|{OfficeCodeCatalog.YeonsuMainWarehouse}",
                            timeout.Token);
                var targetRaceResult = concurrentResults[1];
                if (targetRaceResult.ConflictCount > 0)
                {
                    Assert.DoesNotContain(
                        targetRaceResult.AcceptedItemWarehouseStockKeys,
                        key =>
                            key.ItemId == itemId &&
                            key.WarehouseCode ==
                            OfficeCodeCatalog.YeonsuMainWarehouse);
                    Assert.Null(destinationStock);
                    Assert.Equal(0, destinationStockReceiptCount);
                    Assert.Equal(
                        8m,
                        await currentDb.Items
                            .IgnoreQueryFilters()
                            .Where(item => item.Id == itemId)
                            .Select(item => item.CurrentStock)
                            .SingleAsync(timeout.Token));
                }
                else
                {
                    Assert.Contains(
                        targetRaceResult.AcceptedItemWarehouseStockKeys,
                        key =>
                            key.ItemId == itemId &&
                            key.WarehouseCode ==
                            OfficeCodeCatalog.YeonsuMainWarehouse);
                    Assert.NotNull(destinationStock);
                    Assert.Equal(2m, destinationStock!.Quantity);
                    Assert.Equal(1, destinationStockReceiptCount);
                }
            }

            SyncPushRequest acceptedReceiptRequest;
            if (!string.Equals(
                    InventoryTransferStatusNormalizer.Normalize(
                        currentTransfer.TransferStatus,
                        currentTransfer.ReceivedByUsername,
                        currentTransfer.ReceivedAtUtc,
                        currentTransfer.RejectedByUsername,
                        currentTransfer.RejectedAtUtc),
                    InventoryTransferStatusNormalizer.Received,
                    StringComparison.Ordinal))
            {
                acceptedReceiptRequest = new SyncPushRequest
                {
                    DeviceId = receiptRequest.DeviceId,
                    ItemWarehouseStocks =
                    [
                        BuildDestinationStockDto(
                            itemId,
                            destinationStockUpdatedAtUtc)
                    ],
                    InventoryTransfers =
                    [
                        BuildReceiptDto(
                            currentTransfer,
                            targetUser.Username,
                            receiptMutationId,
                            seededAtUtc.AddMinutes(3))
                    ]
                };
                await using var targetRetryDb = CreateDbContext(targetOptions, targetUser);
                var targetRetryResult = AssertOk(
                    await CreateController(targetRetryDb, targetUser)
                        .Push(acceptedReceiptRequest, timeout.Token));
                Assert.Equal(1, targetRetryResult.AcceptedCount);
                Assert.Equal(0, targetRetryResult.ConflictCount);
                Assert.Equal(0, targetRetryResult.DuplicateMutationCount);
            }
            else
            {
                acceptedReceiptRequest = receiptRequest;
            }

            long revisionBeforeReplay;
            int auditCountBeforeReplay;
            await using (var beforeReplayDb = CreateDbContext(sourceOptions, admin))
            {
                var transfer = await beforeReplayDb.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current => current.Id == transferId, timeout.Token);
                revisionBeforeReplay = transfer.Revision;
                auditCountBeforeReplay = await beforeReplayDb.AuditLogs
                    .AsNoTracking()
                    .CountAsync(
                        audit =>
                            audit.EntityName == nameof(InventoryTransfer) &&
                            audit.EntityId == transferId.ToString("D"),
                        timeout.Token);
            }

            await using (var replayDb = CreateDbContext(targetOptions, targetUser))
            {
                var replayResult = AssertOk(
                    await CreateController(replayDb, targetUser)
                        .Push(acceptedReceiptRequest, timeout.Token));
                Assert.Equal(1, replayResult.AcceptedCount);
                Assert.Equal(0, replayResult.ConflictCount);
                Assert.Equal(1, replayResult.DuplicateMutationCount);
            }

            SyncPushResult sourceAfterFinalResult;
            await using (var sourceAfterFinalDb = CreateDbContext(sourceOptions, sourceUser))
            {
                sourceAfterFinalResult = AssertOk(
                    await CreateController(sourceAfterFinalDb, sourceUser)
                        .Push(sourceReplayRequest, timeout.Token));
                Assert.True(
                    (sourceAfterFinalResult.AcceptedCount == 1 &&
                     sourceAfterFinalResult.ConflictCount == 0 &&
                     sourceAfterFinalResult.DuplicateMutationCount == 1) ||
                    (sourceAfterFinalResult.AcceptedCount == 0 &&
                     sourceAfterFinalResult.ConflictCount == 1 &&
                     sourceAfterFinalResult.DuplicateMutationCount == 0),
                    $"Unexpected source retry result: accepted={sourceAfterFinalResult.AcceptedCount}; " +
                    $"conflicts={sourceAfterFinalResult.ConflictCount}; " +
                    $"duplicates={sourceAfterFinalResult.DuplicateMutationCount}.");
            }

            await using var verificationDb = CreateDbContext(sourceOptions, admin);
            var storedTransfer = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId, timeout.Token);
            Assert.Equal(
                InventoryTransferStatusNormalizer.Received,
                InventoryTransferStatusNormalizer.Normalize(
                    storedTransfer.TransferStatus,
                    storedTransfer.ReceivedByUsername,
                    storedTransfer.ReceivedAtUtc,
                    storedTransfer.RejectedByUsername,
                    storedTransfer.RejectedAtUtc));
            Assert.Equal(targetUser.Username, storedTransfer.ReceivedByUsername);
            Assert.Equal(revisionBeforeReplay, storedTransfer.Revision);
            Assert.Equal(
                auditCountBeforeReplay,
                await verificationDb.AuditLogs
                    .AsNoTracking()
                    .CountAsync(
                        audit =>
                            audit.EntityName == nameof(InventoryTransfer) &&
                            audit.EntityId == transferId.ToString("D"),
                        timeout.Token));

            var stocks = await verificationDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId)
                .OrderBy(stock => stock.WarehouseCode)
                .ToListAsync(timeout.Token);
            Assert.Equal(2, stocks.Count);
            Assert.Equal(
                8m,
                Assert.Single(
                    stocks,
                    stock => stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Quantity);
            Assert.Equal(
                2m,
                Assert.Single(
                    stocks,
                    stock => stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                    .Quantity);
            Assert.Equal(
                10m,
                await verificationDb.Items
                    .IgnoreQueryFilters()
                    .Where(item => item.Id == itemId)
                    .Select(item => item.CurrentStock)
                    .SingleAsync(timeout.Token));

            var transferLedger = await verificationDb.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.SourceDocumentId == transferId &&
                    entry.SourceLineId == lineId)
                .ToListAsync(timeout.Token);
            var outboundLedger = Assert.Single(
                transferLedger,
                entry => entry.SourceType == "InventoryTransfer:Out");
            var inboundLedger = Assert.Single(
                transferLedger,
                entry => entry.SourceType == "InventoryTransfer:In");
            Assert.Equal(-2m, outboundLedger.QuantityDelta);
            Assert.Equal(2m, inboundLedger.QuantityDelta);

            var receipt = Assert.Single(
                await verificationDb.ProcessedSyncMutations
                    .AsNoTracking()
                    .Where(current => current.MutationId == receiptMutationId)
                    .ToListAsync(timeout.Token));
            Assert.Equal(nameof(InventoryTransfer), receipt.EntityName);
            Assert.Equal(transferId.ToString("D"), receipt.EntityId);
            var stockReceipt = Assert.Single(
                await verificationDb.ProcessedSyncMutations
                    .AsNoTracking()
                    .Where(current =>
                        current.EntityName == nameof(ItemWarehouseStock) &&
                        current.EntityId ==
                        $"{itemId:D}|{OfficeCodeCatalog.YeonsuMainWarehouse}")
                    .ToListAsync(timeout.Token));
            Assert.Equal(
                acceptedReceiptRequest.DeviceId,
                stockReceipt.DeviceId);
            var sourceReceiptCount = await verificationDb.ProcessedSyncMutations
                .AsNoTracking()
                .CountAsync(
                    current => current.MutationId == sourceMutationId,
                    timeout.Token);
            Assert.InRange(sourceReceiptCount, 0, 1);
            Assert.Equal(
                sourceReceiptCount,
                sourceAfterFinalResult.DuplicateMutationCount);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task MixedTransferShortage_RollsBackAppliedSiblingStockAndReceipt()
    {
        var configuredConnection =
            Environment.GetEnvironmentVariable(
                ConnectionVariableName);
        Assert.False(
            string.IsNullOrWhiteSpace(
                configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder =
            new NpgsqlConnectionStringBuilder(
                configuredConnection)
            {
                Database = "postgres",
                IncludeErrorDetail = false
            };
        var testDatabaseBuilder =
            new NpgsqlConnectionStringBuilder(
                maintenanceBuilder.ConnectionString)
            {
                Database = databaseName,
                IncludeErrorDetail = false
            };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(
                maintenanceBuilder.ConnectionString,
                databaseName);
            databaseCreated = true;

            var options =
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(
                        testDatabaseBuilder.ConnectionString)
                    .Options;
            var admin = CreateInventoryDeliveryAdminUser(
                "postgres-mixed-transfer-shortage",
                OfficeCodeCatalog.Usenet);
            await using (var initializationDb =
                         CreateDbContext(options, admin))
            {
                await initializationDb.Database
                    .EnsureCreatedAsync();
            }

            var handledItemId = Guid.NewGuid();
            var shortageItemId = Guid.NewGuid();
            var transferId = Guid.NewGuid();
            var now =
                new DateTime(
                    2026,
                    7,
                    31,
                    4,
                    0,
                    0,
                    DateTimeKind.Utc);
            long handledStockRevision;
            await using (var seedDb =
                         CreateDbContext(options, admin))
            {
                seedDb.Items.AddRange(
                    new Item
                    {
                        Id = handledItemId,
                        TenantCode =
                            TenantScopeCatalog.UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Shared,
                        NameOriginal =
                            "PostgreSQL handled transfer item",
                        NameMatchKey =
                            "POSTGRESQLHANDLEDTRANSFERITEM",
                        Unit = "EA",
                        ItemKind = ItemKinds.Product,
                        TrackingType =
                            ItemTrackingTypes.Stock,
                        CurrentStock = 10m
                    },
                    new Item
                    {
                        Id = shortageItemId,
                        TenantCode =
                            TenantScopeCatalog.UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Shared,
                        NameOriginal =
                            "PostgreSQL shortage transfer item",
                        NameMatchKey =
                            "POSTGRESQLSHORTAGETRANSFERITEM",
                        Unit = "EA",
                        ItemKind = ItemKinds.Product,
                        TrackingType =
                            ItemTrackingTypes.Stock,
                        CurrentStock = 1m
                    });
                var handledStock =
                    new ItemWarehouseStock
                    {
                        ItemId = handledItemId,
                        WarehouseCode =
                            OfficeCodeCatalog
                                .UsenetMainWarehouse,
                        Quantity = 10m,
                        UpdatedAtUtc =
                            now.AddMinutes(-5)
                    };
                seedDb.ItemWarehouseStocks.AddRange(
                    handledStock,
                    new ItemWarehouseStock
                    {
                        ItemId = shortageItemId,
                        WarehouseCode =
                            OfficeCodeCatalog
                                .UsenetMainWarehouse,
                        Quantity = 1m,
                        UpdatedAtUtc =
                            now.AddMinutes(-5)
                    });
                await seedDb.SaveChangesAsync();
                handledStockRevision =
                    handledStock.Revision;
            }

            const string deviceId =
                "postgres-mixed-transfer-shortage-device";
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(60));
            await using (var pushDb =
                         CreateDbContext(options, admin))
            {
                var result = AssertOk(
                    await CreateController(pushDb, admin)
                        .Push(
                            new SyncPushRequest
                            {
                                DeviceId = deviceId,
                                ItemWarehouseStocks =
                                [
                                    new ItemWarehouseStockDto
                                    {
                                        ItemId =
                                            handledItemId,
                                        WarehouseCode =
                                            OfficeCodeCatalog
                                                .UsenetMainWarehouse,
                                        Quantity = 4m,
                                        UpdatedAtUtc = now,
                                        Revision =
                                            handledStockRevision,
                                        ExpectedRevision =
                                            handledStockRevision
                                    }
                                ],
                                InventoryTransfers =
                                [
                                    new InventoryTransferDto
                                    {
                                        Id = transferId,
                                        TenantCode =
                                            TenantScopeCatalog
                                                .UsenetGroup,
                                        SourceOfficeCode =
                                            OfficeCodeCatalog
                                                .Usenet,
                                        TargetOfficeCode =
                                            OfficeCodeCatalog
                                                .Yeonsu,
                                        TransferNumber =
                                            $"PG-MIX-{transferId:N}"[..24],
                                        TransferDate =
                                            new DateOnly(
                                                2026,
                                                7,
                                                31),
                                        FromWarehouseCode =
                                            OfficeCodeCatalog
                                                .UsenetMainWarehouse,
                                        ToWarehouseCode =
                                            OfficeCodeCatalog
                                                .YeonsuMainWarehouse,
                                        TransferStatus =
                                            InventoryTransferStatusNormalizer
                                                .Pending,
                                        CreatedByUsername =
                                            admin.Username,
                                        RequestedByUsername =
                                            admin.Username,
                                        RequestedAtUtc = now,
                                        CreatedAtUtc = now,
                                        UpdatedAtUtc = now,
                                        LastSavedByUsername =
                                            admin.Username,
                                        LastSavedAtUtc = now,
                                        MutationId =
                                            $"postgres-mixed-shortage:InventoryTransfer:{transferId:N}",
                                        MutationCreatedAtUtc =
                                            now,
                                        Lines =
                                        [
                                            new InventoryTransferLineDto
                                            {
                                                Id =
                                                    Guid.NewGuid(),
                                                TransferId =
                                                    transferId,
                                                ItemId =
                                                    handledItemId,
                                                ItemNameOriginal =
                                                    "PostgreSQL handled transfer item",
                                                Unit = "EA",
                                                Quantity = 6m
                                            },
                                            new InventoryTransferLineDto
                                            {
                                                Id =
                                                    Guid.NewGuid(),
                                                TransferId =
                                                    transferId,
                                                ItemId =
                                                    shortageItemId,
                                                ItemNameOriginal =
                                                    "PostgreSQL shortage transfer item",
                                                Unit = "EA",
                                                Quantity = 2m
                                            }
                                        ]
                                    }
                                ]
                            },
                            timeout.Token));
                Assert.Equal(0, result.AcceptedCount);
                Assert.Equal(1, result.ConflictCount);
                Assert.Empty(
                    result
                        .AcceptedItemWarehouseStockKeys);
                Assert.Contains(
                    result.Notices,
                    notice =>
                        notice.Code ==
                        "inventory-transfer-stock-atomicity-rollback");
            }

            await using var verificationDb =
                CreateDbContext(options, admin);
            Assert.False(
                await verificationDb
                    .InventoryTransfers
                    .IgnoreQueryFilters()
                    .AnyAsync(
                        transfer =>
                            transfer.Id == transferId,
                        timeout.Token));
            Assert.False(
                await verificationDb
                    .InventoryLedgerEntries
                    .AnyAsync(
                        entry =>
                            entry.SourceDocumentId ==
                            transferId,
                        timeout.Token));
            Assert.Equal(
                10m,
                await verificationDb
                    .ItemWarehouseStocks
                    .Where(
                        stock =>
                            stock.ItemId ==
                                handledItemId &&
                            stock.WarehouseCode ==
                                OfficeCodeCatalog
                                    .UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                1m,
                await verificationDb
                    .ItemWarehouseStocks
                    .Where(
                        stock =>
                            stock.ItemId ==
                                shortageItemId &&
                            stock.WarehouseCode ==
                                OfficeCodeCatalog
                                    .UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync(timeout.Token));
            Assert.False(
                await verificationDb
                    .ProcessedSyncMutations
                    .AsNoTracking()
                    .AnyAsync(
                        receipt =>
                            receipt.EntityName ==
                                nameof(
                                    ItemWarehouseStock) &&
                            receipt.EntityId ==
                                $"{handledItemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}",
                        timeout.Token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
            {
                await DropDatabaseAsync(
                    maintenanceBuilder
                        .ConnectionString,
                    databaseName);
            }
        }
    }

    [PostgreSqlFact]
    public async Task ExistingItemWarehouseStockResponseLossRetry_AcknowledgesReceiptWithoutRewind()
        => await RunItemWarehouseStockResponseLossRetryAsync(
            seedExistingStock: true);

    [PostgreSqlFact]
    public async Task NewZeroRevisionItemWarehouseStockResponseLossRetry_AcknowledgesReceiptWithoutRewind()
        => await RunItemWarehouseStockResponseLossRetryAsync(
            seedExistingStock: false);

    [PostgreSqlFact]
    public async Task Push_501RentalManagementCompanyAliases_UsesBoundedPreloadAndConflictDedupCommands()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var counter = new SyncPushCommandCountingInterceptor();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .AddInterceptors(counter)
                .Options;
            var admin = CreateAdminUser();
            await using var dbContext = CreateDbContext(options, admin);
            await dbContext.Database.EnsureCreatedAsync();

            var canonicalCompany = new RentalManagementCompany
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                Code = OfficeCodeCatalog.Usenet,
                Name = "POSTGRESQL USENET BULK CANONICAL"
            };
            dbContext.RentalManagementCompanies.Add(canonicalCompany);
            await dbContext.SaveChangesAsync();
            var canonicalRevision = canonicalCompany.Revision;

            var smallAliases = CreateRentalManagementCompanyAliases(canonicalCompany, canonicalRevision, 2, "SMALL");
            var controller = CreateController(dbContext, admin);
            counter.Reset();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var smallResult = AssertOk(await controller.Push(new SyncPushRequest
            {
                DeviceId = "postgres-rental-company-small-budget",
                RentalManagementCompanies = smallAliases
            }, timeout.Token));
            Assert.Equal(2, smallResult.ConflictCount);
            var smallCommandCount = counter.TotalCommandCount;

            var aliases = CreateRentalManagementCompanyAliases(canonicalCompany, canonicalRevision, 501, "LARGE");
            const string collisionReason =
                "Multiple incoming rental management company rows resolve to the same canonical company.";
            var historicalConflictIds = aliases
                .Select(_ => Guid.NewGuid())
                .ToList();
            dbContext.ConflictLogs.AddRange(aliases.Select((alias, index) => new ConflictLog
            {
                Id = historicalConflictIds[index],
                EntityName = nameof(RentalManagementCompany),
                EntityId = alias.Id.ToString("D"),
                Reason = collisionReason,
                Status = "Open",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
            }));
            await dbContext.SaveChangesAsync(timeout.Token);
            dbContext.ChangeTracker.Clear();

            counter.Reset();
            var result = AssertOk(await controller.Push(new SyncPushRequest
            {
                DeviceId = "postgres-rental-company-large-budget",
                RentalManagementCompanies = aliases
            }, timeout.Token));

            Assert.Equal(0, result.AcceptedCount);
            Assert.Equal(501, result.ConflictCount);
            Assert.Equal(0, result.DuplicateMutationCount);
            Assert.InRange(counter.RentalManagementCompanySelectCount, 1, 4);
            Assert.Equal(6, counter.ConflictLogSelectCount);
            Assert.Equal(2, counter.ConflictLogDeleteCount);
            Assert.Equal(0, counter.ConflictLogUpdateCount);
            Assert.InRange(counter.TotalCommandCount, 1, smallCommandCount + 12);

            await using var verificationDb = CreateDbContext(options, CreateAdminUser());
            var storedCompany = await verificationDb.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(timeout.Token);
            Assert.Equal(canonicalCompany.Id, storedCompany.Id);
            Assert.Equal("POSTGRESQL USENET BULK CANONICAL", storedCompany.Name);
            Assert.Equal(canonicalRevision, storedCompany.Revision);
            Assert.Equal(0, await verificationDb.ProcessedSyncMutations.CountAsync(timeout.Token));
            var openConflicts = await verificationDb.ConflictLogs
                .AsNoTracking()
                .Where(conflict =>
                    conflict.EntityName == nameof(RentalManagementCompany) &&
                    conflict.Reason == collisionReason &&
                    conflict.Status == "Open")
                .ToListAsync(timeout.Token);
            Assert.Equal(503, openConflicts.Count);
            Assert.DoesNotContain(openConflicts, conflict => historicalConflictIds.Contains(conflict.Id));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task Push_501ExactReplays_UsesTwoConflictUpdates_AndKeepsSameEntityStaleConflictsOpen()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var counter = new SyncPushCommandCountingInterceptor();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .AddInterceptors(counter)
                .Options;
            var admin = CreateAdminUser();
            await using var dbContext = CreateDbContext(options, admin);
            await dbContext.Database.EnsureCreatedAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var category = new CustomerCategory
            {
                Id = Guid.NewGuid(),
                Name = "PostgreSQL replay category"
            };
            var customerMaster = new CustomerMaster
            {
                Id = Guid.NewGuid(),
                CategoryId = category.Id,
                NameOriginal = "PostgreSQL replay master",
                NameMatchKey = "POSTGRESQLREPLAYMASTER",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet
            };
            dbContext.CustomerCategories.Add(category);
            dbContext.CustomerMasters.Add(customerMaster);
            var customers = Enumerable.Range(0, 501)
                .Select(index => CreateRevisionCustomer($"POSTGRES-EXACT-REPLAY-{index:D3}"))
                .ToList();
            foreach (var customer in customers)
            {
                customer.CategoryId = category.Id;
                customer.CustomerMasterId = customerMaster.Id;
            }
            dbContext.Customers.AddRange(customers);
            await dbContext.SaveChangesAsync(timeout.Token);
            dbContext.ChangeTracker.Clear();

            var customerIds = customers
                .Select(customer => customer.Id)
                .ToArray();
            var storedCustomers = await dbContext.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => customerIds.Contains(customer.Id))
                .OrderBy(customer => customer.NameOriginal)
                .ToListAsync(timeout.Token);
            var replayDtos = storedCustomers
                .Select(customer => customer.ToDto())
                .ToList();
            foreach (var dto in replayDtos)
            {
                dto.ExpectedRevision = dto.Revision;
                dto.MutationId = $"postgres-bulk-replay:customer:{dto.Id:N}:{dto.Revision}";
            }

            dbContext.ProcessedSyncMutations.AddRange(replayDtos.Select(dto => new ProcessedSyncMutation
            {
                MutationId = ProcessedSyncMutationRecorder.NormalizeMutationId(dto.MutationId),
                DeviceId = "postgres-bulk-replay-device",
                EntityName = nameof(Customer),
                EntityId = dto.Id.ToString("D"),
                ExpectedRevision = dto.ExpectedRevision,
                PayloadHash = SyncMutationPayloadHasher.Compute(dto),
                ProcessedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            }));
            dbContext.ConflictLogs.AddRange(replayDtos.Select(dto => new ConflictLog
            {
                EntityName = nameof(Customer),
                EntityId = dto.Id.ToString("D"),
                Reason = "Historical exact replay conflict",
                Status = "Open",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
            }));
            var latestActorUserId = Guid.NewGuid();
            var latestActorCreatedAtUtc = DateTime.UtcNow;
            dbContext.AuditLogs.AddRange(Enumerable.Range(0, 501).Select(index => new AuditLog
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Username = $"postgres-historical-actor-{index:D3}",
                EntityName = nameof(Customer),
                EntityId = replayDtos[0].Id.ToString("D"),
                Action = "Modified",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2).AddSeconds(index)
            }));
            dbContext.AuditLogs.AddRange(
                new AuditLog
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    UserId = Guid.NewGuid(),
                    Username = "postgres-tie-break-lower-actor",
                    EntityName = nameof(Customer),
                    EntityId = replayDtos[0].Id.ToString("D"),
                    Action = "Modified",
                    CreatedAtUtc = latestActorCreatedAtUtc
                },
                new AuditLog
                {
                    Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                    UserId = latestActorUserId,
                    Username = "postgres-latest-database-actor",
                    EntityName = nameof(Customer),
                    EntityId = replayDtos[0].Id.ToString("D"),
                    Action = "Modified",
                    CreatedAtUtc = latestActorCreatedAtUtc
                });
            await dbContext.SaveChangesAsync(timeout.Token);
            dbContext.ChangeTracker.Clear();

            var replayThenStale = storedCustomers[0].ToDto();
            replayThenStale.ExpectedRevision++;
            replayThenStale.MutationId =
                $"postgres-same-entity-stale-after:{replayThenStale.Id:N}:{replayThenStale.ExpectedRevision}";
            replayThenStale.NameOriginal = "STALE AFTER REPLAY MUST NOT WRITE";
            replayThenStale.NameMatchKey = "STALEAFTERREPLAYMUSTNOTWRITE";

            var staleThenReplay = storedCustomers[1].ToDto();
            staleThenReplay.ExpectedRevision++;
            staleThenReplay.MutationId =
                $"postgres-same-entity-stale-before:{staleThenReplay.Id:N}:{staleThenReplay.ExpectedRevision}";
            staleThenReplay.NameOriginal = "STALE BEFORE REPLAY MUST NOT WRITE";
            staleThenReplay.NameMatchKey = "STALEBEFOREREPLAYMUSTNOTWRITE";

            var requestDtos = new List<CustomerDto>
            {
                replayDtos[0],
                replayThenStale,
                staleThenReplay,
                replayDtos[1]
            };
            requestDtos.AddRange(replayDtos.Skip(2));

            var controller = CreateController(dbContext, admin);
            counter.Reset();
            var result = AssertOk(await controller.Push(new SyncPushRequest
            {
                DeviceId = "postgres-bulk-replay-device",
                Customers = requestDtos
            }, timeout.Token));

            Assert.Equal(501, result.AcceptedCount);
            Assert.Equal(501, result.DuplicateMutationCount);
            Assert.Equal(2, result.ConflictCount);
            Assert.Equal(2, counter.ConflictLogUpdateCount);
            Assert.Equal(1, counter.ConflictLogSelectCount);
            Assert.Equal(0, counter.ConflictLogDeleteCount);
            Assert.Equal(1, counter.AuditLogSelectCount);
            Assert.Contains("ROW_NUMBER", counter.AuditLogSelectCommandText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PARTITION BY", counter.AuditLogSelectCommandText, StringComparison.OrdinalIgnoreCase);
            // The budget includes the one required, batched open-conflict read
            // asserted above. It must remain constant as replay volume grows.
            Assert.True(
                counter.TotalCommandCount <= 23,
                $"Expected at most 23 commands but observed {counter.TotalCommandCount}." +
                Environment.NewLine +
                counter.CommandSummary);
            var replayThenStaleConflict = Assert.Single(
                result.Conflicts,
                conflict => conflict.EntityId == replayDtos[0].Id.ToString("D"));
            Assert.Equal(latestActorUserId, replayThenStaleConflict.ServerUserId);
            Assert.Equal("postgres-latest-database-actor", replayThenStaleConflict.ServerUsername);

            await using var verificationDb = CreateDbContext(options, CreateAdminUser());
            Assert.Equal(501, await verificationDb.ProcessedSyncMutations.CountAsync(timeout.Token));
            Assert.Equal(
                501,
                await verificationDb.ConflictLogs.CountAsync(
                    conflict =>
                        conflict.Reason == "Historical exact replay conflict" &&
                        conflict.Status == "Resolved",
                    timeout.Token));
            var sameEntityIds = new[]
            {
                replayDtos[0].Id.ToString("D"),
                replayDtos[1].Id.ToString("D")
            };
            var newSamePushConflicts = await verificationDb.ConflictLogs
                .AsNoTracking()
                .Where(conflict =>
                    conflict.EntityName == nameof(Customer) &&
                    sameEntityIds.Contains(conflict.EntityId) &&
                    conflict.Reason.StartsWith("Expected revision mismatch") &&
                    conflict.Status == "Open")
                .ToListAsync(timeout.Token);
            Assert.Equal(2, newSamePushConflicts.Count);
            Assert.All(newSamePushConflicts, conflict =>
            {
                Assert.Null(conflict.ResolvedAtUtc);
                Assert.Empty(conflict.ResolutionNote);
            });
            Assert.Equal(
                0,
                await verificationDb.Customers.CountAsync(
                    customer =>
                        customer.NameOriginal == "STALE AFTER REPLAY MUST NOT WRITE" ||
                        customer.NameOriginal == "STALE BEFORE REPLAY MUST NOT WRITE",
                    timeout.Token));
            Assert.True(await PostgreSqlIndexExistsAsync(
                testDatabaseBuilder.ConnectionString,
                "IX_AuditLogs_EntityName_EntityId_CreatedAtUtc"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentRestCreateAndSyncPush_WithSameCustomerMutation_CommitOneRowReceiptRevisionAndAudit()
    {
        const int iterationCount = 5;
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var restDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Host = "127.0.0.1",
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var syncDatabaseBuilder = new NpgsqlConnectionStringBuilder(restDatabaseBuilder.ConnectionString)
        {
            Host = "localhost"
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var restOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(restDatabaseBuilder.ConnectionString)
                .Options;
            var syncOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(syncDatabaseBuilder.ConnectionString)
                .Options;
            await using (var initializationDb = CreateDbContext(restOptions, CreateAdminUser()))
                await initializationDb.Database.EnsureCreatedAsync();

            for (var iteration = 0; iteration < iterationCount; iteration++)
            {
                var customerId = Guid.NewGuid();
                var mutationId =
                    $"postgres-rest-sync-customer:{iteration}:{customerId:N}";
                var syncDeviceId = $"postgres-rest-sync-device-{iteration}";
                var restUser = CreateAdminUser($"rest-race-{iteration}");
                var syncUser = CreateAdminUser($"sync-race-{iteration}");
                long revisionBefore;
                await using (var beforeDb = CreateDbContext(restOptions, CreateAdminUser()))
                    revisionBefore = await beforeDb.GetCommittedRevisionAsync();

                CustomerDto restResult;
                SyncPushResult syncResult;
                await using (var restDb = CreateDbContext(restOptions, restUser))
                await using (var syncDb = CreateDbContext(syncOptions, syncUser))
                {
                    Assert.NotEqual(
                        restDb.Database.GetDbConnection().DataSource,
                        syncDb.Database.GetDbConnection().DataSource);
                    var restController = CreateCustomersController(restDb, restUser);
                    var syncController = CreateController(syncDb, syncUser);
                    var startGate = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var readyCount = 0;
                    using var timeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(45));

                    async Task<ActionResult<CustomerDto>> CreateFromRest(
                        CancellationToken cancellationToken)
                    {
                        if (Interlocked.Increment(ref readyCount) == 2)
                            startGate.TrySetResult();

                        await startGate.Task.WaitAsync(cancellationToken);
                        return await restController.Create(
                            CreateCustomerDto(customerId, mutationId),
                            cancellationToken);
                    }

                    async Task<ActionResult<SyncPushResult>> CreateFromSync(
                        CancellationToken cancellationToken)
                    {
                        if (Interlocked.Increment(ref readyCount) == 2)
                            startGate.TrySetResult();

                        await startGate.Task.WaitAsync(cancellationToken);
                        return await syncController.Push(
                            new SyncPushRequest
                            {
                                DeviceId = syncDeviceId,
                                Customers =
                                [
                                    CreateCustomerDto(customerId, mutationId)
                                ]
                            },
                            cancellationToken);
                    }

                    var restTask = CreateFromRest(timeout.Token);
                    var syncTask = CreateFromSync(timeout.Token);
                    await Task.WhenAll(restTask, syncTask).WaitAsync(timeout.Token);

                    var restOk = Assert.IsType<OkObjectResult>(
                        (await restTask).Result);
                    restResult = Assert.IsType<CustomerDto>(restOk.Value);
                    syncResult = AssertOk(await syncTask);
                }

                Assert.Equal(customerId, restResult.Id);
                Assert.True(restResult.Revision > revisionBefore);
                Assert.Equal(1, syncResult.AcceptedCount);
                Assert.InRange(syncResult.DuplicateMutationCount, 0, 1);
                Assert.Equal(0, syncResult.ConflictCount);

                await using var verificationDb =
                    CreateDbContext(restOptions, CreateAdminUser());
                var customer = Assert.Single(await verificationDb.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(current => current.Id == customerId)
                    .ToListAsync());
                var receipt = Assert.Single(await verificationDb.ProcessedSyncMutations
                    .AsNoTracking()
                    .Where(current => current.MutationId == mutationId)
                    .ToListAsync());
                var audit = Assert.Single(await verificationDb.AuditLogs
                    .AsNoTracking()
                    .Where(current =>
                        current.EntityName == nameof(Customer) &&
                        current.EntityId == customerId.ToString("D"))
                    .ToListAsync());

                Assert.Equal(restResult.Revision, customer.Revision);
                Assert.Equal(
                    customer.Revision,
                    await verificationDb.GetCommittedRevisionAsync());
                Assert.Equal(nameof(Customer), receipt.EntityName);
                Assert.Equal(customerId.ToString("D"), receipt.EntityId);
                Assert.Equal(0, receipt.ExpectedRevision);
                Assert.Matches("^[0-9a-f]{64}$", receipt.PayloadHash);
                Assert.Equal("Added", audit.Action);
                Assert.Equal(
                    0,
                    await verificationDb.ConflictLogs
                        .AsNoTracking()
                        .CountAsync(current =>
                            current.EntityName == nameof(Customer) &&
                            current.EntityId == customerId.ToString("D")));

                if (string.Equals(
                        audit.Username,
                        restUser.Username,
                        StringComparison.Ordinal))
                {
                    Assert.Equal(1, syncResult.DuplicateMutationCount);
                    Assert.Equal(
                        ProcessedSyncMutationRecorder.DirectApiDeviceId,
                        receipt.DeviceId);
                }
                else
                {
                    Assert.Equal(syncUser.Username, audit.Username);
                    Assert.Equal(0, syncResult.DuplicateMutationCount);
                    Assert.Equal(syncDeviceId, receipt.DeviceId);
                }
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task DeleteLatestInvoiceVersion_PromotesPreviousVersionAndRestoresItsStockSnapshot()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            var customerId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var firstInvoiceId = Guid.NewGuid();
            var secondInvoiceId = Guid.NewGuid();
            var versionGroupId = firstInvoiceId;
            const decimal baselineStock = 10m;
            const decimal firstVersionQuantity = 3m;
            const decimal secondVersionQuantity = 5m;
            const decimal stockBeforeDelete = baselineStock - secondVersionQuantity;
            var seededAtUtc = new DateTime(2026, 7, 31, 1, 0, 0, DateTimeKind.Utc);
            var mutationId = $"pg-delete-latest-invoice-{Guid.NewGuid():N}";
            long expectedRevisionBeforeDelete;
            long acceptedDeletedInvoiceRevision;

            await using (var initializationDb = CreateDbContext(options, CreateAdminUser()))
            {
                await initializationDb.Database.EnsureCreatedAsync();
                initializationDb.Customers.Add(new Customer
                {
                    Id = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "PostgreSQL Latest Invoice Delete Customer",
                    NameMatchKey = "POSTGRESQLLATESTINVOICEDELETECUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                });
                initializationDb.Items.Add(new Item
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "PostgreSQL Latest Invoice Delete Item",
                    NameMatchKey = "POSTGRESQLLATESTINVOICEDELETEITEM",
                    Unit = "EA",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = stockBeforeDelete
                });
                initializationDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = stockBeforeDelete,
                    UpdatedAtUtc = seededAtUtc,
                    Revision = 10
                });
                var firstVersion = CreateInvoice(
                    firstInvoiceId,
                    customerId,
                    itemId,
                    versionGroupId,
                    versionNumber: 1,
                    previousVersionId: null,
                    quantity: firstVersionQuantity,
                    updatedAtUtc: seededAtUtc.AddMinutes(1));
                firstVersion.IsLatestVersion = false;
                var secondVersion = CreateInvoice(
                    secondInvoiceId,
                    customerId,
                    itemId,
                    versionGroupId,
                    versionNumber: 2,
                    previousVersionId: firstInvoiceId,
                    quantity: secondVersionQuantity,
                    updatedAtUtc: seededAtUtc.AddMinutes(2));
                initializationDb.Invoices.AddRange(firstVersion, secondVersion);
                await initializationDb.SaveChangesAsync();
                await new InventoryLedgerService(initializationDb).RebuildAsync();
            }

            var user = CreateAdminUser();
            await using (var pushDb = CreateDbContext(options, user))
            {
                var latestVersion = await pushDb.Invoices
                    .IgnoreQueryFilters()
                    .Include(invoice => invoice.Customer)
                    .Include(invoice => invoice.Lines)
                    .SingleAsync(invoice => invoice.Id == secondInvoiceId);
                var deleteDto = latestVersion.ToDto();
                deleteDto.IsDeleted = true;
                expectedRevisionBeforeDelete = latestVersion.Revision;
                deleteDto.ExpectedRevision = expectedRevisionBeforeDelete;
                deleteDto.UpdatedAtUtc = latestVersion.UpdatedAtUtc.AddMinutes(1);
                deleteDto.MutationId = mutationId;
                deleteDto.MutationCreatedAtUtc = deleteDto.UpdatedAtUtc;

                var result = AssertOk(await CreateController(pushDb, user).Push(
                    new SyncPushRequest
                    {
                        DeviceId = "postgres-delete-latest-invoice-version",
                        Invoices = [deleteDto]
                    },
                    CancellationToken.None));

                Assert.Equal(1, result.AcceptedCount);
                Assert.Equal(0, result.ConflictCount);
                Assert.Equal(0, result.DuplicateMutationCount);
                acceptedDeletedInvoiceRevision = Assert.Single(
                    result.AcceptedRevisions,
                    accepted =>
                        accepted.EntityName == nameof(Invoice) &&
                        accepted.EntityId == secondInvoiceId)
                    .Revision;
            }

            await using var verificationDb = CreateDbContext(options, CreateAdminUser());
            var versions = await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.VersionGroupId == versionGroupId)
                .OrderBy(invoice => invoice.VersionNumber)
                .ToListAsync();
            var firstVersionAfterDelete =
                Assert.Single(versions, invoice => invoice.Id == firstInvoiceId);
            var secondVersionAfterDelete =
                Assert.Single(versions, invoice => invoice.Id == secondInvoiceId);
            Assert.False(firstVersionAfterDelete.IsDeleted);
            Assert.True(secondVersionAfterDelete.IsDeleted);
            Assert.Equal(secondVersionAfterDelete.Revision, acceptedDeletedInvoiceRevision);

            var mutationReceipt = Assert.Single(await verificationDb.ProcessedSyncMutations
                .AsNoTracking()
                .Where(receipt => receipt.MutationId == mutationId)
                .ToListAsync());
            Assert.Equal(nameof(Invoice), mutationReceipt.EntityName);
            Assert.Equal(secondInvoiceId.ToString("D"), mutationReceipt.EntityId);
            Assert.Equal(expectedRevisionBeforeDelete, mutationReceipt.ExpectedRevision);
            Assert.Matches("^[0-9a-f]{64}$", mutationReceipt.PayloadHash);

            var finalWarehouseStock = await verificationDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync();
            Assert.Equal(baselineStock - firstVersionQuantity, finalWarehouseStock);
            Assert.Equal(
                secondVersionQuantity - firstVersionQuantity,
                finalWarehouseStock - stockBeforeDelete);
            Assert.Equal(
                finalWarehouseStock,
                await verificationDb.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.Id == itemId)
                    .Select(item => item.CurrentStock)
                    .SingleAsync());
            Assert.True(firstVersionAfterDelete.IsLatestVersion);
            Assert.False(secondVersionAfterDelete.IsLatestVersion);

            var ledgerEntry = Assert.Single(await verificationDb.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.ItemId == itemId)
                .ToListAsync());
            Assert.Equal(firstInvoiceId, ledgerEntry.SourceDocumentId);
            Assert.Equal(-firstVersionQuantity, ledgerEntry.QuantityDelta);

            var initializerMethod = typeof(DbInitializer).GetMethod(
                "EnsureInvoiceVersionColumnsAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(initializerMethod);
            var initializerTask = Assert.IsAssignableFrom<Task>(
                initializerMethod!.Invoke(
                    null,
                    [verificationDb, CancellationToken.None]));
            await initializerTask;
            await new InventoryLedgerService(verificationDb).RebuildAsync();

            verificationDb.ChangeTracker.Clear();
            var versionsAfterInitializer = await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.VersionGroupId == versionGroupId)
                .OrderBy(invoice => invoice.VersionNumber)
                .ToListAsync();
            Assert.True(
                Assert.Single(
                    versionsAfterInitializer,
                    invoice => invoice.Id == firstInvoiceId)
                .IsLatestVersion);
            Assert.False(
                Assert.Single(
                    versionsAfterInitializer,
                    invoice => invoice.Id == secondInvoiceId)
                .IsLatestVersion);
            Assert.Equal(
                finalWarehouseStock,
                await verificationDb.ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock =>
                        stock.ItemId == itemId &&
                        stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync());
            Assert.Equal(
                finalWarehouseStock,
                await verificationDb.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.Id == itemId)
                    .Select(item => item.CurrentStock)
                    .SingleAsync());
            var ledgerEntryAfterInitializer = Assert.Single(
                await verificationDb.InventoryLedgerEntries
                    .AsNoTracking()
                    .Where(entry => entry.ItemId == itemId)
                    .ToListAsync());
            Assert.Equal(firstInvoiceId, ledgerEntryAfterInitializer.SourceDocumentId);
            Assert.Equal(-firstVersionQuantity, ledgerEntryAfterInitializer.QuantityDelta);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentEmptyPushes_DoNotRepairUnrelatedDuplicateLatestInvoiceChain()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Host = "127.0.0.1",
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var alternateTestDatabaseBuilder =
            new NpgsqlConnectionStringBuilder(testDatabaseBuilder.ConnectionString)
            {
                Host = "localhost"
            };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            var alternateOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(alternateTestDatabaseBuilder.ConnectionString)
                .Options;
            DuplicateLatestStockFixture fixture;

            await using (var initializationDb = CreateDbContext(options, CreateAdminUser()))
            {
                await initializationDb.Database.EnsureCreatedAsync();
                fixture = await SeedDuplicateLatestInvoiceStockAsync(initializationDb);
            }
            Dictionary<Guid, (long Revision, DateTime UpdatedAtUtc)> beforeVersionState;
            await using (var beforeDb = CreateDbContext(options, CreateAdminUser()))
            {
                var beforeVersions = await beforeDb.Invoices
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(invoice => invoice.VersionGroupId == fixture.VersionGroupId)
                    .Select(invoice => new
                    {
                        invoice.Id,
                        invoice.Revision,
                        invoice.UpdatedAtUtc
                    })
                    .ToListAsync();
                beforeVersionState = beforeVersions.ToDictionary(
                    invoice => invoice.Id,
                    invoice => (invoice.Revision, invoice.UpdatedAtUtc));
            }

            var firstUser = CreateAdminUser();
            var secondUser = CreateAdminUser();
            await using var firstDb = CreateDbContext(options, firstUser);
            await using var secondDb = CreateDbContext(alternateOptions, secondUser);
            Assert.NotEqual(
                firstDb.Database.GetDbConnection().DataSource,
                secondDb.Database.GetDbConnection().DataSource);
            var firstController = CreateController(firstDb, firstUser);
            var secondController = CreateController(secondDb, secondUser);
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCount = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            async Task<ActionResult<SyncPushResult>> PushFrom(
                SyncController controller,
                string deviceId,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref readyCount) == 2)
                    startGate.TrySetResult();

                await startGate.Task.WaitAsync(cancellationToken);
                return await controller.Push(
                    new SyncPushRequest { DeviceId = deviceId },
                    cancellationToken);
            }

            var responses = await Task.WhenAll(
                    PushFrom(firstController, "postgres-empty-repair-1", timeout.Token),
                    PushFrom(secondController, "postgres-empty-repair-2", timeout.Token))
                .WaitAsync(timeout.Token);
            Assert.All(responses, response =>
            {
                var result = AssertOk(response);
                Assert.Equal(0, result.ConflictCount);
            });

            await using var verificationDb = CreateDbContext(options, CreateAdminUser());
            var versions = await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.VersionGroupId == fixture.VersionGroupId)
                .ToListAsync(timeout.Token);
            Assert.True(versions.Single(invoice => invoice.Id == fixture.FirstInvoiceId).IsLatestVersion);
            Assert.True(versions.Single(invoice => invoice.Id == fixture.SecondInvoiceId).IsLatestVersion);
            Assert.All(versions, invoice =>
            {
                Assert.Equal(beforeVersionState[invoice.Id].Revision, invoice.Revision);
                Assert.Equal(beforeVersionState[invoice.Id].UpdatedAtUtc, invoice.UpdatedAtUtc);
            });
            Assert.Equal(
                5m,
                await verificationDb.ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock =>
                        stock.ItemId == fixture.ItemId &&
                        stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                5m,
                await verificationDb.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.Id == fixture.ItemId)
                    .Select(item => item.CurrentStock)
                    .SingleAsync(timeout.Token));
            Assert.Empty(await verificationDb.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.ItemId == fixture.ItemId)
                .ToListAsync(timeout.Token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task BusinessSchemaUpgrade_SeedsFutureRevisionBeforePostgreSqlRepairWrites()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            await using var dbContext = CreateDbContext(options, CreateAdminUser());
            await dbContext.Database.EnsureCreatedAsync();

            var existingCustomer = CreateRevisionCustomer("POSTGRES-FUTURE-REVISION-CUSTOMER");
            dbContext.Customers.Add(existingCustomer);
            await dbContext.SaveChangesAsync();
            var futureRevision = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Customers"
                 SET "Revision" = {futureRevision}
                 WHERE "Id" = {existingCustomer.Id};
                 """);
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DROP TABLE "SyncRevisionStates";
                DROP TABLE "PaymentAttachments";
                """);

            var method = typeof(DbInitializer).GetMethod(
                "EnsureBusinessDatabaseSchemaAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = method!.Invoke(
                null,
                new object?[] { dbContext, NullLogger.Instance, CancellationToken.None }) as Task;
            Assert.NotNull(task);
            await task!;

            Assert.True(await dbContext.GetCommittedRevisionAsync() >= futureRevision);
            var postUpgradeCustomer = CreateRevisionCustomer("POSTGRES-POST-UPGRADE-CUSTOMER");
            dbContext.Customers.Add(postUpgradeCustomer);
            await dbContext.SaveChangesAsync();
            Assert.True(postUpgradeCustomer.Revision > futureRevision);

            await using var connection = new NpgsqlConnection(testDatabaseBuilder.ConnectionString);
            await connection.OpenAsync();
            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText =
                """SELECT to_regclass('public."PaymentAttachments"') IS NOT NULL;""";
            Assert.True(Convert.ToBoolean(await tableCommand.ExecuteScalarAsync()));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task RentalAssetIndexMaintenance_SecondRun_PreservesPostgreSqlIndexRelations()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;

            await using var dbContext = CreateDbContext(options, CreateAdminUser());
            await dbContext.Database.EnsureCreatedAsync();

            await InvokeRentalAssetsTableMaintenanceAsync(dbContext);
            var indexRelationsBefore = await ReadRentalAssetIndexRelationsAsync(
                testDatabaseBuilder.ConnectionString);
            Assert.Equal(3, indexRelationsBefore.Length);
            Assert.All(indexRelationsBefore, definition =>
                Assert.Contains(" WHERE ", definition, StringComparison.OrdinalIgnoreCase));

            await InvokeRentalAssetsTableMaintenanceAsync(dbContext);

            Assert.Equal(
                indexRelationsBefore,
                await ReadRentalAssetIndexRelationsAsync(testDatabaseBuilder.ConnectionString));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    [PostgreSqlFact]
    public async Task InventoryTransferPurgeAndMissingDelete_AcknowledgeWithoutPersistingOrChangingStock()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            var sourceUser = CreateInventoryDeliveryAdminUser(
                "postgres-transfer-purge-source",
                OfficeCodeCatalog.Usenet);
            var itemId = Guid.NewGuid();
            var purgedTransferId = Guid.NewGuid();
            var missingDeleteId = Guid.NewGuid();
            var now = new DateTime(2026, 8, 2, 4, 10, 0, DateTimeKind.Utc);
            long purgeRevision;
            RecycleBinPurgeRecordDto expectedPurgeRecord;

            await using (var seedDb = CreateDbContext(options, sourceUser))
            {
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.Items.Add(new Item
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "PostgreSQL purge guard item",
                    NameMatchKey = "POSTGRESQLPURGEGUARDITEM",
                    Unit = "EA",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 10m
                });
                seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    UpdatedAtUtc = now.AddMinutes(-10)
                });
                var purgeRecord = new RecycleBinPurgeRecord
                {
                    Kind = "inventory-transfer",
                    EntityId = purgedTransferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    UpdatedAtUtc = now,
                    PurgedAtUtc = now
                };
                seedDb.RecycleBinPurgeRecords.Add(purgeRecord);
                await seedDb.SaveChangesAsync();
                await seedDb.Entry(purgeRecord).ReloadAsync();
                purgeRevision = purgeRecord.Revision;
                expectedPurgeRecord = purgeRecord.ToDto();
            }

            InventoryTransferDto BuildTransfer(
                Guid transferId,
                string mutationPrefix,
                decimal quantity,
                bool isDeleted,
                long revision) => new()
            {
                Id = transferId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransferNumber = $"TR-{transferId:N}"[..24],
                TransferDate = new DateOnly(2026, 8, 2),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedByUsername = sourceUser.Username,
                RequestedByUsername = sourceUser.Username,
                RequestedAtUtc = now.AddMinutes(-5),
                CreatedAtUtc = now.AddMinutes(-5),
                UpdatedAtUtc = now.AddMinutes(-5),
                LastSavedByUsername = sourceUser.Username,
                LastSavedAtUtc = now.AddMinutes(-5),
                MutationId = $"{mutationPrefix}:InventoryTransfer:{transferId:N}",
                MutationCreatedAtUtc = now.AddMinutes(-5),
                Revision = revision,
                ExpectedRevision = revision,
                IsDeleted = isDeleted,
                Lines =
                [
                    new InventoryTransferLineDto
                    {
                        Id = Guid.NewGuid(),
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "PostgreSQL purge guard item",
                        Unit = "EA",
                        Quantity = quantity
                    }
                ]
            };

            var purgedTransfer = BuildTransfer(
                purgedTransferId,
                "postgres-purged-transfer",
                2m,
                isDeleted: false,
                purgeRevision);
            var missingDelete = BuildTransfer(
                missingDeleteId,
                "postgres-missing-transfer-delete",
                decimal.MaxValue,
                isDeleted: true,
                revision: 42);
            SyncPushRequest CreateRequest() => new()
            {
                DeviceId = "postgres-transfer-purge-device",
                InventoryTransfers = [purgedTransfer, missingDelete]
            };

            await using (var pushDb = CreateDbContext(options, sourceUser))
            {
                var firstResult = AssertOk(
                    await CreateController(pushDb, sourceUser)
                        .Push(CreateRequest(), CancellationToken.None));
                Assert.Equal(2, firstResult.AcceptedCount);
                Assert.Equal(0, firstResult.ConflictCount);
                Assert.Equal(0, firstResult.DuplicateMutationCount);
                var acceptedTransferRevisions = firstResult.AcceptedRevisions
                    .Where(revision =>
                        revision.EntityName == nameof(InventoryTransfer) &&
                        (revision.EntityId == purgedTransferId ||
                         revision.EntityId == missingDeleteId))
                    .ToList();
                Assert.Equal(2, acceptedTransferRevisions.Count);
                Assert.All(
                    acceptedTransferRevisions,
                    revision => Assert.True(revision.IsDeleted));
                var firstPurgeReceipt = Assert.Single(firstResult.PurgeRecords);
                AssertInventoryTransferPurgeReceiptEqual(
                    expectedPurgeRecord,
                    firstPurgeReceipt);

                pushDb.ChangeTracker.Clear();
                Assert.False(
                    await pushDb.InventoryTransfers.IgnoreQueryFilters()
                        .AnyAsync(transfer =>
                            transfer.Id == purgedTransferId ||
                            transfer.Id == missingDeleteId));
                Assert.False(
                    await pushDb.InventoryTransferLines.IgnoreQueryFilters()
                        .AnyAsync(line =>
                            line.TransferId == purgedTransferId ||
                            line.TransferId == missingDeleteId));
                Assert.False(
                    await pushDb.InventoryLedgerEntries.AnyAsync(entry =>
                        entry.SourceDocumentId == purgedTransferId ||
                        entry.SourceDocumentId == missingDeleteId));
                Assert.Equal(
                    10m,
                    await pushDb.ItemWarehouseStocks
                        .Where(stock =>
                            stock.ItemId == itemId &&
                            stock.WarehouseCode ==
                            OfficeCodeCatalog.UsenetMainWarehouse)
                        .Select(stock => stock.Quantity)
                        .SingleAsync());

                var replayResult = AssertOk(
                    await CreateController(pushDb, sourceUser)
                        .Push(CreateRequest(), CancellationToken.None));
                Assert.Equal(2, replayResult.AcceptedCount);
                Assert.Equal(0, replayResult.ConflictCount);
                Assert.Equal(2, replayResult.DuplicateMutationCount);
                var replayPurgeReceipt = Assert.Single(replayResult.PurgeRecords);
                AssertInventoryTransferPurgeReceiptEqual(
                    firstPurgeReceipt,
                    replayPurgeReceipt);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
                await DropDatabaseAsync(maintenanceBuilder.ConnectionString, databaseName);
        }
    }

    private static void AssertInventoryTransferPurgeReceiptEqual(
        RecycleBinPurgeRecordDto expected,
        RecycleBinPurgeRecordDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.EntityId, actual.EntityId);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.TenantCode, actual.TenantCode);
        Assert.Equal(expected.OfficeCode, actual.OfficeCode);
        Assert.Equal(expected.SourceOfficeCode, actual.SourceOfficeCode);
        Assert.Equal(expected.TargetOfficeCode, actual.TargetOfficeCode);
        Assert.Equal(expected.PurgedAtUtc, actual.PurgedAtUtc);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
        Assert.Equal(expected.IsDeleted, actual.IsDeleted);
        Assert.Equal(expected.ExpectedRevision, actual.ExpectedRevision);
        Assert.Equal(expected.MutationId, actual.MutationId);
        Assert.Equal(expected.MutationCreatedAtUtc, actual.MutationCreatedAtUtc);
    }

    private static async Task InvokeRentalAssetsTableMaintenanceAsync(AppDbContext dbContext)
    {
        var method = typeof(DbInitializer).GetMethod(
            "EnsureRentalAssetsTableAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = method!.Invoke(
            null,
            new object?[] { dbContext, NullLogger.Instance, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task<string[]> ReadRentalAssetIndexRelationsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT index_class.oid::text || ':' || index_class.relname || ':' ||
                   pg_catalog.pg_get_indexdef(index_class.oid)
            FROM pg_catalog.pg_class AS index_class
            INNER JOIN pg_catalog.pg_index AS index_metadata
                ON index_metadata.indexrelid = index_class.oid
            INNER JOIN pg_catalog.pg_class AS table_class
                ON table_class.oid = index_metadata.indrelid
            INNER JOIN pg_catalog.pg_namespace AS table_namespace
                ON table_namespace.oid = table_class.relnamespace
            WHERE table_namespace.nspname = current_schema()
              AND table_class.relname = 'RentalAssets'
              AND index_class.relname IN (
                  'IX_RentalAssets_TenantCode_AssetKey',
                  'IX_RentalAssets_ManagementId',
                  'IX_RentalAssets_ManagementNumber')
            ORDER BY index_class.relname;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<string>();
        while (await reader.ReadAsync())
            definitions.Add(reader.GetString(0));

        return definitions.ToArray();
    }

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

    private static List<RentalManagementCompanyDto> CreateRentalManagementCompanyAliases(
        RentalManagementCompany canonicalCompany,
        long canonicalRevision,
        int count,
        string label)
        => Enumerable.Range(0, count)
            .Select(index => new RentalManagementCompanyDto
            {
                Id = Guid.NewGuid(),
                TenantCode = canonicalCompany.TenantCode,
                Code = canonicalCompany.Code,
                Name = $"POSTGRESQL {label} ALIAS {index:D3}",
                Revision = canonicalRevision,
                ExpectedRevision = canonicalRevision,
                CreatedAtUtc = canonicalCompany.CreatedAtUtc,
                UpdatedAtUtc = canonicalCompany.UpdatedAtUtc.AddMinutes(1)
            })
            .ToList();

    private static async Task<bool> PostgreSqlIndexExistsAsync(
        string connectionString,
        string indexName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = @index_name);
            """;
        command.Parameters.AddWithValue("index_name", indexName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task RunItemWarehouseStockResponseLossRetryAsync(
        bool seedExistingStock)
    {
        var configuredConnection =
            Environment.GetEnvironmentVariable(
                ConnectionVariableName);
        Assert.False(
            string.IsNullOrWhiteSpace(
                configuredConnection));

        var databaseName =
            $"gpv1_{Guid.NewGuid():N}";
        var maintenanceBuilder =
            new NpgsqlConnectionStringBuilder(
                configuredConnection)
            {
                Database = "postgres",
                IncludeErrorDetail = false
            };
        var testDatabaseBuilder =
            new NpgsqlConnectionStringBuilder(
                maintenanceBuilder.ConnectionString)
            {
                Database = databaseName,
                IncludeErrorDetail = false
            };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(
                maintenanceBuilder.ConnectionString,
                databaseName);
            databaseCreated = true;

            var options =
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(
                        testDatabaseBuilder.ConnectionString)
                    .Options;
            var itemId = Guid.NewGuid();
            var warehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse;
            var deviceId = seedExistingStock
                ? "postgres-existing-stock-response-loss"
                : "postgres-new-stock-response-loss";
            var firstUpdatedAtUtc = seedExistingStock
                ? new DateTime(
                    2026,
                    7,
                    30,
                    3,
                    0,
                    0,
                    DateTimeKind.Utc)
                : new DateTime(
                    2026,
                    7,
                    30,
                    4,
                    0,
                    0,
                    DateTimeKind.Utc);
            var secondUpdatedAtUtc =
                firstUpdatedAtUtc.AddMinutes(1);
            var firstQuantity =
                seedExistingStock ? 12m : 4m;
            var secondQuantity =
                seedExistingStock ? 18m : 9m;
            long initialRevision = 0;

            await using (var initializationDb =
                         CreateDbContext(
                             options,
                             CreateAdminUser()))
            {
                await initializationDb.Database
                    .EnsureCreatedAsync();
                initializationDb.Items.Add(
                    new Item
                    {
                        Id = itemId,
                        TenantCode =
                            TenantScopeCatalog
                                .UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog
                                .Usenet,
                        NameOriginal =
                            seedExistingStock
                                ? "PostgreSQL existing stock response loss item"
                                : "PostgreSQL new stock response loss item",
                        NameMatchKey =
                            seedExistingStock
                                ? "POSTGRESQLEXISTINGSTOCKRESPONSELOSSITEM"
                                : "POSTGRESQLNEWSTOCKRESPONSELOSSITEM",
                        ItemKind = ItemKinds.Product,
                        TrackingType =
                            ItemTrackingTypes.Stock,
                        CurrentStock =
                            seedExistingStock
                                ? 10m
                                : 0m
                    });

                if (seedExistingStock)
                {
                    var stock =
                        new ItemWarehouseStock
                        {
                            ItemId = itemId,
                            WarehouseCode =
                                warehouseCode,
                            Quantity = 10m,
                            UpdatedAtUtc =
                                firstUpdatedAtUtc
                                    .AddMinutes(-1)
                        };
                    initializationDb
                        .ItemWarehouseStocks
                        .Add(stock);
                    await initializationDb
                        .SaveChangesAsync();
                    initialRevision =
                        stock.Revision;
                }
                else
                {
                    await initializationDb
                        .SaveChangesAsync();
                }
            }

            SyncPushRequest CreateRequest(
                decimal quantity,
                long revision,
                DateTime updatedAtUtc)
                => new()
                {
                    DeviceId = deviceId,
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode =
                                warehouseCode,
                            Quantity = quantity,
                            Revision = revision,
                            ExpectedRevision =
                                revision,
                            UpdatedAtUtc =
                                updatedAtUtc
                        }
                    ]
                };

            async Task<SyncPushResult> PushWithFreshControllerAsync(
                SyncPushRequest request,
                CancellationToken cancellationToken)
            {
                var currentUser =
                    CreateAdminUser();
                await using var dbContext =
                    CreateDbContext(
                        options,
                        currentUser);
                var controller =
                    CreateController(
                        dbContext,
                        currentUser);
                return AssertOk(
                    await controller.Push(
                        request,
                        cancellationToken));
            }

            async Task<(decimal Quantity, long Revision)>
                ReadStockAsync(
                    CancellationToken cancellationToken)
            {
                await using var readDb =
                    CreateDbContext(
                        options,
                        CreateAdminUser());
                var stock = await readDb
                    .ItemWarehouseStocks
                    .AsNoTracking()
                    .SingleAsync(
                        stock =>
                            stock.ItemId == itemId &&
                            stock.WarehouseCode ==
                            warehouseCode,
                        cancellationToken);
                return (
                    stock.Quantity,
                    stock.Revision);
            }

            static void AssertAcceptedStock(
                SyncPushResult result,
                Guid expectedItemId,
                string expectedWarehouseCode)
            {
                Assert.Equal(
                    0,
                    result.ConflictCount);
                var acceptedKey =
                    Assert.Single(
                        result
                            .AcceptedItemWarehouseStockKeys);
                Assert.Equal(
                    expectedItemId,
                    acceptedKey.ItemId);
                Assert.Equal(
                    expectedWarehouseCode,
                    acceptedKey.WarehouseCode);
            }

            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(60));
            var firstRequest =
                CreateRequest(
                    firstQuantity,
                    initialRevision,
                    firstUpdatedAtUtc);
            var firstReceiptIdentity =
                ItemWarehouseStockMutationReceipt
                    .Create(
                        Assert.Single(
                            firstRequest
                                .ItemWarehouseStocks),
                        deviceId);
            var firstResult =
                await PushWithFreshControllerAsync(
                    firstRequest,
                    timeout.Token);
            AssertAcceptedStock(
                firstResult,
                itemId,
                warehouseCode);

            var firstStored =
                await ReadStockAsync(
                    timeout.Token);
            Assert.Equal(
                firstQuantity,
                firstStored.Quantity);
            Assert.True(
                firstStored.Revision >
                initialRevision);

            var secondResult =
                await PushWithFreshControllerAsync(
                    CreateRequest(
                        secondQuantity,
                        firstStored.Revision,
                        secondUpdatedAtUtc),
                    timeout.Token);
            AssertAcceptedStock(
                secondResult,
                itemId,
                warehouseCode);

            var secondStored =
                await ReadStockAsync(
                    timeout.Token);
            Assert.Equal(
                secondQuantity,
                secondStored.Quantity);
            Assert.True(
                secondStored.Revision >
                firstStored.Revision);

            var retryResult =
                await PushWithFreshControllerAsync(
                    CreateRequest(
                        firstQuantity,
                        initialRevision,
                        firstUpdatedAtUtc),
                    timeout.Token);
            AssertAcceptedStock(
                retryResult,
                itemId,
                warehouseCode);

            await using var verificationDb =
                CreateDbContext(
                    options,
                    CreateAdminUser());
            var stored =
                await verificationDb
                    .ItemWarehouseStocks
                    .AsNoTracking()
                    .SingleAsync(
                        stock =>
                            stock.ItemId == itemId &&
                            stock.WarehouseCode ==
                            warehouseCode,
                        timeout.Token);
            Assert.Equal(
                secondQuantity,
                stored.Quantity);
            Assert.Equal(
                secondStored.Revision,
                stored.Revision);

            var durableReceipt =
                Assert.Single(
                    await verificationDb
                        .ProcessedSyncMutations
                        .AsNoTracking()
                        .Where(receipt =>
                            receipt.MutationId ==
                            firstReceiptIdentity
                                .MutationId)
                        .ToListAsync(
                            timeout.Token));
            Assert.Equal(
                deviceId,
                durableReceipt.DeviceId);
            Assert.Equal(
                nameof(ItemWarehouseStock),
                durableReceipt.EntityName);
            Assert.Equal(
                firstReceiptIdentity.EntityId,
                durableReceipt.EntityId);
            Assert.Equal(
                firstReceiptIdentity
                    .ExpectedRevision,
                durableReceipt
                    .ExpectedRevision);
            Assert.Equal(
                firstReceiptIdentity.PayloadHash,
                durableReceipt.PayloadHash);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
            {
                await DropDatabaseAsync(
                    maintenanceBuilder
                        .ConnectionString,
                    databaseName);
            }
        }
    }

    private static SyncPushRequest CreateCustomerRequest(Guid customerId, string mutationId)
        => new()
        {
            DeviceId = "postgres-concurrent-customer-device",
            Customers = [CreateCustomerDto(customerId, mutationId)]
        };

    private static CustomerDto CreateCustomerDto(Guid customerId, string mutationId)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PostgreSQL concurrent customer",
            NameMatchKey = "POSTGRESQLCONCURRENTCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales,
            CreatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
            ExpectedRevision = 0,
            MutationId = mutationId,
            MutationCreatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc)
        };

    private static Customer CreateRevisionCustomer(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name.Replace("-", string.Empty, StringComparison.Ordinal),
            TradeType = CustomerClassificationNormalizer.Sales
        };

    private static async Task<DuplicateLatestStockFixture> SeedDuplicateLatestInvoiceStockAsync(
        AppDbContext dbContext)
    {
        var customerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var versionGroupId = firstInvoiceId;
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PostgreSQL Duplicate Latest Stock Customer",
            NameMatchKey = "POSTGRESQLDUPLICATELATESTSTOCKCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "PostgreSQL Duplicate Latest Stock Item",
            NameMatchKey = "POSTGRESQLDUPLICATELATESTSTOCKITEM",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        dbContext.Invoices.AddRange(
            CreateInvoice(
                firstInvoiceId,
                customerId,
                itemId,
                versionGroupId,
                versionNumber: 1,
                previousVersionId: null,
                quantity: 2m,
                updatedAtUtc: DateTime.UtcNow.AddMinutes(-2)),
            CreateInvoice(
                secondInvoiceId,
                customerId,
                itemId,
                versionGroupId,
                versionNumber: 2,
                previousVersionId: firstInvoiceId,
                quantity: 3m,
                updatedAtUtc: DateTime.UtcNow.AddMinutes(-1)));
        await dbContext.SaveChangesAsync();

        return new DuplicateLatestStockFixture(
            itemId,
            firstInvoiceId,
            secondInvoiceId,
            versionGroupId);
    }

    private static Invoice CreateInvoice(
        Guid invoiceId,
        Guid customerId,
        Guid itemId,
        Guid versionGroupId,
        int versionNumber,
        Guid? previousVersionId,
        decimal quantity,
        DateTime updatedAtUtc)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = $"PG-SYNC-DUP-STOCK-{versionNumber:0000}",
            VersionGroupId = versionGroupId,
            VersionNumber = versionNumber,
            PreviousVersionId = previousVersionId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 27),
            UpdatedAtUtc = updatedAtUtc,
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemId = itemId,
                    ItemNameOriginal = "PostgreSQL Duplicate Latest Stock Item",
                    Unit = "EA",
                    Quantity = quantity,
                    UnitPrice = 1_000m,
                    LineAmount = quantity * 1_000m,
                    ItemTrackingType = ItemTrackingTypes.Stock
                }
            ]
        };

    private sealed record DuplicateLatestStockFixture(
        Guid ItemId,
        Guid FirstInvoiceId,
        Guid SecondInvoiceId,
        Guid VersionGroupId);

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

    private static TestCurrentUserContext CreateDeliveryUser(
        string username,
        string officeCode)
        => new()
        {
            Username = username,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.DeliveryEdit]
        };

    private static TestCurrentUserContext CreateInventoryDeliveryAdminUser(
        string username,
        string officeCode)
        => new()
        {
            Username = username,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true,
            Permissions =
            [
                PermissionNames.DeliveryEdit,
                PermissionNames.ItemEdit
            ]
        };

    private static CustomersController CreateCustomersController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage());

    private static SyncPushResult AssertOk(ActionResult<SyncPushResult> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SyncPushResult>(ok.Value);
    }

    private sealed class SyncPushCommandCountingInterceptor : DbCommandInterceptor
    {
        private int _totalCommandCount;
        private int _rentalManagementCompanySelectCount;
        private int _conflictLogSelectCount;
        private int _conflictLogUpdateCount;
        private int _conflictLogDeleteCount;
        private int _auditLogSelectCount;
        private string _auditLogSelectCommandText = string.Empty;
        private readonly object _commandSummaryLock = new();
        private readonly List<string> _commandSummaries = [];

        public int TotalCommandCount => Volatile.Read(ref _totalCommandCount);
        public int RentalManagementCompanySelectCount =>
            Volatile.Read(ref _rentalManagementCompanySelectCount);
        public int ConflictLogSelectCount => Volatile.Read(ref _conflictLogSelectCount);
        public int ConflictLogUpdateCount => Volatile.Read(ref _conflictLogUpdateCount);
        public int ConflictLogDeleteCount => Volatile.Read(ref _conflictLogDeleteCount);
        public int AuditLogSelectCount => Volatile.Read(ref _auditLogSelectCount);
        public string AuditLogSelectCommandText => _auditLogSelectCommandText;
        public string CommandSummary
        {
            get
            {
                lock (_commandSummaryLock)
                {
                    return string.Join(
                        Environment.NewLine,
                        _commandSummaries.Select((summary, index) =>
                            $"{index + 1:D2}: {summary}"));
                }
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _totalCommandCount, 0);
            Interlocked.Exchange(ref _rentalManagementCompanySelectCount, 0);
            Interlocked.Exchange(ref _conflictLogSelectCount, 0);
            Interlocked.Exchange(ref _conflictLogUpdateCount, 0);
            Interlocked.Exchange(ref _conflictLogDeleteCount, 0);
            Interlocked.Exchange(ref _auditLogSelectCount, 0);
            _auditLogSelectCommandText = string.Empty;
            lock (_commandSummaryLock)
                _commandSummaries.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Count(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Count(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            Interlocked.Increment(ref _totalCommandCount);
            var singleLineCommandText = command.CommandText
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            var boundedCommandText = singleLineCommandText.Length <= 500
                ? singleLineCommandText
                : singleLineCommandText[..500] + "...";
            lock (_commandSummaryLock)
                _commandSummaries.Add(boundedCommandText);

            var commandText = command.CommandText.TrimStart();
            if (commandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                if (commandText.Contains("\"RentalManagementCompanies\"", StringComparison.Ordinal))
                    Interlocked.Increment(ref _rentalManagementCompanySelectCount);
                if (commandText.Contains("\"ConflictLogs\"", StringComparison.Ordinal))
                    Interlocked.Increment(ref _conflictLogSelectCount);
                if (commandText.Contains("\"AuditLogs\"", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _auditLogSelectCount);
                    _auditLogSelectCommandText = command.CommandText;
                }
                return;
            }

            if (commandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                commandText.Contains("\"ConflictLogs\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _conflictLogUpdateCount);
            }
            else if (commandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) &&
                     commandText.Contains("\"ConflictLogs\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _conflictLogDeleteCount);
            }
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
