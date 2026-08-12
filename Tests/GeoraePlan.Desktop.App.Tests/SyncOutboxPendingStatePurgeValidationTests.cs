using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("mixed-active-and-tombstone")]
    [InlineData("same-entity-conflict")]
    [InlineData("stale-receipt-revision")]
    [InlineData("foreign-tenant")]
    [InlineData("missing-source")]
    [InlineData("missing-target")]
    [InlineData("duplicate-receipt")]
    [InlineData("foreign-owner-receipt-collision")]
    [InlineData("same-owner-entity-extra-receipt")]
    [InlineData("wrong-kind")]
    [InlineData("deleted-receipt")]
    [InlineData("non-shared-office")]
    [InlineData("default-created-timestamp")]
    [InlineData("default-updated-timestamp")]
    [InlineData("default-purged-timestamp")]
    [InlineData("duplicate-tombstone-accepted")]
    [InlineData("server-revision-behind-purge")]
    public async Task RuntimeScopedSync_InventoryTransferPurgeMalformedResponse_FailsClosed(
        string scenario)
    {
        PrepareAppRoot($"georaeplan-transfer-purge-validation-{scenario}");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-10);
            var handler = new InventoryTransferPurgeValidationHandler(
                scenario,
                transferId,
                receiptId,
                purgeRevision: 40,
                currentServerRevision: 100,
                purgedAtUtc: now.AddMinutes(5));

            await using var provider = BuildRuntimeProvider(session, handler);
            var fixture = await SeedPurgeValidationTransferAsync(
                provider,
                session,
                transferId,
                receiptId,
                itemId,
                now,
                isDeleted: false,
                addForeignOwnerReceiptCollision: string.Equals(
                    scenario,
                    "foreign-owner-receipt-collision",
                    StringComparison.Ordinal),
                addUnexpectedSameOwnerReceipt: string.Equals(
                    scenario,
                    "same-owner-entity-extra-receipt",
                    StringComparison.Ordinal));

            await using (var syncScope = provider.CreateAsyncScope())
            {
                var sync = syncScope.ServiceProvider
                    .GetRequiredService<SyncService>();
                Assert.False(await sync.TrySyncAsync().WaitAsync(
                    TimeSpan.FromSeconds(15)));
            }

            Assert.Single(handler.PushRequests);
            Assert.Single(handler.PushRequests[0].InventoryTransfers);
            Assert.Equal(0, handler.PullCount);

            await AssertPurgeValidationTransferPreservedAsync(
                fixture,
                expectForeignOwnerReceiptCollision: string.Equals(
                    scenario,
                    "foreign-owner-receipt-collision",
                    StringComparison.Ordinal),
                expectUnexpectedSameOwnerReceipt: string.Equals(
                    scenario,
                    "same-owner-entity-extra-receipt",
                    StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_DeletedInventoryTransferWithDurableReceipt_HardPurges()
    {
        PrepareAppRoot("georaeplan-deleted-transfer-durable-purge-receipt");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-10);
            var handler = new InventoryTransferPurgeValidationHandler(
                "valid-durable-receipt",
                transferId,
                receiptId,
                purgeRevision: 40,
                currentServerRevision: 100,
                purgedAtUtc: now.AddMinutes(5));

            await using var provider = BuildRuntimeProvider(session, handler);
            var fixture = await SeedPurgeValidationTransferAsync(
                provider,
                session,
                transferId,
                receiptId,
                itemId,
                now,
                isDeleted: true,
                addForeignOwnerReceiptCollision: false,
                addUnexpectedSameOwnerReceipt: false);

            await using (var syncScope = provider.CreateAsyncScope())
            {
                var sync = syncScope.ServiceProvider
                    .GetRequiredService<SyncService>();
                Assert.True(await sync.TrySyncAsync().WaitAsync(
                    TimeSpan.FromSeconds(15)));
            }

            Assert.Single(handler.PushRequests);
            Assert.True(Assert.Single(
                handler.PushRequests[0].InventoryTransfers).IsDeleted);
            Assert.Equal(1, handler.PullCount);

            await using var verificationDb = new LocalDbContext();
            Assert.False(await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(transfer => transfer.Id == transferId));
            Assert.False(await verificationDb.InventoryTransferLines
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(line => line.TransferId == transferId));
            Assert.Empty(await verificationDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId)
                .ToListAsync());
            Assert.False(await verificationDb
                .InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .AnyAsync(conflict => conflict.TransferId == transferId));
            Assert.False(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .AnyAsync(record => record.Id == receiptId));
            var outbox = await verificationDb.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(entry =>
                    entry.EntityName == nameof(LocalInventoryTransfer) &&
                    entry.EntityId == transferId);
            Assert.Equal("Acknowledged", outbox.Status);
            Assert.Equal(40, outbox.AcceptedRevision);
            Assert.NotNull(outbox.AcknowledgedAtUtc);
            Assert.False(File.Exists(fixture.EvidencePath));
            Assert.Equal("100", await verificationDb.Settings
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_DeletedInventoryTransferMissingDeleteWithoutReceipt_RemainsCleanTombstone()
    {
        PrepareAppRoot("georaeplan-deleted-transfer-missing-delete-ack");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-10);
            var handler = new InventoryTransferPurgeValidationHandler(
                "missing-delete-without-receipt",
                transferId,
                receiptId,
                purgeRevision: 40,
                currentServerRevision: 100,
                purgedAtUtc: now.AddMinutes(5));

            await using var provider = BuildRuntimeProvider(session, handler);
            var fixture = await SeedPurgeValidationTransferAsync(
                provider,
                session,
                transferId,
                receiptId,
                itemId,
                now,
                isDeleted: true,
                addForeignOwnerReceiptCollision: false,
                addUnexpectedSameOwnerReceipt: false);

            await using (var syncScope = provider.CreateAsyncScope())
            {
                var sync = syncScope.ServiceProvider
                    .GetRequiredService<SyncService>();
                Assert.True(await sync.TrySyncAsync().WaitAsync(
                    TimeSpan.FromSeconds(15)));
            }

            Assert.Single(handler.PushRequests);
            Assert.True(Assert.Single(
                handler.PushRequests[0].InventoryTransfers).IsDeleted);
            Assert.Equal(1, handler.PullCount);

            await using var verificationDb = new LocalDbContext();
            var tombstone = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .AsNoTracking()
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.True(tombstone.IsDeleted);
            Assert.False(tombstone.IsDirty);
            Assert.Equal(40, tombstone.Revision);
            Assert.Equal("purge validation local draft", tombstone.Memo);
            Assert.Equal(fixture.EvidencePath, tombstone.ReceiveEvidencePath);
            var line = Assert.Single(tombstone.Lines);
            Assert.Equal(fixture.LineId, line.Id);
            Assert.Equal(itemId, line.ItemId);
            Assert.Equal(2m, line.Quantity);
            Assert.Empty(await verificationDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId)
                .ToListAsync());
            Assert.False(await verificationDb
                .InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .AnyAsync(conflict => conflict.TransferId == transferId));
            Assert.False(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .AnyAsync(record => record.Id == receiptId));
            var outbox = await verificationDb.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(entry =>
                    entry.EntityName == nameof(LocalInventoryTransfer) &&
                    entry.EntityId == transferId);
            Assert.Equal("Acknowledged", outbox.Status);
            Assert.Equal(40, outbox.AcceptedRevision);
            Assert.NotNull(outbox.AcknowledgedAtUtc);
            Assert.True(File.Exists(fixture.EvidencePath));
            Assert.Equal(
                "purge validation evidence",
                await File.ReadAllTextAsync(fixture.EvidencePath));
            Assert.Equal("100", await verificationDb.Settings
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<PurgeValidationFixture>
        SeedPurgeValidationTransferAsync(
            ServiceProvider provider,
            SessionState session,
            Guid transferId,
            Guid receiptId,
            Guid itemId,
            DateTime now,
            bool isDeleted,
            bool addForeignOwnerReceiptCollision,
            bool addUnexpectedSameOwnerReceipt)
    {
        Directory.CreateDirectory(AppPaths.TransactionAttachmentsDir);
        var evidencePath = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            $"purge-validation-{transferId:N}.pdf");
        await File.WriteAllTextAsync(
            evidencePath,
            "purge validation evidence");
        var lineId = Guid.NewGuid();

        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider
            .GetRequiredService<LocalDbContext>();
        await setupDb.Database.EnsureDeletedAsync();
        await setupDb.Database.EnsureCreatedAsync();
        setupDb.Items.Add(new LocalItem
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "purge validation stock item",
            NameMatchKey = "purge validation stock item",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 0m,
            Revision = 12,
            IsDirty = false,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now
        });
        setupDb.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = -2m,
                Revision = 20,
                UpdatedAtUtc = now
            },
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 2m,
                Revision = 21,
                UpdatedAtUtc = now
            });
        setupDb.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            TransferNumber = "TR-PURGE-VALIDATION",
            TransferDate = DateOnly.FromDateTime(now),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            Memo = "purge validation local draft",
            ReceiveEvidencePath = evidencePath,
            CreatedByUsername = session.User!.Username,
            LastSavedByUsername = session.User.Username,
            Revision = 7,
            IsDirty = true,
            IsDeleted = isDeleted,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "purge validation stock item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        setupDb.Settings.Add(new LocalSetting
        {
            Key = "LastSyncRevision",
            Value = "100"
        });
        if (addForeignOwnerReceiptCollision)
        {
            setupDb.DeferredRecycleBinPurgeRecords.Add(
                new LocalDeferredRecycleBinPurgeRecord
                {
                    Id = receiptId,
                    BusinessDatabaseName = TenantScopeCatalog.Itworld,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Shared,
                    Kind = "inventory-transfer",
                    EntityId = transferId,
                    Revision = 40,
                    PurgedAtUtc = now.AddMinutes(5),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
        }

        Guid? extraReceiptId = null;
        if (addUnexpectedSameOwnerReceipt)
        {
            extraReceiptId = Guid.NewGuid();
            setupDb.DeferredRecycleBinPurgeRecords.Add(
                new LocalDeferredRecycleBinPurgeRecord
                {
                    Id = extraReceiptId.Value,
                    BusinessDatabaseName =
                        TenantScopeCatalog.GetDatabaseName(
                            TenantScopeCatalog.UsenetGroup),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    Kind = "inventory-transfer",
                    EntityId = transferId,
                    Revision = 41,
                    PurgedAtUtc = now.AddMinutes(6),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
        }

        await setupDb.SaveChangesAsync();
        return new PurgeValidationFixture(
            transferId,
            receiptId,
            itemId,
            lineId,
            evidencePath,
            extraReceiptId);
    }

    private static async Task AssertPurgeValidationTransferPreservedAsync(
        PurgeValidationFixture fixture,
        bool expectForeignOwnerReceiptCollision,
        bool expectUnexpectedSameOwnerReceipt)
    {
        await using var verificationDb = new LocalDbContext();
        var transfer = await verificationDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .AsNoTracking()
            .SingleAsync(current => current.Id == fixture.TransferId);
        Assert.False(transfer.IsDeleted);
        Assert.True(transfer.IsDirty);
        Assert.Equal(7, transfer.Revision);
        Assert.Equal("purge validation local draft", transfer.Memo);
        Assert.Equal(fixture.EvidencePath, transfer.ReceiveEvidencePath);
        var line = Assert.Single(transfer.Lines);
        Assert.Equal(fixture.LineId, line.Id);
        Assert.Equal(fixture.ItemId, line.ItemId);
        Assert.Equal("purge validation stock item", line.ItemNameOriginal);
        Assert.Equal("EA", line.Unit);
        Assert.Equal(2m, line.Quantity);
        await AssertPurgeValidationStocksAsync(
            verificationDb,
            fixture.ItemId);
        Assert.False(await verificationDb
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .AnyAsync(conflict => conflict.TransferId == fixture.TransferId));
        var outbox = await verificationDb.SyncOutboxEntries
            .AsNoTracking()
            .SingleAsync(entry =>
                entry.EntityName == nameof(LocalInventoryTransfer) &&
                entry.EntityId == fixture.TransferId);
        Assert.Equal("Failed", outbox.Status);
        Assert.Equal(0, outbox.AcceptedRevision);
        Assert.Null(outbox.AcknowledgedAtUtc);
        Assert.True(File.Exists(fixture.EvidencePath));
        Assert.Equal(
            "purge validation evidence",
            await File.ReadAllTextAsync(fixture.EvidencePath));
        Assert.Equal("100", await verificationDb.Settings
            .Where(setting => setting.Key == "LastSyncRevision")
            .Select(setting => setting.Value)
            .SingleAsync());

        var collision = await verificationDb
            .DeferredRecycleBinPurgeRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == fixture.ReceiptId);
        if (expectForeignOwnerReceiptCollision)
        {
            Assert.NotNull(collision);
            Assert.Equal(
                TenantScopeCatalog.Itworld,
                collision!.BusinessDatabaseName);
            Assert.Equal(TenantScopeCatalog.Itworld, collision.TenantCode);
            Assert.Null(collision.AppliedAtUtc);
        }
        else
        {
            Assert.Null(collision);
        }

        if (expectUnexpectedSameOwnerReceipt)
        {
            Assert.NotNull(fixture.ExtraReceiptId);
            var extraReceipt = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .SingleAsync(record =>
                    record.Id == fixture.ExtraReceiptId.Value);
            Assert.Equal(41, extraReceipt.Revision);
            Assert.Null(extraReceipt.AppliedAtUtc);
        }
        else
        {
            Assert.Null(fixture.ExtraReceiptId);
        }
    }

    private static async Task AssertPurgeValidationStocksAsync(
        LocalDbContext db,
        Guid itemId)
    {
        var stocks = await db.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => stock.ItemId == itemId)
            .OrderBy(stock => stock.WarehouseCode)
            .ToListAsync();
        Assert.Equal(2, stocks.Count);
        Assert.Contains(
            stocks,
            stock =>
                stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse &&
                stock.Quantity == -2m &&
                stock.Revision == 20);
        Assert.Contains(
            stocks,
            stock =>
                stock.WarehouseCode ==
                    OfficeCodeCatalog.YeonsuMainWarehouse &&
                stock.Quantity == 2m &&
                stock.Revision == 21);
    }

    private sealed record PurgeValidationFixture(
        Guid TransferId,
        Guid ReceiptId,
        Guid ItemId,
        Guid LineId,
        string EvidencePath,
        Guid? ExtraReceiptId);

    private sealed class InventoryTransferPurgeValidationHandler
        : HttpMessageHandler
    {
        private readonly string _scenario;
        private readonly Guid _transferId;
        private readonly Guid _receiptId;
        private readonly long _purgeRevision;
        private readonly long _currentServerRevision;
        private readonly DateTime _purgedAtUtc;

        public InventoryTransferPurgeValidationHandler(
            string scenario,
            Guid transferId,
            Guid receiptId,
            long purgeRevision,
            long currentServerRevision,
            DateTime purgedAtUtc)
        {
            _scenario = scenario;
            _transferId = transferId;
            _receiptId = receiptId;
            _purgeRevision = purgeRevision;
            _currentServerRevision = currentServerRevision;
            _purgedAtUtc = purgedAtUtc;
        }

        public List<SyncPushRequest> PushRequests { get; } = [];

        public int PullCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                var pushedRequest = await request.Content!
                    .ReadFromJsonAsync<SyncPushRequest>(
                        cancellationToken: cancellationToken);
                Assert.NotNull(pushedRequest);
                PushRequests.Add(pushedRequest!);
                var pushedTransfer = Assert.Single(
                    pushedRequest!.InventoryTransfers);
                Assert.Equal(_transferId, pushedTransfer.Id);

                var responseRevision = string.Equals(
                    _scenario,
                    "stale-receipt-revision",
                    StringComparison.Ordinal)
                    ? 6
                    : _purgeRevision;
                var acceptedTombstone = new SyncAcceptedRevisionDto
                {
                    EntityName = "InventoryTransfer",
                    EntityId = _transferId,
                    Revision = responseRevision,
                    UpdatedAtUtc = string.Equals(
                        _scenario,
                        "default-updated-timestamp",
                        StringComparison.Ordinal)
                        ? default
                        : _purgedAtUtc,
                    IsDeleted = true
                };
                var acceptedRevisions = new List<SyncAcceptedRevisionDto>
                {
                    acceptedTombstone
                };
                if (string.Equals(
                        _scenario,
                        "mixed-active-and-tombstone",
                        StringComparison.Ordinal))
                {
                    acceptedRevisions.Insert(
                        0,
                        new SyncAcceptedRevisionDto
                        {
                            EntityName = "InventoryTransfer",
                            EntityId = _transferId,
                            Revision = responseRevision,
                            UpdatedAtUtc = _purgedAtUtc,
                            IsDeleted = false
                        });
                }
                if (string.Equals(
                        _scenario,
                        "duplicate-tombstone-accepted",
                        StringComparison.Ordinal))
                {
                    acceptedRevisions.Add(
                        new SyncAcceptedRevisionDto
                        {
                            EntityName = "InventoryTransfer",
                            EntityId = _transferId,
                            Revision = responseRevision,
                            UpdatedAtUtc = acceptedTombstone.UpdatedAtUtc,
                            IsDeleted = true
                        });
                }

                var receipt = new RecycleBinPurgeRecordDto
                {
                    Id = _receiptId,
                    Kind = string.Equals(
                        _scenario,
                        "wrong-kind",
                        StringComparison.Ordinal)
                        ? "invoice"
                        : "inventory-transfer",
                    EntityId = _transferId,
                    TenantCode = string.Equals(
                        _scenario,
                        "foreign-tenant",
                        StringComparison.Ordinal)
                        ? TenantScopeCatalog.Itworld
                        : TenantScopeCatalog.UsenetGroup,
                    OfficeCode = string.Equals(
                        _scenario,
                        "non-shared-office",
                        StringComparison.Ordinal)
                        ? OfficeCodeCatalog.Usenet
                        : OfficeCodeCatalog.Shared,
                    SourceOfficeCode = string.Equals(
                        _scenario,
                        "missing-source",
                        StringComparison.Ordinal)
                        ? string.Empty
                        : OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = string.Equals(
                        _scenario,
                        "missing-target",
                        StringComparison.Ordinal)
                        ? string.Empty
                        : OfficeCodeCatalog.Yeonsu,
                    Revision = responseRevision,
                    PurgedAtUtc = string.Equals(
                        _scenario,
                        "default-purged-timestamp",
                        StringComparison.Ordinal)
                        ? default
                        : _purgedAtUtc,
                    CreatedAtUtc = string.Equals(
                        _scenario,
                        "default-created-timestamp",
                        StringComparison.Ordinal)
                        ? default
                        : _purgedAtUtc,
                    UpdatedAtUtc = string.Equals(
                        _scenario,
                        "default-updated-timestamp",
                        StringComparison.Ordinal)
                        ? default
                        : _purgedAtUtc,
                    IsDeleted = string.Equals(
                        _scenario,
                        "deleted-receipt",
                        StringComparison.Ordinal)
                };
                var purgeRecords = string.Equals(
                    _scenario,
                    "missing-delete-without-receipt",
                    StringComparison.Ordinal)
                    ? []
                    : new List<RecycleBinPurgeRecordDto> { receipt };
                if (string.Equals(
                        _scenario,
                        "duplicate-receipt",
                        StringComparison.Ordinal))
                {
                    purgeRecords.Add(new RecycleBinPurgeRecordDto
                    {
                        Id = receipt.Id,
                        Kind = receipt.Kind,
                        EntityId = receipt.EntityId,
                        TenantCode = receipt.TenantCode,
                        OfficeCode = receipt.OfficeCode,
                        SourceOfficeCode = receipt.SourceOfficeCode,
                        TargetOfficeCode = receipt.TargetOfficeCode,
                        Revision = receipt.Revision,
                        PurgedAtUtc = receipt.PurgedAtUtc,
                        CreatedAtUtc = receipt.CreatedAtUtc,
                        UpdatedAtUtc = receipt.UpdatedAtUtc,
                        IsDeleted = receipt.IsDeleted
                    });
                }

                var conflicts = new List<ConflictLogDto>();
                if (string.Equals(
                        _scenario,
                        "same-entity-conflict",
                        StringComparison.Ordinal))
                {
                    conflicts.Add(new ConflictLogDto
                    {
                        EntityName = "InventoryTransfer",
                        EntityId = _transferId.ToString("D"),
                        Reason = "contradictory accepted and conflict response",
                        ClientJson = "{}",
                        ServerJson = "{}"
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        AcceptedCount = acceptedRevisions.Count,
                        ConflictCount = conflicts.Count,
                        CurrentServerRevision = string.Equals(
                            _scenario,
                            "server-revision-behind-purge",
                            StringComparison.Ordinal)
                            ? _purgeRevision - 1
                            : _currentServerRevision,
                        AcceptedRevisions = acceptedRevisions,
                        PurgeRecords = purgeRecords,
                        Conflicts = conflicts
                    })
                };
            }

            if (path == "/sync/pull")
            {
                PullCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse
                    {
                        CurrentServerRevision = _currentServerRevision
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
